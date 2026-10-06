using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;

/// <summary>Production cloud lighting, fog visibility and real tile traversal under numerical fixtures.</summary>
internal static class CloudBalanceProbe
{
    internal static void Run(string directory)
    {
        var seen = new HashSet<string>();
        string Expand(string name) => string.Join('\n', File.ReadAllLines(Path.Combine(directory, name)).Select(line =>
            line.Trim().StartsWith("#include ") ? (seen.Add(line.Trim()[9..]) ? Expand(line.Trim()[9..]) : "") : line));
        string shared = Expand("drtagx_cloud_lighting.fsh");
        string cloudPath = Path.GetFullPath(Path.Combine(directory, "../../../game/shaders/cloudvolumetric.fsh"));
        string cloud = File.ReadAllText(cloudPath);
        string oit = File.ReadAllText(Path.Combine(Environment.GetEnvironmentVariable("VINTAGE_STORY")!, "assets/game/shaderincludes/oit.fsh"));
        string source = "#version 430 core\nlayout(local_size_x=1) in;\n" + shared + """
            uniform sampler2D cloudMap;
            uniform sampler2D cloudCol;
            uniform int mode;
            uniform float probeVisibility;
            uniform float probeStart;
            vec4 OITreveal;
            const int OIT_BINS=3;
            const float OIT_BIN_SCALE=30.0;
            layout(std430,binding=11) buffer Results { vec4 values[4]; };
            """ + ProbeShader.Function(oit, "float OITbellcurve(") +
            ProbeShader.Function(cloud, "float halfsmooth(") + ProbeShader.Function(cloud, "vec4 traverse(") + """
            void main() {
                OITreveal=vec4(1.0);
                if(mode==0) {
                    values[0]=vec4(drtCloudLight(),drtCloudSunlight());
                    values[1]=vec4(drtCloudDirection(vec3(1,0,0),vec3(0,0,1)),
                        drtCloudDirection(vec3(-1,0,0),vec3(0,0,1)),0,0);
                } else if(mode==1) {
                    // Huge scatter must never be painted over the existing background by clouds.
                    vec4 c=drtCloudThroughFog(vec4(4,2,1,.5),DrtFogTransport(vec3(probeVisibility),vec3(100)));
                    values[0]=c;
                    values[1]=vec4(c.rgb*c.a, c.a);
                } else {
                    values[0]=traverse(vec3(.1,0,.5),vec3(1,0,0),1.5,probeStart,vec3(0));
                    values[1]=OITreveal;
                }
                values[2]=vec4(drtCloudInterval(-2,1,vec2(-1,1),5),
                    drtCloudInterval(0,0,vec2(-1,1),2));
                values[3]=vec4(drtCloudInterval(2,0,vec2(-1,1),2),DRT_MOON_GAIN,0);
            }
            """;
        int program = ProbeShader.Program((ShaderType.ComputeShader, source));
        GL.UseProgram(program);
        GL.Uniform1(GL.GetUniformLocation(program,"probeStart"),20f);
        GL.UniformBlockBinding(program, GL.GetUniformBlockIndex(program,"DrtAtmosphere"),10);
        foreach (var (name,unit) in new[]{("drtSkyViewPrevious",0),("drtSkyViewCurrent",0),("drtFogVolume",1),("cloudMap",2),("cloudCol",3)})
            GL.Uniform1(GL.GetUniformLocation(program,name),unit);
        int map = Texture(2,new[]{.5f,0f,-1f,1f,.5f,0f,-1f,1f,.5f,0f,-1f,1f});
        int color = Texture(3,new[]{1f,1f,1f,1f,1f,1f,1f,1f,1f,1f,1f,1f});
        var frame = new float[AtmosphereRenderer.FrameFloatCount];
        frame[3]=1;frame[9]=200;frame[12]=1;frame[13]=0;frame[17]=-1;
        int buffer=GL.GenBuffer();GL.BindBuffer(BufferTarget.UniformBuffer,buffer);
        GL.BufferData(BufferTarget.UniformBuffer,frame.Length*4,frame,BufferUsageHint.DynamicDraw);
        GL.BindBufferBase(BufferRangeTarget.UniformBuffer,10,buffer);
        int results=GL.GenBuffer();GL.BindBuffer(BufferTarget.ShaderStorageBuffer,results);
        GL.BufferData(BufferTarget.ShaderStorageBuffer,64,IntPtr.Zero,BufferUsageHint.DynamicRead);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer,11,results);
        float[] Run(int mode) {
            GL.UseProgram(program);GL.Uniform1(GL.GetUniformLocation(program,"mode"),mode);
            GL.BindBuffer(BufferTarget.UniformBuffer,buffer);GL.BufferSubData(BufferTarget.UniformBuffer,IntPtr.Zero,frame.Length*4,frame);
            GL.DispatchCompute(1,1,1);GL.MemoryBarrier(MemoryBarrierFlags.BufferUpdateBarrierBit);
            var data=new float[16];GL.BindBuffer(BufferTarget.ShaderStorageBuffer,results);GL.GetBufferSubData(BufferTarget.ShaderStorageBuffer,IntPtr.Zero,64,data);
            Check(data.All(float.IsFinite),"finite cloud result including horizontal ray slabs");return data;
        }
        float last=float.MaxValue;
        foreach(float elevation in new[]{20f,10f,0f,-3f,-6f,-10f}) {
            frame[13]=MathF.Sin(elevation*MathF.PI/180);frame[12]=MathF.Cos(elevation*MathF.PI/180);
            var data=Run(0);float y=Luma(data);
            Check(y<=last+.0001f,"cloud light follows solar descent rather than native RGB");last=y;
            if(elevation==-6)Check(y<.006f,"clouds lose sunlight before midnight");
            if(elevation==0)Check(y<1.2f && y>.5f,"sunset cloud energy below daytime but above deep night");
            Console.WriteLine($"Cloud cycle sun={elevation}deg RGB=({data[0]:F5},{data[1]:F5},{data[2]:F5})");
        }
        frame[13]=0;frame[12]=1;var facing=Run(0);
        Check(facing[4]>facing[5] && facing[4]<1.31f && facing[5]>.7f,"bounded sun-facing cloud relief");
        frame[13]=-1;frame[12]=0;frame[17]=1;frame[19]=.02f;var moon=Run(0);
        // Follow the current authored shared moon gain, rather than the obsolete
        // 3.5x balance. The GPU also publishes that constant in the fixture.
        float moonGain=moon[14];
        Near(moon[0],.005f+.5f*.02f*moonGain,"cloud lunar strength shares the current celestial gain");frame[19]=0;
        foreach(float visibility in new[]{1f,.25f,0f}) {
            GL.Uniform1(GL.GetUniformLocation(program,"probeVisibility"),visibility);var data=Run(1);
            Near(data[3],.5f*visibility,"fog suppresses visible cloud coverage");
            Near(data[4],2f*visibility,"premultiplied cloud contrast attenuates once");
            Near(data[5],visibility,"fog preserves native color ratio and the underlying gradient");
            Near(data[8],1,"slab starts at occupied near boundary");Near(data[9],3,"slab exits at occupied far boundary");
            Near(data[10],0,"horizontal slab starts at cell edge");Near(data[11],2,"horizontal occupied slab length");
            Near(data[12],0,"horizontal empty slab near");Near(data[13],0,"horizontal empty slab far");Near(data[14],moonGain,"shared lunar gain");
        }
        frame[4]=0;var clear=Run(2);Near(clear[3],1-MathF.Exp(-.75f),"native front-to-back cloud coverage in clear air");
        Near(clear[0],clear[3],"native traversal keeps RGB premultiplied by opacity");
        frame[4]=.1f;var fog=Run(2);
        Check(fog.Take(4).All(x=>Math.Abs(x)<.00001f),"dense foreground fog hides actual cloud traversal");
        Check(fog.Skip(4).Take(4).All(x=>Math.Abs(x-1)<.00001f),"concealed clouds leave native OIT revealage unchanged");
        // At zero weather density only the shared terrain-boundary term can
        // hide these real occupied cloud slabs (1,000..1,075 blocks away).
        frame[4]=0;frame[8]=2000;var near=Run(2);
        Near(near[3],clear[3],"clouds inside 66% horizon onset retain native coverage");
        frame[8]=1200;var transition=Run(2);
        Check(transition[3]>0 && transition[3]<near[3],"actual cloud coverage fades inside terrain horizon band");
        frame[8]=512;var hidden=Run(2);
        Check(hidden.Take(4).All(x=>Math.Abs(x)<.00001f) && hidden.Skip(4).Take(4).All(x=>Math.Abs(x-1)<.00001f),
            "clouds beyond terrain horizon disappear with untouched OIT revealage");
        frame[118]=2500;var lod=Run(2);
        Check(lod[3]>0 && lod[3]<near[3],"cloud horizon follows supported extended LOD endpoint");
        GL.Uniform1(GL.GetUniformLocation(program,"probeStart"),60f);var beyondLod=Run(2);
        Check(beyondLod.Take(4).All(x=>Math.Abs(x)<.00001f),"clouds beyond extended LOD endpoint disappear");
        frame[3]=0;var disabled=Run(2);Near(disabled[3],clear[3],"disabled atmosphere leaves cloud coverage unchanged");
        string simple=File.ReadAllText(Path.Combine(Path.GetDirectoryName(cloudPath)!,"clouds.fsh"));
        Check(simple.Contains("drtSurfaceMediumTransport(drtCloudWorldPos, gl_FragCoord.z, true)",StringComparison.Ordinal),
            "simple cloud draw requests the same shared boundary transport");
        Console.WriteLine($"PASS cloud horizon: near alpha={near[3]:F6}, transition={transition[3]:F6}, hidden={hidden[3]:F6}, LOD={lod[3]:F6}; OIT revealage preserved");
        Check(GL.GetError()==ErrorCode.NoError,"cloud GL NoError");
        GL.BindBufferBase(BufferRangeTarget.UniformBuffer,10,0);GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer,11,0);
        GL.UseProgram(0);GL.DeleteProgram(program);GL.DeleteBuffer(buffer);GL.DeleteBuffer(results);GL.DeleteTexture(map);GL.DeleteTexture(color);
        Console.WriteLine("PASS solar cloud dimming, moon gain, directional relief, slab fog distance, terrain horizon/LOD and actual fog/OIT traversal");
    }
    private static int Texture(int unit,float[] pixels) {
        int texture=GL.GenTexture();GL.ActiveTexture(TextureUnit.Texture0+unit);GL.BindTexture(TextureTarget.Texture2D,texture);
        GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.Rgba32f,3,1,0,PixelFormat.Rgba,PixelType.Float,pixels);
        GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)TextureMagFilter.Nearest);return texture;
    }
    private static float Luma(float[] c)=>c[0]*.2126f+c[1]*.7152f+c[2]*.0722f;
    private static void Near(float actual,float expected,string name)=>Check(Math.Abs(actual-expected)<.0001f,name+$" actual={actual} expected={expected}");
    private static void Check(bool ok,string name){if(!ok)throw new Exception(name);}
}
