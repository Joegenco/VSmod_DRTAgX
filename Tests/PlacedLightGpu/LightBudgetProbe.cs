using System;
using System.IO;
using System.Linq;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

// Execute maintained accumulation, placed receiver loops and forward shaders.
// Ratios isolate the new budget from the user's current single-light tuning.
internal static class LightBudgetProbe
{
    internal static void Run(string deferredPath)
    {
        using var saved = new ShadowGlState();
        string lighting = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(deferredPath)!, "../../drtagx/shaders/lighting"));
        string common = File.ReadAllText(Path.Combine(lighting, "drtagx_light_balance.ash"));
        string dynamic = File.ReadAllText(Path.Combine(lighting, "drtagx_dynamic_accumulation.ash"));
        string forward = File.ReadAllText(Path.Combine(lighting, "drtagx_forward_placedlights.ash"));
        string placed = File.ReadAllText(Path.Combine(lighting, "../deferred/drtagx_deferred_placedlights.fsh"));
        string pair = ProbeShader.Function(File.ReadAllText(Path.Combine(lighting, "../deferred/drtagx_deferred_dynamiclights.fsh")), "vec3 drtAccumulateMovingPair(");
        bool rankState=common.Contains("struct DrtLightRanks",StringComparison.Ordinal);
        const string vertex = "#version 430 core\nvoid main(){vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);gl_Position=vec4(p*2.-1.,0,1);}";
        const string header = """
            #version 430 core
            #define DRT_DYNAMIC_CAPACITY 16
            layout(std430,binding=4)readonly buffer Sources{vec4 drtStaticSourceData[];};
            layout(std430,binding=5)readonly buffer Tiles{uint drtStaticTileData[];};
            uniform int mode, count, drtStaticCount;
            const int drtStaticTileWidth=1, drtShadowGridEnabled=0;
            const int drtStaticAllTerrainPasses=0;
            const float drtStaticBlend=1;
            const mat4 invModelViewMatrix=mat4(1);
            uniform vec3 contributions[16];
            uniform float visibility[129];
            vec3 drtSkyLight=vec3(0);
            vec3 drtStaticGridOffset(vec3 p,vec3 n){return vec3(0);}
            float drtStaticVisibility(vec3 p,vec3 n,float d,float nd,int slot,int secondary,float blend){return visibility[slot];}
            float drtStaticTwoSidedVisibility(vec3 p,vec3 n,int slot,int secondary,float blend){return visibility[slot];}
            float drtStaticTwoSidedVisibilityWithTolerance(vec3 p,vec3 n,int slot,int secondary,float blend,float tolerance){return visibility[slot];}
            out vec4 color;
            """;
        const string main = """
            void main(){
                if(mode==4)color=vec4(drtAccumulateMovingPair(contributions[0],contributions[1]),1);
                else if(mode==0)color=vec4(drtAccumulateDynamic(contributions,count),1);
                else if(mode==1){float e;vec3 s; color=drtPlacedLights(vec3(0),vec3(0,0,1),true,false,e,s);}
                else if(mode==2){float f; color=drtPlacedFoliageLight(vec3(0),vec3(0,0,1),f);}
                else color=vec4(drtForwardPlacedLocal(vec3(0),vec3(0,0,1),vec3(1),0.,0.),1);
            }
            """;
        int texture=GL.GenTexture(), fbo=GL.GenFramebuffer(), vao=GL.GenVertexArray();
        int sources=GL.GenBuffer(), tiles=GL.GenBuffer(), checks=0;
        try
        {
            GL.ActiveTexture(TextureUnit.Texture0); GL.BindTexture(TextureTarget.Texture2D,texture);
            GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.Rgba32f,1,1,0,PixelFormat.Rgba,PixelType.Float,IntPtr.Zero);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer,fbo);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,FramebufferAttachment.ColorAttachment0,TextureTarget.Texture2D,texture,0);
            GL.DrawBuffer(DrawBufferMode.ColorAttachment0); GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
            GL.BindVertexArray(vao); GL.Viewport(0,0,1,1);
            GL.Disable(EnableCap.DepthTest); GL.Disable(EnableCap.Blend); GL.Disable(EnableCap.ScissorTest); GL.ColorMask(true,true,true,true);
            foreach(bool animated in new[]{false,true})
            {
                string rankMain=rankState?main.Replace("if(mode==4)","""
                    if(mode==5){DrtLightRanks ranks;drtClearRanks(ranks);
                        for(int i=0;i<min(count,129);i++)drtInsertRank(ranks,drtStaticSourceData[i*4+1].rgb*drtStaticSourceData[i*4+2].x);
                        color=vec4(drtRankRadiance(ranks),1);}
                    else if(mode==4)
                    """):main;
                if(common.Contains("drtInsertRankLimited(",StringComparison.Ordinal))
                    rankMain=rankMain.Replace("drtInsertRank(ranks,drtStaticSourceData[i*4+1].rgb*drtStaticSourceData[i*4+2].x)",
                        "drtInsertRankLimited(ranks,drtStaticSourceData[i*4+1].rgb*drtStaticSourceData[i*4+2].x,count)")
                        .Replace("drtRankRadiance(ranks)","drtRankRadianceLimited(ranks,count)");
                string fragment=header+(animated?"\n#define DRT_ANIMATED_PLACED_FACING\n":"\n")+common+dynamic+pair+forward+placed+rankMain;
                int program=ProbeShader.Program((ShaderType.VertexShader,vertex),(ShaderType.FragmentShader,fragment));
                try
                {
                    GL.UseProgram(program);
                    int Loc(string name)=>GL.GetUniformLocation(program,name);
                    void Equal(Vector3 actual,Vector3 expected,string label)
                    {
                        ++checks;
                        if(!float.IsFinite(actual.X)||!float.IsFinite(actual.Y)||!float.IsFinite(actual.Z)||
                            (actual-expected).Length>4e-5f*Math.Max(1,expected.Length))
                            throw new Exception($"{label}: {actual} vs {expected}");
                    }
                    Vector3 Draw(int mode,float scale)
                    {
                        GL.Uniform1(Loc("mode"),mode);
                        float[] calibration=Enumerable.Repeat(scale,64).ToArray();
                        GL.Uniform2(Loc("drtPlacedCalibration[0]"),32,calibration);
                        GL.DrawArrays(PrimitiveType.Triangles,0,3);
                        float[] pixel=new float[4]; GL.ReadPixels(0,0,1,1,PixelFormat.Rgba,PixelType.Float,pixel);
                        return new(pixel[0],pixel[1],pixel[2]);
                    }
                    void Upload(Vector3[] colors,float[] fades=null,int pairA=-1,int pairB=-1,int blocked=-1)
                    {
                        int count=colors.Length;
                        float[] records=new float[129*16], positions=new float[36], forwardColors=new float[36], forwardFades=new float[9];
                        uint[] masks=new uint[5]; float[] visible=Enumerable.Repeat(1f,129).ToArray(), dynamicColors=new float[48];
                        if(blocked>=0) visible[blocked]=0;
                        int pairMask=0;
                        for(int i=0;i<count;i++)
                        {
                            int o=i*16; float fade=fades==null?1:fades[i];
                            records[o+2]=3; records[o+3]=22;
                            records[o+4]=colors[i].X; records[o+5]=colors[i].Y; records[o+6]=colors[i].Z; records[o+7]=i;
                            records[o+8]=fade; records[o+10]=i; records[o+11]=(i==pairA||i==pairB)?1:0;
                            records[o+14]=3; records[o+15]=18;
                            masks[i/32]|=1u<<(i%32);
                            if(i<9){positions[i*4+2]=3;positions[i*4+3]=22;forwardColors[i*4]=colors[i].X;forwardColors[i*4+1]=colors[i].Y;forwardColors[i*4+2]=colors[i].Z;forwardColors[i*4+3]=18;forwardFades[i]=fade;}
                            if(i<9&&(i==pairA||i==pairB)) pairMask|=1<<i;
                            if(i<16){dynamicColors[i*3]=colors[i].X;dynamicColors[i*3+1]=colors[i].Y;dynamicColors[i*3+2]=colors[i].Z;}
                        }
                        GL.BindBuffer(BufferTarget.ShaderStorageBuffer,sources);GL.BufferData(BufferTarget.ShaderStorageBuffer,records.Length*4,records,BufferUsageHint.StaticDraw);GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer,4,sources);
                        GL.BindBuffer(BufferTarget.ShaderStorageBuffer,tiles);GL.BufferData(BufferTarget.ShaderStorageBuffer,masks.Length*4,masks,BufferUsageHint.StaticDraw);GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer,5,tiles);
                        GL.Uniform1(Loc("drtStaticCount"),count);GL.Uniform1(Loc("count"),count);GL.Uniform1(Loc("drtEntityPlacedCount"),Math.Min(count,9));
                        GL.Uniform1(Loc("visibility[0]"),visible.Length,visible);GL.Uniform3(Loc("contributions[0]"),16,dynamicColors);
                        GL.Uniform4(Loc("drtEntityPlacedSources[0]"),9,positions);GL.Uniform4(Loc("drtEntityPlacedColors[0]"),9,forwardColors);
                        GL.Uniform4(Loc("drtEntityPlacedFades"),forwardFades[0],forwardFades[1],forwardFades[2],forwardFades[3]);
                        GL.Uniform4(Loc("drtEntityPlacedFadesExtra"),forwardFades[4],forwardFades[5],forwardFades[6],forwardFades[7]);
                        GL.Uniform1(Loc("drtEntityPlacedFadeOverflow"),forwardFades[8]);GL.Uniform1(Loc("drtEntityPlacedPairMask"),pairMask);
                    }
                    // Subtract two calibration captures to remove the unchanged
                    // voxel baseline, including the tuned animated gain/facing.
                    Vector3 Energy(int mode)=>mode==0?Draw(mode,1):Draw(mode,1)-Draw(mode,.5f);
                    foreach(int mode in animated?new[]{3}:new[]{0,1,2,3})
                    {
                        Upload([Vector3.One]);Vector3 single=Energy(mode);
                        foreach(int n in new[]{0,1,2,3,8,9,16,129})
                        {
                            Upload(Enumerable.Repeat(Vector3.One,n).ToArray());
                            float h=0;for(int i=1;i<=Math.Min(n,8);i++)h+=1f/i;
                            Equal(Energy(mode),single*h,$"{(animated?"animated":"generic")} mode {mode}, {n} lights: harmonic/cap");
                        }
                        // The ninth is within the old 5% tie window but weaker.
                        // It must affect neither brightness nor chroma.
                        Upload(Enumerable.Repeat(Vector3.One,8).Append(new Vector3(1,.99f,1)).ToArray());
                        float h8=0;for(int i=1;i<=8;i++)h8+=1f/i;
                        Equal(Energy(mode),single*h8,$"mode {mode}: near-equal ninth excluded from colored ties");
                        Vector3[] strengths=Enumerable.Range(0,9).Select(i=>new Vector3(MathF.Pow(.7f,i))).ToArray();
                        float expected=0;for(int i=0;i<8;i++)expected+=MathF.Pow(.7f,i)/(i+1);
                        Upload(strengths);Equal(Energy(mode),single*expected,$"mode {mode}: strongest eight receive exact rank weights");
                        Array.Reverse(strengths);Upload(strengths);Equal(Energy(mode),single*expected,$"mode {mode}: source-order independence");
                        if(mode!=0)
                        {
                            float[] fades=Enumerable.Repeat(1f,9).ToArray();fades[0]=.3f;fades[8]=.7f;
                            Upload(Enumerable.Repeat(Vector3.One,9).ToArray(),fades,0,8);
                            Equal(Energy(mode),single*h8,$"mode {mode}: replacement pair consumes one rank");
                        }
                        Console.WriteLine($"PASS {(animated?"animated":"generic")} light mode {mode}: H8={h8:F7}, strict ninth cutoff, ranking and fades");
                    }
                    if(!animated)
                    {
                        // Independent sorted CPU reference: broad HDR colors,
                        // clustered ties and source permutations exercise the
                        // shader's fixed-index ranking without copying its insertion code.
                        var random=new Random(20261006);
                        if(rankState)foreach(int n in new[]{0,1,2,3,8,9,16,129})
                        {
                            for(int trial=0;trial<16;trial++)
                            {
                                Vector3[] radiance=Enumerable.Range(0,n).Select(i=>new Vector3(
                                    (float)random.NextDouble(),(float)random.NextDouble(),(float)random.NextDouble())*
                                    (trial%2==0?100f:1f)).ToArray();
                                if(n>2&&trial%3==0)radiance[1]=radiance[2]=radiance[0];
                                Vector3 expected=RankReference(radiance);
                                Upload(radiance);Equal(Draw(5,1),expected,"array-free selected eight match independent CPU reference through 129 records");
                                Array.Reverse(radiance);Upload(radiance);
                                Equal(Draw(5,1),expected,"array-free colored ties and ordering retain publication independence");
                            }
                        }
                        for(int trial=0;trial<256;trial++)
                        {
                            int n=trial%17;
                            Vector3[] radiance=new Vector3[n];
                            for(int i=0;i<n;i++)
                            {
                                float magnitude=trial%2==0?(float)Math.Pow(10,random.NextDouble()*5-3):1f+(float)random.NextDouble()*.08f;
                                radiance[i]=new Vector3((float)random.NextDouble(),(float)random.NextDouble(),(float)random.NextDouble())*magnitude;
                            }
                            if(n>2&&trial%3==0)radiance[1]=radiance[2]=radiance[0];
                            Vector3 expected=RankReference(radiance);
                            Upload(radiance);Equal(Energy(0),expected,"sorted CPU reference for HDR ranking and colored ties");
                            Array.Reverse(radiance);Upload(radiance);
                            Equal(Energy(0),expected,"HDR ranking remains independent of reversed source publication");
                        }
                        foreach(Vector3[] pairColors in new[]{
                            new[]{Vector3.One,new Vector3(.5f)},
                            new[]{Vector3.One,new Vector3(1,.99f,1)},
                            new[]{new Vector3(1,.99f,1),Vector3.One},
                            new[]{Vector3.Zero,Vector3.One}})
                        {
                            Upload(pairColors);Equal(Draw(4,1),Draw(0,1),"optimized dynamic pair retains existing two-light balance");
                        }
                        Upload(Enumerable.Repeat(Vector3.One,8).ToArray());Vector3 unobstructed=Energy(1);
                        Upload(Enumerable.Repeat(Vector3.One,8).Append(new Vector3(10,0,0)).ToArray(),blocked:8);
                        Equal(Energy(1),unobstructed,"shadowed bright ninth cannot displace a visible rank");
                        Upload([Vector3.One]);Vector3 single=Energy(1);
                        Vector3[] colors=new Vector3[129];float[] fades=new float[129];colors[0]=colors[128]=Vector3.One;fades[0]=.3f;fades[128]=.7f;
                        Upload(colors,fades,0,128);Equal(Energy(1),single,"staging bit 128 merges replacement radiance");
                        Upload([new Vector3(float.NaN),new Vector3(float.PositiveInfinity),Vector3.One]);
                        Equal(Energy(0),Vector3.One,"invalid dynamic radiance consumes no rank");
                    }
                }
                finally{GL.DeleteProgram(program);}
            }
            if(GL.GetError()!=ErrorCode.NoError)throw new Exception("Light budget GL error");
            Console.WriteLine($"PASS eight-light budget: {checks} GPU checks; current single-light gains/colors preserved by ratio checks");
        }
        finally{GL.DeleteFramebuffer(fbo);GL.DeleteTexture(texture);GL.DeleteVertexArray(vao);GL.DeleteBuffer(sources);GL.DeleteBuffer(tiles);}
    }

    private static Vector3 RankReference(Vector3[] radiance)
    {
        static double Score(Vector3 c)=>.2126*c.X+.7152*c.Y+.0722*c.Z;
        var selected=radiance.Where(c=>Score(c)>0&&double.IsFinite(Score(c)))
            .OrderByDescending(Score).ThenByDescending(c=>c.X).ThenByDescending(c=>c.Y).ThenByDescending(c=>c.Z).Take(8).ToArray();
        var scores=selected.Select(Score).ToArray();
        var weights=new double[selected.Length];
        static double Relative(double a,double b)=>Math.Abs(a-b)/Math.Max(Math.Max(a,b),1e-8);
        for(int rank=0;rank<selected.Length;rank++)
        {
            double harmonic=1.0/(rank+1);
            bool tied=(rank>0&&Relative(scores[rank],scores[rank-1])<.05)||
                (rank+1<scores.Length&&Relative(scores[rank],scores[rank+1])<.05);
            if(!tied){weights[rank]+=harmonic;continue;}
            // Each selected rank distributes its harmonic energy through the
            // existing smoothstep similarity kernel over the selected eight.
            var kernels=scores.Select(s=>{
                double t=Math.Clamp(Relative(s,scores[rank])/.05,0,1);
                return 1-t*t*(3-2*t);
            }).ToArray();
            double total=kernels.Sum();
            for(int i=0;i<weights.Length;i++)weights[i]+=harmonic*kernels[i]/Math.Max(total,1e-8);
        }
        Vector3 result=Vector3.Zero;
        for(int i=0;i<selected.Length;i++)result+=selected[i]*(float)weights[i];
        return result;
    }
}
