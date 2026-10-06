using System;
using System.IO;
using System.Linq;
using System.Reflection;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;

/// <summary>Production GPU reduction, same-frame UBO publication and linear forward/fragment parity.</summary>
internal static class AmbientSkyProbe
{
    internal static void Run(string directory)
    {
        AmbientInputsProbe.Run();
        var api=ProbeAssets.AtmosphereApi(directory);
        using var resources=new AtmosphereAmbientResources();
        var sky=new AtmosphereSkyResources(api);
        int previous=Texture(.2f,.3f,.4f),current=Texture(.4f,.5f,.6f);
        typeof(AtmosphereSkyResources).GetProperty("Previous",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(sky,previous);
        typeof(AtmosphereSkyResources).GetProperty("Current",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(sky,current);
        var frame=new float[AtmosphereRenderer.FrameFloatCount];
        // A synthetic night LUT isolates E/pi/history from the daytime mixture's artistic grading.
        frame[3]=1; frame[11]=.5f; frame[12]=0; frame[13]=-1; frame[24]=1; frame[27]=1;
        frame[92]=frame[93]=frame[94]=frame[95]=1;
        frame[100]=frame[101]=frame[102]=1;
        int buffer=GL.GenBuffer(); GL.BindBuffer(BufferTarget.UniformBuffer,buffer);
        GL.BufferData(BufferTarget.UniformBuffer,frame.Length*4,frame,BufferUsageHint.DynamicDraw);
        int alignment=Math.Max(GL.GetInteger(GetPName.ShaderStorageBufferOffsetAlignment),
            GL.GetInteger(GetPName.UniformBufferOffsetAlignment));
        int sentinel=GL.GenBuffer(); GL.BindBuffer(BufferTarget.ShaderStorageBuffer,sentinel);
        GL.BufferData(BufferTarget.ShaderStorageBuffer,alignment+frame.Length*4,IntPtr.Zero,BufferUsageHint.DynamicDraw);
        GL.BindBufferRange(BufferRangeTarget.ShaderStorageBuffer,11,sentinel,(IntPtr)alignment,64);
        // Both indexed targets may arrive as ranges in a larger renderer-owned allocation.
        GL.BindBufferRange(BufferRangeTarget.UniformBuffer,10,sentinel,(IntPtr)alignment,frame.Length*4);
        var state=new AtmosphereComputeState(); state.Capture(); resources.Initialize(api); state.Dispose();

        float[] Reduce()
        {
            GL.BindBuffer(BufferTarget.UniformBuffer,buffer);
            GL.BufferSubData(BufferTarget.UniformBuffer,IntPtr.Zero,frame.Length*4,frame);
            state.Capture(); resources.Render(sky,buffer); state.Dispose();
            GL.BindBuffer(BufferTarget.UniformBuffer,buffer);
            var result=new float[frame.Length]; GL.GetBufferSubData(BufferTarget.UniformBuffer,IntPtr.Zero,result.Length*4,result);
            for(int i=0;i<104;i++) Equal(result[i],frame[i],"compute preserves UBO prefix");
            for(int i=108;i<112;i++) Equal(result[i],frame[i],"compute preserves profile tail");
            GL.GetInteger(GetIndexedPName.ShaderStorageBufferBinding,11,out int binding);
            GL.GetInteger64(GetIndexedPName.ShaderStorageBufferStart,11,out long start);
            GL.GetInteger64(GetIndexedPName.ShaderStorageBufferSize,11,out long size);
            Check(binding==sentinel && start==alignment && size==64,"SSBO indexed range restored");
            GL.GetInteger(GetIndexedPName.UniformBufferBinding,10,out int uboBinding);
            GL.GetInteger64(GetIndexedPName.UniformBufferStart,10,out long uboStart);
            GL.GetInteger64(GetIndexedPName.UniformBufferSize,10,out long uboSize);
            Check(uboBinding==sentinel && uboStart==alignment && uboSize==frame.Length*4,"UBO indexed range restored");
            return result;
        }
        var output=Reduce();
        Equal(output[104],.15f,"cosine-weighted constant sky radiance, red at current 0.5 night exposure");
        Equal(output[105],.2f,"cosine-weighted constant sky radiance, green");
        Equal(output[106],.25f,"cosine-weighted constant sky radiance, blue");
        Equal(output[107],1,"same-frame ambient publication ready");
        frame[11]=0; output=Reduce(); Equal(output[104],.1f,"previous LUT endpoint");
        frame[11]=1; output=Reduce(); Equal(output[104],.2f,"current LUT endpoint");
        frame[11]=.5f; frame[13]=1; output=Reduce();
        Equal(Luma(output,104),(.3f*.2126f+.4f*.7152f+.5f*.0722f)*3f*6f,"peak daylight is six times integrated sky luminance");
        Check(output.Skip(104).Take(3).Max()/output.Skip(104).Take(3).Min()<1.05f,"peak mixture approaches neutral D65 rather than blue diffuse sky");
        // Grading redistributes RGB at fixed luminance; cloud warmth suppression cannot add energy.
        frame[12]=1; frame[13]=0; output=Reduce();
        float energy=Luma(output,104);
        frame[103]=1; var cloudy=Reduce(); Equal(Luma(cloudy,104),energy,"solar chroma bias never changes irradiance");
        frame[103]=0;
        Console.WriteLine("PASS 6x peak daytime mixture, neutral daylight chroma and energy-neutral warm/cloud tint");
        Set(current,0,0,0); Set(previous,0,0,0);
        frame[12]=0; frame[13]=-1; output=Reduce(); Equal(Luma(output,104),0,"dark sky has no artificial irradiance");
        VerifySurface(directory,frame,buffer);
        VerifyVariation(directory,resources,sky,frame,buffer,current,previous);
        Check(GL.GetError()==ErrorCode.NoError,"ambient GL NoError");
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer,11,0);
        GL.BindBufferBase(BufferRangeTarget.UniformBuffer,10,0);
        GL.DeleteBuffer(buffer); GL.DeleteBuffer(sentinel); GL.DeleteTexture(previous); GL.DeleteTexture(current);
        Console.WriteLine("PASS sky irradiance/history, preserved UBO/range state, exact floor, cave gating, linear fallback and surface parity");
    }

    private static void VerifySurface(string directory,float[] frame,int buffer)
    {
        string common=File.ReadAllText(Path.Combine(directory,"drtagx_fog_transport.ash"))+"\n"+
            File.ReadAllText(Path.Combine(directory,"../lighting/drtagx_light_balance.ash"));
        string vertex="#version 330 core\n"+common+"""
            uniform vec3 sky;
            out vec3 vertexAmbient;
            void main(){vertexAmbient=drtAmbientSky(sky);gl_Position=vec4(gl_VertexID==1?3:-1,gl_VertexID==2?3:-1,0,1);}
            """;
        string fragment="#version 330 core\n"+common+"""
            uniform vec3 sky;
            uniform float access;
            in vec3 vertexAmbient;
            out vec4 color;
            void main(){vec3 ambient=drtAmbientSky(sky);color=vec4(drtSunRadiance(access,ambient),length(vertexAmbient-ambient));}
            """;
        int program=ProbeShader.Program((ShaderType.VertexShader,vertex),(ShaderType.FragmentShader,fragment));
        int texture=Texture(0,0,0),fbo=GL.GenFramebuffer(),vao=GL.GenVertexArray();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer,fbo);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,FramebufferAttachment.ColorAttachment0,TextureTarget.Texture2D,texture,0);
        GL.BindVertexArray(vao); GL.Viewport(0,0,1,1); GL.Disable(EnableCap.Blend);GL.Disable(EnableCap.DepthTest);GL.Disable(EnableCap.ScissorTest);
        GL.UseProgram(program); GL.UniformBlockBinding(program,GL.GetUniformBlockIndex(program,"DrtAtmosphere"),10);
        GL.BindBufferBase(BufferRangeTarget.UniformBuffer,10,buffer);
        GL.Uniform3(GL.GetUniformLocation(program,"sky"),1f,1f,1f);
        GL.Uniform1(GL.GetUniformLocation(program,"access"),1f);
        float[] Draw()
        {
            GL.BindBuffer(BufferTarget.UniformBuffer,buffer);GL.BufferSubData(BufferTarget.UniformBuffer,IntPtr.Zero,frame.Length*4,frame);
            GL.DrawArrays(PrimitiveType.Triangles,0,3);var pixel=new float[4];GL.ReadPixels(0,0,1,1,PixelFormat.Rgba,PixelType.Float,pixel);
            Equal(pixel[3],0,"vertex/fragment ambient parity");return pixel;
        }
        frame[107]=1; var p=Draw(); Equal(p[0],.05f,"authored red minimum retained");Equal(p[1],.1f,"authored green minimum retained");Equal(p[2],.15f,"authored blue minimum retained");
        frame[104]=.7f;frame[105]=.8f;frame[106]=.9f; p=Draw();Equal(p[0],.7f,"resolved ambient remains linear");
        GL.Uniform3(GL.GetUniformLocation(program,"sky"),.5f,1f,1f);p=Draw();Equal(p[0],.35f,"caller-specific ambient tint retained");
        GL.Uniform1(GL.GetUniformLocation(program,"access"),0f);p=Draw();Equal(p[0]+p[1]+p[2],0,"minimum cannot illuminate sealed caves");
        frame[95]=0;GL.Uniform1(GL.GetUniformLocation(program,"access"),1f);
        GL.Uniform3(GL.GetUniformLocation(program,"sky"),.3f,.4f,.5f);p=Draw();Equal(p[0],.6f,"native fallback has no RGB power curve");
        frame[95]=1; frame[104]=frame[105]=frame[106]=frame[107]=0;
        GL.BindFramebuffer(FramebufferTarget.Framebuffer,0);GL.BindVertexArray(0);GL.UseProgram(0);
        GL.DeleteTexture(texture);GL.DeleteFramebuffer(fbo);GL.DeleteVertexArray(vao);GL.DeleteProgram(program);
    }

    private static void VerifyVariation(string directory,AtmosphereAmbientResources resources,AtmosphereSkyResources sky,
        float[] frame,int buffer,int current,int previous)
    {
        var api=ProbeAssets.AtmosphereApi(directory);
        int trans=AtmosphereCompute.Load(api,"drtagx_atmosphere_transmittance"),multiple=AtmosphereCompute.Load(api,"drtagx_atmosphere_multiscatter"),
            view=AtmosphereCompute.Load(api,"drtagx_atmosphere_skyview");
        int tt=AtmosphereCompute.Texture(256,64),mt=AtmosphereCompute.Texture(32,32),vt=AtmosphereCompute.Texture(192,108);
        // Sampling the production LUT through the real reduction also verifies that ambient follows daily sky changes.
        typeof(AtmosphereSkyResources).GetProperty("Previous",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(sky,vt);
        typeof(AtmosphereSkyResources).GetProperty("Current",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(sky,vt);
        GL.UseProgram(multiple);GL.Uniform1(GL.GetUniformLocation(multiple,"transmittanceTable"),0);
        GL.UseProgram(view);GL.Uniform1(GL.GetUniformLocation(view,"transmittanceTable"),0);GL.Uniform1(GL.GetUniformLocation(view,"multiscatterTable"),1);
        GL.Uniform1(GL.GetUniformLocation(view,"cameraAltitude"),1f);GL.Uniform4(GL.GetUniformLocation(view,"moonDirection"),0f,-1f,0f,0f);
        var profiles=new[]{new[]{1f,1f,1f,.35f},new[]{.35f,2.4f,2.6f,.75f},new[]{1.85f,.8f,.65f,.15f}};
        float last=-1f;
        foreach(var profile in profiles)
        {
            foreach(int program in new[]{trans,multiple,view}) {GL.UseProgram(program);GL.Uniform4(GL.GetUniformLocation(program,"drtScatteringProfile"),profile[0],profile[1],profile[2],profile[3]);}
            AtmosphereCompute.Dispatch2D(trans,tt,256,64);GL.ActiveTexture(TextureUnit.Texture0);GL.BindTexture(TextureTarget.Texture2D,tt);
            AtmosphereCompute.Dispatch2D(multiple,mt,32,32);GL.ActiveTexture(TextureUnit.Texture1);GL.BindTexture(TextureTarget.Texture2D,mt);
            GL.UseProgram(view);float angle=-3f*MathF.PI/180f;GL.Uniform4(GL.GetUniformLocation(view,"sunDirection"),MathF.Cos(angle),MathF.Sin(angle),0f,0f);
            AtmosphereCompute.Dispatch2D(view,vt,192,108);
            GL.ActiveTexture(TextureUnit.Texture0);GL.BindTexture(TextureTarget.Texture2D,vt);
            var data=new float[192*108*4];GL.MemoryBarrier(MemoryBarrierFlags.TextureUpdateBarrierBit);GL.GetTexImage(TextureTarget.Texture2D,0,PixelFormat.Rgba,PixelType.Float,data);
            Check(data.All(float.IsFinite)&&data.All(x=>x>=0),"daily transport stays finite and nonnegative");
            frame[12]=MathF.Cos(angle);frame[13]=MathF.Sin(angle);frame[27]=13.1319f;frame[11]=1;
            GL.BindBuffer(BufferTarget.UniformBuffer,buffer);GL.BufferSubData(BufferTarget.UniformBuffer,IntPtr.Zero,frame.Length*4,frame);
            var state=new AtmosphereComputeState();state.Capture();resources.Render(sky,buffer);state.Dispose();
            var result=new float[frame.Length];GL.BindBuffer(BufferTarget.UniformBuffer,buffer);GL.GetBufferSubData(BufferTarget.UniformBuffer,IntPtr.Zero,result.Length*4,result);
            float y=Luma(result,104);Check(float.IsFinite(y)&&y>0,"illuminated twilight persists without native ground light");
            if(last>=0)Check(Math.Abs(last-y)>.00001f,"daily atmosphere variation reaches ambient brightness");last=y;
            int horizon=(54*192+96)*4,upper=(72*192+96)*4;
            Console.WriteLine($"Daily profile=({string.Join(',',profile)}) horizon=({data[horizon]:F6},{data[horizon+1]:F6},{data[horizon+2]:F6}) upper=({data[upper]:F6},{data[upper+1]:F6},{data[upper+2]:F6}) ambient=({result[104]:F5},{result[105]:F5},{result[106]:F5}) Y={y:F5}");
            if(profile==profiles[0])
                VerifyCycle(resources,sky,frame,buffer,view,tt,mt,vt);
        }
        foreach(int program in new[]{trans,multiple,view})GL.DeleteProgram(program);
        foreach(int texture in new[]{tt,mt,vt})GL.DeleteTexture(texture);
        Console.WriteLine("PASS coherent aerosol/ozone/spread variation in transmittance, multiple scattering, sky and ambient");
    }

    private static void VerifyCycle(AtmosphereAmbientResources resources,AtmosphereSkyResources sky,float[] frame,
        int buffer,int view,int transmittance,int multiple,int target)
    {
        float noon=0,twilight=0,last=-1;
        foreach(float elevation in new[]{90f,20f,15f,10f,6f,5f,0f,-3f,-6f,-8f,-90f})
        {
            float angle=elevation*MathF.PI/180;
            GL.ActiveTexture(TextureUnit.Texture0);GL.BindTexture(TextureTarget.Texture2D,transmittance);
            GL.ActiveTexture(TextureUnit.Texture1);GL.BindTexture(TextureTarget.Texture2D,multiple);
            GL.UseProgram(view);GL.Uniform4(GL.GetUniformLocation(view,"sunDirection"),MathF.Cos(angle),MathF.Sin(angle),0f,elevation>=0?1f:0f);
            AtmosphereCompute.Dispatch2D(view,target,192,108);
            frame[12]=MathF.Cos(angle);frame[13]=MathF.Sin(angle);
            GL.BindBuffer(BufferTarget.UniformBuffer,buffer);GL.BufferSubData(BufferTarget.UniformBuffer,IntPtr.Zero,frame.Length*4,frame);
            var state=new AtmosphereComputeState();state.Capture();resources.Render(sky,buffer);state.Dispose();
            var output=new float[frame.Length];GL.BindBuffer(BufferTarget.UniformBuffer,buffer);GL.GetBufferSubData(BufferTarget.UniformBuffer,IntPtr.Zero,output.Length*4,output);
            float y=Luma(output,104);Check(float.IsFinite(y)&&y>=0,"finite ambient through solar cycle");
            if(last>=0)Check(y<=last+.0001f,"ambient luminance falls continuously toward night");last=y;
            if(elevation==90)noon=y;if(elevation==-3)twilight=y;
            if(elevation==90)Check(output.Skip(104).Take(3).Min()>2f && output.Skip(104).Take(3).Max()/output.Skip(104).Take(3).Min()<1.05f,
                "physical LUT peak has HDR multipliers above two and neutral daylight chroma");
            if(elevation==0)Check(output[104]>output[105]*2f && output[105]>output[106]*2f,"sunset mixture warms toward 2200 K before the preserved floor");
            if(elevation==-90)Equal(y,0,"moonless midnight reaches zero sky irradiance before the authored floor");
            Console.WriteLine($"Ambient cycle sun={elevation:F0}deg RGB=({output[104]:F5},{output[105]:F5},{output[106]:F5}) Y={y:F5}; floored=({Math.Max(.05f,output[104]):F5},{Math.Max(.1f,output[105]):F5},{Math.Max(.15f,output[106]):F5})");
        }
        Check(noon>twilight*10&&twilight>0,"daylight and twilight irradiance follow the same atmospheric source");
        GL.ActiveTexture(TextureUnit.Texture0);GL.BindTexture(TextureTarget.Texture2D,transmittance);
        GL.ActiveTexture(TextureUnit.Texture1);GL.BindTexture(TextureTarget.Texture2D,multiple);
        GL.UseProgram(view);GL.Uniform4(GL.GetUniformLocation(view,"moonDirection"),0f,1f,0f,.02f);
        AtmosphereCompute.Dispatch2D(view,target,192,108);
        var guard=new AtmosphereComputeState();guard.Capture();resources.Render(sky,buffer);guard.Dispose();
        var moon=new float[frame.Length];GL.BindBuffer(BufferTarget.UniformBuffer,buffer);GL.GetBufferSubData(BufferTarget.UniformBuffer,IntPtr.Zero,moon.Length*4,moon);
        Check(Luma(moon,104)>0,"native lunar strength contributes to night sky irradiance");
        GL.UseProgram(view);GL.Uniform4(GL.GetUniformLocation(view,"moonDirection"),0f,-1f,0f,0f);
        Console.WriteLine("PASS day/twilight/moonless midnight curve and native lunar illumination");
    }

    private static int Texture(float r,float g,float b)
    {
        int texture=GL.GenTexture();GL.ActiveTexture(TextureUnit.Texture0);GL.BindTexture(TextureTarget.Texture2D,texture);
        GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.Rgba32f,1,1,0,PixelFormat.Rgba,PixelType.Float,new[]{r,g,b,1f});
        GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)TextureMagFilter.Linear);return texture;
    }
    private static void Set(int texture,float r,float g,float b)
    {GL.ActiveTexture(TextureUnit.Texture0);GL.BindTexture(TextureTarget.Texture2D,texture);GL.TexSubImage2D(TextureTarget.Texture2D,0,0,0,1,1,PixelFormat.Rgba,PixelType.Float,new[]{r,g,b,1f});}
    private static float Luma(float[] color,int offset)=>color[offset]*.2126f+color[offset+1]*.7152f+color[offset+2]*.0722f;
    private static void Equal(float actual,float expected,string name)=>Check(Math.Abs(actual-expected)<.0001f,name+ $": actual={actual}, expected={expected}");
    private static void Check(bool ok,string name){if(!ok)throw new Exception(name);}
}
