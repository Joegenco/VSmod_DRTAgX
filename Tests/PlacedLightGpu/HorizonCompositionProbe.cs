using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;

/// <summary>Continuous boundary color, solid depth and production sky/celestial math in HDR.</summary>
internal static class HorizonCompositionProbe
{
    private const string Vertex = "#version 430 core\nvoid main(){gl_Position=vec4(gl_VertexID==1?3:-1,gl_VertexID==2?3:-1,0,1);}";

    internal static void Run(string directory)
    {
        var seen = new HashSet<string>();
        string Expand(string name) => string.Join('\n', File.ReadAllLines(Path.Combine(directory, name)).Select(line =>
            line.Trim().StartsWith("#include ") ? (seen.Add(line.Trim()[9..]) ? Expand(line.Trim()[9..]) : "") : line));
        seen.Add("drtagx_atmospheric_sky.fsh");
        string shared = Expand("drtagx_atmospheric_sky.fsh");
        string boundary = File.ReadAllText(Path.Combine(directory, "drtagx_terrain_boundary.fsh"));
        int coverage = ProbeShader.Program((ShaderType.VertexShader, Vertex), (ShaderType.FragmentShader,
            "#version 430 core\n" + shared + boundary + """
            uniform float distance; uniform int layer;
            out vec4 result;
            void main(){
                if(layer!=2) drtDiscardTerrainBoundary(drtAtmosphereCamera.xyz+vec3(distance,0,0));
                // Opaque terrain, deeper soil, then a still farther cloud.
                gl_FragDepth=layer==0?.25:layer==1?.4:.75;
                result=layer==0?vec4(1,0,0,1):layer==1?vec4(0,2,0,1):vec4(0,0,3,1);
                if(layer!=2) result=drtApplyAirFog(result,drtAtmosphereCamera.xyz+vec3(distance,0,0),true);
            }
            """));
        int diagnostic = ProbeShader.Program((ShaderType.VertexShader, Vertex), (ShaderType.FragmentShader,
            "#version 430 core\n" + shared + """
            out vec4 result;
            void main(){result=vec4(drtClearSkyRadiance(vec3(0,1,0)),drtStarVisibility());}
            """));
        int ambient = ProbeShader.Program((ShaderType.VertexShader, Vertex), (ShaderType.FragmentShader,
            "#version 430 core\n#define DRT_AMBIENT_SKY_SAMPLING\n" + shared + """
            out vec4 result;
            void main(){result=vec4(drtClearSkyRadiance(vec3(0,1,0)),drtStarVisibility());}
            """));
        int background = ProbeShader.Program((ShaderType.VertexShader,Vertex),(ShaderType.FragmentShader,
            "#version 430 core\n"+shared+"out vec4 result;void main(){result=vec4(drtSkyBackground(vec3(0,1,0)),1);}"));
        string night = File.ReadAllText(Path.GetFullPath(Path.Combine(directory,"../../../game/shaders/nightsky.fsh")));
        int stars = ProbeShader.Program((ShaderType.VertexShader,Vertex),(ShaderType.FragmentShader,
            "#version 430 core\n"+shared+"""
            uniform samplerCube ctex; uniform float dayLight;
            const vec3 texCoords=vec3(1,0,0); const vec3 drtStarDirection=vec3(0,1,0);
            const float nightVisionStrengthv=0;
            out vec4 outColor;
            """+ProbeShader.Function(night,"void main (").Replace("void main (","void nativeNightBase (")+"""
            void main(){
                nativeNightBase();vec3 base=outColor.rgb;vec4 c,g;
                getSkyColorAt(drtStarDirection,vec3(1,0,0),0,dayLight,0,c,g);
                vec3 overlay=drtSkyOverlayRadiance(c.rgb,drtStarDirection,dayLight,c.a);
                outColor=vec4(overlay*c.a+base*(1-c.a),outColor.a);
            }
            """));
        string water = Expand("drtagx_water_transport.fsh");
        string celestial = File.ReadAllText(Path.GetFullPath(Path.Combine(directory,"../../../game/shaders/celestialobject.fsh")));
        string main = ProbeShader.Function(celestial, "void main (");
        string spriteSource = "#version 430 core\n#define SSAOLEVEL 0\n" + shared + """
            const float zNear=.1; const float zFar=10000;
            vec4 applySpheresFog(vec4 c,float f,vec3 p){return c;}
            vec4 applyFog(vec4 c,float f){return c;}
            """ + water + File.ReadAllText(Path.Combine(directory,"drtagx_celestial_balance.ash")) + """
            uniform sampler2D tex; uniform vec2 uv; uniform vec4 color;
            uniform vec3 vertexPosition; uniform vec3 sunPosition; uniform vec3 moonPosition;
            uniform int weirdMathToMakeMoonLookNicer;
            uniform float moonSunAngle; uniform float dayLight;
            const float alphaTest=.001; const float extraGodray=0;
            const float horizonFog=0; const float glowLevel=1;
            layout(location=0) out vec4 outColor; layout(location=1) out vec4 outGlow;
            """ + main;
        int sprite = ProbeShader.Program((ShaderType.VertexShader, Vertex), (ShaderType.FragmentShader, spriteSource));

        int lut = Texture(0, .3f, .4f, .5f, 1);
        int tex = Texture(2, 1, 1, 1, 1);
        int cube=GL.GenTexture();GL.ActiveTexture(TextureUnit.Texture4);GL.BindTexture(TextureTarget.TextureCubeMap,cube);
        GL.TexParameter(TextureTarget.TextureCubeMap,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.TextureCubeMap,TextureParameterName.TextureMagFilter,(int)TextureMagFilter.Nearest);
        int target = GL.GenTexture(); GL.BindTexture(TextureTarget.Texture2D, target);
        GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.Rgba16f,64,64,0,PixelFormat.Rgba,PixelType.Float,IntPtr.Zero);
        int depth = GL.GenRenderbuffer(); GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer,depth);
        GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer,RenderbufferStorage.DepthComponent24,64,64);
        int fbo = GL.GenFramebuffer(); GL.BindFramebuffer(FramebufferTarget.Framebuffer,fbo);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,FramebufferAttachment.ColorAttachment0,TextureTarget.Texture2D,target,0);
        GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer,FramebufferAttachment.DepthAttachment,RenderbufferTarget.Renderbuffer,depth);
        GL.DrawBuffer(DrawBufferMode.ColorAttachment0); GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
        Check(GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer)==FramebufferErrorCode.FramebufferComplete,"HDR/depth fixture complete");
        int vao=GL.GenVertexArray();GL.BindVertexArray(vao);GL.Viewport(0,0,64,64);
        GL.Disable(EnableCap.Blend);GL.Disable(EnableCap.ScissorTest);GL.Disable(EnableCap.CullFace);
        GL.ColorMask(true,true,true,true);GL.DepthMask(true);GL.DepthFunc(DepthFunction.Less);
        var frame = new float[AtmosphereRenderer.FrameFloatCount];
        frame[0]=.1f;frame[1]=.12f;frame[2]=.14f;frame[3]=1;frame[8]=512;frame[9]=200;
        frame[13]=1;frame[24]=1;frame[27]=1;
        int buffer=GL.GenBuffer();GL.BindBuffer(BufferTarget.UniformBuffer,buffer);
        GL.BufferData(BufferTarget.UniformBuffer,frame.Length*4,frame,BufferUsageHint.DynamicDraw);
        GL.BindBufferBase(BufferRangeTarget.UniformBuffer,10,buffer);
        foreach(int program in new[]{coverage,diagnostic,ambient,sprite,background,stars}) {
            GL.UseProgram(program);GL.UniformBlockBinding(program,GL.GetUniformBlockIndex(program,"DrtAtmosphere"),10);
            GL.Uniform1(GL.GetUniformLocation(program,"drtSkyViewPrevious"),0);
            GL.Uniform1(GL.GetUniformLocation(program,"drtSkyViewCurrent"),0);
            GL.Uniform1(GL.GetUniformLocation(program,"drtFogVolume"),1);
        }
        GL.UseProgram(stars);GL.Uniform1(GL.GetUniformLocation(stars,"ctex"),4);
        // A deliberately delayed native clock must not suppress sunset stars.
        GL.Uniform1(GL.GetUniformLocation(stars,"dayLight"),1f);
        GL.UseProgram(sprite);GL.Uniform1(GL.GetUniformLocation(sprite,"tex"),2);
        GL.Uniform1(GL.GetUniformLocation(sprite,"liquidDepth"),3);
        GL.Uniform2(GL.GetUniformLocation(sprite,"frameSize"),64f,64f);
        GL.Uniform3(GL.GetUniformLocation(sprite,"vertexPosition"),0f,1f,0f);
        GL.Uniform4(GL.GetUniformLocation(sprite,"color"),1f,1f,1f,1f);
        GL.Uniform2(GL.GetUniformLocation(sprite,"uv"),.5f,.5f);
        GL.Uniform3(GL.GetUniformLocation(sprite,"moonPosition"),0f,1f,0f);

        void Upload(){GL.BindBuffer(BufferTarget.UniformBuffer,buffer);GL.BufferSubData(BufferTarget.UniformBuffer,IntPtr.Zero,frame.Length*4,frame);}
        float[] Draw(int program) {
            Upload();GL.Disable(EnableCap.DepthTest);GL.UseProgram(program);
            GL.ActiveTexture(TextureUnit.Texture0);GL.BindTexture(TextureTarget.Texture2D,lut);
            GL.ActiveTexture(TextureUnit.Texture2);GL.BindTexture(TextureTarget.Texture2D,tex);
            GL.DrawArrays(PrimitiveType.Triangles,0,3);
            var pixel=new float[4];GL.ReadPixels(0,0,1,1,PixelFormat.Rgba,PixelType.Float,pixel);return pixel;
        }
        // The fade is continuous RGB with solid depth, rather than spatial holes.
        // Respect the current user-authored fog band while keeping analytical
        // fourth-power transmission and depth/coverage checks independent.
        var band = System.Text.RegularExpressions.Regex.Match(shared,
            @"smoothstep\(([0-9.]+) \* distance, ([0-9.]+) \* distance, horizontalDistance\)");
        Check(band.Success, "authored horizon band is discoverable");
        float startBand = float.Parse(band.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        float endBand = float.Parse(band.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        // Clouds pass the depth test only at/beyond the fully concealed endpoint.
        var boundaryBackground=Draw(background);
        foreach(float range in new[]{128f,256f,512f,1024f}) {
            frame[8]=range;Upload();GL.Enable(EnableCap.DepthTest);GL.UseProgram(coverage);
            float finish=endBand*range,start=startBand*range;
            foreach(var (distance,boundaryTransmission) in new[]{(start,1f),((start+finish)/2,.0625f),(finish,0f)}) {
                GL.ClearDepth(1);GL.Clear(ClearBufferMask.DepthBufferBit|ClearBufferMask.ColorBufferBit);
                foreach(int layer in new[]{0,1,2}) {
                    GL.Uniform1(GL.GetUniformLocation(coverage,"distance"),distance+(layer==1?1:0));
                    GL.Uniform1(GL.GetUniformLocation(coverage,"layer"),layer);GL.DrawArrays(PrimitiveType.Triangles,0,3);
                }
                var pixels=new float[64*64*4];GL.ReadPixels(0,0,64,64,PixelFormat.Rgba,PixelType.Float,pixels);
                for(int i=0;i<pixels.Length;i+=4) {
                    for(int c=0;c<4;c++)Check(pixels[i+c]==pixels[c],"boundary has no per-pixel grain or depth holes");
                }
                if(boundaryTransmission==0) {
                    Near(pixels[0],0,"fully hidden endpoint releases depth");Near(pixels[2],3,"cloud fills concealed endpoint");
                } else {
                    for(int c=0;c<3;c++)Near(pixels[c],(c==0?boundaryTransmission:0)+boundaryBackground[c]*(1-boundaryTransmission),
                        "uniform color fade with solid terrain depth throughout authored band");
                }
            }
        }
        Console.WriteLine("PASS smooth authored fourth-power boundary at four view distances: no grain, solid depth during fade, endpoint releases clouds");

        frame[8]=512;
        foreach(float elevation in new[]{90f,10f,0f,-.5f,-1f,-3f,-6f,-30f}) {
            frame[13]=MathF.Sin(elevation*MathF.PI/180);
            var display=Draw(diagnostic);var surface=Draw(ambient);
            float ratio=elevation>=10?2:1;
            for(int c=0;c<3;c++)Near(display[c],surface[c]*ratio,"sky-only daytime gain leaves ambient source unchanged");
            if(elevation>=0)Check(display[3]==0,"day has no stars");
            if(elevation==-.5f)Check(display[3]>.13f,"stars start just after sunset");
            if(elevation<=-6)Near(display[3],1,"civil twilight stars fully visible");
        }
        Console.WriteLine("PASS independent 2x daytime sky and retained ambient source; solar star onset immediately below horizon");

        foreach(float intensity in new[]{.02f,.7f}) {
            GL.ActiveTexture(TextureUnit.Texture4);GL.BindTexture(TextureTarget.TextureCubeMap,cube);
            for(int face=0;face<6;face++)GL.TexImage2D(TextureTarget.TextureCubeMapPositiveX+face,0,PixelInternalFormat.Rgba32f,
                1,1,0,PixelFormat.Rgba,PixelType.Float,new[]{intensity,intensity,intensity,1f});
            foreach(float elevation in new[]{90f,-.5f,-3f,-30f})
            foreach(float density in new[]{0f,.00005f,.1f}) {
                frame[13]=MathF.Sin(elevation*MathF.PI/180);frame[4]=density;
                var skyPixel=Draw(background);var starPixel=Draw(stars);var visibility=Draw(diagnostic)[3];
                float t=MathF.Exp(-density*(.05f+7.23933f*density)*200);
                float signal=Math.Max(intensity-.03f,0)*.66f*visibility*t;
                for(int c=0;c<3;c++)Near(starPixel[c],skyPixel[c]+signal,"actual night base and sky overlay apply weather to stars once");
                Near(starPixel[3],1,"atmosphere base retains coverage with delayed native daylight");
            }
        }
        Console.WriteLine("PASS actual night-sky main and weather overlay: empty cube texels never darken atmosphere; early stars survive delayed native daylight; weather attenuates once");

        // Black texture has real coverage but zero emission: its pixels must
        // equal the separately computed weather sky, without a rectangle.
        foreach(float elevation in new[]{90f,0f,-30f})
        foreach(float density in new[]{0f,.0001f,.1f}) {
            frame[13]=MathF.Sin(elevation*MathF.PI/180);frame[4]=density;
            Set(tex,0,0,0,1);var expected=Draw(background);var actual=Draw(sprite);
            for(int c=0;c<3;c++)Near(actual[c],expected[c],"zero-emission sprite equals sky in day/night/weather");
        }
        frame[4]=0;frame[13]=0;
        GL.UseProgram(sprite);GL.Uniform1(GL.GetUniformLocation(sprite,"weirdMathToMakeMoonLookNicer"),0);
        Set(tex,0,0,0,1);var baseline=Draw(sprite);Set(tex,1,1,1,1);
        foreach(var uv in new[]{(.02f,.5f),(.5f,.5f),(.98f,.5f)}) {
            GL.UseProgram(sprite);GL.Uniform2(GL.GetUniformLocation(sprite,"uv"),uv.Item1,uv.Item2);
            GL.Uniform3(GL.GetUniformLocation(sprite,"sunPosition"),0f,1f,0f);
            var pixel=Draw(sprite);Near(pixel[0]-baseline[0],8,"sun remains HDR at center/edge and lunar conjunction");
        }
        Set(tex,2,2,2,1);var doubleSun=Draw(sprite);Near(doubleSun[0]-baseline[0],16,"gain survives RGBA16F composition above one");

        GL.UseProgram(sprite);GL.Uniform1(GL.GetUniformLocation(sprite,"weirdMathToMakeMoonLookNicer"),1);
        GL.Uniform2(GL.GetUniformLocation(sprite,"uv"),.5f,.5f);GL.Uniform3(GL.GetUniformLocation(sprite,"sunPosition"),1f,0f,0f);
        var clear=Draw(sprite);
        frame[4]=.00005f;Set(tex,0,0,0,1);var hazyBase=Draw(sprite);Set(tex,2,2,2,1);var hazy=Draw(sprite);
        // Zenith depth uses the calibrated air density times the height
        // integral; celestial phase/brightness and exposure stay separate.
        float transmission=MathF.Exp(-.00005f*(.05f+7.23933f*.00005f)*200);
        Near((hazy[0]-hazyBase[0])/(clear[0]-baseline[0]),transmission,"moon extinction is linear after intrinsic phase shading");
        Console.WriteLine("PASS production celestial HDR: sky-continuous black texels, bright sun without lunar zero-clipping, >1 radiance, linear moon weather extinction");

        GL.BindFramebuffer(FramebufferTarget.Framebuffer,0);GL.BindVertexArray(0);GL.UseProgram(0);
        GL.BindBufferBase(BufferRangeTarget.UniformBuffer,10,0);GL.Disable(EnableCap.DepthTest);
        foreach(int p in new[]{coverage,diagnostic,ambient,sprite,background,stars})GL.DeleteProgram(p);
        foreach(int t in new[]{lut,tex,target,cube})GL.DeleteTexture(t);
        GL.DeleteRenderbuffer(depth);GL.DeleteFramebuffer(fbo);GL.DeleteVertexArray(vao);GL.DeleteBuffer(buffer);
        Check(GL.GetError()==ErrorCode.NoError,"horizon fixture GL NoError");
    }
    private static int Texture(int unit,params float[] pixel) {
        int tex=GL.GenTexture();GL.ActiveTexture(TextureUnit.Texture0+unit);GL.BindTexture(TextureTarget.Texture2D,tex);
        GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.Rgba32f,1,1,0,PixelFormat.Rgba,PixelType.Float,pixel);
        GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)TextureMagFilter.Nearest);return tex;
    }
    private static void Set(int tex,params float[] pixel){GL.ActiveTexture(TextureUnit.Texture2);GL.BindTexture(TextureTarget.Texture2D,tex);GL.TexSubImage2D(TextureTarget.Texture2D,0,0,0,1,1,PixelFormat.Rgba,PixelType.Float,pixel);}
    private static void Near(float actual,float expected,string name){Check(Math.Abs(actual-expected)<.01f*Math.Max(1,Math.Abs(expected)),name+$" ({actual} vs {expected})");}
    private static void Check(bool ok,string name){if(!ok)throw new Exception(name);}
}
