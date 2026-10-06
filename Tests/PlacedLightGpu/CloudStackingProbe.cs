using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;

// Uniform foreground fog must scale the complete native cloud image once,
// independently of how its optical depth is divided into map cells.
internal static class CloudStackingProbe
{
    internal static void Run(string directory)
    {
        using var state=new HdrPassState();
        using var uboRange=new BorrowedBufferRanges(BufferRangeTarget.UniformBuffer,10);
        using var ssboRange=new BorrowedBufferRanges(BufferRangeTarget.ShaderStorageBuffer,11);
        var seen=new HashSet<string>();
        string Expand(string name)=>string.Join('\n',File.ReadAllLines(Path.Combine(directory,name)).Select(line=>
            line.Trim().StartsWith("#include ") ? (seen.Add(line.Trim()[9..]) ? Expand(line.Trim()[9..]) : "") : line));
        string shared=Expand("drtagx_cloud_lighting.fsh");
        string oit=File.ReadAllText(Path.Combine(Environment.GetEnvironmentVariable("VINTAGE_STORY")!,"assets/game/shaderincludes/oit.fsh"));
        string current=File.ReadAllText(Path.GetFullPath(Path.Combine(directory,"../../../game/shaders/cloudvolumetric.fsh")));
        // Historical comparisons are optional and supplied outside source control.
        string beforePath=Path.Combine(Environment.GetEnvironmentVariable("DRTAGX_BASELINE_DIR") ?? "Tests/results/baseline", "cloudvolumetric.fsh");
        int Compile(string cloud)=>ProbeShader.Program((ShaderType.ComputeShader,"#version 430 core\nlayout(local_size_x=1) in;\n"+shared+"""
            uniform sampler2D cloudMap,cloudCol;
            uniform float cells,phase,start;
            vec4 OITreveal;
            const int OIT_BINS=3;
            const float OIT_BIN_SCALE=30.0;
            layout(std430,binding=11) buffer Results { vec4 values[3]; };
            """+ProbeShader.Function(oit,"float OITbellcurve(")+ProbeShader.Function(cloud,"float halfsmooth(")+
            ProbeShader.Function(cloud,"vec4 traverse(")+"""
            void main() {
                OITreveal=vec4(1.0);
                values[0]=traverse(vec3(phase,0,.5),vec3(1,0,0),cells,start,vec3(0));
                values[1]=OITreveal;
                values[2]=vec4(drtSurfaceAirTransport(vec3((start+.5)*50.,0,0),true).transmittance,1);
            }
            """));
        int production=Compile(current),before=File.Exists(beforePath)?Compile(File.ReadAllText(beforePath)):0;
        int map=GL.GenTexture(),color=GL.GenTexture(),ubo=GL.GenBuffer(),results=GL.GenBuffer();
        float[] frame=new float[AtmosphereRenderer.FrameFloatCount];
        frame[9]=200;frame[13]=-1;frame[17]=-1;
        float[] native=new float[12],fogged=new float[12],old=new float[12];
        int cases=0;float maximumError=0,oldMaximumError=0,phaseVariation=0;
        void Texture(int unit,int texture,int width,float[] pixels) {
            GL.ActiveTexture(TextureUnit.Texture0+unit);GL.BindSampler(unit,0);GL.BindTexture(TextureTarget.Texture2D,texture);
            GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.Rgba32f,width,1,0,PixelFormat.Rgba,PixelType.Float,pixels);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)TextureMagFilter.Nearest);
        }
        void Read(int p,float[] output,bool fog) {
            frame[3]=fog?1:0;frame[5]=fog?.8f:0;
            GL.BindBuffer(BufferTarget.UniformBuffer,ubo);GL.BufferSubData(BufferTarget.UniformBuffer,IntPtr.Zero,frame.Length*4,frame);
            GL.UseProgram(p);GL.DispatchCompute(1,1,1);GL.MemoryBarrier(MemoryBarrierFlags.BufferUpdateBarrierBit);
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer,results);GL.GetBufferSubData(BufferTarget.ShaderStorageBuffer,IntPtr.Zero,48,output);
            if(output.Any(x=>!float.IsFinite(x)))throw new Exception("Non-finite stacked cloud result");
        }
        try {
            GL.BindBuffer(BufferTarget.UniformBuffer,ubo);GL.BufferData(BufferTarget.UniformBuffer,frame.Length*4,frame,BufferUsageHint.DynamicDraw);
            GL.BindBufferBase(BufferRangeTarget.UniformBuffer,10,ubo);
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer,results);GL.BufferData(BufferTarget.ShaderStorageBuffer,48,IntPtr.Zero,BufferUsageHint.DynamicRead);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer,11,results);
            foreach(int p in new[]{production,before}.Where(p=>p!=0)) {
                GL.UseProgram(p);GL.UniformBlockBinding(p,GL.GetUniformBlockIndex(p,"DrtAtmosphere"),10);
                GL.Uniform1(GL.GetUniformLocation(p,"cloudMap"),2);GL.Uniform1(GL.GetUniformLocation(p,"cloudCol"),3);
            }
            foreach(int count in new[]{1,2,4,8,16,32})foreach(float start in new[]{0f,2f,8f,20f}) {
                int width=count+2;var density=new float[width*4];var colors=new float[density.Length];
                for(int x=0;x<width;++x) {
                    density[x*4]=1.8f/count;density[x*4+2]=-1;density[x*4+3]=1;
                    colors[x*4]=.9f;colors[x*4+1]=.5f;colors[x*4+2]=.25f;colors[x*4+3]=1;
                }
                Texture(2,map,width,density);Texture(3,color,width,colors);
                float minAlpha=float.MaxValue,maxAlpha=0;
                foreach(float phase in new[]{.01f,.25f,.5f,.75f,.99f}) {
                    foreach(int p in new[]{production,before}.Where(p=>p!=0)) {
                        GL.UseProgram(p);GL.Uniform1(GL.GetUniformLocation(p,"cells"),(float)count);
                        GL.Uniform1(GL.GetUniformLocation(p,"phase"),phase);GL.Uniform1(GL.GetUniformLocation(p,"start"),start);
                    }
                    Read(production,native,false);Read(production,fogged,true);
                    float visibility=fogged[8];
                    if(Math.Abs(native[3]-(1-MathF.Exp(-1.8f)))>3e-6f)throw new Exception("Native cloud optical depth changed under subdivision");
                    for(int c=0;c<8;++c) {
                        float expected=c<4?native[c]*visibility:1-visibility*(1-native[c]);
                        float error=Math.Abs(fogged[c]-expected);maximumError=Math.Max(maximumError,error);
                        if(error>3e-6f)throw new Exception($"Fog exposes cloud partitions: cells={count},phase={phase},channel={c},error={error}");
                    }
                    minAlpha=Math.Min(minAlpha,fogged[3]);maxAlpha=Math.Max(maxAlpha,fogged[3]);
                    if(before!=0) {
                        Read(before,old,true);
                        oldMaximumError=Math.Max(oldMaximumError,Math.Abs(old[3]-native[3]*visibility));
                        Read(before,old,false);
                        for(int c=0;c<8;++c)if(Math.Abs(old[c]-native[c])>3e-6f)throw new Exception("Unfogged native cloud/OIT behavior changed");
                    }
                    ++cases;
                }
                phaseVariation=Math.Max(phaseVariation,maxAlpha-minAlpha);
            }
            if(before!=0 && oldMaximumError<.05f)throw new Exception("Fixture failed to reproduce stacked fog opacity");
            if(GL.GetError()!=ErrorCode.NoError)throw new Exception("Cloud stacking GL error");
            string evidence=Path.Combine("Tests","results","cloud-fog-stacking");Directory.CreateDirectory(evidence);
            File.WriteAllText(Path.Combine(evidence,"stacking.json"),JsonSerializer.Serialize(new{cases,maximumError,oldMaximumError,phaseVariation,
                cellCounts=new[]{1,2,4,8,16,32},nativeCloudAndOitPreserved=true,liveWorldVerified=false},new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine($"PASS cloud stacking: {cases} GPU cases; complete-cloud fog/rgba/bin error={maximumError:G6}; old opacity error={oldMaximumError:G6}; grid-motion variation={phaseVariation:G6}; native cloud/OIT preserved");
        }
        finally {
            GL.UseProgram(0);GL.DeleteProgram(production);if(before!=0)GL.DeleteProgram(before);
            GL.DeleteBuffer(ubo);GL.DeleteBuffer(results);GL.DeleteTexture(map);GL.DeleteTexture(color);
        }
    }
}
