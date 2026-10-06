using System;
using System.IO;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;

internal static class SunShadowProbe
{
    internal static void RunCascadeCoverage(string assets, string capture)
    {
        // Execute the actual native-forward and deferred coverage snippets.
        // This tests map validity and blend continuity independently of PCF.
        using var state = new ShadowGlState();
        string shadowFile = Path.Combine(assets, "game/shaders/shadowcoords.vsh");
        if (!File.Exists(shadowFile)) shadowFile = Path.Combine(Environment.GetEnvironmentVariable("VINTAGE_STORY")!, "assets/game/shaderincludes/shadowcoords.vsh");
        string forward = File.ReadAllText(shadowFile);
        string deferred = File.ReadAllText(Path.Combine(assets, "drtagx/shaders/deferred/drtagx_deferred_directional.fsh"));
        string body = ProbeShader.Function(deferred, "float deferredShadowBrightness");
        body = body[..body.IndexOf("    // Match forward PCF", StringComparison.Ordinal)];
        body = body.Replace("float deferredShadowBrightness(vec4 worldPos, float blockBright, float receiverBiasScale)", "vec2 probeDeferred(vec4 worldPos)") + "return vec2(nearSub, scFar.w);\n}";
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(capture));
        var native = json.RootElement;
        float[] near = new float[16], far = new float[16];
        for (int i=0;i<16;++i) { near[i]=native.GetProperty("Chunkopaque/toShadowMapSpaceMatrixNear")[i].GetSingle(); far[i]=native.GetProperty("Chunkopaque/toShadowMapSpaceMatrixFar")[i].GetSingle(); }
        float nearRange=native.GetProperty("Native/ShadowRangeNear").GetSingle(), farRange=native.GetProperty("Native/ShadowRangeFar").GetSingle();
        bool retained = forward.Contains("len / shadowRangeNear - 0.5",StringComparison.Ordinal);
        if (retained != deferred.Contains("len / shadowRangeNear - 0.5",StringComparison.Ordinal)) throw new Exception("Forward/deferred near policy mismatch");
        float fadeStart=retained?.5f:.15f;
        string fragment = "#version 430 core\n#define SHADOWQUALITY 3\n" + forward.Replace("out vec4", "vec4") + body + "\nuniform vec3 receiver; layout(location=0) out vec4 result; void main(){calcShadowMapCoords(mat4(1),vec4(receiver,1));result=vec4(shadowCoordsNear.w,shadowCoordsFar.w,probeDeferred(vec4(receiver,1)));}";
        string vertex = "#version 430 core\nvoid main(){vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);gl_Position=vec4(p*2-1,0,1);}";
        int program=ProbeShader.Program((ShaderType.VertexShader,vertex),(ShaderType.FragmentShader,fragment));
        int target=GL.GenTexture(),fbo=GL.GenFramebuffer(),vao=GL.GenVertexArray();
        try
        {
            GL.BindTexture(TextureTarget.Texture2D,target); GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.Rgba32f,1,1,0,PixelFormat.Rgba,PixelType.Float,IntPtr.Zero);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer,fbo); GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,FramebufferAttachment.ColorAttachment0,TextureTarget.Texture2D,target,0);
            GL.DrawBuffer(DrawBufferMode.ColorAttachment0); GL.ReadBuffer(ReadBufferMode.ColorAttachment0); GL.Viewport(0,0,1,1);
            GL.Disable(EnableCap.DepthTest); GL.Disable(EnableCap.Blend); GL.Disable(EnableCap.ScissorTest); GL.ColorMask(true,true,true,true); GL.BindVertexArray(vao); GL.UseProgram(program);
            GL.UniformMatrix4(GL.GetUniformLocation(program,"toShadowMapSpaceMatrixNear"),1,false,near); GL.UniformMatrix4(GL.GetUniformLocation(program,"toShadowMapSpaceMatrixFar"),1,false,far);
            GL.Uniform1(GL.GetUniformLocation(program,"shadowRangeNear"),nearRange); GL.Uniform1(GL.GetUniformLocation(program,"shadowRangeFar"),farRange);
            float[] pixel=new float[4]; int checks=0;
            double[] Coord(float[] m,double x,double y,double z) => new[]{m[0]*x+m[4]*y+m[8]*z+m[12],m[1]*x+m[5]*y+m[9]*z+m[13],m[2]*x+m[6]*y+m[10]*z+m[14]};
            double Edge(double[] c,double scale) => Math.Max(0,.03-c[0])*scale+Math.Max(0,c[0]-.97)*scale+Math.Max(0,.03-c[1])*scale+Math.Max(0,c[1]-.97)*scale+Math.Max(0,c[2]-.98)*scale;
            double Clamp(double v) => Math.Clamp(v,0,1);
            foreach (double radius in new[]{0d,1,5.4,8,16,17.999,18,18.001,24,32,36,41.4,54,64,128,256,390})
            for (int direction=0;direction<32;++direction)
            {
                double angle=direction*Math.PI/16,x=radius*Math.Cos(angle),z=radius*Math.Sin(angle),y=direction%3-1;
                GL.Uniform3(GL.GetUniformLocation(program,"receiver"),(float)x,(float)y,(float)z); GL.DrawArrays(PrimitiveType.Triangles,0,3); GL.ReadPixels(0,0,1,1,PixelFormat.Rgba,PixelType.Float,pixel);
                double[] nc=Coord(near,x,y,z),fc=Coord(far,x,y,z);
                for(int path=0;path<2;++path)
                {
                    // Native's longstanding vec4 length includes homogeneous W;
                    // deferred uses XYZ. Preserve and verify each independently.
                    double len=Math.Sqrt(x*x+y*y+z*z+(path==0?1:0));
                    double nw=nc[2]>=.999?0:1-Clamp(Edge(nc,100)+Math.Max(0,len/nearRange-fadeStart));
                    double fw=fc[2]>=.999?0:Math.Max(0,Clamp(1-(Clamp(Edge(fc,10)+Math.Max(0,len/farRange-.15))*2-.5))-nw);
                    if(Math.Abs(pixel[path*2]-nw)>2e-5 || Math.Abs(pixel[path*2+1]-fw)>2e-5 || pixel[path*2]+pixel[path*2+1]>1.00001) throw new Exception("Sun coverage/reference failure");
                    ++checks;
                }
                // Account for the native vec4-length ABI with an independent
                // Lipschitz bound; do not silently change it to deferred XYZ.
                double distance3=Math.Sqrt(x*x+y*y+z*z);
                double delta=Math.Sqrt(distance3*distance3+1)-distance3;
                double agreement=delta/nearRange+2*delta/farRange+2e-5;
                if(Math.Abs(pixel[0]-pixel[2])>agreement || Math.Abs(pixel[1]-pixel[3])>agreement) throw new Exception("Sun forward/deferred transition mismatch");
            }
            if(GL.GetError()!=ErrorCode.NoError) throw new Exception("Sun cascade GL error");
            Console.WriteLine($"PASS sun cascade coverage: {checks} captured near/middle/far, split-adjacent and map-edge GPU comparisons; normalized weights and forward/deferred agreement");
        }
        finally { GL.DeleteProgram(program); GL.DeleteFramebuffer(fbo); GL.DeleteTexture(target); GL.DeleteVertexArray(vao); }
    }

    internal static void Run(string deferredPath)
    {
        using var state = new ShadowGlState();
        string folder = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(deferredPath)!, "../../drtagx/shaders/lighting"));
        string fragment = """
            #version 330 core
            uniform sampler2DShadow depthMap;
            uniform vec2 slope;
            uniform float precisionBias;
            uniform int mode;
            uniform vec3 gridReceiver, gridNormal, gridPhase;
            out vec4 color;
            """ + File.ReadAllText(Path.Combine(folder, "drtagx_sun_shadow_sampling.fsh")) + """
            void main() {
                if (mode == 6) {
                    vec3 offset = drtSunGridOffset(gridReceiver, gridNormal, gridPhase);
                    color = vec4(gridReceiver + gridPhase + offset, dot(offset, gridNormal));
                    return;
                }
                vec2 uv = vec2(0.5) + gl_FragCoord.xy * 0.0001;
                if (mode == 2) uv = vec2(0.5); // Singular projected derivatives.
                vec3 coord = vec3(uv, 0.5 + dot(slope, uv - 0.5));
                // Background lanes beside a thin grass blade reconstruct unrelated depth.
                if (mode == 3 || mode == 4) coord.z += mod(floor(gl_FragCoord.x), 2.0) * (mode == 3 ? 0.25 : 0.1);
                vec2 gradient = mode >= 3 ? drtSunPlaneGradient(mat4(1.0), vec3(-slope, 1.0)) : drtSunReceiverGradient(coord);
                float visibility = drtSunShadowVisibility(depthMap, coord, gradient, precisionBias, mode >= 3 ? 4.0 : 1.0);
                if (mode == 1) {
                    visibility = 0.0;
                    vec2 texel = 1.0 / vec2(textureSize(depthMap, 0));
                    for (int y = -1; y <= 1; ++y) for (int x = -1; x <= 1; ++x)
                        visibility += texture(depthMap, vec3(uv + vec2(x,y) * texel, coord.z - 0.00045));
                    visibility /= 9.0;
                }
                color = vec4(visibility, gradient, 1.0);
            }
            """;
        string vertex = "#version 330 core\nvoid main(){vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);gl_Position=vec4(p*2.0-1.0,0,1);}";
        int program = ProbeShader.Program((ShaderType.VertexShader, vertex), (ShaderType.FragmentShader, fragment));
        int depth = GL.GenTexture(), output = GL.GenTexture(), fbo = GL.GenFramebuffer(), vao = GL.GenVertexArray();
        int previous = GL.GetInteger(GetPName.CurrentProgram);
        try
        {
            GL.ActiveTexture(TextureUnit.Texture14); GL.BindSampler(14, 0);
            GL.BindTexture(TextureTarget.Texture2D, output);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba32f, 4, 4, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, output, 0);
            GL.DrawBuffer(DrawBufferMode.ColorAttachment0); GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
            GL.BindTexture(TextureTarget.Texture2D, depth);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareMode, (int)TextureCompareMode.CompareRefToTexture);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareFunc, (int)DepthFunction.Lequal);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.UseProgram(program); GL.Uniform1(GL.GetUniformLocation(program, "depthMap"), 14);
            GL.BindVertexArray(vao); GL.Viewport(0, 0, 4, 4);
            GL.Disable(EnableCap.DepthTest); GL.Disable(EnableCap.Blend); GL.Disable(EnableCap.ScissorTest); GL.ColorMask(true,true,true,true);
            float[] data = new float[128*128];
            void Plane(float sx, float sy, float gap)
            {
                for (int y=0;y<128;++y) for(int x=0;x<128;++x)
                    data[y*128+x] = 0.5f + sx*((x+0.5f)/128-0.5f) + sy*((y+0.5f)/128-0.5f) - gap;
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.DepthComponent24, 128,128,0,PixelFormat.DepthComponent,PixelType.Float,data);
                GL.Uniform2(GL.GetUniformLocation(program,"slope"),sx,sy);
            }
            float[] Draw(int mode)
            {
                GL.Uniform1(GL.GetUniformLocation(program,"mode"),mode); GL.DrawArrays(PrimitiveType.Triangles,0,3);
                float[] result=new float[4]; GL.ReadPixels(1,1,1,1,PixelFormat.Rgba,PixelType.Float,result);
                if (GL.GetError()!=ErrorCode.NoError) throw new Exception("Sun shadow probe GL error");
                foreach(float value in result) if(!float.IsFinite(value)) throw new Exception("Nonfinite sun shadow result");
                return result;
            }
            void Check(bool valid,string name) { if(!valid) throw new Exception(name); Console.WriteLine("PASS "+name); }
            foreach(float bias in new[]{0.00003f,0.00002f})
            {
                GL.Uniform1(GL.GetUniformLocation(program,"precisionBias"),bias);
                Plane(0,0,0.0001f);
                Check(Draw(1)[0]>0.999f && Draw(0)[0]<0.001f,"sun: nearby caster is retained instead of disappearing under fixed bias");
                foreach(var slope in new[]{(0f,0f),(0.03f,0.01f),(-0.12f,0.07f),(0.4f,-0.3f)})
                {
                    Plane(slope.Item1,slope.Item2,0);
                    float[] pixel=Draw(0);
                    Check(pixel[0]>0.999f && Math.Abs(pixel[1]-slope.Item1)<0.0005 && Math.Abs(pixel[2]-slope.Item2)<0.0005,
                        "sun: sloped coplanar receiver stays lit with bilinear PCF");
                    float footprint=(Math.Abs(slope.Item1)+Math.Abs(slope.Item2))/128;
                    Plane(slope.Item1,slope.Item2,2*footprint+2*bias);
                    Check(Draw(0)[0]<0.001f,"sun: sloped occluder remains shadowed beyond one-texel footprint");
                }
            }
            Plane(0,0,0.001f);
            Check(Draw(3)[0]<0.001f && Draw(4)[0]<0.001f,
                "sun: grass blade remains shadowed across changing background helper depth");
            // Vary the card's real plane with wind. Precision-only grass bias
            // cannot keep a sloped coplanar receiver lit as that plane changes.
            foreach(var slope in new[]{(.01f,.02f),(.12f,-.07f),(.4f,-.3f),(-.7f,.2f)}) {
                Plane(slope.Item1,slope.Item2,0);
                Check(Draw(5)[0]>.999f,"sun: wind-bent grass retains coplanar visibility");
                float footprint=(Math.Abs(slope.Item1)+Math.Abs(slope.Item2))/128;
                Plane(slope.Item1,slope.Item2,2*footprint+.001f);
                Check(Draw(5)[0]<.001f,"sun: wind-bent grass retains an external blocker");
            }
            Plane(0,0,0); float[] singular=Draw(2);
            Check(singular[0]>0.999f && singular[1]==0 && singular[2]==0,"sun: degenerate projection has finite precision-only fallback");
            GL.Uniform3(GL.GetUniformLocation(program,"gridNormal"),0f,1f,0f);
            GL.Uniform3(GL.GetUniformLocation(program,"gridReceiver"),.2f,.7f,.3f);
            GL.Uniform3(GL.GetUniformLocation(program,"gridPhase"),.1383667f,.69921875f,.2911987f);
            float[] grid=Draw(6);
            Check(Math.Abs(grid[0]-.328125f)<1e-6 && Math.Abs(grid[2]-.578125f)<1e-6 &&
                Math.Abs(grid[1]-1.39921875f)<1e-6 && Math.Abs(grid[3])<1e-6,
                "sun grid: 1/32-block centres preserve receiver depth");
            GL.Uniform3(GL.GetUniformLocation(program,"gridReceiver"),.19f,.7f,.29f);
            GL.Uniform3(GL.GetUniformLocation(program,"gridPhase"),.1483667f,.69921875f,.3011987f);
            float[] moved=Draw(6);
            Check(Math.Abs(moved[0]-grid[0])<1e-6 && Math.Abs(moved[2]-grid[2])<1e-6,
                "sun grid: camera motion preserves the world cell at large coordinates");
        }
        finally { GL.UseProgram(previous); GL.DeleteProgram(program); GL.DeleteFramebuffer(fbo); GL.DeleteTexture(depth); GL.DeleteTexture(output); GL.DeleteVertexArray(vao); }
    }
}
