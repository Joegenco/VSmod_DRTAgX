using System;
using System.Collections.Generic;
using System.IO;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using Vintagestory.API.MathTools;

internal static class GpuChecks
{
    internal const string Fullscreen="""
        #version 430 core
        void main(){vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);gl_Position=vec4(p*2.0-1.0,0,1);}
        """;
    internal static GameWindow Window()
    {
        var w=new GameWindow(GameWindowSettings.Default,new NativeWindowSettings {StartVisible=false,ClientSize=new Vector2i(32,32),API=ContextAPI.OpenGL,APIVersion=new Version(4,3),Profile=ContextProfile.Core});
        w.MakeCurrent();GL.LoadBindings(new OpenTK.Windowing.GraphicsLibraryFramework.GLFWBindingsContext());return w;
    }
    internal static int Compile(params (ShaderType Type,string Source)[] stages)
    {
        int p=GL.CreateProgram();
        foreach(var stage in stages){int s=GL.CreateShader(stage.Type);GL.ShaderSource(s,stage.Source);GL.CompileShader(s);GL.GetShader(s,ShaderParameter.CompileStatus,out int ok);if(ok==0)throw new Exception(GL.GetShaderInfoLog(s));GL.AttachShader(p,s);GL.DeleteShader(s);}
        GL.LinkProgram(p);GL.GetProgram(p,GetProgramParameterName.LinkStatus,out int linked);if(linked==0)throw new Exception(GL.GetProgramInfoLog(p));return p;
    }
    internal static string Expand(string assets,string path)
    {
        var seen=new HashSet<string>();
        string Read(string file)
        {
            if(!seen.Add(Path.GetFileName(file)))return "";
            string text=File.ReadAllText(file);var lines=text.Split('\n');
            for(int i=0;i<lines.Length;++i)
            {
                string line=lines[i].Trim();if(!line.StartsWith("#include "))continue;string name=line[9..].Trim();
                string? found=null;
                foreach(var dir in new[]{"game/shaderincludes","game/shaders","drtagx/shaders","drtagx/shaders/deferred","drtagx/shaders/lighting","drtagx/shaders/atmosphere"})if(File.Exists(Path.Combine(assets,dir,name))){found=Path.Combine(assets,dir,name);break;}
                found??=Path.Combine(Environment.GetEnvironmentVariable("VINTAGE_STORY")!,"assets/game/shaderincludes",name);
                lines[i]=Read(found);
            }
            return string.Join('\n',lines);
        }
        return Read(Path.Combine(assets,path));
    }
    internal static float[] Camera(float fov,float aspect)
    {float[] p=new float[16];Mat4f.Perspective(p,fov*MathF.PI/180,aspect,.05f,128);return p;}
    internal static int Buffer(float[] data,int binding,BufferRangeTarget target=BufferRangeTarget.ShaderStorageBuffer)
    {int b=GL.GenBuffer();GL.BindBuffer(BufferTarget.ShaderStorageBuffer,b);GL.BufferData(BufferTarget.ShaderStorageBuffer,data.Length*4,data,BufferUsageHint.DynamicDraw);GL.BindBufferBase(target,binding,b);return b;}
    internal static int Texture(int w,int h,PixelInternalFormat format=PixelInternalFormat.DepthComponent32f,float[]? values=null)
    {int t=GL.GenTexture();GL.BindTexture(TextureTarget.Texture2D,t);GL.TexImage2D(TextureTarget.Texture2D,0,format,w,h,0,format==PixelInternalFormat.Rgba32f?PixelFormat.Rgba:format==PixelInternalFormat.R32f?PixelFormat.Red:PixelFormat.DepthComponent,PixelType.Float,values);GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.Nearest);GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)TextureMagFilter.Nearest);return t;}
    internal static void DispatchFit(int tile,int final,int depth,int output,int width,int height,float[] camera,float[] lights,int size)
    {
        var inverse=new float[16];Mat4f.Invert(inverse,camera);int tw=(width+63)/64,tc=tw*((height+63)/64);
        GL.ActiveTexture(TextureUnit.Texture13);GL.BindTexture(TextureTarget.Texture2D,depth);GL.BindSampler(13,0);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer,7,output);
        for(int pass=0;pass<2;++pass)
        {
            int p=pass==0?tile:final;GL.UseProgram(p);GL.Uniform4(GL.GetUniformLocation(p,"handLight[0]"),2,lights);GL.Uniform1(GL.GetUniformLocation(p,"tileCount"),tc);
            if(pass==0){GL.UniformMatrix4(GL.GetUniformLocation(p,"inverseProjection"),1,false,inverse);GL.Uniform2(GL.GetUniformLocation(p,"dimensions"),width,height);GL.Uniform1(GL.GetUniformLocation(p,"tileWidth"),tw);GL.Uniform1(GL.GetUniformLocation(p,"terrainDepth"),13);}
            else GL.Uniform1(GL.GetUniformLocation(p,"heldSize"),size);
            GL.DispatchCompute(pass==0?tc:2,1,1);GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit|MemoryBarrierFlags.UniformBarrierBit|MemoryBarrierFlags.BufferUpdateBarrierBit);
        }
    }
    internal static float DepthAt(float depth,float[] p)=>.5f*(1-p[10]+p[14]/depth);

    internal static void Run(string assets)
    {
        using var window=Window();Console.WriteLine("GPU "+GL.GetString(StringName.Renderer));
        string compute=File.ReadAllText(Path.Combine(assets,"drtagx/shaders/movinglightfit.csh"));
        int tile=Compile((ShaderType.ComputeShader,compute)),final=Compile((ShaderType.ComputeShader,compute.Replace("#version 430 core","#version 430 core\n#define DRT_FINAL_FIT")));
        const int width=192,height=113;
        int heldSize=6*MovingLightShadowRenderer.FaceSizeForDimensions(1920,1080);
        int tiles=Buffer(new float[((width+15)/16)*((height+15)/16)*16],6);
        int output=Buffer(new float[40],7);
        float[] values=new float[width*height],read=new float[40],lights={.4f,-.2f,0,22,-.4f,-.2f,0,18};
        foreach(float fov in new[]{50f,90f,130f})foreach(float aspect in new[]{.75f,1.777f,3.5f})
        {
            float[] camera=Camera(fov,aspect),inverse=new float[16];Mat4f.Invert(inverse,camera);
            for(int y=0;y<height;++y)for(int x=0;x<width;++x)values[x+y*width]=DepthAt(x<width/4?.051f:x<width/2?1.0f:12f,camera);
            int depth=Texture(width,height,values:values);DispatchFit(tile,final,depth,output,width,height,camera,lights,heldSize);
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer,output);GL.GetBufferSubData(BufferTarget.ShaderStorageBuffer,IntPtr.Zero,160,read);
            for(int hand=0;hand<2;++hand)
            {
                bool covered=read[32+hand*4]==1;
                for(int y=0;y<height&&covered;++y)for(int x=0;x<width;++x)
                {
                    float d=values[x+y*width],nx=(x+.5f)/width*2-1,ny=(y+.5f)/height*2-1;
                    float w=inverse[3]*nx+inverse[7]*ny+inverse[11]*(d*2-1)+inverse[15];
                    float ex=(inverse[0]*nx+inverse[4]*ny+inverse[8]*(d*2-1)+inverse[12])/w;
                    float ey=(inverse[1]*nx+inverse[5]*ny+inverse[9]*(d*2-1)+inverse[13])/w;
                    float ez=(inverse[2]*nx+inverse[6]*ny+inverse[10]*(d*2-1)+inverse[14])/w;
                    float qx=ex-lights[hand*4],qy=ey-lights[hand*4+1],range=lights[hand*4+3];if(qx*qx+qy*qy+ez*ez>range*range)continue;
                    int m=hand*16;float cx=read[m]*ex+read[m+8]*ez+read[m+12],cy=read[m+5]*ey+read[m+9]*ez+read[m+13];
                    float cz=read[m+10]*ez+read[m+14],cw=-ez;
                    if(Math.Abs(cx)>cw||Math.Abs(cy)>cw||Math.Abs(cz)>cw)covered=false;
                }
                Program.Check(covered,$"GPU receiver coverage fov={fov}, aspect={aspect}, hand={hand}");
            }
            GL.DeleteTexture(depth);
        }
        Array.Fill(values,1);int empty=Texture(width,height,values:values);DispatchFit(tile,final,empty,output,width,height,Camera(90,1.77f),lights,heldSize);
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer,output);GL.GetBufferSubData(BufferTarget.ShaderStorageBuffer,IntPtr.Zero,160,read);
        Program.Check(read[32]==0&&read[36]==0,"empty current-frame depth clears both fits without stale matrix reuse");
        // Invalid input must leave the uploaded conservative fallback intact.
        float[] fallback=new float[40];var proj=Camera(90,1.77f);
        for(int h=0;h<2;++h){HeldProjectionFit.Fallback(proj,new float[]{lights[h*4],lights[h*4+1],0},lights[h*4+3],fallback,h*16);fallback[32+h*4]=1;}
        values[0]=float.NaN;int bad=Texture(width,height,PixelInternalFormat.R32f,values);
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer,output);GL.BufferSubData(BufferTarget.ShaderStorageBuffer,IntPtr.Zero,160,fallback);
        DispatchFit(tile,final,bad,output,width,height,proj,lights,heldSize);GL.BindBuffer(BufferTarget.ShaderStorageBuffer,output);GL.GetBufferSubData(BufferTarget.ShaderStorageBuffer,IntPtr.Zero,160,read);
        Program.Check(read[32]==1&&read[36]==1&&read[0]==fallback[0]&&read[16]==fallback[16],"invalid depth retains current conservative fallback");
        CheckState();CheckAllocation();CheckLighting(assets,output);CheckPair(assets);CheckGutters(assets,output);
        Program.Check(GL.GetError()==ErrorCode.NoError,"GPU checks have no OpenGL errors");
        GL.DeleteTexture(empty);GL.DeleteTexture(bad);GL.DeleteBuffer(tiles);GL.DeleteBuffer(output);GL.DeleteProgram(tile);GL.DeleteProgram(final);
    }

    private static void CheckState()
    {
        int alignment=GL.GetInteger(GetPName.UniformBufferOffsetAlignment);int b=Buffer(new float[(alignment+512)/4],5,BufferRangeTarget.UniformBuffer);
        GL.BindBufferRange(BufferRangeTarget.UniformBuffer,5,b,(IntPtr)alignment,(IntPtr)160);
        GL.Viewport(2,3,31,29);GL.Scissor(4,5,19,21);GL.Enable(EnableCap.ScissorTest);GL.Enable(EnableCap.Blend);GL.Enable(EnableCap.PolygonOffsetFill);
        GL.DepthMask(false);GL.ClearDepth(.37);GL.ActiveTexture(TextureUnit.Texture11);
        var saved=new ShadowGlState(false);saved.Capture();
        GL.BindBufferBase(BufferRangeTarget.UniformBuffer,5,0);GL.Viewport(0,0,1,1);GL.Disable(EnableCap.Blend);GL.Disable(EnableCap.ScissorTest);GL.Disable(EnableCap.PolygonOffsetFill);GL.DepthMask(true);GL.ClearDepth(1);saved.Dispose();
        GL.GetInteger64(GetIndexedPName.UniformBufferStart,5,out long start);GL.GetInteger64(GetIndexedPName.UniformBufferSize,5,out long size);
        int[] v=new int[4];GL.GetInteger(GetPName.Viewport,v);GL.GetBoolean(GetPName.DepthWritemask,out bool depth);
        Program.Check(start==alignment&&size==160,"RAII restores UBO subrange offset and size");
        Program.Check(v[0]==2&&v[1]==3&&v[2]==31&&v[3]==29&&GL.IsEnabled(EnableCap.Blend)&&GL.IsEnabled(EnableCap.ScissorTest)&&GL.IsEnabled(EnableCap.PolygonOffsetFill)&&!depth&&Math.Abs(GL.GetDouble(GetPName.DepthClearValue)-.37)<1e-6&&GL.GetInteger(GetPName.ActiveTexture)==(int)TextureUnit.Texture11,"RAII restores viewport, flags, masks, clear depth and active texture");
        GL.BindBufferBase(BufferRangeTarget.UniformBuffer,5,0);GL.DeleteBuffer(b);GL.Disable(EnableCap.ScissorTest);GL.Disable(EnableCap.Blend);GL.Disable(EnableCap.PolygonOffsetFill);GL.DepthMask(true);GL.ClearDepth(1);
        int storeAlign=GL.GetInteger(GetPName.ShaderStorageBufferOffsetAlignment),store=Buffer(new float[(storeAlign+512)/4],6);
        GL.BindBufferRange(BufferRangeTarget.ShaderStorageBuffer,6,store,(IntPtr)storeAlign,(IntPtr)128);
        var ssbo=IndexedBufferState.Capture(BufferRangeTarget.ShaderStorageBuffer,6);GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer,6,0);ssbo.Restore();
        GL.GetInteger64(GetIndexedPName.ShaderStorageBufferStart,6,out start);GL.GetInteger64(GetIndexedPName.ShaderStorageBufferSize,6,out size);
        Program.Check(start==storeAlign&&size==128,"temporary compute SSBO restore preserves subrange offset and size");GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer,6,0);GL.DeleteBuffer(store);
    }

    private static void CheckAllocation()
    {
        // DispatchProxy resolves all public interface signatures, including
        // optional UI types; load installed managed dependencies read-only.
        System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (context, name) => {
            string install = Environment.GetEnvironmentVariable("VINTAGE_STORY")!;
            string path = Path.Combine(install,"Lib",name.Name+".dll");
            if (!File.Exists(path)) path = Path.Combine(install,name.Name+".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
        // Fail if resolution selection reads internal terrain buffers/scale.
        // Final output dimensions are the only supported inputs in this proxy.
        var render = RenderSizeProbeProxy.Make<Vintagestory.API.Client.IRenderAPI>(m => m.Name switch {
            "get_FrameWidth" => 1920, "get_FrameHeight" => 1080,
            _ => throw new Exception("Unexpected internal resolution input: " + m.Name)
        });
        var api = RenderSizeProbeProxy.Make<Vintagestory.API.Client.ICoreClientAPI>(m => m.Name == "get_Render"
            ? render : throw new Exception(m.Name));
        using var owner = new MovingLightShadowRenderer();
        const System.Reflection.BindingFlags fields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        int[] hands = (int[])typeof(MovingLightShadowRenderer).GetField("_handSources",fields)!.GetValue(owner)!;
        var ensure = typeof(MovingLightShadowRenderer).GetMethod("Ensure",fields)!;
        int Value(string name) => (int)typeof(MovingLightShadowRenderer).GetField(name,fields)!.GetValue(owner)!;
        Array.Fill(hands,-1);
        Program.Check((bool)ensure.Invoke(owner,[api,1])! && Value("_size")==96 && Value("_width")==208 && Value("_height")==312,
            "production cube-only allocation uses final output dimensions and no handheld strip");
        hands[0]=0;
        Program.Check((bool)ensure.Invoke(owner,[api,1])! && Value("_width")==792 && Value("_height")==584,
            "production one-hand allocation contains a 576px projector beside unchanged 96px cube faces");
        hands[1]=1;
        Program.Check((bool)ensure.Invoke(owner,[api,2])! && Value("_width")==1000 && Value("_height")==1168,
            "production two-hand allocation reserves separate 576px projector rows");
    }

    internal static string DynamicSource(string assets)=>Expand(assets,"drtagx/shaders/deferred/drtagx_deferred_dynamiclights.fsh");
    private static void CheckPair(string assets)
    {
        string body="uniform vec3 pairA,pairB;void main(){vec3 c[16];c[0]=pairA;c[1]=pairB;color=vec4(drtAccumulateMovingPair(pairA,pairB)-drtAccumulateDynamic(c,2),1);}";
        int program=Compile((ShaderType.VertexShader,Fullscreen),(ShaderType.FragmentShader,LightingFragment(assets,body)));
        int vao=GL.GenVertexArray(),target=Texture(1,1,PixelInternalFormat.Rgba32f),fbo=GL.GenFramebuffer();GL.BindVertexArray(vao);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer,fbo);GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,FramebufferAttachment.ColorAttachment0,TextureTarget.Texture2D,target,0);GL.DrawBuffer(DrawBufferMode.ColorAttachment0);GL.Viewport(0,0,1,1);GL.UseProgram(program);
        var rng=new Random(913);float[] pixel=new float[4];bool equal=true;
        for(int i=0;i<500;++i)
        {
            float a=(float)rng.NextDouble(),b=i<250?a*(.94f+.12f*(float)rng.NextDouble()):(float)rng.NextDouble();
            float[] first={a,a*.5f,a*.2f},second={b*.2f,b*.4f,b};
            if(i<250){float lumA=first[0]*.2126f+first[1]*.7152f+first[2]*.0722f,lumB=second[0]*.2126f+second[1]*.7152f+second[2]*.0722f;for(int j=0;j<3;++j)second[j]*=lumA/lumB*(.94f+.12f*(float)rng.NextDouble());}
            if(i==0)Array.Clear(first);if(i==1)Array.Clear(second);if(i==2){Array.Clear(first);Array.Clear(second);}
            GL.Uniform3(GL.GetUniformLocation(program,"pairA"),first[0],first[1],first[2]);GL.Uniform3(GL.GetUniformLocation(program,"pairB"),second[0],second[1],second[2]);GL.DrawArrays(PrimitiveType.Triangles,0,3);GL.ReadPixels(0,0,1,1,PixelFormat.Rgba,PixelType.Float,pixel);
            equal&=Math.Abs(pixel[0])<1e-6&&Math.Abs(pixel[1])<1e-6&&Math.Abs(pixel[2])<1e-6;
        }
        Program.Check(equal,"two-source fast path matches general harmonic/tie blending across 500 pairs");
        GL.DeleteProgram(program);GL.DeleteFramebuffer(fbo);GL.DeleteTexture(target);GL.DeleteVertexArray(vao);
    }
    internal static string LightingFragment(string assets,string main)=>"#version 430 core\nuniform mat4 invModelViewMatrix = mat4(1.0);\n"+Expand(assets,"drtagx/shaders/deferred/drtagx_deferred_uniforms.fsh")+"\n"+Expand(assets,"drtagx/shaders/lighting/drtagx_light_balance.ash")+"\n"+Expand(assets,"drtagx/shaders/deferred/drtagx_deferred_cube.fsh")+"\n"+DynamicSource(assets)+"\nuniform vec3 receiver; uniform vec3 probeNormal; uniform int probeMode;out vec4 color;\n"+main;
    private static void CheckGutters(string assets,int output)
    {
        int p=Compile((ShaderType.VertexShader,Fullscreen),(ShaderType.FragmentShader,LightingFragment(assets,"uniform vec3 shadowUv;uniform int face;uniform bool held;uniform int slot;void main(){color=vec4(drtMovingPcf(shadowUv,vec2(0),slot,face,held));}")));
        int vao=GL.GenVertexArray(),color=Texture(1,1,PixelInternalFormat.Rgba32f),atlas=Texture(520,592,PixelInternalFormat.DepthComponent24),fbo=GL.GenFramebuffer();
        GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureCompareMode,(int)TextureCompareMode.CompareRefToTexture);GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureCompareFunc,(int)DepthFunction.Lequal);GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.Linear);GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)TextureMagFilter.Linear);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer,fbo);GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,FramebufferAttachment.ColorAttachment0,TextureTarget.Texture2D,color,0);GL.DrawBuffer(DrawBufferMode.ColorAttachment0);GL.BindVertexArray(vao);GL.Viewport(0,0,1,1);GL.UseProgram(p);
        GL.Uniform1(GL.GetUniformLocation(p,"drtShadowMaps"),15);GL.Uniform4(GL.GetUniformLocation(p,"drtMovingAtlasInfo"),520f,592f,48f,56f);GL.Uniform1(GL.GetUniformLocation(p,"drtMovingAtlasColumns"),2);GL.BindBufferBase(BufferRangeTarget.UniformBuffer,5,output);
        float[] data=new float[520*592],pixel=new float[4];
        for(int quality=0;quality<2;++quality)
        for(int face=0;face<8;++face)
        {
            GL.Uniform1(GL.GetUniformLocation(p,"drtPerformanceMode"),quality);
            Array.Clear(data);int bx=face<6?4+(face%2)*56:228,by=face<6?4+(face/2)*56:4+(face-6)*296,size=face<6?48:288;
            for(int y=0;y<size;++y)for(int x=0;x<size;++x)data[(by+y)*520+bx+x]=1;
            GL.ActiveTexture(TextureUnit.Texture15);GL.BindTexture(TextureTarget.Texture2D,atlas);GL.TexSubImage2D(TextureTarget.Texture2D,0,0,0,520,592,PixelFormat.DepthComponent,PixelType.Float,data);
            GL.Uniform1(GL.GetUniformLocation(p,"face"),face<6?face:0);GL.Uniform1(GL.GetUniformLocation(p,"held"),face>=6?1:0);GL.Uniform1(GL.GetUniformLocation(p,"slot"),face<6?0:face-6);bool clear=true;
            foreach(float u in new[]{.00001f,.5f,.99999f})foreach(float v in new[]{.00001f,.5f,.99999f})
            {GL.Uniform3(GL.GetUniformLocation(p,"shadowUv"),u,v,.5f);GL.DrawArrays(PrimitiveType.Triangles,0,3);GL.ReadPixels(0,0,1,1,PixelFormat.Rgba,PixelType.Float,pixel);clear&=pixel[0]==1;}
            Program.Check(clear,$"PCF remains inside cleared map with occluded neighbors/gutters, map={face}, performance={quality}");
        }
        GL.DeleteProgram(p);GL.DeleteFramebuffer(fbo);GL.DeleteVertexArray(vao);GL.DeleteTexture(atlas);GL.DeleteTexture(color);
    }
    private static void CheckLighting(string assets,int output)
    {
        int program=Compile((ShaderType.VertexShader,Fullscreen),(ShaderType.FragmentShader,LightingFragment(assets,"void main(){color=probeMode==0?vec4(drtCurrentFrameLights(receiver,probeNormal),1):vec4(drtCurrentFrameVisibility(receiver,drtPointPos[0],0,probeNormal));}")));
        int color=Texture(1,1,PixelInternalFormat.Rgba32f),fbo=GL.GenFramebuffer(),vao=GL.GenVertexArray();GL.BindVertexArray(vao);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer,fbo);GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,FramebufferAttachment.ColorAttachment0,TextureTarget.Texture2D,color,0);GL.DrawBuffer(DrawBufferMode.ColorAttachment0);GL.Disable(EnableCap.DepthTest);GL.Viewport(0,0,1,1);
        GL.UseProgram(program);GL.Uniform1(GL.GetUniformLocation(program,"drtPointCount"),1);GL.Uniform3(GL.GetUniformLocation(program,"drtPointPos[0]"),0f,0f,0f);GL.Uniform4(GL.GetUniformLocation(program,"drtPointRadiance[0]"),1f,.5f,.25f,20f);GL.Uniform3(GL.GetUniformLocation(program,"receiver"),0f,0f,-4f);GL.Uniform3(GL.GetUniformLocation(program,"probeNormal"),0f,0f,1f);
        GL.BindBufferBase(BufferRangeTarget.UniformBuffer,5,output);
        float[] pixel=new float[4];
        void Draw(){GL.DrawArrays(PrimitiveType.Triangles,0,3);GL.ReadPixels(0,0,1,1,PixelFormat.Rgba,PixelType.Float,pixel);}
        GL.Uniform1(GL.GetUniformLocation(program,"drtShadowCount"),0);Draw();float baseline=pixel[0];
        Program.Check(baseline>0&&Math.Abs(pixel[1]/baseline-.5f)<1e-5&&Math.Abs(pixel[2]/baseline-.25f)<1e-5,"prepared radiance retains source color ratios and finite brightness");
        // Mix in a second independent hue and verify harmonic accumulation stays finite.
        GL.Uniform1(GL.GetUniformLocation(program,"drtPointCount"),2);GL.Uniform3(GL.GetUniformLocation(program,"drtPointPos[1]"),0f,0f,0f);GL.Uniform4(GL.GetUniformLocation(program,"drtPointRadiance[1]"),.25f,.5f,1f,20f);Draw();Program.Check(pixel[0]>baseline&&pixel[2]>baseline*.25f&&float.IsFinite(pixel[0]+pixel[2]),"both hands preserve harmonic color blending");
        GL.Uniform1(GL.GetUniformLocation(program,"drtPointCount"),1);
        int atlas=Texture(408,296,PixelInternalFormat.DepthComponent24);
        GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureCompareMode,(int)TextureCompareMode.CompareRefToTexture);GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureCompareFunc,(int)DepthFunction.Lequal);GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.Linear);GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)TextureMagFilter.Linear);
        int depthFbo=GL.GenFramebuffer();GL.BindFramebuffer(FramebufferTarget.Framebuffer,depthFbo);GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,FramebufferAttachment.DepthAttachment,TextureTarget.Texture2D,atlas,0);GL.DrawBuffer(DrawBufferMode.None);GL.ReadBuffer(ReadBufferMode.None);GL.ClearDepth(0);GL.Clear(ClearBufferMask.DepthBufferBit);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer,fbo);GL.ActiveTexture(TextureUnit.Texture15);GL.BindTexture(TextureTarget.Texture2D,atlas);
        GL.Uniform1(GL.GetUniformLocation(program,"drtShadowMaps"),15);GL.Uniform1(GL.GetUniformLocation(program,"drtShadowCount"),1);GL.Uniform1(GL.GetUniformLocation(program,"drtShadowSlot[0]"),0);GL.Uniform1(GL.GetUniformLocation(program,"drtShadowRange[0]"),20f);GL.Uniform4(GL.GetUniformLocation(program,"drtMovingAtlasInfo"),408f,296f,48f,56f);GL.Uniform1(GL.GetUniformLocation(program,"drtMovingAtlasColumns"),1);
        GL.Uniform1(GL.GetUniformLocation(program,"probeMode"),1);
        GL.Uniform1(GL.GetUniformLocation(program,"drtShadowKind[0]"),0);Draw();Program.Check(pixel[0]==0,"single-range cube map samples occluder");
        GL.Uniform1(GL.GetUniformLocation(program,"drtShadowKind[0]"),1);Draw();Program.Check(pixel[0]==0,"held map samples occluder through shared fitted-matrix ABI");
        // A blocker 3 cm in front of a receiver must survive the held near-plane
        // precision margin. This fails with the old .00002 projected bias.
        float[] heldBlock=new float[40];var heldCamera=Camera(90,1);
        HeldProjectionFit.Fallback(heldCamera,new float[]{0,0,0},20,heldBlock,0);heldBlock[32]=1;
        GL.BindBuffer(BufferTarget.UniformBuffer,output);GL.BufferSubData(BufferTarget.UniformBuffer,IntPtr.Zero,160,heldBlock);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer,depthFbo);GL.ClearDepth(.5*(1-heldBlock[10]+heldBlock[14]/3.97));GL.Clear(ClearBufferMask.DepthBufferBit);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer,fbo);
        for(int quality=0;quality<2;++quality) {
            GL.Uniform1(GL.GetUniformLocation(program,"drtPerformanceMode"),quality);
            Draw();Program.Check(pixel[0]==0,$"held near-plane precision preserves 3 cm close blocker, performance={quality}");
        }
        // Sloped receiving planes store an affine projected depth. Filter taps
        // must track that plane instead of creating illuminated surface bands.
        for(int quality=0;quality<2;++quality)
        for(int kind=0;kind<2;++kind)
        {
            GL.Uniform1(GL.GetUniformLocation(program,"drtPerformanceMode"),quality);
            GL.Uniform1(GL.GetUniformLocation(program,"drtShadowKind[0]"),kind);
            float px=kind==0?.94f:heldBlock[0],py=kind==0?.94f:heldBlock[5];
            float a=kind==0?(20.5f+.1f)/(.1f-20.5f):heldBlock[10],b=kind==0?.2f*20.5f/(.1f-20.5f):heldBlock[14];
            float center=.5f*(1-a+b/4),gx=b*1.25f/(px*-4),gy=b*.6f/(py*-4);
            float[] planar=new float[408*296];Array.Fill(planar,0);
            int mapSize=kind==0?48:288,baseY=kind==0?116:4,baseX=kind==0?60:116;
            for(int y=0;y<mapSize;++y)for(int x=0;x<mapSize;++x)
                planar[(baseY+y)*408+baseX+x]=Math.Clamp(center+gx*((x+.5f)/mapSize-.5f)+gy*((y+.5f)/mapSize-.5f),0,1);
            GL.ActiveTexture(TextureUnit.Texture15);GL.BindTexture(TextureTarget.Texture2D,atlas);GL.TexSubImage2D(TextureTarget.Texture2D,0,0,0,408,296,PixelFormat.DepthComponent,PixelType.Float,planar);
            // Face -Z maps vertical side to -Y; use the matching plane normal.
            GL.Uniform3(GL.GetUniformLocation(program,"probeNormal"),1.25f,kind==0?-.6f:.6f,1f);Draw();
            Program.Check(pixel[0]>.999f,$"receiver-plane PCF keeps steep illuminated surface free of bands, kind={kind}, performance={quality}");
        }
        GL.Uniform1(GL.GetUniformLocation(program,"drtPerformanceMode"),0);
        GL.Uniform1(GL.GetUniformLocation(program,"drtShadowKind[0]"),1);
        GL.Uniform3(GL.GetUniformLocation(program,"receiver"),0f,0f,4f);GL.Uniform3(GL.GetUniformLocation(program,"probeNormal"),0f,0f,-1f);GL.Uniform1(GL.GetUniformLocation(program,"probeMode"),0);Draw();Program.Check(Math.Abs(pixel[0]-baseline)<1e-5,"held projection never turns omnidirectional emission into spotlight");
        GL.DeleteFramebuffer(depthFbo);GL.DeleteTexture(atlas);GL.DeleteFramebuffer(fbo);GL.DeleteTexture(color);GL.DeleteVertexArray(vao);GL.DeleteProgram(program);
    }
}


// Narrow public-API input adapter; no engine construction or private ABI guesses.
public class RenderSizeProbeProxy : System.Reflection.DispatchProxy
{
    private Func<System.Reflection.MethodInfo, object?> _invoke = null!;
    internal static T Make<T>(Func<System.Reflection.MethodInfo, object?> invoke) where T : class
    {
        T value = Create<T, RenderSizeProbeProxy>();
        ((RenderSizeProbeProxy)(object)value)._invoke = invoke;
        return value;
    }
    protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args) => _invoke(method!);
}
