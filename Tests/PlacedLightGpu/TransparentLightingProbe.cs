using System;
using System.IO;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;

internal static class TransparentLightingProbe
{
    // Execute the maintained transparent main and native OIT writer. Fog and
    // material effects are neutral so readback measures lighting/coverage only;
    // the poisoned legacy RGB case verifies that it cannot bypass the light data.
    internal static void Run(string deferredPath)
    {
        using var state = new ShadowGlState();
        string assets = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(deferredPath)!, "../.."));
        string lighting = Path.Combine(assets,"drtagx/shaders/lighting");
        string vertexFog = File.ReadAllText(Path.Combine(assets,"game/shaders/fogandlight.vsh"));
        string fragmentFog = File.ReadAllText(Path.Combine(assets,"game/shaders/fogandlight.fsh"));
        string transparent = File.ReadAllText(Path.Combine(assets,"game/shaders/chunktransparent.fsh"));
        string surface = File.ReadAllText(Path.Combine(lighting,"drtagx_surface_lighting.fsh"))
            .Replace("#include drtagx_fog_transport.ash","") // Neutral transport stub; real ambient UBO is exercised separately.
            .Replace("#include drtagx_light_balance.ash","").Replace("in vec3 drt","vec3 drt");
        string fragment = """
            #version 330 core
            #define DYNLIGHTS 0
            #define SHADOWQUALITY 0
            #define SHINYEFFECT 0
            #define USEOIT 1
            #define DRT_ALBEDO_SURFACE_LIGHTING
            #define DRT_SURFACE_FOG_AFTER_LIGHTING
            const int GlowLevelBitMask = 255;
            float glowLevel;
            vec3 blockLight;
            float sm_voxSunLight;
            float drtVertexFogSunlight;
            const vec3 drtPlacedRestPos=vec3(0);
            const float drtWindPresence=0;
            vec4 rgba;
            vec4 rgbaFog = vec4(0.0);
            float fogAmount = 0.0;
            vec2 uv = vec2(0.5);
            vec4 worldPos = vec4(0.0);
            vec3 vertexPos = vec3(0.0);
            vec3 normal = vec3(0.0, 1.0, 0.0);
            vec3 lightPosition = vec3(0.0, 1.0, 0.0);
            float normalShadeIntensity = 1.0;
            float shadowIntensity = 0.0;
            float glitchStrengthFL = 0.0;
            float nightVisionStrength = 0.0;
            float psychedelicStrength = 0.0;
            float Epsilon = 0.02;
            uniform sampler2D terrainTex;
            uniform vec3 sky;
            uniform vec4 light;
            uniform int renderFlags;
            uniform float vertexAlpha;
            uniform int legacyWhite;
            vec4 getColorMapped(sampler2D t, vec4 c) { return c; }
            vec4 applyPsychedelicEffect(vec4 c, vec3 p, int sub) { return c; }
            float getUnderwaterMurkiness() { return 0.0; }
            vec3 applyUnderwaterEffects(vec3 c, float m) { return c; }
            float getBrightnessFromShadowMap() { return 1.0; }
            void ox_prepare(vec3 n, float b) { }
            void ox_apply(inout vec3 c, vec3 p) { }
            vec4 applyFog(vec4 c, float a) { return c; }
            vec4 applySpheresFog(vec4 c, float a, vec3 p) { return c; }
            vec4 applyWorldFog(vec4 c, vec3 p) { return c; }
            vec4 drtApplySurfaceFog(vec4 c, vec3 p, float d, bool boundary, float sun) { return c; }
            void drtCaptureTerrainPlacedPlane(vec3 p) { }
            void drtPrepareTerrainPlacedVisibility(vec3 p, bool foliage, float glow) { }
            void drtPrepareSunGrid(vec3 p, vec3 n) { }
            // This fixture isolates lighting with a near receiver. The real
            // boundary discard/depth behavior is checked by HorizonCompositionProbe.
            void drtDiscardTerrainBoundary(vec3 p) { }
            """ + File.ReadAllText(Path.Combine(lighting,"drtagx_light_balance.ash")) + "\n#define DRT_FOG_TRANSPORT\n" + surface +
            ProbeShader.Function(vertexFog,"vec4 getPointLightRgbv(") +
            ProbeShader.Function(vertexFog,"vec4 applyLightWithoutPointLight(") +
            ProbeShader.Function(vertexFog,"vec4 applyLightWithNormal(") +
            ProbeShader.Function(fragmentFog,"float getBrightnessFromNormal(") +
            ProbeShader.Function(fragmentFog,"vec4 applyFogAndShadowFromBrightness(") +
            ProbeShader.Function(fragmentFog,"vec4 applyFogAndShadowWithNormal(") + "\n" +
            File.ReadAllText(Path.Combine(Environment.GetEnvironmentVariable("VINTAGE_STORY")!,"assets/game/shaderincludes/oit.fsh")) + "\n" +
            ProbeShader.Function(transparent,"void main()").Replace("void main()","void transparentMain()") + """
            void main() {
                rgba = applyLightWithNormal(sky, light, renderFlags, worldPos, normal);
                rgba.a = vertexAlpha;
                if (legacyWhite != 0) rgba.rgb = vec3(1.0);
                transparentMain();
            }
            """;
        string vertex="#version 330 core\nvoid main(){vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);gl_Position=vec4(p*2.0-1.0,0,1);}";
        int program=ProbeShader.Program((ShaderType.VertexShader,vertex),(ShaderType.FragmentShader,fragment));
        int fbo=GL.GenFramebuffer(),vao=GL.GenVertexArray(),texture=GL.GenTexture();
        int[] outputs=new int[6]; GL.GenTextures(6,outputs);
        int previous=GL.GetInteger(GetPName.CurrentProgram);
        try
        {
            GL.ActiveTexture(TextureUnit.Texture14); GL.BindSampler(14,0);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer,fbo);
            var drawBuffers=new DrawBuffersEnum[6];
            for(int i=0;i<6;++i) {
                GL.BindTexture(TextureTarget.Texture2D,outputs[i]);
                GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.Rgba32f,1,1,0,PixelFormat.Rgba,PixelType.Float,IntPtr.Zero);
                GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,FramebufferAttachment.ColorAttachment0+i,TextureTarget.Texture2D,outputs[i],0);
                drawBuffers[i]=DrawBuffersEnum.ColorAttachment0+i;
            }
            GL.DrawBuffers(6,drawBuffers); GL.ReadBuffer(ReadBufferMode.ColorAttachment3);
            GL.BindTexture(TextureTarget.Texture2D,texture);
            float[] material=[0.8f,0.4f,0.2f,0.4f];
            GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.Rgba32f,1,1,0,PixelFormat.Rgba,PixelType.Float,material);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)TextureMagFilter.Nearest);
            GL.UseProgram(program); GL.Uniform1(GL.GetUniformLocation(program,"terrainTex"),14);
            GL.Uniform2(GL.GetUniformLocation(program,"drtSunlightBounds"),0f,1f);
            GL.BindVertexArray(vao); GL.Viewport(0,0,1,1);
            GL.Disable(EnableCap.DepthTest); GL.Disable(EnableCap.Blend); GL.Disable(EnableCap.ScissorTest); GL.ColorMask(true,true,true,true);
            foreach(var input in new[]{(1f,1f,0f,0),(0.01f,1f,0f,0),(0f,1f,0f,0),(0f,0f,0f,0),(0f,0f,0.5f,0),(0f,0f,0f,64)})
            foreach(float alpha in new[]{1f,0.5f}) foreach(int legacyWhite in new[]{0,1}) {
                GL.Uniform3(GL.GetUniformLocation(program,"sky"),input.Item1,input.Item1,input.Item1);
                GL.Uniform4(GL.GetUniformLocation(program,"light"),input.Item3,input.Item3*0.5f,0f,input.Item2);
                GL.Uniform1(GL.GetUniformLocation(program,"renderFlags"),input.Item4);
                GL.Uniform1(GL.GetUniformLocation(program,"vertexAlpha"),alpha);
                GL.Uniform1(GL.GetUniformLocation(program,"legacyWhite"),legacyWhite);
                GL.DrawArrays(PrimitiveType.Triangles,0,3);
                float[] pixel=new float[4]; GL.ReadPixels(0,0,1,1,PixelFormat.Rgba,PixelType.Float,pixel);
                // The RGB night floor is a sky multiplier, still masked by
                // access and texture albedo before native OIT accumulation.
                float skyRadiance=2*input.Item1,emission=4*input.Item4/256f;
                float[] expected=[material[0]*(input.Item2*MathF.Max(skyRadiance,0.05f)+emission+input.Item3),
                    material[1]*(input.Item2*MathF.Max(skyRadiance,0.1f)+emission+input.Item3*0.5f),
                    material[2]*(input.Item2*MathF.Max(skyRadiance,0.15f)+emission)];
                for(int c=0;c<3;++c) if(!float.IsFinite(pixel[c]) || Math.Abs(pixel[c]/pixel[3]-expected[c])>2e-5)
                    throw new Exception($"Transparent OIT light/alpha mismatch: {input}, channel {c}, {pixel[c]/pixel[3]} != {expected[c]}");
                GL.ReadBuffer(ReadBufferMode.ColorAttachment1); float[] reveal=new float[4];
                GL.ReadPixels(0,0,1,1,PixelFormat.Rgba,PixelType.Float,reveal); GL.ReadBuffer(ReadBufferMode.ColorAttachment3);
                if(Math.Abs(reveal[0]-(1-material[3]*alpha))>2e-5 || GL.GetError()!=ErrorCode.NoError)
                    throw new Exception("Transparent OIT changed native coverage or GL state");
                Console.WriteLine($"PASS transparent native OIT: sky/access/block/glow={input}, alpha={alpha}, legacyWhite={legacyWhite}");
            }
        }
        finally { GL.UseProgram(previous); GL.DeleteProgram(program); GL.DeleteFramebuffer(fbo); GL.DeleteTexture(texture); GL.DeleteTextures(6,outputs); GL.DeleteVertexArray(vao); }
    }
}
