using System;
using System.IO;
using System.Linq;
using System.Reflection;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;

// Numeric correctness only: actual maintained receivers and real binder,
// D24 foreground/same-map occlusion, no timing queries or screenshots.
internal static class AllPassPlacedOcclusionProbe
{
    internal static void Run(string assets)
    {
        using var state = new ShadowGlState();
        string lighting = Path.Combine(assets, "drtagx/shaders/lighting");
        string deferred = Path.Combine(assets, "drtagx/shaders/deferred");
        string balance = File.ReadAllText(Path.Combine(lighting, "drtagx_light_balance.ash"));
        string cube = File.ReadAllText(Path.Combine(deferred, "drtagx_deferred_cube.fsh"));
        string shadows = File.ReadAllText(Path.Combine(deferred, "drtagx_deferred_staticshadows.fsh"));
        string placed = File.ReadAllText(Path.Combine(deferred, "drtagx_deferred_placedlights.fsh"));
        string forward = File.ReadAllText(Path.Combine(lighting, "drtagx_terrain_placed_occlusion.fsh"))
            .Replace("#include drtagx_deferred_cube.fsh", cube)
            .Replace("#include drtagx_deferred_staticshadows.fsh", shadows);
        string surface = File.ReadAllText(Path.Combine(lighting, "drtagx_surface_lighting.fsh"));
        int localStart = surface.IndexOf("vec3 drtVisibleLocalLight()", StringComparison.Ordinal);
        int localEnd = surface.IndexOf("\n}", localStart, StringComparison.Ordinal) + 2;
        // Use native includes from the required 1.22.7 installation.
        string nativeIncludes = Path.Combine(Environment.GetEnvironmentVariable("VINTAGE_STORY")!, "assets/game/shaderincludes");
        string warp = File.ReadAllText(Path.Combine(nativeIncludes, "vertexflagbits.ash")) +
            File.ReadAllText(Path.Combine(nativeIncludes, "vertexwarp.vsh")).Replace("#include noise3d.ash", File.ReadAllText(Path.Combine(nativeIncludes, "noise3d.ash")));
        string opaqueVertex = File.ReadAllText(Path.Combine(assets, "game/shaders/chunkopaque.vsh"));
        string restAssignment = opaqueVertex.Split('\n').Single(line => line.TrimStart().StartsWith("drtPlacedRestPos =", StringComparison.Ordinal));
        string vertex = "#version 430 core\n#define WAVINGSTUFF 1\n" + warp + """
            #version 430 core
            uniform vec3 worldOffset=vec3(0);
            uniform int testWindMode=0;
            uniform bool reverseWinding=false;
            uniform float projectionFlip=1.;
            uniform float restFootprint=.2;
            out vec3 probeWorld;
            out vec3 drtPlacedRestPos;
            void main(){int id=reverseWinding&&gl_VertexID>0?3-gl_VertexID:gl_VertexID;
                vec2 p=vec2((id<<1)&2,id&2)*2.-1.;
                gl_Position=vec4(p*vec2(projectionFlip,1),0,1);vec4 truePos=vec4(worldOffset+vec3(p*restFootprint,3.),1);
                DRT_REST_ASSIGNMENT
                probeWorld=applyGlobalWarping(applyVertexWarping(testWindMode<<25,truePos)).xyz;}
            """.Replace("#version 430 core", "").Replace("DRT_REST_ASSIGNMENT", restAssignment);
        string shared = "#version 430 core\n" + balance + """
            uniform vec3 worldOffset=vec3(0);
            uniform vec3 testNormal=vec3(0,0,1);
            vec3 drtSkyColor=vec3(0),drtSkyLight=vec3(0),drtSunLight=vec3(0);
            vec3 drtVoxelLight=vec3(.2,.3,.4),drtLocalLight=vec3(.6,.8,1.);
            const mat4 invModelViewMatrix=mat4(1);
            in vec3 probeWorld;
            in vec3 drtPlacedRestPos;
            out vec4 color;
            """;
        int grass = ProbeShader.Program((ShaderType.VertexShader, vertex), (ShaderType.FragmentShader, shared + """
            layout(std430,binding=4)readonly buffer Sources{vec4 drtStaticSourceData[];};
            layout(std430,binding=5)readonly buffer Tiles{uint drtStaticTileData[];};
            layout(binding=14)uniform sampler2DArrayShadow drtStaticMaps;
            uniform int drtStaticCount=1,drtStaticAllTerrainPasses=1,drtShadowGridEnabled=1;
            const int drtStaticTileWidth=1;
            const float drtStaticBlend=1.;
            uniform vec2 drtPlacedCalibration[32];
            """ + cube + shadows + placed + """
            void main(){float voxelVisibility;vec4 light=drtPlacedFoliageLight(
                probeWorld-worldOffset,testNormal,voxelVisibility);color=vec4(light.rgb,voxelVisibility);}
            """));
        int terrain = ProbeShader.Program((ShaderType.VertexShader, vertex), (ShaderType.FragmentShader, shared + "\n" + """
            #define DRT_TERRAIN_PLACED_OCCLUSION
            vec3 drtAtmosphereCamera=vec3(0);
            float drtSurfaceSunAccess=0.,drtTerrainPlacedVisibility=1.;
            vec3 drtTerrainPlacedRadiance=vec3(0);
            uniform bool testFoliage=false;
            uniform vec3 testEye=vec3(0);
            uniform bool reproduceFixedWinding=false;
            uniform bool testShadowPlane=false;
            """ + surface[localStart..localEnd] + forward + """
            void main(){drtAtmosphereCamera=worldOffset+testEye;drtCaptureTerrainPlacedPlane(drtPlacedRestPos);
                drtPrepareTerrainPlacedVisibility(drtPlacedRestPos,testFoliage,0.);
                color=vec4(drtVisibleLocalLight(),drtTerrainPlacedVisibility);
                if(testShadowPlane) color=vec4(drtStaticTwoSidedVisibilityWithTolerance(
                    drtPlacedRestPos-worldOffset,testNormal,0,1,0.,.02));
                if(reproduceFixedWinding) {
                    // Previous production direction: fixed physical winding
                    // lights the same triangle identically from either camera side.
                    vec3 n=drtTerrainPlacedPlane*(gl_FrontFacing?1.:-1.);
                    color=vec4(max(dot(n,normalize(worldOffset-drtPlacedRestPos)),0.));
                }}
            """));
        int vao=GL.GenVertexArray(), fbo=GL.GenFramebuffer(), target=GL.GenTexture();
        int depth=GL.GenTexture(), sources=GL.GenBuffer(), tiles=GL.GenBuffer();
        try
        {
            GL.BindVertexArray(vao); GL.BindFramebuffer(FramebufferTarget.Framebuffer,fbo);
            GL.ActiveTexture(TextureUnit.Texture0); GL.BindTexture(TextureTarget.Texture2D,target);
            GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.Rgba32f,1,1,0,PixelFormat.Rgba,PixelType.Float,IntPtr.Zero);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,FramebufferAttachment.ColorAttachment0,TextureTarget.Texture2D,target,0);
            GL.DrawBuffer(DrawBufferMode.ColorAttachment0); GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
            GL.Viewport(0,0,1,1); GL.Disable(EnableCap.DepthTest); GL.Disable(EnableCap.Blend); GL.Disable(EnableCap.ScissorTest);
            GL.Disable(EnableCap.CullFace); GL.ColorMask(true,true,true,true);
            GL.ActiveTexture(TextureUnit.Texture14); GL.BindSampler(14,0); GL.BindTexture(TextureTarget.Texture2DArray,depth);
            const int size=96;
            GL.TexStorage3D(TextureTarget3d.Texture2DArray,1,SizedInternalFormat.DepthComponent24,size,size,12);
            GL.TexParameter(TextureTarget.Texture2DArray,TextureParameterName.TextureCompareMode,(int)TextureCompareMode.CompareRefToTexture);
            GL.TexParameter(TextureTarget.Texture2DArray,TextureParameterName.TextureCompareFunc,(int)DepthFunction.Lequal);
            GL.TexParameter(TextureTarget.Texture2DArray,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2DArray,TextureParameterName.TextureMagFilter,(int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2DArray,TextureParameterName.TextureWrapS,(int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2DArray,TextureParameterName.TextureWrapT,(int)TextureWrapMode.ClampToEdge);
            // A nearer card in the same source's map blocks the receiver at z=3.
            // Independent native near/far perspective depth, not a filter stub.
            float blocker=(22f/(22f-.1f))-(22f*.1f/((22f-.1f)*2f));
            float[] blocked=Enumerable.Repeat(blocker,size*size).ToArray(), clear=Enumerable.Repeat(1f,size*size).ToArray();
            void Depth(bool occluded)
            {
                GL.ActiveTexture(TextureUnit.Texture14); GL.BindTexture(TextureTarget.Texture2DArray,depth);
                for(int layer=0;layer<12;++layer) GL.TexSubImage3D(TextureTarget.Texture2DArray,0,0,0,layer,size,size,1,
                    PixelFormat.DepthComponent,PixelType.Float,layer<6&&occluded?blocked:clear);
            }
            float[] records={0,0,0,22, 1,1,1,0, 1,0,1,0, 0,0,0,31,
                0,0,0,22, 1,1,1,1, 1,0,1,0, 0,0,0,31};
            void Records(){GL.BindBuffer(BufferTarget.ShaderStorageBuffer,sources);GL.BufferData(BufferTarget.ShaderStorageBuffer,records.Length*4,records,BufferUsageHint.DynamicDraw);}
            Records(); GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer,4,sources);
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer,tiles);GL.BufferData(BufferTarget.ShaderStorageBuffer,20,new uint[]{3,0,0,0,0},BufferUsageHint.StaticDraw);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer,5,tiles);
            float[] calibration=Enumerable.Repeat(1f,64).ToArray();
            foreach(int program in new[]{grass,terrain}){GL.UseProgram(program);GL.Uniform2(GL.GetUniformLocation(program,"drtPlacedCalibration[0]"),32,calibration);}
            GL.UseProgram(terrain);GL.Uniform1(GL.GetUniformLocation(terrain,"drtTerrainPlacedAllPasses"),1);
            float[] Pixel(int program,float sign,int count,bool shifted,float priorSourceShift=0)
            {
                GL.UseProgram(program);GL.Uniform1(GL.GetUniformLocation(program,program==grass?"drtStaticCount":"drtTerrainPlacedCount"),count);
                if(program==grass)GL.Uniform3(GL.GetUniformLocation(program,"testNormal"),0f,0f,sign);
                else {
                    // Same physical card and lamp, observed from opposite sides.
                    // Rebase the old source record as the real binder does.
                    float eye=sign>0?6f:0f;
                    GL.Uniform3(GL.GetUniformLocation(program,"testEye"),0f,0f,eye);
                    GL.Uniform3(GL.GetUniformLocation(program,"drtTerrainPlacedSourceShift"),priorSourceShift,0f,-eye);
                }
                GL.Uniform3(GL.GetUniformLocation(program,"worldOffset"),shifted?10.25f:0f,shifted?-3.5f:0f,shifted?7.75f:0f);
                GL.DrawArrays(PrimitiveType.Triangles,0,3);float[] pixel=new float[4];GL.ReadPixels(0,0,1,1,PixelFormat.Rgba,PixelType.Float,pixel);return pixel;
            }
            int checks=0;
            void Near(float actual,float expected,string name){if(!float.IsFinite(actual)||Math.Abs(actual-expected)>.0005f)throw new Exception($"{name}: {actual} != {expected}");checks++;}
            foreach(bool occluded in new[]{false,true})foreach(float sign in new[]{-1f,1f})foreach(bool shifted in new[]{false,true})
            {
                Depth(occluded);float[] g=Pixel(grass,sign,1,shifted), f=Pixel(terrain,sign,1,shifted);
                Near(g[3],occluded?0:1,"visible normal ignores mesh winding, translated origin");
                if(occluded)Near(g[0],0,"grass direct self-shadow");else if(g[0]<=0)throw new Exception("Unoccluded grass lost source RGB");
                Near(f[3],occluded?0:1,"forward terrain self-shadow");Near(f[0],occluded?.4f:.6f,"preserved moving/emission share");
            }
            Depth(true);
            foreach(float blend in new[]{0f,.25f,1f})
            {
                records[9]=blend;Records();Near(Pixel(grass,-1,1,false)[3],blend,"grass refresh blend");
                Near(Pixel(terrain,1,1,false)[3],blend,"forward refresh blend");
            }
            records[9]=0;Records();Near(Pixel(grass,-1,2,false)[3],.5f,"blocked source still owns voxel energy");
            Near(Pixel(terrain,1,2,false)[3],.5f,"mixed forward source occlusion");
            Near(Pixel(terrain,1,0,false)[3],1,"disabled/unready forward fallback");
            // Old prepared camera is 25 blocks away: without the source shift
            // this receiver lies outside the light radius and incorrectly stays lit.
            records[12]=25;Records();GL.UseProgram(terrain);
            GL.Uniform3(GL.GetUniformLocation(terrain,"drtTerrainPlacedSourceShift"),-25f,0f,0f);
            Near(Pixel(terrain,1,1,false,-25)[3],0,"early forward draw rebases prior camera snapshot");
            records[12]=0;Records();GL.UseProgram(terrain);
            GL.Uniform3(GL.GetUniformLocation(terrain,"drtTerrainPlacedSourceShift"),0f,0f,0f);
            GL.UseProgram(grass);GL.Uniform1(GL.GetUniformLocation(grass,"drtStaticAllTerrainPasses"),0);
            Near(Pixel(grass,-1,1,false)[3],1,"legacy visible-side normal");Near(Pixel(grass,1,1,false)[3],1,"legacy ignores reversed mesh normal on the same visible side");
            // Put the cached blade on its own receiving plane. The old live
            // wind receiver crosses that stale depth; the rest receiver stays lit.
            float coplanar=(22f/(22f-.1f))-(22f*.1f/((22f-.1f)*3f));
            Array.Fill(blocked,coplanar);Depth(true);
            GL.UseProgram(grass);GL.Uniform1(GL.GetUniformLocation(grass,"drtStaticAllTerrainPasses"),1);
            GL.UseProgram(terrain);GL.Uniform1(GL.GetUniformLocation(terrain,"testFoliage"),1);
            float[] stable=Pixel(terrain,-1,1,false),stableBack=Pixel(terrain,1,1,false);
            float[] originalGrass=Pixel(grass,-1,1,false);
            Near(stable[0],.5f+originalGrass[0]*.5f,"preserved foliage source RGB and half voxel base");
            Near(stable[1],.65f+originalGrass[1]*.5f,"preserved green foliage compositor");
            Near(stable[2],.8f+originalGrass[2]*.5f,"preserved blue foliage compositor");
            Near(stable[3],.5f,"front-facing half voxel share");
            Near(stableBack[3],.5f,"all-pass away-facing grass keeps unoccluded half voxel share");
            for(int c=0;c<3;++c) {
                Near(stableBack[c],stable[c],"all-pass grass shades by depth on both visible sides");
            }
            GL.UseProgram(terrain);GL.Uniform1(GL.GetUniformLocation(terrain,"drtTerrainPlacedAllPasses"),0);
            float[] ordinaryBack=Pixel(terrain,1,1,false);
            Near(ordinaryBack[3],.125f,"ordinary PLS away-facing half voxel share at 25% facing floor");
            for(int c=0;c<3;++c) {
                float independent=.4f+c*.1f;
                Near(ordinaryBack[c],independent+(stable[c]-independent)*.25f,"ordinary PLS has 4:1 visible-side response; moving/emission stays intact");
            }
            GL.UseProgram(terrain);GL.Uniform1(GL.GetUniformLocation(terrain,"reverseWinding"),1);
            GL.Uniform1(GL.GetUniformLocation(terrain,"reproduceFixedWinding"),1);
            Near(Pixel(terrain,-1,1,false)[0],1,"old fixed winding lights the front");
            Near(Pixel(terrain,1,1,false)[0],1,"old fixed winding also lights the visible back; floor edits cannot help");
            GL.Uniform1(GL.GetUniformLocation(terrain,"reproduceFixedWinding"),0);
            GL.Uniform1(GL.GetUniformLocation(terrain,"reverseWinding"),0);
            float oldMin=1,oldMax=0;
            foreach(int mode in new[]{1,2,3,4,5,6,7,8,9,10,11,12,13})foreach(float phase in new[]{0f,1f,4f,11f,27f,63f}) {
                foreach(int program in new[]{grass,terrain}) {
                    GL.UseProgram(program);GL.Uniform1(GL.GetUniformLocation(program,"testWindMode"),mode);
                    GL.Uniform1(GL.GetUniformLocation(program,"windSpeed"),1.5f);
                    GL.Uniform1(GL.GetUniformLocation(program,"windWaveCounter"),phase);
                    GL.Uniform1(GL.GetUniformLocation(program,"windWaveCounterHighFreq"),phase*2f);
                    GL.Uniform1(GL.GetUniformLocation(program,"waterWaveCounter"),phase);
                }
                // Both modes, both mesh windings and camera sides: changing
                // winding alone must never substitute for viewing a back side.
                foreach(bool allPass in new[]{false,true})foreach(bool reversed in new[]{false,true})foreach(float flip in new[]{-1f,1f}) {
                    GL.UseProgram(terrain);GL.Uniform1(GL.GetUniformLocation(terrain,"projectionFlip"),flip);
                    GL.Uniform1(GL.GetUniformLocation(terrain,"reverseWinding"),reversed?1:0);
                    GL.Uniform1(GL.GetUniformLocation(terrain,"drtTerrainPlacedAllPasses"),allPass?1:0);
                    float[] current=Pixel(terrain,-1,1,false),currentBack=Pixel(terrain,1,1,false);
                    for(int c=0;c<4;++c) {
                        Near(current[c],stable[c],"front-facing wind stability in both PLS modes");
                        Near(currentBack[c],allPass?stableBack[c]:ordinaryBack[c],"mode-specific away side stays stable through wind and winding changes");
                    }
                }
                float[] stale=Pixel(grass,-1,1,false);
                oldMin=Math.Min(oldMin,stale[3]);oldMax=Math.Max(oldMax,stale[3]);
            }
            if(oldMax-oldMin<.5f)throw new Exception("Wind sweep failed to reproduce stale-depth flickering");
            // Very small valid cards used to lose their derivative plane at
            // the absolute area cutoff, restoring full light to ordinary backs.
            foreach(float footprint in new[]{.2f,.001f,.00001f})foreach(bool allPass in new[]{false,true}) {
                GL.UseProgram(terrain);GL.Uniform1(GL.GetUniformLocation(terrain,"restFootprint"),footprint);
                GL.Uniform1(GL.GetUniformLocation(terrain,"drtTerrainPlacedAllPasses"),allPass?1:0);
                float[] current=Pixel(terrain,-1,1,false),currentBack=Pixel(terrain,1,1,false);
                for(int c=0;c<4;++c) {
                    Near(current[c],stable[c],"valid small rest plane retains fully facing brightness");
                    Near(currentBack[c],allPass?stableBack[c]:ordinaryBack[c],"small rest plane preserves mode-specific shading");
                }
            }
            GL.Uniform1(GL.GetUniformLocation(terrain,"restFootprint"),.2f);
            // Put the lamp across the camera-visible plane to exercise the
            // deferred fallback's away-facing response, independent of the MRT path.
            records[2]=records[14]=6;Records();Depth(false);
            GL.UseProgram(grass);GL.Uniform1(GL.GetUniformLocation(grass,"testWindMode"),0);
            foreach(bool allPass in new[]{false,true})foreach(float sign in new[]{-1f,1f}) {
                GL.Uniform1(GL.GetUniformLocation(grass,"drtStaticAllTerrainPasses"),allPass?1:0);
                Near(Pixel(grass,sign,1,false)[3],allPass?1f:.25f,"deferred fallback uses depth-only all-pass / quarter-strength ordinary PLS");
            }
            records[2]=records[14]=0;Records();
            // Near-contact depth shadows stationary no-cull geometry, but sits
            // inside the explicit wind tolerance. A distant crossing still occludes.
            foreach((float blockerDistance,bool windLit) in new[]{(2.965f,true),(2.8f,false)}) {
                float depthValue=(22f/(22f-.1f))-(22f*.1f/((22f-.1f)*blockerDistance));
                Array.Fill(blocked,depthValue);Depth(true);
                GL.UseProgram(terrain);GL.Uniform1(GL.GetUniformLocation(terrain,"drtTerrainPlacedAllPasses"),1);
                GL.Uniform1(GL.GetUniformLocation(terrain,"testFoliage"),0);
                Near(Pixel(terrain,-1,1,false)[3],0,"stationary no-cull retains contact/self occlusion");
                GL.Uniform1(GL.GetUniformLocation(terrain,"testFoliage"),1);
                Near(Pixel(terrain,-1,1,false)[3],windLit?.5f:0,"wind tolerance preserves distant self occlusion");
                GL.Uniform1(GL.GetUniformLocation(terrain,"drtTerrainPlacedAllPasses"),0);
                Near(Pixel(terrain,-1,1,false)[3],.5f,"PLS without all passes keeps wind facing without additional depth");
            }
            // A crossing blade in this same map must still block a grazing
            // receiving plane; its analytical PCF bias must not erase the caster.
            GL.Uniform1(GL.GetUniformLocation(terrain,"testShadowPlane"),1);
            foreach(float cosine in new[]{0f,.0005f,.0011f,.01f,.1f,.5f,1f})foreach(float sign in new[]{-1f,1f}) {
                GL.Uniform3(GL.GetUniformLocation(terrain,"testNormal"),MathF.Sqrt(1-cosine*cosine),0,cosine*sign);
                Near(Pixel(terrain,-1,1,false)[0],0,"grazing wind plane retains crossed-blade self-occlusion");
                Depth(false);
                Near(Pixel(terrain,-1,1,false)[0],1,"grazing wind plane retains unoccluded light");
                Depth(true);
            }
            GL.Uniform1(GL.GetUniformLocation(terrain,"testShadowPlane"),0);
            CheckWindBakes(assets,warp,ref checks);
            CheckPreparedHandoff(assets,balance,ref checks);
            CheckBindings(terrain,depth,sources);
            if(GL.GetError()!=ErrorCode.NoError)throw new Exception("All-pass occlusion probe left a GL error");
            Console.WriteLine($"PASS PLS modes: {checks} assertions; ordinary camera-side PLS ratio {(stable[0]-.4f)/(ordinaryBack[0]-.4f):F6}; all-pass depth-only ratio {(stable[0]-.4f)/(stableBack[0]-.4f):F6}; both modes/windings, small rest planes, wind contact tolerance, self-shadow, source handoff and real GL bindings; stale-depth {oldMin:F3}..{oldMax:F3}");
        }
        finally
        {
            GL.UseProgram(0);GL.BindSampler(14,0);GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer,4,0);GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer,5,0);
            GL.DeleteProgram(grass);GL.DeleteProgram(terrain);GL.DeleteVertexArray(vao);GL.DeleteFramebuffer(fbo);
            GL.DeleteTexture(target);GL.DeleteTexture(depth);GL.DeleteBuffer(sources);GL.DeleteBuffer(tiles);
        }
    }

    private static void CheckWindBakes(string assets,string warp,ref int checks)
    {
        using var state=new ShadowGlState();
        string vertex=File.ReadAllText(Path.Combine(assets,"game/shaders/chunkshadowmap.vsh"))
            .Replace("#include vertexflagbits.ash",warp).Replace("#include vertexwarp.vsh","")
            .Replace("#version 330 core","#version 330 core\n#define USESSBO 0\n#define WAVINGSTUFF 1");
        int program=ProbeShader.Program((ShaderType.VertexShader,vertex),(ShaderType.FragmentShader,
            File.ReadAllText(Path.Combine(assets,"game/shaders/chunkshadowmap.fsh"))));
        int vao=GL.GenVertexArray(),vbo=GL.GenBuffer(),fbo=GL.GenFramebuffer(),depth=GL.GenTexture(),white=GL.GenTexture();
        int active=GL.GetInteger(GetPName.ActiveTexture);
        GL.ActiveTexture(TextureUnit.Texture0);int oldTexture=GL.GetInteger(GetPName.TextureBinding2D);
        try {
            GL.BindVertexArray(vao);GL.BindBuffer(BufferTarget.ArrayBuffer,vbo);
            float[] quad={-.6f,.2f,3,.6f,.2f,3,.6f,1.2f,3,-.6f,.2f,3,.6f,1.2f,3,-.6f,1.2f,3};
            GL.BufferData(BufferTarget.ArrayBuffer,quad.Length*4,quad,BufferUsageHint.StaticDraw);
            GL.VertexAttribPointer(0,3,VertexAttribPointerType.Float,false,12,0);GL.EnableVertexAttribArray(0);
            GL.VertexAttrib2(1,.5f,.5f);GL.VertexAttribI1(6,1);
            GL.BindTexture(TextureTarget.Texture2D,white);
            GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.Rgba8,1,1,0,PixelFormat.Rgba,PixelType.UnsignedByte,new byte[]{255,255,255,255});
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)TextureMagFilter.Nearest);
            GL.BindTexture(TextureTarget.Texture2D,depth);
            GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.DepthComponent24,32,32,0,PixelFormat.DepthComponent,PixelType.Float,IntPtr.Zero);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer,fbo);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,FramebufferAttachment.DepthAttachment,TextureTarget.Texture2D,depth,0);
            GL.DrawBuffer(DrawBufferMode.None);GL.ReadBuffer(ReadBufferMode.None);
            if(GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer)!=FramebufferErrorCode.FramebufferComplete)throw new Exception("Wind caster FBO incomplete");
            GL.BindTexture(TextureTarget.Texture2D,white);GL.UseProgram(program);
            GL.Viewport(0,0,32,32);GL.Enable(EnableCap.DepthTest);GL.DepthMask(true);
            GL.Disable(EnableCap.CullFace);GL.Disable(EnableCap.Blend);GL.Disable(EnableCap.ScissorTest);GL.Disable(EnableCap.PolygonOffsetFill);
            GL.Uniform1(GL.GetUniformLocation(program,"tex2d"),0);
            GL.Uniform3(GL.GetUniformLocation(program,"drtEmitterBlockMin"),-100f,-100f,-100f);
            // Orthographic fixture isolates geometry deformation from cubemap
            // filtering; actual production native warping and depth rasterize.
            float[] matrix={.7f,0,0,0,0,.7f,0,0,0,0,.1f,0,0,-.4f,-.3f,1};
            GL.UniformMatrix4(GL.GetUniformLocation(program,"mvpMatrix"),1,false,matrix);
            float[] pixels=new float[32*32];
            void Draw(int mode,float phase,bool cached,bool liquid) {
                GL.VertexAttribI1(3,mode<<25);
                GL.Uniform1(GL.GetUniformLocation(program,"drtShadowTerrainPass"),liquid?4:1);
                GL.Uniform1(GL.GetUniformLocation(program,"drtExcludeEmitter"),cached?1:0);
                GL.Uniform1(GL.GetUniformLocation(program,"windSpeed"),1.5f);
                GL.Uniform1(GL.GetUniformLocation(program,"windWaveCounter"),phase);
                GL.Uniform1(GL.GetUniformLocation(program,"windWaveCounterHighFreq"),phase*2);
                GL.Uniform1(GL.GetUniformLocation(program,"waterWaveCounter"),phase);
                GL.Clear(ClearBufferMask.DepthBufferBit);GL.DrawArrays(PrimitiveType.Triangles,0,6);
                GL.ReadPixels(0,0,32,32,PixelFormat.DepthComponent,PixelType.Float,pixels);
            }
            Draw(2,0,true,false);float[] rest=(float[])pixels.Clone();
            if(!rest.Any(x=>x<1))throw new Exception("Cached wind casters disappeared");
            foreach(int mode in Enumerable.Range(0,14))foreach(float phase in new[]{0f,1f,4f,11f,27f,63f}) {
                Draw(mode,phase,true,false);
                if(!rest.SequenceEqual(pixels))throw new Exception($"Cached wind depth moved: mode {mode}, phase {phase}");
                checks++;
            }
            Draw(0,0,true,true);float[] liquidRest=(float[])pixels.Clone();
            foreach(float phase in new[]{1f,4f,11f,27f,63f}) {
                Draw(0,phase,true,true);
                if(!liquidRest.SequenceEqual(pixels))throw new Exception("Cached liquid depth moved with wind/waves");
                checks++;
            }
            Draw(3,0,false,false);float[] live=(float[])pixels.Clone();Draw(3,27,false,false);
            if(live.SequenceEqual(pixels))throw new Exception("Native sun/moving shadow wind was frozen");
            checks++;
        }
        finally {
            GL.ActiveTexture(TextureUnit.Texture0);GL.BindTexture(TextureTarget.Texture2D,oldTexture);GL.ActiveTexture((TextureUnit)active);
            GL.DeleteProgram(program);GL.DeleteVertexArray(vao);GL.DeleteBuffer(vbo);GL.DeleteFramebuffer(fbo);GL.DeleteTexture(depth);GL.DeleteTexture(white);
        }
    }

    private static void CheckPreparedHandoff(string assets,string balance,ref int checks)
    {
        using var state=new ShadowGlState();
        string opaque=File.ReadAllText(Path.Combine(assets,"game/shaders/chunkopaque.fsh"));
        string deferred=ProbeShader.DeferredSource(Path.Combine(assets,"sheydermod/shaders/deferredlighting.fsh"));
        string decode=string.Join("\n",deferred.Split('\n').Where(line=>line.TrimStart().StartsWith("bool placedPrepared =") ||
            line.TrimStart().StartsWith("if (placedPrepared) ptGPos.w")));
        string vertex="#version 430 core\nvoid main(){vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);gl_Position=vec4(p*2.-1.,0,1);}";
        string fragment="#version 430 core\n#define SSAOLEVEL 1\n#define SHADOWQUALITY 0\n"+balance+"""
            const int WindModeBitMask=0x1e000000,WindModeLeavesMask=0x06000000;
            const int renderFlags=2<<25,haxyFade=1,drtTerrainPlacedCount=1;
            const int drtStaticCount=1,drtStaticTileWidth=1;
            const float drtWindPresence=1.,specularStrength=0.,voxSunLight=0.,alphaTest=.42,nb=.7;
            const float fogAmount=.125,glowLevel=.1,drtStaticBlend=1.;
            const vec2 uv=vec2(0);
            const mat4 modelViewMatrix=mat4(1);
            uniform sampler2D specularTex;
            vec3 drtPlacedRestPos=vec3(0,0,3),drtTerrainSunPlaneNormal=vec3(0,0,1);
            vec3 drtVoxelLight=vec3(.2,.3,.4),drtEmissionLight=vec3(1),drtTerrainPlacedRadiance=vec3(0);
            vec3 drtSkyColor=vec3(0),drtSkyLight=vec3(0),drtSunLight=vec3(0),drtLocalLight=vec3(0);
            float drtTerrainPlacedVisibility=1.,drtSurfaceSunAccess=0.,shadowCalls=0.;
            vec4 gnormal=vec4(0,0,1,0),outColor,outGlow,outGPosition,outGNormal;
            layout(location=0)out vec4 color;
            // Independent supplied PLS result: test the maintained producer,
            // negative-W handoff and consumer without another depth lookup.
            void drtPrepareTerrainPlacedVisibility(vec3 p,bool f,float g){
                drtTerrainPlacedVisibility=.5;drtTerrainPlacedRadiance=vec3(.2,.3,.4);}
            float drtSunPackFoliageLighting(float n,float w){return n;}
            vec4 drtPlacedLights(vec3 r,vec3 n,bool d,bool g,out float e,out vec3 s){shadowCalls++;e=0;s=vec3(0);return vec4(0);}
            vec4 drtPlacedFoliageLight(vec3 r,vec3 n,out float v){shadowCalls++;v=1;return vec4(0);}
            vec3 drtCurrentFrameLights(vec3 r,vec3 n){return vec3(.1,.2,.3);}
            """+ProbeShader.Function(opaque,"void sm_deferredFill(")+ProbeShader.Function(deferred,"float drtPlacedWeight(")+
            ProbeShader.Function(deferred,"void drtDeferredRelight(")+"""
            void main(){
                sm_deferredFill(vec4(1,1,1,.7),vec3(1));
                vec3 prepared=outColor.rgb;vec4 ptGPos=outGPosition;
                DRT_HANDOFF_DECODE
                vec3 albedo=prepared;float brightness;
                drtDeferredRelight(albedo,brightness,vec3(1),vec3(0,0,3),vec3(0,0,1),vec3(0,0,1),.5,.5,glowLevel,placedPrepared?-1.:1.);
                // Sun, moving light and emission survive; PLS remains single
                // application and the fog payload/coverage are restored exactly.
                color=vec4(albedo-prepared-drtSunLight-drtCurrentFrameLights(vec3(0),vec3(0)),
                    shadowCalls+abs(ptGPos.w-.35)+abs(outColor.a-.7)+(placedPrepared?0.:1.));
            }
            """.Replace("DRT_HANDOFF_DECODE",decode);
        int program=ProbeShader.Program((ShaderType.VertexShader,vertex),(ShaderType.FragmentShader,fragment));
        try {
            GL.UseProgram(program);GL.DrawArrays(PrimitiveType.Triangles,0,3);float[] pixel=new float[4];
            GL.ReadPixels(0,0,1,1,PixelFormat.Rgba,PixelType.Float,pixel);
            foreach(float value in pixel){if(!float.IsFinite(value)||Math.Abs(value)>.00001f)throw new Exception($"Prepared PLS/fog handoff mismatch: {value}");checks++;}
        }
        finally {GL.DeleteProgram(program);}
    }

    private static void CheckBindings(int program,int depth,int source)
    {
        ProbeAssets.Api([]); // Load optional engine signature dependencies before patching native Use/Stop.
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var maps=new StaticTerrainShadowMaps(new MovingLightShadowRenderer()){AllTerrainPassesEnabled=true};
        var tiles=new StaticLightTileBindings();
        typeof(StaticTerrainShadowMaps).GetField("_texture",flags)!.SetValue(maps,depth);
        typeof(StaticTerrainShadowMaps).GetProperty("Ready",flags)!.SetValue(maps,true);
        typeof(StaticLightTileBindings).GetField("_sourceBuffer",flags)!.SetValue(tiles,source);
        typeof(StaticLightTileBindings).GetProperty("PublishedCount",flags)!.SetValue(tiles,2);
        using var binding=new PlacedLightTerrainBindings(maps,tiles){Enabled=true};
        var bind=typeof(PlacedLightTerrainBindings).GetMethod("Bind",flags)!;
        int sentinel=GL.GenBuffer(), generic=GL.GenBuffer(), texture=GL.GenTexture(), sampler=GL.GenSampler();
        try
        {
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer,4,sentinel);GL.BindBuffer(BufferTarget.ShaderStorageBuffer,generic);
            GL.ActiveTexture(TextureUnit.Texture14);GL.BindTexture(TextureTarget.Texture2DArray,texture);GL.BindSampler(14,sampler);
            GL.ActiveTexture(TextureUnit.Texture3);GL.UseProgram(program);
            foreach(bool allPass in new[]{true,false}) {
                maps.AllTerrainPassesEnabled=allPass;
                typeof(StaticTerrainShadowMaps).GetProperty("Ready",flags)!.SetValue(maps,true);
                bind.Invoke(binding,new object[]{program});
                GL.GetInteger(GetIndexedPName.ShaderStorageBufferBinding,4,out int activeSource);
                GL.GetUniform(program,GL.GetUniformLocation(program,"drtTerrainPlacedCount"),out int sourceCount);
                GL.GetUniform(program,GL.GetUniformLocation(program,"drtTerrainPlacedAllPasses"),out int mode);
                if(activeSource!=source||sourceCount!=2||mode!=(allPass?1:0))throw new Exception($"Real PLS binder gated directionality incorrectly: allPass={allPass}");
                binding.Restore();
            }
            GL.GetInteger(GetIndexedPName.ShaderStorageBufferBinding,4,out int restoredSource);
            if(restoredSource!=sentinel||GL.GetInteger(GetPName.ShaderStorageBufferBinding)!=generic||GL.GetInteger(GetPName.ActiveTexture)!=(int)TextureUnit.Texture3)
                throw new Exception("Forward binder leaked SSBO or active texture state");
            GL.ActiveTexture(TextureUnit.Texture14);
            if(GL.GetInteger(GetPName.TextureBinding2DArray)!=texture||GL.GetInteger(GetPName.SamplerBinding)!=sampler)
                throw new Exception("Forward binder leaked array texture/sampler");
            binding.Enabled=false;bind.Invoke(binding,new object[]{program});GL.GetUniform(program,GL.GetUniformLocation(program,"drtTerrainPlacedCount"),out int count);
            if(count!=0)throw new Exception("Disabled forward receiving retained sources");
            for(int toggle=0;toggle<12;++toggle) {
                binding.Restore();binding.Enabled=new FrameQuality(true).PlacedLights(true);
                bind.Invoke(binding,new object[]{program});GL.GetUniform(program,GL.GetUniformLocation(program,"drtTerrainPlacedCount"),out count);
                GL.GetInteger(GetIndexedPName.ShaderStorageBufferBinding,4,out int dormantSource);
                if(count!=0||dormantSource!=sentinel)throw new Exception("Performance terrain PLS retained sources/bindings");
                binding.Restore();binding.Enabled=new FrameQuality(false).PlacedLights(true);
                bind.Invoke(binding,new object[]{program});GL.GetUniform(program,GL.GetUniformLocation(program,"drtTerrainPlacedCount"),out count);
                if(count!=2)throw new Exception("Normal terrain PLS did not restore completed sources");
                binding.Restore();
            }
            // A shader generation change must retire the old wind snapshot.
            maps.ResetShaderLocations();binding.Enabled=true;bind.Invoke(binding,new object[]{program});
            GL.GetUniform(program,GL.GetUniformLocation(program,"drtTerrainPlacedCount"),out count);
            if(maps.Ready||count!=0)throw new Exception("Shader reload retained old cached geometry/source publication");
            binding.Reload();
        }
        finally
        {
            binding.Restore();GL.BindSampler(14,0);GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer,4,0);GL.BindBuffer(BufferTarget.ShaderStorageBuffer,0);
            GL.DeleteBuffer(sentinel);GL.DeleteBuffer(generic);GL.DeleteTexture(texture);GL.DeleteSampler(sampler);
        }
    }
}
