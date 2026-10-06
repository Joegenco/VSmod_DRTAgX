using System;
using System.IO;
using System.Linq;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;

/// <summary>
/// Real-MRT gradient plus before/after blend-contract checks. Numerical checks
/// do not establish in-game appearance or native draw-state captures. The
/// rejected Glow-A layout must not be restored. No images or timing measurements.
/// </summary>
internal static class DeferredGradientProbe
{
    private const int Width = 1024;

    internal static void Run(string deferredPath, string originals = null)
    {
        using var window = new GameWindow(GameWindowSettings.Default, new NativeWindowSettings
        { StartVisible = false, ClientSize = new Vector2i(16, 16), API = ContextAPI.OpenGL,
          APIVersion = new Version(4, 3), Profile = ContextProfile.Core });
        window.MakeCurrent();
        GL.LoadBindings(new OpenTK.Windowing.GraphicsLibraryFramework.GLFWBindingsContext());
        string shaders = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(deferredPath)!, "../../game/shaders"));
        string lighting = Path.GetFullPath(Path.Combine(shaders, "../../drtagx/shaders/lighting"));
        string common = File.ReadAllText(Path.Combine(lighting, "drtagx_light_balance.ash"));
        string surface = File.ReadAllText(Path.Combine(lighting, "drtagx_surface_lighting.fsh"))
            .Replace("#include drtagx_fog_transport.ash", "") // Controlled legacy-path packing fixture.
            .Replace("#include drtagx_light_balance.ash", "");
        string deferred = ProbeShader.DeferredSource(deferredPath);
        string main = ProbeShader.Function(originals == null ? deferred :
            File.ReadAllText(Path.Combine(originals,"deferredlighting.fsh")), "void main(void)");
        // Execute the production marker/passthrough and sun/block decode, rather
        // than writing a second implementation of the communication contract.
        string decode = main[(main.IndexOf('{') + 1)..main.IndexOf("    float specMap", StringComparison.Ordinal)];
        int glowStart = deferred.IndexOf("    outGlow = mix(ptGlow",StringComparison.Ordinal);
        string restoreGlow = deferred[glowStart..(deferred.IndexOf(';',glowStart)+1)];
        string consumer = """
            #version 430 core
            #define DRT_DEFERRED_LIGHTING
            uniform sampler2D gColor, gGlow, gPositionIn;
            uniform vec3 drtSkyColor;
            uniform float drtStaticBlend;
            uniform int drtStaticCount, drtStaticTileWidth;
            in vec2 texcoord;
            layout(location=0) out vec4 outColor;
            layout(location=1) out vec4 outGlow;
            layout(location=3) out vec4 outGPosition;
            """ + common + surface + """
            vec4 drtPlacedLights(vec3 r, vec3 n, bool direct, bool grass, out float e, out vec3 s) { e=0; s=vec3(0); return vec4(0); }
            vec4 drtPlacedFoliageLight(vec3 r, vec3 n, out float facing) { facing=1.0; return vec4(0.0); }
            float drtPlacedWeight(vec3 r, float s, float g) { return 0; }
            vec3 drtCurrentFrameLights(vec3 r, vec3 n) { return vec3(0); }
            """ + ProbeShader.Function(deferred, "void drtDeferredRelight(") +
            "\nvoid main() {\n" + decode + """
                drtDeferredRelight(albedo,blockBright,rawAlbedo,vec3(0),vec3(0,1,0),vec3(0,1,0),voxSun,actualVoxSun,glowVec.r,0.0);
                outColor=vec4(albedo,color.a);
                float glow=0, bloomMask=0;
                """ + restoreGlow + """
                outGPosition=ptGPos;
            }
            """;
        const string vertex = """
            #version 430 core
            uniform int probeMode;
            out vec2 texcoord;
            out float voxSunLight;
            out float gradient;
            void main() {
                vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);
                gl_Position=vec4(p*2-1,0,1);
                texcoord=p; gradient=p.x;
                voxSunLight=probeMode==0 ? p.x : 0;
            }
            """;
        int relight = ProbeShader.Program((ShaderType.VertexShader, vertex), (ShaderType.FragmentShader, consumer));
        int input = GL.GenFramebuffer(), output = GL.GenFramebuffer(), vao = GL.GenVertexArray();
        int[] inputs = Enumerable.Range(0,4).Select(_ => GL.GenTexture()).ToArray();
        int[] outputs = Enumerable.Range(0,4).Select(_ => GL.GenTexture()).ToArray();
        MakeMrt(input, inputs); MakeMrt(output, outputs);
        GL.BindVertexArray(vao); GL.Viewport(0,0,Width,1);
        GL.Disable(EnableCap.Blend); GL.Disable(EnableCap.DepthTest);
        GL.UseProgram(relight);
        GL.Uniform1(GL.GetUniformLocation(relight,"gColor"),0);
        GL.Uniform1(GL.GetUniformLocation(relight,"gGlow"),1);
        GL.Uniform1(GL.GetUniformLocation(relight,"gPositionIn"),2);
        GL.Uniform3(GL.GetUniformLocation(relight,"drtSkyColor"),1f,1f,1f);

        foreach(string file in new[]{"chunkopaque.fsh","chunktopsoil.fsh"})
        {
            string producer = """
                #version 430 core
                #define SSAOLEVEL 1
                #define SHADOWQUALITY 1
                #define NORMALVIEW 1
                in float voxSunLight;
                in float gradient;
                uniform int probeMode;
                uniform float haxyFade;
                uniform int renderFlags;
                uniform float probeAlpha;
                uniform sampler2D specularTex, terrainTex;
                const int WindModeBitMask=0x1e000000;
                const int WindModeLeavesMask=0x0a000000;
                #define drtWindPresence ((renderFlags & WindModeBitMask)!=0?1.:0.)
                vec4 gnormal=vec4(0,1,0,0), rgba=vec4(1), rgbaFog=vec4(0,0,0,.37);
                vec3 normal=vec3(0,1,0), drtVoxelLight, drtEmissionLight=vec3(0);
                vec2 uv=vec2(0), uv2=vec2(0), blockTextureSize=vec2(1);
                float specularStrength=0, alphaTest=0, blockBrightness, tl_isWind=0;
                float fogAmount=.2, glowLevel=0, drtVoxSunLight, nb=.95;
                vec3 drtTerrainSunPlaneNormal=vec3(0,1,0);
                const mat4 modelViewMatrix=mat4(1.0);
                layout(location=0) out vec4 outColor;
                layout(location=1) out vec4 outGlow;
                layout(location=2) out vec4 outGNormal;
                layout(location=3) out vec4 outGPosition;
                """ + common + File.ReadAllText(Path.Combine(lighting,"drtagx_sun_shadow_sampling.fsh")) +
                ProbeShader.Function(File.ReadAllText(Path.Combine(originals ?? shaders,file)), "void sm_deferredFill(") + """
                void main() {
                    rgbaFog.a=probeAlpha;
                    drtVoxSunLight=voxSunLight;
                    drtVoxelLight=vec3(.4,.3,.2)*(probeMode==1 ? gradient : .5);
                    blockBrightness=dot(drtVoxelLight,DRT_LUMINANCE);
                    sm_deferredFill(vec4(.75,.5,.25,probeAlpha),vec3(.75,.5,.25));
                    if(probeMode==2) {
                        // Unmarked forward pixel must keep its native coverage.
                        outColor=vec4(.2,.3,.4,.37); outGlow=vec4(.1,0,.2,.37);
                    }
                }
                """;
            int fill = ProbeShader.Program((ShaderType.VertexShader,vertex),(ShaderType.FragmentShader,producer));
            foreach(int foliage in new[]{0,1})
            foreach(int mode in new[]{0,1,2})
            {
                GL.BindFramebuffer(FramebufferTarget.Framebuffer,input);
                GL.UseProgram(fill);
                GL.Uniform1(GL.GetUniformLocation(fill,"probeMode"),mode);
                GL.Uniform1(GL.GetUniformLocation(fill,"probeAlpha"),.37f);
                GL.Uniform1(GL.GetUniformLocation(fill,"haxyFade"),(float)foliage);
                GL.Uniform1(GL.GetUniformLocation(fill,"renderFlags"),foliage==1 ? 0x0a000000 : 0);
                GL.DrawArrays(PrimitiveType.Triangles,0,3);
                GL.BindFramebuffer(FramebufferTarget.Framebuffer,output);
                for(int unit=0;unit<3;unit++) {
                    GL.ActiveTexture(TextureUnit.Texture0+unit);
                    GL.BindTexture(TextureTarget.Texture2D,inputs[unit==2 ? 3 : unit]);
                }
                GL.UseProgram(relight); GL.DrawArrays(PrimitiveType.Triangles,0,3);
                float[] rgb=new float[Width*4], glow=new float[Width*4];
                GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
                GL.ReadPixels(0,0,Width,1,PixelFormat.Rgba,PixelType.Float,rgb);
                GL.ReadBuffer(ReadBufferMode.ColorAttachment1);
                GL.ReadPixels(0,0,Width,1,PixelFormat.Rgba,PixelType.Float,glow);
                int levels=0; float last=-1, maxError=0;
                float[] raw={.75f,.5f,.25f}, block={.4f,.3f,.2f}, passthrough={.2f,.3f,.4f};
                for(int x=0;x<Width;x++) {
                    float t=(x+.5f)/Width;
                    float access=mode==0 ? t : 0;
                    float local=mode==1 ? t : .5f;
                    for(int channel=0;channel<3;channel++) {
                        float expected=mode==2 ? passthrough[channel] :
                            raw[channel]*(block[channel]*local+access*Math.Max(.15f,2f));
                        // Real half-float MRT round trips should be within a few
                        // half-float ULPs of the unquantized forward expression.
                        maxError=Math.Max(maxError,Math.Abs(rgb[x*4+channel]-expected));
                    }
                    float alpha=file=="chunkopaque.fsh" && foliage==0 && mode!=2 ? 1f : .37f;
                    Assert(Math.Abs(rgb[x*4+3]-alpha)<.001f && Math.Abs(glow[x*4+3]-alpha)<.001f,"material/glow coverage restored");
                    if(rgb[x*4]>last) levels++;
                    Assert(mode==2 || rgb[x*4]>=last,"monotonic sun/block gradient");
                    last=rgb[x*4];
                }
                Console.WriteLine($"{file}, foliage={foliage}, mode={mode}: {levels} levels, maximum linear RGB error={maxError:F6}");
                Assert(mode==2 || levels>900,$"{file}: gradient collapsed to {levels} levels");
                Assert(maxError<.003f,$"{file} mode{mode}: excessive error versus forward lighting");
            }
            if(originals==null) VerifyCoverage(file,producer,vertex,shaders,input,output);
            GL.DeleteProgram(fill);
        }
        Assert(GL.GetError()==ErrorCode.NoError,"GL error after gradient checks");
        GL.BindFramebuffer(FramebufferTarget.Framebuffer,0); GL.BindVertexArray(0);
        GL.DeleteProgram(relight); GL.DeleteFramebuffer(input); GL.DeleteFramebuffer(output); GL.DeleteVertexArray(vao);
        foreach(int texture in inputs.Concat(outputs)) GL.DeleteTexture(texture);
        Console.WriteLine("PASS production deferred gradients, forward parity, native coverage/blending and passthrough; GL NoError");
    }

    private static void VerifyCoverage(string file,string producer,string vertex,string shaders,
        int input,int output)
    {
        // Keep the historical comparison explicit; never substitute the current
        // shader for its own baseline or ship private development snapshots.
        string backup=Environment.GetEnvironmentVariable("DRTAGX_BASELINE_DIR");
        if(string.IsNullOrWhiteSpace(backup)) {
            Console.WriteLine("SKIP historical deferred coverage comparison: set DRTAGX_BASELINE_DIR.");
            return;
        }
        backup=Path.GetFullPath(backup);
        string currentFill=ProbeShader.Function(File.ReadAllText(Path.Combine(shaders,file)),"void sm_deferredFill(");
        string oldFill=ProbeShader.Function(File.ReadAllText(Path.Combine(backup,file)),"void sm_deferredFill(");
        int before=ProbeShader.Program((ShaderType.VertexShader,vertex),(ShaderType.FragmentShader,producer.Replace(currentFill,oldFill)));
        int after=ProbeShader.Program((ShaderType.VertexShader,vertex),(ShaderType.FragmentShader,producer));
        // Ensure this regression actually detects the rejected storage choice:
        // a low sun in Glow A changes blending before any later alpha restore.
        int rejected=ProbeShader.Program((ShaderType.VertexShader,vertex),(ShaderType.FragmentShader,
            producer.Replace("min(1.0, fogAmount + texColor.a)","clamp(drtVoxSunLight, 0.0, 1.0)")));
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha,BlendingFactor.OneMinusSrcAlpha);
        float[][] stable=Draw(before,input,1f,0), broken=Draw(rejected,output,1f,0);
        Assert(stable[1][3]!=broken[1][3] && stable[1][1]!=broken[1][1],"blend regression must reject sunlight in Glow A");
        GL.DeleteProgram(rejected);
        foreach(int blend in new[]{0,1,2,3})
        foreach(float alpha in new[]{0f,.37f,1f})
        foreach(int foliage in new[]{0,1})
        {
            // Blended no-cull batches use the forward shader; the real production
            // routing predicate is exercised by DecalAlphaProbe. Only cutout
            // foliage writes normal-W receiver metadata to an unblended MRT.
            if(file=="chunkopaque.fsh" && foliage==1 && blend!=0) continue;
            if(blend==0) GL.Disable(EnableCap.Blend); else GL.Enable(EnableCap.Blend);
            if(blend==1) GL.BlendFunc(BlendingFactor.SrcAlpha,BlendingFactor.OneMinusSrcAlpha);
            if(blend==2) GL.BlendFuncSeparate(BlendingFactorSrc.SrcAlpha,BlendingFactorDest.OneMinusSrcAlpha,
                BlendingFactorSrc.One,BlendingFactorDest.OneMinusSrcAlpha);
            if(blend==3) GL.BlendFunc(BlendingFactor.One,BlendingFactor.OneMinusSrcAlpha);
            GL.BlendEquation(BlendEquationMode.FuncAdd);
            float[][] a=Draw(before,input,alpha,foliage), b=Draw(after,output,alpha,foliage);
            // Coverage channels stay identical before decoding. The intentional
            // foliage normal-W payload is validated by SunFoliageProbe, including
            // half-float precision, authored shade, backlight and native SSAO sign.
            // this catches coverage-based corruption at the producing draw,
            // including source-alpha feedback and nonzero destination layers.
            for(int attachment=0;attachment<4;attachment++)
            for(int i=0;i<a[attachment].Length;i++) {
                if(attachment==1 && i%4==2) continue;
                // Static no-cull now has its own local-light tag. This metadata
                // change does not alter any alpha/coverage channel under test.
                if(file=="chunkopaque.fsh" && foliage==1 && attachment==1 && i%4==1) continue;
                if(file=="chunkopaque.fsh" && foliage==1 && attachment==2 && i%4==3) continue;
                Assert(a[attachment][i]==b[attachment][i],$"{file}: blend {blend} alpha {alpha} changed attachment {attachment} channel {i%4}");
            }
            // At full coverage, tag classification must survive every sun value.
            if(alpha==1f) for(int x=0;x<Width;x++) {
                Assert((a[1][x*4+2]>=64)==(b[1][x*4+2]>=64),"caster tags preserved through half-float blend");
                Assert(b[1][x*4+2]<128,"continuous sun never aliases forward-foliage tag");
            }
        }
        GL.Disable(EnableCap.Blend); GL.DeleteProgram(before); GL.DeleteProgram(after);
        Console.WriteLine($"PASS {file}: unchanged scene/glow coverage, markers, normal XYZ, fog/albedo; native deferred batch blend contracts and caster tags; rejects Glow-A sunlight storage");

        float[][] Draw(int program,int framebuffer,float alpha,int foliage)
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer,framebuffer);
            // Use identical nonzero destination pixels, testing actual blending
            // rather than accepting an overwrite-only, zero-background fixture.
            for(int i=0;i<4;i++) GL.ClearBuffer(ClearBuffer.Color,i,new[]{.125f,.25f,.375f,.5f});
            GL.UseProgram(program);
            GL.Uniform1(GL.GetUniformLocation(program,"probeMode"),0);
            GL.Uniform1(GL.GetUniformLocation(program,"probeAlpha"),alpha);
            GL.Uniform1(GL.GetUniformLocation(program,"haxyFade"),(float)foliage);
            GL.Uniform1(GL.GetUniformLocation(program,"renderFlags"),foliage==1 ? 0x0a000000 : 0);
            GL.DrawArrays(PrimitiveType.Triangles,0,3);
            var data=new float[4][];
            for(int i=0;i<4;i++) {
                data[i]=new float[Width*4];
                GL.ReadBuffer(ReadBufferMode.ColorAttachment0+i);
                GL.ReadPixels(0,0,Width,1,PixelFormat.Rgba,PixelType.Float,data[i]);
            }
            return data;
        }
    }

    private static void MakeMrt(int framebuffer,int[] textures)
    {
        GL.BindFramebuffer(FramebufferTarget.Framebuffer,framebuffer);
        GL.ActiveTexture(TextureUnit.Texture3);
        for(int i=0;i<4;i++) {
            GL.BindTexture(TextureTarget.Texture2D,textures[i]);
            GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.Rgba16f,Width,1,0,PixelFormat.Rgba,PixelType.Float,IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)TextureMagFilter.Nearest);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,FramebufferAttachment.ColorAttachment0+i,TextureTarget.Texture2D,textures[i],0);
        }
        GL.DrawBuffers(4,new[]{DrawBuffersEnum.ColorAttachment0,DrawBuffersEnum.ColorAttachment1,DrawBuffersEnum.ColorAttachment2,DrawBuffersEnum.ColorAttachment3});
        Assert(GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer)==FramebufferErrorCode.FramebufferComplete,"gradient MRT framebuffer");
    }

    private static void Assert(bool condition,string message) { if(!condition) throw new Exception(message); }
}
