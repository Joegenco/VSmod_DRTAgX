using System;
using System.IO;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;

// Real draw fixtures execute maintained receiver preparation, PCF, G-buffer
// packing and material shade selection. Numerical checks leave visuals to humans.
internal static class SunFoliageProbe
{
    private const int Size = 16, MapSize = 512;

    internal static void Run(string deferredPath, string assetsOverride = null)
    {
        using var state = new ShadowGlState();
        string assets = Path.GetFullPath(assetsOverride ?? Path.Combine(Path.GetDirectoryName(deferredPath)!, "../.."));
        string opaque = File.ReadAllText(Path.Combine(assets, "game/shaders/chunkopaque.fsh"));
        string fog = File.ReadAllText(Path.Combine(assets, "game/shaders/fogandlight.fsh"));
        string sampling = File.ReadAllText(Path.Combine(assets, "drtagx/shaders/lighting/drtagx_sun_shadow_sampling.fsh"));
        string balance = File.ReadAllText(Path.Combine(assets, "drtagx/shaders/lighting/drtagx_light_balance.ash"));
        string directional = File.ReadAllText(Path.Combine(assets, "drtagx/shaders/deferred/drtagx_deferred_directional.fsh"));
        string deferred = File.ReadAllText(Path.Combine(assets, "sheydermod/shaders/deferredlighting.fsh"));
        string main = ProbeShader.Function(opaque, "void main()");
        string preparation = main[(main.IndexOf('{') + 1)..main.IndexOf("    drtDiscardTerrainBoundary", StringComparison.Ordinal)];
        // This fixture isolates sun planes. Placed-light preparation belongs
        // to the all-pass fixture and is absent from this shader's declarations.
        preparation = preparation.Replace("drtCaptureTerrainPlacedPlane(drtPlacedRestPos);", "", StringComparison.Ordinal);
        // The before shader declares windMode later; keep the fixture compatible
        // so it can demonstrate the actual old receiver/material failures.
        if (!preparation.Contains("int windMode", StringComparison.Ordinal)) preparation += "int windMode=renderFlags & WindModeBitMask;\n";
        int callerStart = main.IndexOf("    drtSunReceiverBiasScale =", StringComparison.Ordinal);
        string forwardCaller = main[callerStart..main.IndexOf("    outColor = applyFogAndShadowFromBrightness", callerStart, StringComparison.Ordinal)];
        int shadeStart = deferred.IndexOf("    vec2 foliageLighting", StringComparison.Ordinal);
        if (shadeStart < 0) shadeStart = deferred.IndexOf("    float baseNb", StringComparison.Ordinal);
        string shade = deferred[shadeStart..deferred.IndexOf("    float nb = baseNb", shadeStart, StringComparison.Ordinal)];
        string wind = shade.Contains("foliageLighting", StringComparison.Ordinal) ? "foliageLighting.y" : "nrm4.w";
        // Execute the maintained deferred card classification when present;
        // before snapshots remain runnable for an actual failure comparison.
        string foliageClassification = string.Join("\n", Array.FindAll(deferred.Split('\n'),
            line => line.TrimStart().StartsWith("drtSunFoliageReceiver =", StringComparison.Ordinal)));

        const string vertex = """
            #version 430 core
            uniform vec2 slope;
            uniform float grazingIncidence = 1.0, geometryScale = 1.0;
            uniform mat4 toShadowMapSpaceMatrixFar, toShadowMapSpaceMatrixNear;
            out vec4 worldPos, shadowCoordsFar, shadowCoordsNear;
            void main() {
                vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);
                gl_Position=vec4(p*2.-1.,0,1);
                vec2 uv=.46+p*.08;
                worldPos=vec4(uv,.5+dot(slope,uv-.5),1);
                // Rotate a finite card through the sun's grazing angle while
                // it remains visible to the camera; no infinite-plane blocker.
                if (grazingIncidence < .5) {
                    worldPos.x=.5+(uv.x-.5)*grazingIncidence;
                    worldPos.z=uv.x;
                }
                worldPos.xyz=.5+(worldPos.xyz-.5)*geometryScale;
                shadowCoordsFar=toShadowMapSpaceMatrixFar*worldPos; shadowCoordsNear=toShadowMapSpaceMatrixNear*worldPos;
            }
            """;
        const string declarations = """
            in vec4 worldPos, shadowCoordsFar, shadowCoordsNear;
            uniform sampler2DShadow shadowMapFar, shadowMapNear;
            uniform sampler2D specularTex, gNormalInput;
            uniform mat4 modelViewMatrix, invModelViewMatrix, toShadowMapSpaceMatrixFar, toShadowMapSpaceMatrixNear;
            uniform vec4 drtSunGridFrame, drtAtmosphereCamera;
            uniform vec3 drtSunGridCameraPhase, lightPosition;
            uniform float nb, tl_isWind;
            uniform vec2 slope;
            uniform int cascade, renderFlags;
            const float shadowIntensity=1, shadowRangeFar=1000, shadowRangeNear=1000;
            const int haxyFade=1, WindModeBitMask=0x1e000000, WindModeLeavesMask=0x06000000;
            // This fixture uses one wind mode per primitive, including static
            // no-cull cards. The crossed-root/tip case is in PlacedFoliageProbe.
            #define drtWindPresence ((renderFlags & WindModeBitMask)!=0?1.:0.)
            const float specularStrength=0, alphaTest=.42, fogAmount=0, glowLevel=0, voxSunLight=1;
            // No placed sources in this sun-only scene; keep current MRT fill
            // declarations without introducing unrelated placed-light work.
            const int drtTerrainPlacedCount=0;
            const float drtTerrainPlacedVisibility=1;
            const vec3 drtPlacedRestPos=vec3(0), drtTerrainPlacedRadiance=vec3(0);
            void drtPrepareTerrainPlacedVisibility(vec3 p,bool wind,float glow) {}
            const vec2 uv=vec2(0);
            vec3 normal=vec3(0,1,0), drtVoxelLight=vec3(.2), drtEmissionLight=vec3(0);
            vec4 gnormal=vec4(0,1,0,0);
            vec3 drtTerrainSunPlaneNormal=vec3(0,1,0), drtForwardSunPlaneNormal=vec3(0);
            vec3 drtForwardSunGridOffset=vec3(0), drtSunGridReceiverOffset=vec3(0), drtSunReceiverPlaneNormal=vec3(0);
            float drtSunReceiverBiasScale=1, sm_sunShadowBright=1, df_sunShadow=1;
            const int drtContactShadowsEnabled=0;
            float drtFoliageContact(vec3 p,vec3 n){return 0.;}
            """;
        int output = GL.GenTexture(), normals = GL.GenTexture(), depth = GL.GenTexture();
        int outputFbo = GL.GenFramebuffer(), normalFbo = GL.GenFramebuffer(), vao = GL.GenVertexArray();
        int previous = GL.GetInteger(GetPName.CurrentProgram);
        int failures = 0, checks = 0;
        void Check(bool valid, string name) {
            ++checks;
            if (!valid) { ++failures; Console.WriteLine("FAIL " + name); }
        }
        try
        {
            GL.ActiveTexture(TextureUnit.Texture14); GL.BindSampler(14, 0);
            GL.BindTexture(TextureTarget.Texture2D, output);
            GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.Rgba32f,Size,Size,0,PixelFormat.Rgba,PixelType.Float,IntPtr.Zero);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer,outputFbo);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,FramebufferAttachment.ColorAttachment0,TextureTarget.Texture2D,output,0);
            GL.DrawBuffer(DrawBufferMode.ColorAttachment0); GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
            GL.BindTexture(TextureTarget.Texture2D, normals);
            GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.Rgba16f,Size,Size,0,PixelFormat.Rgba,PixelType.Float,IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)TextureMagFilter.Nearest);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer,normalFbo);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,FramebufferAttachment.ColorAttachment2,TextureTarget.Texture2D,normals,0);
            GL.DrawBuffers(4,new[]{DrawBuffersEnum.None,DrawBuffersEnum.None,DrawBuffersEnum.ColorAttachment2,DrawBuffersEnum.None});
            GL.ReadBuffer(ReadBufferMode.ColorAttachment2);
            GL.ActiveTexture(TextureUnit.Texture0); GL.BindSampler(0,0); GL.BindTexture(TextureTarget.Texture2D,depth);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureCompareMode,(int)TextureCompareMode.CompareRefToTexture);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureCompareFunc,(int)DepthFunction.Lequal);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureWrapS,(int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureWrapT,(int)TextureWrapMode.ClampToEdge);
            GL.ActiveTexture(TextureUnit.Texture1); GL.BindSampler(1,0); GL.BindTexture(TextureTarget.Texture2D,normals);
            GL.BindVertexArray(vao); GL.Viewport(0,0,Size,Size);
            GL.Disable(EnableCap.DepthTest); GL.Disable(EnableCap.Blend); GL.Disable(EnableCap.ScissorTest); GL.ColorMask(true,true,true,true);
            float[] data = new float[MapSize*MapSize], pixels = new float[Size*Size*4];
            float[] identity = {1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1};

            void Setup(int program,float sx,float sy,int grid,int cascade,float shade,float windClass,int windMode=1) {
                GL.UseProgram(program);
                void I(string n,int v){int l=GL.GetUniformLocation(program,n);if(l>=0)GL.Uniform1(l,v);}
                void F(string n,float v){int l=GL.GetUniformLocation(program,n);if(l>=0)GL.Uniform1(l,v);}
                I("shadowMapFar",0); I("shadowMapNear",0); I("gNormalInput",1); I("specularTex",1); I("cascade",cascade);
                I("renderFlags",windMode<<25);
                F("nb",shade); F("tl_isWind",windClass);
                GL.Uniform2(GL.GetUniformLocation(program,"slope"),sx,sy);
                GL.Uniform1(GL.GetUniformLocation(program,"grazingIncidence"),1f);
                GL.Uniform1(GL.GetUniformLocation(program,"geometryScale"),1f);
                foreach(string n in new[]{"modelViewMatrix","invModelViewMatrix","toShadowMapSpaceMatrixFar","toShadowMapSpaceMatrixNear"}) {
                    int l=GL.GetUniformLocation(program,n); if(l>=0)GL.UniformMatrix4(l,1,false,identity);
                }
                int frame=GL.GetUniformLocation(program,"drtSunGridFrame"); if(frame>=0)GL.Uniform4(frame,0f,0f,0f,(float)grid);
                int light=GL.GetUniformLocation(program,"lightPosition"); if(light>=0)GL.Uniform3(light,0f,1f,0f);
            }
            void Plane(float sx,float sy,float gap) {
                for(int y=0;y<MapSize;++y)for(int x=0;x<MapSize;++x)
                    data[y*MapSize+x]=.5f+sx*((x+.5f)/MapSize-.5f)+sy*((y+.5f)/MapSize-.5f)-gap;
                GL.ActiveTexture(TextureUnit.Texture0); GL.BindTexture(TextureTarget.Texture2D,depth);
                GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.DepthComponent24,MapSize,MapSize,0,PixelFormat.DepthComponent,PixelType.Float,data);
            }
            void Draw(int fbo,bool read) {
                GL.BindFramebuffer(FramebufferTarget.Framebuffer,fbo); GL.ClearColor(0,0,0,0); GL.Clear(ClearBufferMask.ColorBufferBit);
                GL.DrawArrays(PrimitiveType.Triangles,0,3);
                if(read) GL.ReadPixels(0,0,Size,Size,PixelFormat.Rgba,PixelType.Float,pixels);
                if(GL.GetError()!=ErrorCode.NoError)throw new Exception("Foliage receiver fixture GL error");
            }
            bool All(int channel,float value,float tolerance) {
                for(int y=0;y<Size;++y)for(int x=0;x<Size;++x) {
                    if(x%3==0)continue; // Deliberately discarded cutout/helper lanes.
                    float v=pixels[(y*Size+x)*4+channel];
                    if(!float.IsFinite(v)||Math.Abs(v-value)>tolerance)return false;
                }
                return true;
            }

            foreach(int quality in new[]{1,2})
            {
                string header="#version 430 core\n#define SHADOWQUALITY "+quality+"\n#define SSAOLEVEL 1\n"+declarations+balance+sampling;
                // Copy the production caller and receiver filter, including its
                // preparation before discard. Do not substitute a fixture filter.
                string forwardSource=header+"\nlayout(location=0)out vec4 outColor;\n"+
                    ProbeShader.Function(fog,"void drtPrepareSunGrid(")+ProbeShader.Function(fog,"float getBrightnessFromShadowMap(")+
                    "\nvoid main(){"+preparation+"\nif(int(gl_FragCoord.x)%3==0)discard;"+
                    forwardCaller+"\noutColor=vec4(b,dot(vec3(-slope,1),drtForwardSunGridOffset),0,1);}";
                // Cascade weights are vertex varyings in forward mode. Change
                // their W only; leave comparison positions and geometry intact.
                string weightedVertex=vertex.Replace("shadowCoordsFar=toShadowMapSpaceMatrixFar*worldPos; shadowCoordsNear=toShadowMapSpaceMatrixNear*worldPos;",
                    "shadowCoordsFar=toShadowMapSpaceMatrixFar*worldPos; shadowCoordsNear=toShadowMapSpaceMatrixNear*worldPos; shadowCoordsFar.w="+(quality==1?"1.;":"0.;"));
                int forward=ProbeShader.Program((ShaderType.VertexShader,weightedVertex),(ShaderType.FragmentShader,forwardSource));
                int fill=ProbeShader.Program((ShaderType.VertexShader,vertex),(ShaderType.FragmentShader,
                    header+"\nlayout(location=0)out vec4 outColor;layout(location=1)out vec4 outGlow;layout(location=2)out vec4 outGNormal;layout(location=3)out vec4 outGPosition;\n"+
                    ProbeShader.Function(opaque,"void sm_deferredFill(")+"\nvoid main(){"+preparation+
                    "\nif(int(gl_FragCoord.x)%3==0)discard;sm_deferredFill(vec4(1),vec3(1));}"));
                int consume=ProbeShader.Program((ShaderType.VertexShader,vertex),(ShaderType.FragmentShader,
                    header+"\nlayout(location=0)out vec4 outColor;\n"+
                    ProbeShader.Function(fog,"float getBrightnessFromNormal(")+
                    ProbeShader.Function(directional,"float deferredShadowBrightness(")+
                    ProbeShader.Function(directional,"float drtDeferredDirectionalBrightness(")+
                    "\nvoid main(){if(int(gl_FragCoord.x)%3==0)discard;vec4 nrm4=texelFetch(gNormalInput,ivec2(gl_FragCoord.xy),0);normal=normalize(nrm4.xyz);float grassTag=renderFlags!=0 && renderFlags!=WindModeLeavesMask?1.:0.;float staticNoCullTag=renderFlags==0?1.:0.;float foliageTag=1.-grassTag-staticNoCullTag,noCullTag=1.,intensity=.34;\n"+
                    shade+"\n"+foliageClassification+"\nfloat b=drtDeferredDirectionalBrightness(worldPos,0.,grassTag,0.,1.,worldPos.xyz,normal,normal);"+
                    "outColor=vec4(b,baseNb,"+wind+",nrm4.w>0.?1.:0.);}"));
                try
                {
                    foreach(var slope in new[]{(.01f,.02f),(.12f,-.07f),(.4f,-.3f),(-.7f,.2f)})
                    foreach(int grid in new[]{0,1})
                    foreach(float gap in new[]{0f,.04f})
                    foreach(int windMode in new[]{1,3,0})
                    {
                        Plane(slope.Item1,slope.Item2,gap);
                        float expected=gap==0?1f:.35f;
                        Setup(forward,slope.Item1,slope.Item2,grid,quality,1,1,windMode); Draw(outputFbo,true);
                        Check(All(0,expected,.002f),$"forward q{quality}/grid{grid}: wind plane visibility, gap {gap}");
                        Check(All(1,0,.000002f),$"forward q{quality}/grid{grid}: grid stays on the wind plane");
                        Setup(fill,slope.Item1,slope.Item2,grid,quality,1,1,windMode); Draw(normalFbo,false);
                        Setup(consume,slope.Item1,slope.Item2,grid,quality,1,1,windMode); Draw(outputFbo,true);
                        Check(All(0,expected,.003f),$"deferred q{quality}/grid{grid}: self plane/external caster, gap {gap}");
                        Check(All(1,1,.004f),$"deferred q{quality}: authored sun-facing foliage stays bright");
                        Check(All(2,1,.0001f)&&All(3,1,.0001f),"deferred: backlight and native SSAO sign retained");
                    }
                    foreach(float materialShade in new[]{.34f,.5f,.95f,1f})
                    foreach(float windClass in new[]{0f,.5f,1f}) {
                        Setup(fill,.4f,-.3f,0,quality,materialShade,windClass); Draw(normalFbo,false);
                        Setup(consume,.4f,-.3f,0,quality,materialShade,windClass); Draw(outputFbo,true);
                        Check(All(1,materialShade,.004f)&&All(2,windClass,.0001f)&&All(3,windClass>0?1:0,.0001f),
                            "deferred: half-float material/backlight payload round trip");
                    }
                    // A separate, flat opaque blocker stays in front of the
                    // entire card during wind rotation, including N.L = 0.
                    Array.Fill(data,.25f);
                    GL.ActiveTexture(TextureUnit.Texture0); GL.BindTexture(TextureTarget.Texture2D,depth);
                    GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.DepthComponent24,MapSize,MapSize,0,PixelFormat.DepthComponent,PixelType.Float,data);
                    foreach(int grid in new[]{0,1})
                    foreach(int windMode in new[]{1,3,0})
                    foreach(float incidence in new[]{.1f,.01f,.001f,.0002f,.00011f,.000101f,.000099f,.00001f,0f,-.00001f,-.000099f,-.000101f,-.00011f,-.0002f,-.001f,-.01f,-.1f}) {
                        Setup(forward,0,0,grid,quality,1,1,windMode);
                        GL.Uniform1(GL.GetUniformLocation(forward,"grazingIncidence"),incidence); Draw(outputFbo,true);
                        if(windMode==1 && grid==0 && (incidence==.001f || incidence==.000099f))
                            Console.WriteLine($"Grazing q{quality}, N.L~{incidence}: forward brightness {pixels[4]:F6}");
                        Check(All(0,.35f,.002f),$"forward q{quality}/grid{grid}: flat blocker across wind grazing {incidence}");
                        Setup(fill,0,0,grid,quality,1,1,windMode);
                        GL.Uniform1(GL.GetUniformLocation(fill,"grazingIncidence"),incidence); Draw(normalFbo,false);
                        Setup(consume,0,0,grid,quality,1,1,windMode);
                        GL.Uniform1(GL.GetUniformLocation(consume,"grazingIncidence"),incidence); Draw(outputFbo,true);
                        if(windMode==1 && grid==0 && (incidence==.001f || incidence==.000099f))
                            Console.WriteLine($"Grazing q{quality}, N.L~{incidence}: deferred brightness {pixels[4]:F6}");
                        Check(All(0,.35f,.003f),$"deferred q{quality}/grid{grid}: flat blocker across wind grazing {incidence}");
                    }
                    // Cascade-normalized depth units must not become a world
                    // distance constant. Test independent XY/Z projection scales.
                    foreach(float depthScale in new[]{1f/8,1f/64,1f/256,1f/512})
                    foreach(int grid in new[]{0,1})
                    foreach(float incidence in new[]{.01f,.001f,.000101f,.000099f,0f,-.000099f,-.000101f,-.001f,-.01f}) {
                        Array.Fill(data,.5f-.25f*depthScale);
                        GL.ActiveTexture(TextureUnit.Texture0); GL.BindTexture(TextureTarget.Texture2D,depth);
                        GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.DepthComponent24,MapSize,MapSize,0,PixelFormat.DepthComponent,PixelType.Float,data);
                        float[] matrix={1f/64,0,0,0,0,1f/64,0,0,0,0,depthScale,0,.5f-.5f/64,.5f-.5f/64,.5f-.5f*depthScale,1};
                        void Project(int program) {
                            foreach(string n in new[]{"toShadowMapSpaceMatrixFar","toShadowMapSpaceMatrixNear"})
                                GL.UniformMatrix4(GL.GetUniformLocation(program,n),1,false,matrix);
                            GL.Uniform1(GL.GetUniformLocation(program,"grazingIncidence"),incidence);
                        }
                        Setup(forward,0,0,grid,quality,1,1); Project(forward); Draw(outputFbo,true);
                        Check(All(0,.35f,.002f),$"forward q{quality}/grid{grid}: grazing blocker at depth scale {depthScale}");
                        Setup(fill,0,0,grid,quality,1,1); Project(fill); Draw(normalFbo,false);
                        Setup(consume,0,0,grid,quality,1,1); Project(consume); Draw(outputFbo,true);
                        Check(All(0,.35f,.003f),$"deferred q{quality}/grid{grid}: grazing blocker at depth scale {depthScale}");
                    }
                    foreach(float scale in new[]{1f,.1f,.01f,.001f}) {
                        // Close-up pixels have tiny world-space derivatives;
                        // that must not switch a valid plane to authored normals.
                        Plane(.4f,-.3f,0);
                        Setup(forward,.4f,-.3f,0,quality,1,1);
                        GL.Uniform1(GL.GetUniformLocation(forward,"grazingIncidence"),1f);
                        GL.Uniform1(GL.GetUniformLocation(forward,"geometryScale"),scale); Draw(outputFbo,true);
                        Check(All(0,1f,.002f),$"forward q{quality}: own plane at pixel world scale {scale}");
                        Setup(fill,.4f,-.3f,0,quality,1,1);
                        GL.Uniform1(GL.GetUniformLocation(fill,"grazingIncidence"),1f);
                        GL.Uniform1(GL.GetUniformLocation(fill,"geometryScale"),scale); Draw(normalFbo,false);
                        Setup(consume,.4f,-.3f,0,quality,1,1);
                        GL.Uniform1(GL.GetUniformLocation(consume,"grazingIncidence"),1f);
                        GL.Uniform1(GL.GetUniformLocation(consume,"geometryScale"),scale); Draw(outputFbo,true);
                        Check(All(0,1f,.003f),$"deferred q{quality}: own plane at pixel world scale {scale}");
                    }
                }
                finally {GL.DeleteProgram(forward);GL.DeleteProgram(fill);GL.DeleteProgram(consume);}
            }
            Console.WriteLine($"Foliage receiver checks: {checks-failures}/{checks} passed");
            if(failures!=0)throw new Exception($"{failures} foliage receiver regressions");
        }
        finally {
            GL.UseProgram(previous);GL.DeleteFramebuffer(outputFbo);GL.DeleteFramebuffer(normalFbo);GL.DeleteVertexArray(vao);
            GL.DeleteTexture(output);GL.DeleteTexture(normals);GL.DeleteTexture(depth);
        }
    }
}
