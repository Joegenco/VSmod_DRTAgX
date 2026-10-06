using System;
using System.IO;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;

/// <summary>Independent CPU box/linear/Gaussian references for actual HDR shader footprints.</summary>
internal static class HdrFilterFootprintProbe
{
    internal static void Run(string directory)
    {
        using var state=new HdrPassState();
        string vertex=File.ReadAllText(Path.Combine(directory,"hdr_downsample.vsh"));
        int down=ProbeShader.Program((ShaderType.VertexShader,vertex),(ShaderType.FragmentShader,File.ReadAllText(Path.Combine(directory,"hdr_downsample.fsh"))));
        int blur=ProbeShader.Program((ShaderType.VertexShader,vertex),(ShaderType.FragmentShader,File.ReadAllText(Path.Combine(directory,"hdr_blur.fsh"))));
        int input=GL.GenTexture(),output=GL.GenTexture(),fbo=GL.GenFramebuffer(),vao=GL.GenVertexArray();
        void Texture(int id,int w,int h,float[] data) {
            GL.ActiveTexture(TextureUnit.Texture0);GL.BindTexture(TextureTarget.Texture2D,id);
            GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.Rgba32f,w,h,0,PixelFormat.Rgba,PixelType.Float,data);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureWrapS,(int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureWrapT,(int)TextureWrapMode.ClampToEdge);
        }
        void Target(int w,int h) {
            Texture(output,w,h,new float[w*h*4]);GL.BindFramebuffer(FramebufferTarget.Framebuffer,fbo);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,FramebufferAttachment.ColorAttachment0,TextureTarget.Texture2D,output,0);
            GL.DrawBuffer(DrawBufferMode.ColorAttachment0);GL.ReadBuffer(ReadBufferMode.ColorAttachment0);GL.Viewport(0,0,w,h);GL.BindVertexArray(vao);
            GL.BindTexture(TextureTarget.Texture2D,input);
        }
        float maxError=0;
        void Check(float actual,float expected,string label,float interpolationTolerance=0) {
            float error=Math.Abs(actual-expected);maxError=Math.Max(maxError,error);
            if(!float.IsFinite(actual)||error>Math.Max(interpolationTolerance,4e-5f*Math.Max(1,Math.Abs(expected))))throw new Exception($"{label}: {actual} vs {expected}");
        }
        try {
            var random=new Random(103);
            foreach(var (w,h,reduction) in new[]{(16,16,4),(8,8,2),(9,7,2),(19,15,4)}) {
                int dw=w/reduction,dh=h/reduction;var source=new float[w*h*4];for(int i=0;i<source.Length;++i)source[i]=(float)random.NextDouble()*12;
                Texture(input,w,h,source);Target(dw,dh);GL.UseProgram(down);
                GL.Uniform1(GL.GetUniformLocation(down,"source"),0);GL.Uniform2(GL.GetUniformLocation(down,"texelSize"),1f/w,1f/h);
                GL.Uniform1(GL.GetUniformLocation(down,"reduction"),reduction);GL.Uniform1(GL.GetUniformLocation(down,"alignedHalf"),reduction==2&&w==2*dw&&h==2*dh?1:0);
                GL.Uniform1(GL.GetUniformLocation(down,"meterFirstLevel"),0);GL.Uniform1(GL.GetUniformLocation(down,"emissiveFirstLevel"),0);
                GL.DrawArrays(PrimitiveType.Triangles,0,3);var pixels=new float[dw*dh*4];GL.ReadPixels(0,0,dw,dh,PixelFormat.Rgba,PixelType.Float,pixels);
                float Sample(float u,float v,int c) {
                    float px=u*w-.5f,py=v*h-.5f;int x=(int)MathF.Floor(px),y=(int)MathF.Floor(py);float fx=px-x,fy=py-y;
                    float Value(int ix,int iy)=>source[(Math.Clamp(iy,0,h-1)*w+Math.Clamp(ix,0,w-1))*4+c];
                    return (Value(x,y)*(1-fx)+Value(x+1,y)*fx)*(1-fy)+(Value(x,y+1)*(1-fx)+Value(x+1,y+1)*fx)*fy;
                }
                for(int y=0;y<dh;++y)for(int x=0;x<dw;++x)for(int c=0;c<3;++c) {
                    float u=(x+.5f)/dw,v=(y+.5f)/dh,offset=reduction==4?1:.5f;
                    float expected=(Sample(u-offset/w,v-offset/h,c)+Sample(u+offset/w,v-offset/h,c)+Sample(u-offset/w,v+offset/h,c)+Sample(u+offset/w,v+offset/h,c))*.25f;
                    // Odd destination centers use fractional hardware interpolation. NVIDIA's
                    // fractional-weight rounding differs from a float CPU lerp; aligned boxes stay strict.
                    Check(pixels[(y*dw+x)*4+c],expected,$"HDR {w}x{h} / {reduction} footprint",w%reduction!=0 || h%reduction!=0?.012f:0);
                }
            }
            const int size=17;var impulse=new float[size*size*4];impulse[(8*size+8)*4]=1;
            foreach(bool impulseCase in new[]{true,false}) {
                var source=impulseCase?impulse:new float[impulse.Length];if(!impulseCase)for(int i=0;i<source.Length;++i)source[i]=(float)random.NextDouble()*8;
                foreach(bool horizontal in new[]{true,false})foreach(int paired in new[]{0,1}) {
                    Texture(input,size,size,source);Target(size,size);GL.UseProgram(blur);
                    GL.Uniform1(GL.GetUniformLocation(blur,"source"),0);GL.Uniform1(GL.GetUniformLocation(blur,"radiusOne"),paired);
                    GL.Uniform2(GL.GetUniformLocation(blur,"blurStep"),horizontal?1f/size:0,horizontal?0:1f/size);
                    GL.DrawArrays(PrimitiveType.Triangles,0,3);var pixels=new float[source.Length];GL.ReadPixels(0,0,size,size,PixelFormat.Rgba,PixelType.Float,pixels);
                    float[] weights={.0625f,.25f,.375f,.25f,.0625f};double energy=0,variance=0;
                    for(int y=0;y<size;++y)for(int x=0;x<size;++x)for(int c=0;c<3;++c) {
                        float expected=0;for(int tap=-2;tap<=2;++tap)expected+=source[(Math.Clamp(y+(horizontal?0:tap),0,size-1)*size+Math.Clamp(x+(horizontal?tap:0),0,size-1))*4+c]*weights[tap+2];
                        // Two .3125 pairs on an 0..8 HDR signal bound 8-bit interpolation rounding.
                        Check(pixels[(y*size+x)*4+c],expected,"radius-one Gaussian matches independent five-tap reference",paired!=0?.006f:0);
                        if(c==0){energy+=pixels[(y*size+x)*4];variance+=pixels[(y*size+x)*4]*Math.Pow((horizontal?x:y)-8,2);}
                    }
                    if(impulseCase){Check((float)energy,1,"Gaussian impulse energy");Check((float)variance,1,"Gaussian impulse radius",paired!=0?.002f:0);}
                }
            }
            if(GL.GetError()!=ErrorCode.NoError)throw new Exception("HDR footprint GL error");
            Console.WriteLine($"PASS HDR footprints: quarter 4x4, aligned half 2x2, odd sizes; three/five-tap Gaussian HDR parity and impulse energy/radius; max hardware-interpolation absolute error {maxError:G6}");
        }
        finally {GL.UseProgram(0);GL.DeleteProgram(down);GL.DeleteProgram(blur);GL.DeleteTexture(input);GL.DeleteTexture(output);GL.DeleteFramebuffer(fbo);GL.DeleteVertexArray(vao);}
    }
}
