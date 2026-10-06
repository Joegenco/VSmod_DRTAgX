using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.MathTools;
using DRTAgX;

// Compare the shipping fog against actual installed Sheyder shaders. Atmospheric
// color/HDR and bounded distant-ray eligibility are the permitted differences;
// ray placement, shadow sampling and filtering must match.
internal static class VolumetricNativeProbe
{
    internal static void Run(string assets)
    {
        VolumetricBridgeProbe.VerifyNativeQuality();
        // Allow external dependency archives without machine-specific paths.
        string archive=Environment.GetEnvironmentVariable("SHEYDER_MOD_ZIP") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"VintagestoryData/Mods/SheyderMod 1.1.3.zip");
        using var zip=ZipFile.OpenRead(archive);
        string Native(string name) {using var reader=new StreamReader(zip.GetEntry("assets/sheydermod/shaders/"+name)!.Open());return reader.ReadToEnd().Replace("\r\n","\n");}
        static string Tokens(string source)=>Regex.Replace(Regex.Replace(source,@"//[^\r\n]*|/\*[\s\S]*?\*/",""),@"\s+","");
        string native=Native("vfscatter.fsh"),current=File.ReadAllText(Path.Combine(assets,"sheydermod/shaders/vfscatter.fsh")).Replace("\r\n","\n");
        string nativeColor="float sunAlign = dot(rayDir, vf_SunDir);\n    float ringT    = sunAlign * 0.5 + 0.5;\n    float colorMix = ringT * ringT * ringT;\n    vec3  useColor = mix(vf_BackColor, vf_FrontColor * 1.5, colorMix);";
        string main=ProbeShader.Function(current,"void main(void)").Replace("vec3 useColor = drtVolumetricSunRadiance(rayDir);",nativeColor);
        main=main.Replace("if (dist < 1e-4)","if (dist >= vf_RenderRange || dist < 1e-4)")
            .Replace("min(dist, vf_RenderRange) * vf_InvRenderRange","dist * vf_InvRenderRange");
        if(Tokens(main)!=Tokens(ProbeShader.Function(native,"void main(void)")))throw new Exception("Scatter changed native marching rather than only atmosphere color");
        foreach(string function in new[]{"float bayer2(","float bayer4(","vec3 reconstructWorldPos("})
            if(Tokens(ProbeShader.Function(current,function))!=Tokens(ProbeShader.Function(native,function)))throw new Exception("Native ray/Bayer contract changed: "+function);
        string originalComposite=Native("vfscatter_composite.fsh"),composite=File.ReadAllText(Path.Combine(assets,"sheydermod/shaders/vfscatter_composite.fsh"));
        foreach(string function in new[]{"float vf_depthWeight(","void vf_tap("})
            if(Tokens(ProbeShader.Function(composite,function))!=Tokens(ProbeShader.Function(originalComposite,function)))throw new Exception("Native filter primitive changed");
        string nativeFilter=ProbeShader.Function(originalComposite,"vec3 vf_compositeVolumetric(");
        string currentFilter=ProbeShader.Function(composite,"vec3 vf_compositeVolumetric(");
        if(Tokens(nativeFilter[..nativeFilter.IndexOf("vec3 additive",StringComparison.Ordinal)])!=Tokens(currentFilter[..currentFilter.IndexOf("// Preserve HDR",StringComparison.Ordinal)]))
            throw new Exception("Native composite filtering changed");
        if(typeof(DrtagxModSystem).Assembly.GetType("DRTAgX.VolumetricFilter")!=null)throw new Exception("Retired filter owner still ships");
        Console.WriteLine("PASS native VF source contracts: installed march/depth/Bayer/matrix/bias/response and mip/bilateral/alpha filter; only color, distant eligibility and bounded density differ; retired owner absent");

        using var saved=new HdrPassState();
        using var ranges=new BorrowedBufferRanges(BufferRangeTarget.UniformBuffer,10);
        string vertex="#version 430 core\nout vec2 texcoord;void main(){vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);texcoord=p;gl_Position=vec4(p*2.-1.,0,1);}";
        int vanilla=ProbeShader.Program((ShaderType.VertexShader,vertex),(ShaderType.FragmentShader,native));
        // Independent native baseline with only the requested distant eligibility
        // and density limit. Also compare every originally valid pixel to vanilla.
        string boundedNative=native.Replace("dist >= vf_RenderRange || dist < 1e-4","dist < 1e-4")
            .Replace("dist * vf_InvRenderRange","min(dist, vf_RenderRange) * vf_InvRenderRange");
        int extended=ProbeShader.Program((ShaderType.VertexShader,vertex),(ShaderType.FragmentShader,boundedNative));
        int maintained=ProbeShader.Program((ShaderType.VertexShader,vertex),(ShaderType.FragmentShader,ProbeShader.DeferredSource(Path.Combine(assets,"sheydermod/shaders/vfscatter.fsh"))));
        // Reference keeps native filtering verbatim but allows linear HDR addition.
        string hdrReference=originalComposite[..originalComposite.IndexOf("    vec3 additive",StringComparison.Ordinal)]+"return sceneColor+s;\n}";
        int[] composePrograms=new[]{hdrReference,composite}.Select(s=>ProbeShader.Program((ShaderType.VertexShader,vertex),(ShaderType.FragmentShader,
            "#version 430 core\nin vec2 texcoord;out vec4 color;\n"+s+"\nvoid main(){color=vec4(vf_compositeVolumetric(vec3(2,1,.5),texcoord),.37);}"))).ToArray();
        int[] textures={GL.GenTexture(),GL.GenTexture(),GL.GenTexture(),GL.GenTexture(),GL.GenTexture()};
        int fbo=GL.GenFramebuffer(),vao=GL.GenVertexArray(),ubo=GL.GenBuffer();
        float[] reference=new float[32*32*4],actual=new float[reference.Length],nativePixels=new float[reference.Length];
        int nativeValidPixels=0,extendedPixels=0;
        int groups=0;float maxError=0;
        void Texture(int index,int unit,int w,int h,float[] values, bool shadow=false) {
            GL.ActiveTexture(TextureUnit.Texture0+unit);GL.BindTexture(TextureTarget.Texture2D,textures[index]);GL.BindSampler(unit,0);
            GL.TexImage2D(TextureTarget.Texture2D,0,shadow?PixelInternalFormat.DepthComponent32f:PixelInternalFormat.Rgba32f,w,h,0,
                shadow?PixelFormat.DepthComponent:PixelFormat.Rgba,PixelType.Float,values);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureWrapS,(int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureWrapT,(int)TextureWrapMode.ClampToEdge);
            if(shadow) {
                GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureCompareMode,(int)TextureCompareMode.CompareRefToTexture);
                GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureCompareFunc,(int)DepthFunction.Lequal);
            }
        }
        void Float(int p,string n,float v)=>GL.Uniform1(GL.GetUniformLocation(p,n),v);
        void Int(int p,string n,int v)=>GL.Uniform1(GL.GetUniformLocation(p,n),v);
        void Read(int p,float[] pixels) {GL.UseProgram(p);GL.DrawArrays(PrimitiveType.Triangles,0,3);GL.ReadPixels(0,0,32,32,PixelFormat.Rgba,PixelType.Float,pixels);}
        void Compare(string name) {
            ++groups;
            for(int i=0;i<actual.Length;++i) {
                if(!float.IsFinite(actual[i])||Math.Abs(actual[i]-reference[i])>2e-5f)throw new Exception(name+": native parity "+i+" "+actual[i]+" vs "+reference[i]);
                maxError=Math.Max(maxError,Math.Abs(actual[i]-reference[i]));
            }
        }
        try {
            Texture(2,2,32,32,new float[reference.Length]);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer,fbo);GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,FramebufferAttachment.ColorAttachment0,TextureTarget.Texture2D,textures[2],0);
            GL.DrawBuffer(DrawBufferMode.ColorAttachment0);GL.ReadBuffer(ReadBufferMode.ColorAttachment0);GL.BindVertexArray(vao);GL.Viewport(0,0,32,32);
            GL.Disable(EnableCap.DepthTest);GL.Disable(EnableCap.Blend);GL.Disable(EnableCap.ScissorTest);
            if(GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer)!=FramebufferErrorCode.FramebufferComplete)throw new Exception("Native fog framebuffer");
            float[] shadows=new float[128*128];
            for(int y=0;y<128;++y)for(int x=0;x<128;++x)shadows[y*128+x]=(x>=42&&x<67&&y>=10&&y<115)?0:.8f;
            Texture(1,1,128,128,shadows,true);
            GL.UniformBlockBinding(maintained,GL.GetUniformBlockIndex(maintained,"DrtAtmosphere"),10);
            GL.BindBuffer(BufferTarget.UniformBuffer,ubo);GL.BufferData(BufferTarget.UniformBuffer,480,new float[120],BufferUsageHint.StaticDraw);GL.BindBufferBase(BufferRangeTarget.UniformBuffer,10,ubo);
            const float near=.3f,far=1500;float pa=-(far+near)/(far-near),pb=-2*far*near/(far-near);
            float[] inverseProjection={1,0,0,0,0,.5625f,0,0,0,0,0,1/pb,0,0,-1,pa/pb},inverseView=new float[16],inverseWorld=new float[16];
            float[] shadowMatrix={1f/780,0,0,0,0,1f/780,0,0,0,0,1f/780,0,.5f,.5f,.75f,1};
            foreach(int p in new[]{vanilla,extended,maintained}) {
                GL.UseProgram(p);Int(p,"depthTexture",0);Int(p,"shadowMapFar",1);Int(p,"vf_LiquidDepth",2);
                GL.Uniform3(GL.GetUniformLocation(p,"vf_FrontColor"),.8f,.5f,.2f);GL.Uniform3(GL.GetUniformLocation(p,"vf_BackColor"),.2f,.4f,.8f);
                GL.Uniform3(GL.GetUniformLocation(p,"vf_SunDir"),0f,.6f,.8f);
                Float(p,"vf_Intensity",.2f);Float(p,"vf_MaxRange",390);Float(p,"vf_RenderRange",512);Float(p,"vf_InvRenderRange",1f/512);
                GL.UniformMatrix4(GL.GetUniformLocation(p,"vf_ToShadowSpaceFar"),1,false,shadowMatrix);
            }
            foreach(int steps in new[]{1,4,16,64})foreach(float distance in new[]{4f,12f,40f,80f,200f,390f,800f,1500f})
            foreach(float camera in new[]{0f,.1f,80f})foreach(float angle in new[]{-.4f,0f,.4f}) {
                float d=(-pa+pb/distance+1)*.5f;
                Texture(0,0,1,1,new[]{d,0f,0,1});
                Texture(3,2,1,1,new[]{(-pa+pb/(distance*.5f)+1)*.5f,0f,0,1});
                Array.Clear(inverseView);float c=MathF.Cos(angle),s=MathF.Sin(angle);
                inverseView[0]=c;inverseView[2]=-s;inverseView[5]=1;inverseView[8]=s;inverseView[10]=c;inverseView[12]=camera;inverseView[14]=camera*.1f;inverseView[15]=1;
                Mat4f.Mul(inverseWorld,inverseView,inverseProjection);
                foreach(int p in new[]{vanilla,extended,maintained}) {
                    GL.UseProgram(p);GL.UniformMatrix4(GL.GetUniformLocation(p,"vf_InvViewProj"),1,false,inverseWorld);
                    GL.Uniform3(GL.GetUniformLocation(p,"vf_CameraWorldPos"),camera,0f,camera*.1f);
                    GL.Uniform3(GL.GetUniformLocation(p,"vf_ShadowRayStart"),.5f+camera/780,.5f,.75f+camera*.1f/780);
                    Int(p,"vf_StepCount",steps);Float(p,"vf_InvSteps",1f/steps);Float(p,"vf_DistantDensity",steps==4?2:1);
                    Float(p,"vf_HasWaterDepth",steps==64?1:0);
                }
                Read(extended,reference);Read(maintained,actual);Compare("configured steps, bounded distant rays, affine shadow camera and liquid parity");
                Read(vanilla,nativePixels);
                for(int i=0;i<actual.Length;i+=4) {
                    if(nativePixels[i+3]>.5f) {
                        ++nativeValidPixels;
                        for(int channel=0;channel<4;++channel)if(Math.Abs(actual[i+channel]-nativePixels[i+channel])>2e-5f)
                            throw new Exception("Originally valid native terrain pixel changed");
                    }
                    else if(actual[i+3]>.5f) ++extendedPixels;
                }
                if(steps==16&&distance==80&&camera==0&&angle==0) {
                    Read(maintained,reference);Read(maintained,actual);Compare("stationary native Bayer pattern");
                }
            }
            // Native mip smoothing and coverage normalization over a mixed field.
            float[] scatter=new float[reference.Length],depth=new float[reference.Length];
            for(int y=0;y<32;++y)for(int x=0;x<32;++x) {
                int i=(y*32+x)*4;float valid=(x+y)%5==0?0:1;
                scatter[i]=(x%4)*valid;scatter[i+1]=.5f*valid;scatter[i+2]=2*valid;scatter[i+3]=valid;
                depth[i]=x<15?.4f:.97f;depth[i+3]=1;
            }
            Texture(4,0,32,32,scatter);GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.LinearMipmapLinear);
            GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
            GL.GetTexLevelParameter(TextureTarget.Texture2D,3,GetTextureParameter.TextureWidth,out int mipWidth);
            if(mipWidth!=4)throw new Exception("Native blur mip chain unavailable");
            Texture(0,1,32,32,depth);
            foreach(float radius in new[]{0f,.5f,1f,2.56f,5f,8f}) foreach(float enabled in new[]{0f,1f}) {
                foreach(int p in composePrograms) {GL.UseProgram(p);Int(p,"vfScatterTex",0);Int(p,"vfDepthTex",1);Float(p,"vf_BlurRadius",radius);Float(p,"vf_Enabled",enabled);}
                Read(composePrograms[0],reference);Read(composePrograms[1],actual);Compare("native mip/bilateral/validity HDR composite parity");
                for(int i=3;i<actual.Length;i+=4)if(Math.Abs(actual[i]-.37f)>1e-6)throw new Exception("Scene alpha changed");
            }
            if(GL.GetError()!=ErrorCode.NoError)throw new Exception("Native fog parity GL error");
            if(nativeValidPixels==0||extendedPixels==0)throw new Exception("Missing original terrain or newly enabled distant-ray coverage");
            string results=Path.Combine("Tests","results","volumetric-sky-horizon");Directory.CreateDirectory(results);
            File.WriteAllText(Path.Combine(results,"native-parity.json"),JsonSerializer.Serialize(new{groups,rgbaComparisons=groups*actual.Length,maxError,
                sourceMarchMatchesInstalled=true,distantEligibilityAndDensityExtended=true,nativeValidPixels,extendedPixels,
                sourceFilterMatchesInstalled=true,retiredFilterOwnerAbsent=true,steps=new[]{1,4,16,64},radii=new[]{0f,.5f,1f,2.56f,5f,8f},
                liveWorldVerified=false},new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine($"PASS native fog GPU parity: {groups} groups / {groups*actual.Length} RGBA comparisons, max error {maxError:G6}; {nativeValidPixels} original terrain pixels unchanged; {extendedPixels} distant pixels enabled; configured steps, camera, water, Bayer, mip/bilateral/alpha/HDR parity");
        }
        finally {
            GL.UseProgram(0);foreach(int p in new[]{vanilla,extended,maintained}.Concat(composePrograms))GL.DeleteProgram(p);
            foreach(int t in textures)GL.DeleteTexture(t);GL.DeleteFramebuffer(fbo);GL.DeleteVertexArray(vao);GL.DeleteBuffer(ubo);
        }
    }
}
