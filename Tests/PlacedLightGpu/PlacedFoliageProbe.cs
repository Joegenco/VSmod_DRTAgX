using System;
using System.IO;
using System.Linq;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

// Native crossed-card wind patterns flow through the maintained MRT producer,
// receiver-tag handoff and legacy-mode compositor. Cached D24 maps must never shade grass in that mode;
// captured RGB preserves configured foliage energy; complete back-face local RGB
// must retain the existing 20%, including when voxel light dominates its brightness.
internal static class PlacedFoliageProbe
{
    // In this fixture the lamp is at the camera. A visible two-sided card
    // therefore uses the absolute projected cosine, regardless of mesh winding.
    private static float VisibleFacing(Vector3 normal,Vector3 position)
    {
        if(normal.LengthSquared<1e-6f||position.LengthSquared<1e-10f)return 1;
        float cosine=Math.Abs(Vector3.Dot(Vector3.Normalize(normal),-Vector3.Normalize(position)));
        float edge=Math.Clamp((cosine-.01f)/.03f,0,1);edge=edge*edge*(3-2*edge);
        return .25f+.75f*cosine*edge;
    }
    private const int Size = 16;

    internal static void Run(string deferredPath, string assetsOverride = null, bool staticNoCullOnly = false, bool windTemporalOnly = false)
    {
        using var state = new ShadowGlState();
        string assets = Path.GetFullPath(assetsOverride ?? Path.Combine(Path.GetDirectoryName(deferredPath)!, "../.."));
        string deferred = File.ReadAllText(Path.Combine(assets, "sheydermod/shaders/deferredlighting.fsh"));
        string opaque = File.ReadAllText(Path.Combine(assets, "game/shaders/chunkopaque.fsh"));
        string opaqueVertex = File.ReadAllText(Path.Combine(assets, "game/shaders/chunkopaque.vsh"));
        string lighting = Path.Combine(assets, "drtagx/shaders/lighting");
        string modules = Path.Combine(assets, "drtagx/shaders/deferred");
        string sampling = File.ReadAllText(Path.Combine(lighting, "drtagx_sun_shadow_sampling.fsh"));
        string balance = File.ReadAllText(Path.Combine(lighting, "drtagx_light_balance.ash"));
        string shadows = File.ReadAllText(Path.Combine(modules, "drtagx_deferred_staticshadows.fsh"));
        string placed = File.ReadAllText(Path.Combine(modules, "drtagx_deferred_placedlights.fsh"));
        string relighting = File.ReadAllText(Path.Combine(modules, "drtagx_deferred_relighting.fsh"));
        string cube = File.ReadAllText(Path.Combine(modules, "drtagx_deferred_cube.fsh"));
        int visibility = shadows.IndexOf("float drtStaticVisibility(", StringComparison.Ordinal);
        int entry = shadows.IndexOf('{', visibility) + 1;
        // Count entry into the maintained filter, including early rejection.
        // No second filter implementation can accidentally make this pass.
        shadows = shadows.Insert(entry, "\nstaticShadowCalls += 1.0;\n");
        int call = deferred.IndexOf("    drtDeferredRelight(", StringComparison.Ordinal);
        string receiverArgument = deferred[call..deferred.IndexOf(';', call)];
        receiverArgument = receiverArgument[(receiverArgument.LastIndexOf(',') + 1)..receiverArgument.LastIndexOf(')')].Trim();
        string tags = string.Join("\n", deferred.Split('\n').Where(line =>
            line.TrimStart().StartsWith("float receiverTag =", StringComparison.Ordinal) ||
            line.TrimStart().StartsWith("float grassTag =", StringComparison.Ordinal) ||
            line.TrimStart().StartsWith("float foliageTag =", StringComparison.Ordinal) ||
            line.TrimStart().StartsWith("float staticNoCullTag =", StringComparison.Ordinal)));
        // Execute the maintained decoder and vertex wind-presence assignment.
        // The fallback keeps the old asset snapshot runnable for reproduction.
        int specStart = deferred.IndexOf("    float specMap =", StringComparison.Ordinal);
        string specDecoder = deferred[specStart..(deferred.IndexOf(';', specStart) + 1)];
        string windPresence = opaqueVertex.Split('\n').FirstOrDefault(line => line.TrimStart().StartsWith("drtWindPresence =", StringComparison.Ordinal))
            ?? "drtWindPresence = windMode != 0 ? 1.0 : 0.0;";
        string staticTag = tags.Contains("float staticNoCullTag =", StringComparison.Ordinal) ? "staticNoCullTag" : "0.";
        const string vertex = """
            #version 430 core
            uniform int pattern;
            uniform vec3 receiverCentre;
            uniform float cardAngle=0, cardPitch=0;
            uniform bool reverseWinding=true;
            uniform bool projectionWindFixture=false;
            out vec4 worldPos;
            flat out int renderFlags;
            out float drtWindPresence;
            void main() {
                const int indices[6]=int[6](0,1,2,0,2,3);
                const int clockwise[6]=int[6](0,2,1,0,3,2);
                const vec2 corners[4]=vec2[4](vec2(0,0),vec2(1,0),vec2(1,1),vec2(0,1));
                const int north[4]=int[4](0,2,2,0), south[4]=int[4](0,0,2,2);
                int i=reverseWinding?clockwise[gl_VertexID]:indices[gl_VertexID]; vec2 p=corners[i];
                gl_Position=vec4(p*2.-1.,0,1);
                // Keep a grazing card visible to the small probe viewport, but
                // preserve the winding reversal of its perspective projection.
                if(projectionWindFixture && cos(cardAngle)<0)gl_Position.x=-gl_Position.x;
                vec2 local=(p-.5)*.2;
                // Rotate actual interpolated geometry, so the maintained
                // derivative normal follows the card rather than a test stub.
                vec3 card=vec3(local.x*cos(cardAngle),local.y,-local.x*sin(cardAngle));
                card.yz=mat2(cos(cardPitch),sin(cardPitch),-sin(cardPitch),cos(cardPitch))*card.yz;
                worldPos=vec4(receiverCentre+card,1);
                int mode=pattern==0?north[i]:pattern==1?south[i]:pattern==2?0:pattern==3?3:pattern==4?13:1;
                // WindMode is bits 25..28. Roots deliberately have zero wind;
                // flat interpolation uses a provoking vertex for each triangle.
                renderFlags=mode<<25;
                int windMode=renderFlags & 0x1e000000;
                DRT_WIND_PRESENCE_ASSIGNMENT
            }
            """;
        const string declarations = """
            in vec4 worldPos;
            flat in int renderFlags;
            in float drtWindPresence;
            uniform vec3 authoredNormal;
            uniform int haxyFade=1;
            uniform float alphaTest=.42, fixtureAlpha=1;
            const int WindModeBitMask=0x1e000000, WindModeLeavesMask=0x06000000;
            const float specularStrength=1, fogAmount=0, glowLevel=0, voxSunLight=0, nb=.7, tl_isWind=0;
            const vec2 uv=vec2(0);
            uniform sampler2D specularTex;
            const mat4 modelViewMatrix=mat4(1);
            vec3 drtVoxelLight=vec3(.2,.3,.4), drtEmissionLight=vec3(0), drtTerrainSunPlaneNormal=vec3(0);
            // This fixture deliberately exercises the legacy cache-off path.
            const int drtTerrainPlacedCount=0;
            const float drtTerrainPlacedVisibility=1.;
            const vec3 drtTerrainPlacedRadiance=vec3(0),drtPlacedRestPos=vec3(0);
            void drtPrepareTerrainPlacedVisibility(vec3 p,bool foliage,float glow){}
            vec4 gnormal=vec4(0);
            """;
        string fillSource = "#version 430 core\n#define SSAOLEVEL 1\n#define SHADOWQUALITY 2\n" + declarations + balance + sampling +
            "\nlayout(location=0)out vec4 outColor;layout(location=1)out vec4 outGlow;layout(location=2)out vec4 outGNormal;layout(location=3)out vec4 outGPosition;\n" +
            ProbeShader.Function(opaque, "void sm_deferredFill(") + """
            void main() {
                drtTerrainSunPlaneNormal=drtSunPrimitiveNormal(worldPos.xyz,authoredNormal);
                gnormal=vec4(authoredNormal,0);
                sm_deferredFill(vec4(vec3(1),fixtureAlpha),vec3(1));
            }
            """;
        string consumeSource = """
            #version 430 core
            layout(std430,binding=4)readonly buffer Sources{vec4 drtStaticSourceData[];};
            layout(std430,binding=5)readonly buffer Tiles{uint drtStaticTileData[];};
            uniform sampler2DArrayShadow drtStaticMaps;
            uniform sampler2D gGlow, gNormal;
            uniform vec2 drtPlacedCalibration[32];
            uniform int drtStaticCount=1;
            uniform int drtStaticAllTerrainPasses=0; // Explicitly retain the legacy-mode regression fixtures.
            uniform int drtShadowGridEnabled=1;
            const int drtStaticTileWidth=1;
            uniform float drtStaticBlend=1, glowAmount=0;
            uniform vec3 voxelRgb=vec3(.2,.3,.4);
            uniform vec3 rawRgb=vec3(1);
            uniform bool unknownNormal=false;
            uniform bool solidReference=false;
            uniform bool inspectMetadata=false;
            const bool placedPrepared=false;
            const mat4 invModelViewMatrix=mat4(1);
            vec3 drtSkyColor=vec3(0), drtSkyLight=vec3(0), drtSunLight=vec3(0), drtLocalLight=vec3(0);
            float drtSurfaceSunAccess=0, staticShadowCalls=0;
            vec3 drtCurrentFrameLights(vec3 r,vec3 n){return vec3(0);}
            in vec4 worldPos;
            out vec4 color;
            """ + balance + cube + shadows + placed + relighting + "\nvoid main(){\n" +
            "vec4 glowVec=texelFetch(gGlow,ivec2(gl_FragCoord.xy),0);\n" + tags + "\n" + specDecoder + "\n" +
            "if(inspectMetadata){color=vec4(grassTag,foliageTag," + staticTag + ",specMap);return;}\n" +
            "vec3 n=unknownNormal?vec3(0):normalize(texelFetch(gNormal,ivec2(gl_FragCoord.xy),0).xyz);\n" +
            // A head-on solid reference isolates calibrated source energy from
            // the foliage response. The fixture source is at the eye origin.
            "if(solidReference)n=normalize(-worldPos.xyz);\n" +
            "vec3 albedo=rawRgb*(voxelRgb+vec3(10.*glowAmount));float bb=0;\n" +
            "drtDeferredRelight(albedo,bb,rawRgb,worldPos.xyz,n,n,0.,0.,glowAmount,solidReference?0.:(" + receiverArgument + "));\n" +
            "color=vec4(albedo,staticShadowCalls);}\n";
        string maintainedVertex = vertex.Replace("DRT_WIND_PRESENCE_ASSIGNMENT", windPresence);
        int fill = ProbeShader.Program((ShaderType.VertexShader,maintainedVertex),(ShaderType.FragmentShader,fillSource));
        int consume = ProbeShader.Program((ShaderType.VertexShader,maintainedVertex),(ShaderType.FragmentShader,consumeSource));
        int[] textures=new int[6]; GL.GenTextures(textures.Length,textures);
        int fillFbo=GL.GenFramebuffer(), outputFbo=GL.GenFramebuffer(), vao=GL.GenVertexArray();
        int sources=GL.GenBuffer(), tiles=GL.GenBuffer(), failures=0, checks=0;
        int checkedDraws=0;
        float[] pixels=new float[Size*Size*4];
        try {
            GL.BindVertexArray(vao); GL.Viewport(0,0,Size,Size);
            GL.Disable(EnableCap.DepthTest); GL.Disable(EnableCap.Blend); GL.Disable(EnableCap.ScissorTest); GL.Disable(EnableCap.CullFace);
            GL.ColorMask(true,true,true,true);
            GL.ActiveTexture(TextureUnit.Texture14); GL.BindSampler(14,0);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer,fillFbo);
            for(int i=0;i<4;i++) {
                GL.BindTexture(TextureTarget.Texture2D,textures[i]);
                GL.TexImage2D(TextureTarget.Texture2D,0,i==2?PixelInternalFormat.Rgba16f:PixelInternalFormat.Rgba32f,Size,Size,0,PixelFormat.Rgba,PixelType.Float,IntPtr.Zero);
                GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.Nearest);
                GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)TextureMagFilter.Nearest);
                GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,FramebufferAttachment.ColorAttachment0+i,TextureTarget.Texture2D,textures[i],0);
            }
            GL.DrawBuffers(4,new[]{DrawBuffersEnum.ColorAttachment0,DrawBuffersEnum.ColorAttachment1,DrawBuffersEnum.ColorAttachment2,DrawBuffersEnum.ColorAttachment3});
            GL.BindFramebuffer(FramebufferTarget.Framebuffer,outputFbo);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,FramebufferAttachment.ColorAttachment0,TextureTarget.Texture2D,textures[0],0);
            GL.DrawBuffer(DrawBufferMode.ColorAttachment0); GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
            int size=StaticTerrainShadowMaps.FaceSize;
            GL.BindTexture(TextureTarget.Texture2DArray,textures[4]);
            GL.TexStorage3D(TextureTarget3d.Texture2DArray,1,SizedInternalFormat.DepthComponent24,size,size,12);
            GL.TexParameter(TextureTarget.Texture2DArray,TextureParameterName.TextureCompareMode,(int)TextureCompareMode.CompareRefToTexture);
            GL.TexParameter(TextureTarget.Texture2DArray,TextureParameterName.TextureCompareFunc,(int)DepthFunction.Lequal);
            GL.TexParameter(TextureTarget.Texture2DArray,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2DArray,TextureParameterName.TextureMagFilter,(int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2DArray,TextureParameterName.TextureWrapS,(int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2DArray,TextureParameterName.TextureWrapT,(int)TextureWrapMode.ClampToEdge);
            GL.ActiveTexture(TextureUnit.Texture2); GL.BindSampler(2,0); GL.BindTexture(TextureTarget.Texture2D,textures[5]);
            GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.R32f,1,1,0,PixelFormat.Red,PixelType.Float,new[]{0f});
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)TextureMagFilter.Nearest);
            GL.UseProgram(fill); GL.Uniform1(GL.GetUniformLocation(fill,"specularTex"),2);
            float[] records={0,0,0,22, 1,1,1,0, 1,0,1,0, 0,0,0,31};
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer,sources); GL.BufferData(BufferTarget.ShaderStorageBuffer,records.Length*4,records,BufferUsageHint.StaticDraw); GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer,4,sources);
            uint[] mask={1,0,0,0,0};
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer,tiles); GL.BufferData(BufferTarget.ShaderStorageBuffer,mask.Length*4,mask,BufferUsageHint.StaticDraw); GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer,5,tiles);
            GL.UseProgram(consume); GL.Uniform1(GL.GetUniformLocation(consume,"drtStaticMaps"),14); GL.Uniform1(GL.GetUniformLocation(consume,"gGlow"),0); GL.Uniform1(GL.GetUniformLocation(consume,"gNormal"),1);
            float[] calibration=Enumerable.Repeat(1f,64).ToArray(); GL.Uniform2(GL.GetUniformLocation(consume,"drtPlacedCalibration[0]"),32,calibration);
            float[] data=new float[size*size];
            foreach(bool blocked in new[]{false,true}) {
                // Independent point-light rays intersect an opaque wall at x=.75,
                // outside the owning emitter voxel. The second slot mirrors it.
                for(int layer=0;layer<12;layer++) {
                    int face=layer%6;
                    for(int y=0;y<size;y++)for(int x=0;x<size;x++) {
                        float u=((x+.5f)/size-.5f)/.47f,v=((y+.5f)/size-.5f)/.47f;
                        Vector3 ray=face switch{0=>new(1,-v,u),1=>new(-1,-v,-u),2=>new(-u,1,v),3=>new(-u,-1,-v),4=>new(-u,-v,1),_=>new(u,-v,-1)};
                        double hit=blocked&&ray.X>0?.75/ray.X:double.PositiveInfinity;
                        data[y*size+x]=hit>=.1&&hit<=22?(float)(22/21.9-2.2/(21.9*hit)):1;
                    }
                    GL.ActiveTexture(TextureUnit.Texture14); GL.BindTexture(TextureTarget.Texture2DArray,textures[4]);
                    GL.TexSubImage3D(TextureTarget.Texture2DArray,0,0,0,layer,size,size,1,PixelFormat.DepthComponent,PixelType.Float,data);
                }
                if(windTemporalOnly)continue;
                if (staticNoCullOnly) {
                    // Decode real MRT tags at both specular interval endpoints.
                    // Mixed roots/tips must stay wind foliage; static no-cull
                    // must not gain a false full-strength specular highlight.
                    foreach(int pattern in new[]{0,1,2,3,4})
                    foreach(float specular in new[]{0f,.4f,1f}) {
                        GL.ActiveTexture(TextureUnit.Texture2); GL.BindTexture(TextureTarget.Texture2D,textures[5]);
                        GL.TexSubImage2D(TextureTarget.Texture2D,0,0,0,1,1,PixelFormat.Red,PixelType.Float,new[]{specular});
                        GL.UseProgram(fill); GL.Uniform1(GL.GetUniformLocation(fill,"pattern"),pattern);
                        GL.Uniform3(GL.GetUniformLocation(fill,"receiverCentre"),1f,.4f,1f);
                        GL.Uniform3(GL.GetUniformLocation(fill,"authoredNormal"),0f,0f,-1f);
                        GL.Uniform1(GL.GetUniformLocation(fill,"cardAngle"),0f);
                        GL.Uniform1(GL.GetUniformLocation(fill,"haxyFade"),1);
                        GL.Uniform1(GL.GetUniformLocation(fill,"alphaTest"),.42f);
                        GL.BindFramebuffer(FramebufferTarget.Framebuffer,fillFbo); GL.DrawArrays(PrimitiveType.Triangles,0,6);
                        GL.ActiveTexture(TextureUnit.Texture0); GL.BindSampler(0,0); GL.BindTexture(TextureTarget.Texture2D,textures[1]);
                        GL.UseProgram(consume); GL.Uniform1(GL.GetUniformLocation(consume,"inspectMetadata"),1);
                        GL.BindFramebuffer(FramebufferTarget.Framebuffer,outputFbo); GL.DrawArrays(PrimitiveType.Triangles,0,6);
                        GL.ReadPixels(0,0,Size,Size,PixelFormat.Rgba,PixelType.Float,pixels);
                        ++checks;++checkedDraws;
                        bool valid=true;
                        for(int i=0;i<pixels.Length;i+=4)
                            valid &= Math.Abs(pixels[i+3]-specular)<1e-5 && (pattern==2
                                ? pixels[i]==0 && pixels[i+1]==0 && pixels[i+2]==1
                                : pixels[i]+pixels[i+1]==1 && pixels[i+2]==0);
                        if(!valid){++failures;Console.WriteLine($"FAIL no-cull / wind-foliage tag and specular decode: pattern={pattern}, specular={specular}");}
                    }
                    GL.ActiveTexture(TextureUnit.Texture2); GL.BindTexture(TextureTarget.Texture2D,textures[5]);
                    GL.TexSubImage2D(TextureTarget.Texture2D,0,0,0,1,1,PixelFormat.Red,PixelType.Float,new[]{0f});
                    GL.UseProgram(consume); GL.Uniform1(GL.GetUniformLocation(consume,"inspectMetadata"),0);
                    // Compare the actual no-cull MRT classification with Opaque,
                    // using identical geometry, albedo, normals and cached depth.
                    // A blocked source must filter both routes rather than enter
                    // the additive/unshadowed wind-foliage compositor.
                    foreach(float angle in new[]{0f, MathF.PI/4, MathF.PI})
                    foreach(float coverage in new[]{.55f,1f})
                    foreach(var fixture in new[]{
                        (Voxels:new Vector3(.2f,.3f,.4f),Glow:0f,Count:1,Blend:1f,Unknown:false),
                        (Voxels:new Vector3(.8f,.4f,.2f),Glow:0f,Count:1,Blend:1f,Unknown:false),
                        (Voxels:Vector3.Zero,Glow:0f,Count:1,Blend:1f,Unknown:false),
                        (Voxels:new Vector3(.2f,.3f,.4f),Glow:.1f,Count:1,Blend:1f,Unknown:false),
                        (Voxels:new Vector3(.2f,.3f,.4f),Glow:0f,Count:0,Blend:1f,Unknown:false),
                        (Voxels:new Vector3(.2f,.3f,.4f),Glow:0f,Count:1,Blend:0f,Unknown:false),
                        (Voxels:new Vector3(.2f,.3f,.4f),Glow:0f,Count:1,Blend:.5f,Unknown:false),
                        (Voxels:new Vector3(.2f,.3f,.4f),Glow:0f,Count:1,Blend:1f,Unknown:true)}) {
                        Vector3 centre=new(1,.4f,1), normal=new(-MathF.Sin(angle),0,-MathF.Cos(angle));
                        float[][] captures=new float[2][];
                        for(int route=0;route<2;route++) {
                            GL.UseProgram(fill);
                            GL.Uniform1(GL.GetUniformLocation(fill,"pattern"),2);
                            GL.Uniform1(GL.GetUniformLocation(fill,"cardAngle"),angle);
                            GL.Uniform1(GL.GetUniformLocation(fill,"haxyFade"),route==0?1:0);
                            GL.Uniform1(GL.GetUniformLocation(fill,"alphaTest"),route==0?.42f:.001f);
                            GL.Uniform1(GL.GetUniformLocation(fill,"fixtureAlpha"),coverage);
                            GL.Uniform3(GL.GetUniformLocation(fill,"authoredNormal"),normal);
                            GL.Uniform3(GL.GetUniformLocation(fill,"receiverCentre"),centre);
                            GL.BindFramebuffer(FramebufferTarget.Framebuffer,fillFbo);
                            GL.DrawArrays(PrimitiveType.Triangles,0,6);
                            if(route==0) {
                                GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
                                GL.ReadPixels(0,0,Size,Size,PixelFormat.Rgba,PixelType.Float,pixels);
                                ++checks;
                                if(pixels.Where((v,i)=>i%4==3).Any(v=>Math.Abs(v-coverage)>1e-6)) {
                                    ++failures;Console.WriteLine("FAIL static no-cull native alpha preservation");
                                }
                            }
                            GL.ActiveTexture(TextureUnit.Texture0); GL.BindSampler(0,0); GL.BindTexture(TextureTarget.Texture2D,textures[1]);
                            GL.ActiveTexture(TextureUnit.Texture1); GL.BindSampler(1,0); GL.BindTexture(TextureTarget.Texture2D,textures[2]);
                            GL.UseProgram(consume);
                            GL.Uniform1(GL.GetUniformLocation(consume,"cardAngle"),angle);
                            GL.Uniform3(GL.GetUniformLocation(consume,"receiverCentre"),centre);
                            GL.Uniform3(GL.GetUniformLocation(consume,"voxelRgb"),fixture.Voxels);
                            GL.Uniform1(GL.GetUniformLocation(consume,"glowAmount"),fixture.Glow);
                            GL.Uniform1(GL.GetUniformLocation(consume,"drtStaticCount"),fixture.Count);
                            GL.Uniform1(GL.GetUniformLocation(consume,"drtStaticBlend"),fixture.Blend);
                            GL.Uniform1(GL.GetUniformLocation(consume,"unknownNormal"),fixture.Unknown?1:0);
                            GL.BindFramebuffer(FramebufferTarget.Framebuffer,outputFbo);
                            GL.DrawArrays(PrimitiveType.Triangles,0,6);
                            captures[route]=new float[pixels.Length];
                            GL.ReadPixels(0,0,Size,Size,PixelFormat.Rgba,PixelType.Float,captures[route]);
                            ++checkedDraws;
                        }
                        ++checks;
                        if(captures[0].Zip(captures[1]).Any(p=>!float.IsFinite(p.First)||Math.Abs(p.First-p.Second)>3e-5)) {
                            ++failures;
                            Console.WriteLine($"FAIL static no-cull / opaque RGB and filter parity: wall={blocked}, angle={angle}, alpha={coverage}, input={fixture}");
                        }
                        if(blocked && angle==0 && coverage==1 && fixture.Count==1 && fixture.Blend==1 && fixture.Glow==0 && !fixture.Unknown && fixture.Voxels.X==.2f)
                            Console.WriteLine($"Blocked no-cull RGB={captures[0][544]:F6}/{captures[0][545]:F6}/{captures[0][546]:F6}, opaque={captures[1][544]:F6}/{captures[1][545]:F6}/{captures[1][546]:F6}; filters={captures[0][547]}/{captures[1][547]}");
                    }
                    continue;
                }
                foreach(int pattern in new[]{0,1,3,4})
                foreach(float height in new[]{-.4f,.4f})
                foreach(float normalSign in new[]{-1f,1f})
                foreach(var fixture in new[]{
                    (Voxels:new Vector3(.2f,.3f,.4f),Glow:0f,Count:1,Blend:1f,Unknown:false),
                    (Voxels:new Vector3(.8f,.4f,.2f),Glow:0f,Count:1,Blend:1f,Unknown:false),
                    (Voxels:Vector3.Zero,Glow:0f,Count:1,Blend:1f,Unknown:false),
                    (Voxels:Vector3.Zero,Glow:.25f,Count:1,Blend:1f,Unknown:false),
                    (Voxels:new Vector3(.2f,.3f,.4f),Glow:.1f,Count:1,Blend:1f,Unknown:false),
                    (Voxels:new Vector3(.2f,.3f,.4f),Glow:0f,Count:0,Blend:1f,Unknown:false),
                    (Voxels:new Vector3(.2f,.3f,.4f),Glow:0f,Count:1,Blend:0f,Unknown:false),
                    (Voxels:new Vector3(.2f,.3f,.4f),Glow:0f,Count:1,Blend:.5f,Unknown:false),
                    (Voxels:new Vector3(.2f,.3f,.4f),Glow:0f,Count:1,Blend:1f,Unknown:true)}) {
                    Vector3 centre=new(1,height,1);
                    GL.UseProgram(fill); GL.Uniform1(GL.GetUniformLocation(fill,"pattern"),pattern); GL.Uniform3(GL.GetUniformLocation(fill,"receiverCentre"),centre); GL.Uniform3(GL.GetUniformLocation(fill,"authoredNormal"),0f,0f,normalSign);
                    GL.Uniform1(GL.GetUniformLocation(fill,"reverseWinding"),normalSign<0?1:0);
                    GL.BindFramebuffer(FramebufferTarget.Framebuffer,fillFbo); GL.DrawArrays(PrimitiveType.Triangles,0,6);
                    GL.ActiveTexture(TextureUnit.Texture0); GL.BindSampler(0,0); GL.BindTexture(TextureTarget.Texture2D,textures[1]);
                    GL.ActiveTexture(TextureUnit.Texture1); GL.BindSampler(1,0); GL.BindTexture(TextureTarget.Texture2D,textures[2]);
                    GL.UseProgram(consume); GL.Uniform1(GL.GetUniformLocation(consume,"pattern"),pattern); GL.Uniform3(GL.GetUniformLocation(consume,"receiverCentre"),centre);
                    GL.Uniform3(GL.GetUniformLocation(consume,"voxelRgb"),fixture.Voxels);
                    GL.Uniform1(GL.GetUniformLocation(consume,"glowAmount"),fixture.Glow);
                    GL.Uniform1(GL.GetUniformLocation(consume,"drtStaticCount"),fixture.Count);
                    GL.Uniform1(GL.GetUniformLocation(consume,"drtStaticBlend"),fixture.Blend);
                    GL.Uniform1(GL.GetUniformLocation(consume,"unknownNormal"),fixture.Unknown?1:0);
                    GL.BindFramebuffer(FramebufferTarget.Framebuffer,outputFbo); GL.DrawArrays(PrimitiveType.Triangles,0,6);
                    GL.ReadPixels(0,0,Size,Size,PixelFormat.Rgba,PixelType.Float,pixels);
                    ++checkedDraws;
                    int bad=0;
                    for(int i=0;i<pixels.Length;i+=4) {
                        int x=(i/4)%Size,y=(i/4)/Size;
                        Vector3 q=centre+new Vector3(((x+.5f)/Size-.5f)*.2f,((y+.5f)/Size-.5f)*.2f,0);
                        // Closed-form white-source oracle; the maintained solid
                        // compositor below independently supplies head-on energy.
                        float facing=fixture.Unknown?1:VisibleFacing(new Vector3(0,0,normalSign),q);
                        float direct=(1-q.Length/22)*(1/(1+.25f*q.LengthSquared)+.33f)*facing;
                        float weight=fixture.Count==0?0:fixture.Blend*(1-Math.Clamp(fixture.Glow*4,0,1));
                        float gate=Math.Clamp(Math.Max(fixture.Voxels.X,Math.Max(fixture.Voxels.Y,fixture.Voxels.Z))*32,0,1);
                        Vector3 expected=fixture.Voxels*(1-weight*(1-.5f*facing))+new Vector3(.5f*direct*weight*gate+10*fixture.Glow);
                        Vector3 actual=new(pixels[i],pixels[i+1],pixels[i+2]);
                        if(!float.IsFinite(actual.X)||!float.IsFinite(actual.Y)||!float.IsFinite(actual.Z)||
                            (actual-expected).Length>2e-5 || pixels[i+3]!=0) ++bad;
                    }
                    ++checks;
                    if(bad>0) {++failures;Console.WriteLine($"FAIL foliage pattern={pattern}, height={height}, normal={normalSign}, wall={blocked}, input={fixture}: {bad}/{Size*Size} bad pixels");}
                    if (!blocked && normalSign < 0 && fixture.Count == 1 && fixture.Blend == 1 && fixture.Glow == 0 && !fixture.Unknown && fixture.Voxels.LengthSquared > 0) {
                        float[] foliagePixels=(float[])pixels.Clone();
                        // Neutral orientation isolates the accepted foliage
                        // energy. Solid falloff/voxel blending were manually
                        // tuned separately and no longer supply a parity oracle.
                        GL.Uniform1(GL.GetUniformLocation(consume,"unknownNormal"),1);
                        GL.DrawArrays(PrimitiveType.Triangles,0,6);
                        GL.ReadPixels(0,0,Size,Size,PixelFormat.Rgba,PixelType.Float,pixels);
                        ++checkedDraws;
                        GL.Uniform1(GL.GetUniformLocation(consume,"unknownNormal"),0);
                        int parityBad=0;
                        for(int i=0;i<pixels.Length;i+=4) {
                            Vector3 solid=new(pixels[i],pixels[i+1],pixels[i+2]);
                            Vector3 foliage=new(foliagePixels[i],foliagePixels[i+1],foliagePixels[i+2]);
                            int x=(i/4)%Size,y=(i/4)/Size;
                            Vector3 q=centre+new Vector3(((x+.5f)/Size-.5f)*.2f,((y+.5f)/Size-.5f)*.2f,0);
                            float facing=VisibleFacing(-Vector3.UnitZ,q);
                            if((solid*facing-foliage).Length>2e-5)++parityBad;
                        }
                        ++checks;
                        if(parityBad>0) {++failures;Console.WriteLine($"FAIL calibrated head-on energy times foliage facing: {parityBad}/{Size*Size} pixels");}
                    }
                }
            }
            if(staticNoCullOnly) {
                if(GL.GetError()!=ErrorCode.NoError) throw new Exception("Static no-cull fixture GL error");
                Console.WriteLine($"Static no-cull: {checks-failures}/{checks} checks passed; {checkedDraws} lighting draws ({checkedDraws*Size*Size} pixels)");
                if(failures>0) throw new Exception($"{failures} static no-cull receiver regressions");
                return;
            }
            // Native shade:false berry cards carry +Y shading normals even on
            // vertical faces. Bend through a zero Y component without changing
            // that authored normal; both mesh windings must keep their physical
            // orientation. Also cover missing normals and camera grazing.
            if(windTemporalOnly) {
                GL.UseProgram(consume);GL.Uniform1(GL.GetUniformLocation(consume,"unknownNormal"),0);
                GL.Uniform1(GL.GetUniformLocation(consume,"inspectMetadata"),0);
                GL.Uniform1(GL.GetUniformLocation(consume,"solidReference"),0);
                GL.Uniform1(GL.GetUniformLocation(consume,"drtStaticCount"),1);
                GL.Uniform1(GL.GetUniformLocation(consume,"drtStaticBlend"),1f);
                GL.Uniform1(GL.GetUniformLocation(consume,"glowAmount"),0f);
                GL.Uniform3(GL.GetUniformLocation(consume,"voxelRgb"),.2f,.2f,.2f);
                GL.Uniform3(GL.GetUniformLocation(consume,"rawRgb"),1f,1f,1f);
                foreach(Vector3 authored in new[]{Vector3.UnitY,Vector3.Zero})
                foreach(bool reversed in new[]{false,true}) {
                float darkest=float.MaxValue,brightest=0;
                foreach(int pattern in new[]{0,1,3,4,5})
                foreach(bool grazing in new[]{false,true})
                foreach(float degrees in new[]{-.5f,-.1f,.1f,.5f}) {
                    float angle=grazing?(90+degrees)*MathF.PI/180:0;
                    float pitch=grazing?0:degrees*MathF.PI/180;
                    Vector3 centre=grazing?new(5.5f,0,0):new(0,0,5.5f);
                    GL.UseProgram(fill);GL.Uniform1(GL.GetUniformLocation(fill,"pattern"),pattern);
                    GL.Uniform1(GL.GetUniformLocation(fill,"projectionWindFixture"),1);
                    GL.Uniform1(GL.GetUniformLocation(fill,"haxyFade"),1);
                    GL.Uniform3(GL.GetUniformLocation(fill,"authoredNormal"),authored);
                    GL.Uniform1(GL.GetUniformLocation(fill,"reverseWinding"),reversed?1:0);
                    GL.Uniform1(GL.GetUniformLocation(fill,"cardPitch"),pitch);
                    GL.Uniform1(GL.GetUniformLocation(fill,"cardAngle"),angle);
                    GL.Uniform3(GL.GetUniformLocation(fill,"receiverCentre"),centre);
                    GL.BindFramebuffer(FramebufferTarget.Framebuffer,fillFbo);GL.DrawArrays(PrimitiveType.Triangles,0,6);
                    GL.ActiveTexture(TextureUnit.Texture0);GL.BindTexture(TextureTarget.Texture2D,textures[1]);
                    GL.ActiveTexture(TextureUnit.Texture1);GL.BindTexture(TextureTarget.Texture2D,textures[2]);
                    GL.UseProgram(consume);GL.Uniform1(GL.GetUniformLocation(consume,"cardAngle"),angle);
                    GL.Uniform1(GL.GetUniformLocation(consume,"cardPitch"),pitch);
                    GL.Uniform1(GL.GetUniformLocation(consume,"projectionWindFixture"),1);
                    GL.Uniform3(GL.GetUniformLocation(consume,"receiverCentre"),centre);
                    GL.BindFramebuffer(FramebufferTarget.Framebuffer,outputFbo);GL.DrawArrays(PrimitiveType.Triangles,0,6);
                    GL.ReadPixels(0,0,Size,Size,PixelFormat.Rgba,PixelType.Float,pixels);
                    ++checks;++checkedDraws;
                    int bad=0;
                    for(int i=0;i<pixels.Length;i+=4) {
                        int x=i/4%Size,y=i/4/Size;
                        float u=((x+.5f)/Size-.5f)*.2f,v=((y+.5f)/Size-.5f)*.2f;
                        if(MathF.Cos(angle)<0)u=-u;
                        Vector3 q=centre+new Vector3(u*MathF.Cos(angle),v,-u*MathF.Sin(angle));
                        q.Y=v*MathF.Cos(pitch)+u*MathF.Sin(angle)*MathF.Sin(pitch);
                        q.Z=centre.Z+v*MathF.Sin(pitch)-u*MathF.Sin(angle)*MathF.Cos(pitch);
                        Vector3 normal=new(MathF.Sin(angle),-MathF.Cos(angle)*MathF.Sin(pitch),MathF.Cos(angle)*MathF.Cos(pitch));
                        if(reversed)normal=-normal;
                        float facing=VisibleFacing(normal,q);
                        float direct=(1-q.Length/22)*(1/(1+.25f*q.LengthSquared)+.33f);
                        float expected=(.1f+.5f*direct)*facing;
                        if(!float.IsFinite(pixels[i])||Math.Abs(pixels[i]-expected)>3e-5||pixels[i+3]!=0)++bad;
                    }
                    darkest=Math.Min(darkest,pixels[544]);brightest=Math.Max(brightest,pixels[544]);
                    if(bad>0) {++failures;Console.WriteLine($"FAIL swaying foliage: authored={authored}, reversed={reversed}, mode={pattern}, grazing={grazing}, bend={degrees}, RGB={pixels[544]:F6}, bad pixels={bad}");}
                }
                ++checks;
                if(brightest/darkest>1.002f){++failures;Console.WriteLine("FAIL sub-degree wind continuity");}
                Console.WriteLine($"Wind sweep authored={authored}, reversed={reversed}: brightest/darkest={brightest/darkest:F6}");
                }
                Console.WriteLine($"Wind temporal: {checks-failures}/{checks} passed; {checkedDraws} lighting draws");
                if(failures>0)throw new Exception($"{failures} wind temporal/normal orientation regressions");
                return;
            }
            // Colored/source-transition tests reuse both maintained compositors,
            // with actual native HSV conversion and the live-table calibrator.
            Array.Fill(data,1f);
            GL.ActiveTexture(TextureUnit.Texture14); GL.BindTexture(TextureTarget.Texture2DArray,textures[4]);
            for(int layer=0;layer<12;layer++) GL.TexSubImage3D(TextureTarget.Texture2DArray,0,0,0,layer,size,size,1,PixelFormat.DepthComponent,PixelType.Float,data);
            float[] table=Enumerable.Range(0,32).Select(i=>i/31f).ToArray();
            SurfaceLightBindings.Calibrate(table,calibration);
            GL.UseProgram(consume); GL.Uniform2(GL.GetUniformLocation(consume,"drtPlacedCalibration[0]"),32,calibration);
            records=new float[129*StaticLightGpuRecord.FloatCount];
            void Record(int index,Vector3 rgb,int level=18,float fade=1,bool pair=false) {
                int o=index*16;
                Array.Clear(records,o,16);
                records[o+3]=Math.Min(1.4f*level,22f);
                records[o+4]=rgb.X; records[o+5]=rgb.Y; records[o+6]=rgb.Z;
                records[o+8]=fade; records[o+10]=1; records[o+11]=pair?1:0; records[o+15]=level;
            }
            void Upload(int count,params uint[] bits) {
                GL.BindBuffer(BufferTarget.ShaderStorageBuffer,sources); GL.BufferData(BufferTarget.ShaderStorageBuffer,records.Length*4,records,BufferUsageHint.StaticDraw);
                Array.Clear(mask); Array.Copy(bits,mask,bits.Length);
                GL.BindBuffer(BufferTarget.ShaderStorageBuffer,tiles); GL.BufferData(BufferTarget.ShaderStorageBuffer,mask.Length*4,mask,BufferUsageHint.StaticDraw);
                GL.UseProgram(consume); GL.Uniform1(GL.GetUniformLocation(consume,"drtStaticCount"),count);
            }
            void Require(bool ok,string label) {
                ++checks;
                if(!ok) {++failures;Console.WriteLine("FAIL "+label);}
            }
            float[] Render(Vector3 centre,Vector3 voxel,Vector3 raw,Vector3 normal,float blend=1,bool solid=false,float angle=0) {
                GL.UseProgram(fill); GL.Uniform1(GL.GetUniformLocation(fill,"pattern"),0);
                GL.Uniform1(GL.GetUniformLocation(fill,"cardAngle"),angle);
                // Authored normals in these older fixtures specify the actual
                // mesh face. Keep its winding consistent with that orientation.
                GL.Uniform1(GL.GetUniformLocation(fill,"reverseWinding"),Vector3.Dot(normal,new Vector3(MathF.Sin(angle),0,MathF.Cos(angle)))<0?1:0);
                GL.Uniform3(GL.GetUniformLocation(fill,"receiverCentre"),centre); GL.Uniform3(GL.GetUniformLocation(fill,"authoredNormal"),normal);
                GL.BindFramebuffer(FramebufferTarget.Framebuffer,fillFbo); GL.DrawArrays(PrimitiveType.Triangles,0,6);
                GL.UseProgram(consume); GL.Uniform1(GL.GetUniformLocation(consume,"pattern"),0);
                GL.Uniform1(GL.GetUniformLocation(consume,"cardAngle"),angle);
                GL.Uniform3(GL.GetUniformLocation(consume,"receiverCentre"),centre); GL.Uniform3(GL.GetUniformLocation(consume,"voxelRgb"),voxel);
                GL.Uniform3(GL.GetUniformLocation(consume,"rawRgb"),raw);
                GL.Uniform1(GL.GetUniformLocation(consume,"drtStaticBlend"),blend); GL.Uniform1(GL.GetUniformLocation(consume,"glowAmount"),0f);
                GL.Uniform1(GL.GetUniformLocation(consume,"unknownNormal"),normal.LengthSquared<1e-6?1:0);
                GL.Uniform1(GL.GetUniformLocation(consume,"solidReference"),solid?1:0);
                GL.BindFramebuffer(FramebufferTarget.Framebuffer,outputFbo); GL.DrawArrays(PrimitiveType.Triangles,0,6);
                float[] result=new float[pixels.Length]; GL.ReadPixels(0,0,Size,Size,PixelFormat.Rgba,PixelType.Float,result);
                ++checkedDraws;
                Require(result.Where((value,i)=>i%4!=3).All(float.IsFinite) &&
                    (solid||result.Where((value,i)=>i%4==3).All(value=>value==0)),"finite RGB and zero foliage filter calls");
                return result;
            }
            bool Same(float[] a,float[] b) => a.Where((value,i)=>i%4!=3).Zip(b.Where((value,i)=>i%4!=3)).All(pair=>Math.Abs(pair.First-pair.Second)<3e-5);
            Vector3 Position(Vector3 centre,int pixel,float angle=0) {
                int x=pixel%Size,y=pixel/Size;
                float u=((x+.5f)/Size-.5f)*.2f,v=((y+.5f)/Size-.5f)*.2f;
                return centre+new Vector3(u*MathF.Cos(angle),v,-u*MathF.Sin(angle));
            }
            float Facing(Vector3 centre,int pixel,Vector3 normal,float angle=0) => VisibleFacing(normal,Position(centre,pixel,angle));
            bool CalibratedFacing(float[] foliage,float[] neutral,Vector3 centre,Vector3 normal,float angle=0) {
                for(int i=0;i<foliage.Length;i+=4) {
                    float facing=Facing(centre,i/4,normal,angle);
                    for(int c=0;c<3;c++) if(Math.Abs(foliage[i+c]-neutral[i+c]*facing)>3e-5) return false;
                }
                return true;
            }
            Vector3 raw=new(.18f,.55f,.09f), centreRgb=new(0,.4f,4), vox=new(.02f,.2f,.01f);
            foreach(var lamp in new[]{(Hue:2,Sat:5,Level:14),(Hue:40,Sat:5,Level:18),(Hue:12,Sat:2,Level:20)}) {
                StaticLightGpuRecord.Rgb(lamp.Hue,lamp.Sat,out float r,out float g,out float b);
                Vector3 rgb=new(r,g,b); Record(0,rgb,lamp.Level); Upload(1,1u);
                foreach(float distance in new[]{1f,4f,5.5f,10f}) {
                    Vector3 centre=new(0,.4f,distance), voxel=rgb*.25f;
                    float[] foliage=Render(centre,voxel,raw,-Vector3.UnitZ);
                    float[] neutral=Render(centre,voxel,raw,Vector3.Zero);
                    Require(CalibratedFacing(foliage,neutral,centre,-Vector3.UnitZ),$"native HSV {lamp.Hue}/{lamp.Sat}, level {lamp.Level}, distance {distance}: calibrated energy and facing");
                }
            }
            Vector3 warm=new(1,.24f,.04f),cool=new(.08f,.32f,1);
            Record(0,warm); Upload(1,1u);
            float[] warmFull=Render(centreRgb,vox,raw,-Vector3.UnitZ);
            foreach(Vector3 normal in new[]{-Vector3.UnitZ,Vector3.UnitZ,Vector3.Zero}) {
                float[] lit=Render(centreRgb,vox,raw,normal);
                // Subtract the known voxel base, then remove texture albedo.
                // All placed channels must retain the captured source chroma,
                // even though the voxel hue is green and the lamp is orange.
                bool chroma=true;
                for(int i=0;i<lit.Length;i+=4) {
                    float baseFacing=Facing(centreRgb,i/4,normal);
                    Vector3 added=new(lit[i]/raw.X-.5f*vox.X*baseFacing,lit[i+1]/raw.Y-.5f*vox.Y*baseFacing,lit[i+2]/raw.Z-.5f*vox.Z*baseFacing);
                    chroma &= added.X>0 && (added/added.X-warm).Length<3e-5;
                }
                Require(chroma,"captured lamp hue survives front/back/unknown normals and a differently colored voxel base");
            }
            float[] unready=Render(centreRgb,vox,raw,-Vector3.UnitZ,blend:0);
            float[] halfReady=Render(centreRgb,vox,raw,-Vector3.UnitZ,blend:.5f);
            Require(halfReady.Where((value,i)=>i%4!=3).Zip(unready.Where((value,i)=>i%4!=3).Zip(warmFull.Where((value,i)=>i%4!=3))).All(p=>Math.Abs(p.First-.5f*(p.Second.First+p.Second.Second))<3e-5),"cache readiness is a linear RGB handoff");
            Record(0,warm,fade:.3f,pair:true); Record(128,warm,fade:.7f,pair:true); Upload(129,1u,0u,0u,0u,1u);
            Require(Same(warmFull,Render(centreRgb,vox,raw,-Vector3.UnitZ)),"replacement pair and staging bit 128 preserve full RGB and voxel base");
            Record(0,cool); Upload(1,1u);
            float[] coolFull=Render(centreRgb,vox,raw,-Vector3.UnitZ);
            Require(!Same(warmFull,coolFull),"updated captured source RGB changes grass color without rebaking depth");
            Record(0,warm); Record(1,cool); Upload(2,3u);
            float[] overlap=Render(centreRgb,vox,raw,-Vector3.UnitZ);
            bool summed=true;
            for(int i=0;i<overlap.Length;i+=4) for(int c=0;c<3;c++) {
                float baseLight=.5f*vox[c]*raw[c]*Facing(centreRgb,i/4,-Vector3.UnitZ);
                // Same position/level, but warm has greater luminance than cool:
                // the accepted two-light harmonic weights are 1 and 1/2.
                summed &= Math.Abs(overlap[i+c]-(warmFull[i+c]+.5f*(coolFull[i+c]-baseLight)))<3e-5;
            }
            Require(summed,"overlapping colored lamps use harmonic RGB without duplicating voxel light");
            Record(0,warm); Upload(1,1u);
            float last=-1;
            foreach(float brightness in new[]{0f,.001f,.01f,.03125f,.2f}) {
                float[] lit=Render(centreRgb,new Vector3(brightness),Vector3.One,-Vector3.UnitZ);
                Require(lit[544]>=last,"voxel darkness gate brightens continuously"); last=lit[544];
                if(brightness==0) Require(lit.Where((v,i)=>i%4!=3).All(v=>v==0),"zero voxel light remains dark with bright nearby cached sources");
            }
            foreach(var absent in new[]{(Count:0,Bit:1u,Fade:1f,Level:18),(Count:1,Bit:0u,Fade:1f,Level:18),(Count:1,Bit:1u,Fade:0f,Level:18),(Count:1,Bit:1u,Fade:1f,Level:0)}) {
                Record(0,warm,absent.Level,absent.Fade); Upload(absent.Count,absent.Bit);
                Require(Same(unready,Render(centreRgb,vox,raw,-Vector3.UnitZ)),"missing/empty/faded/nonemitting records preserve complete voxel RGB");
            }
            Record(0,warm); Upload(1,1u);
            float[] centreLight=Render(Vector3.Zero,vox,raw,Vector3.Zero);
            Require(centreLight.Where((v,i)=>i%4!=3).All(v=>v>=0 && v<1),"owning emitter voxel stays finite and bounded at the source centre");
            // Legacy tuning: turning the card away leaves about 20% of
            // its complete placed-lit RGB, including a dominant voxel base.
            Record(0,warm); Upload(1,1u);
            foreach(float distance in new[]{1f,4f,5.5f,10f,17f})
            foreach(Vector3 voxel in new[]{new Vector3(.003f),vox,new Vector3(.8f)}) {
                Vector3 centre=new(0,0,distance);
                float[] front=Render(centre,voxel,raw,-Vector3.UnitZ);
                float[] back=Render(centre,voxel,raw,Vector3.UnitZ);
                Require(Same(front,back),$"distance {distance}, voxel {voxel}: reversing mesh winding keeps the same camera-visible face");
                if(distance==5.5f && voxel==vox) {
                    Vector3 luminance=new(.2126f,.7152f,.0722f);
                    float frontBrightness=Vector3.Dot(new Vector3(front[544],front[545],front[546]),luminance);
                    float backBrightness=Vector3.Dot(new Vector3(back[544],back[545],back[546]),luminance);
                    Console.WriteLine($"Winding invariance at 5.5 blocks: first={frontBrightness:F6}, reversed={backBrightness:F6}, ratio={backBrightness/frontBrightness:F6}; actual camera-side 4:1 is checked in AllPassPlacedOcclusionProbe");
                }
            }
            // Tilt both geometry and its authored orientation through grazing
            // incidence. The actual G-buffer/derivative path must remain smooth
            // and keep the 25% floor through the edge-on tolerance band.
            foreach(float degrees in new[]{0f,30f,60f,89f,90f,91f,120f,150f,180f}) {
                float angle=degrees*MathF.PI/180;
                Vector3 normal=new(-MathF.Sin(angle),0,-MathF.Cos(angle)),centre=new(0,0,5.5f);
                float[] tilted=Render(centre,vox,raw,normal,angle:angle);
                float[] reference=Render(centre,vox,raw,Vector3.Zero,angle:angle);
                Require(CalibratedFacing(tilted,reference,centre,normal,angle),$"bent card at {degrees} degrees follows its geometric normal continuously");
            }
            if(GL.GetError()!=ErrorCode.NoError)throw new Exception("Placed foliage fixture GL error");
            Console.WriteLine($"Placed foliage: {checks-failures}/{checks} checks passed; {checkedDraws} lighting draws ({checkedDraws*Size*Size} pixels)");
            if(failures>0)throw new Exception($"{failures} placed foliage receiver regressions");
        }
        finally {
            GL.DeleteProgram(fill);GL.DeleteProgram(consume);GL.DeleteFramebuffer(fillFbo);GL.DeleteFramebuffer(outputFbo);GL.DeleteVertexArray(vao);
            GL.DeleteBuffer(sources);GL.DeleteBuffer(tiles);GL.DeleteTextures(textures.Length,textures);
        }
    }
}
