using System;
using System.IO;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.MathTools;

// Isolated reproducible cost comparison: identical conventional resident mesh,
// current-frame receiver depth, production shadow shaders and dynamic relighting.
// Does not claim gameplay FPS or model native pool preparation CPU cost.
internal static class MovingBenchmark
{
    internal static void Run(string assets,string before)
    {
        using var window=GpuChecks.Window();Console.WriteLine("BENCH GPU "+GL.GetString(StringName.Renderer));
        string main="""
            uniform sampler2D receiverDepth;uniform vec2 frameSize;uniform mat4 inverseProjection;
            void main(){vec2 uv=gl_FragCoord.xy/frameSize;float d=texelFetch(receiverDepth,ivec2(gl_FragCoord.xy),0).r;
                if(d==1.0){color=vec4(0);return;}vec4 v=inverseProjection*vec4(uv*2.0-1.0,d*2.0-1.0,1);
                color=vec4(drtCurrentFrameLights(v.xyz/v.w,vec3(0,0,1)),1);}
            """;
        int newer=GpuChecks.Compile((ShaderType.VertexShader,GpuChecks.Fullscreen),(ShaderType.FragmentShader,GpuChecks.LightingFragment(assets,main)));
        string oldFragment="#version 430 core\n"+GpuChecks.Expand(before,"drtagx/shaders/deferred/drtagx_deferred_uniforms.fsh")+"\n"+GpuChecks.Expand(before,"drtagx/shaders/lighting/drtagx_light_balance.ash")+"\n"+GpuChecks.Expand(before,"drtagx/shaders/deferred/drtagx_deferred_cube.fsh")+"\n"+GpuChecks.DynamicSource(before)+"\nout vec4 color;\n"+main;
        int older=GpuChecks.Compile((ShaderType.VertexShader,GpuChecks.Fullscreen),(ShaderType.FragmentShader,oldFragment));
        int Shadow(string root)=>GpuChecks.Compile((ShaderType.VertexShader,GpuChecks.Expand(root,"game/shaders/chunkshadowmap.vsh").Replace("#version 330 core","#version 430 core\n#define USESSBO 0\n#define WAVINGSTUFF 0")),(ShaderType.FragmentShader,GpuChecks.Expand(root,"game/shaders/chunkshadowmap.fsh")));
        int shadowOld=Shadow(before),shadowNew=Shadow(assets);
        string fit=File.ReadAllText(Path.Combine(assets,"drtagx/shaders/movinglightfit.csh"));
        int tile=GpuChecks.Compile((ShaderType.ComputeShader,fit)),final=GpuChecks.Compile((ShaderType.ComputeShader,fit.Replace("#version 430 core","#version 430 core\n#define DRT_FINAL_FIT")));
        int fullscreen=GL.GenVertexArray(),mesh=GL.GenVertexArray(),vertices=GL.GenBuffer();
        // 8192 triangles, tiled over a large wall plus alternating close furniture.
        const int subdivisions=64;float[] xyz=new float[subdivisions*subdivisions*6*3];int index=0;
        for(int y=0;y<subdivisions;++y)for(int x=0;x<subdivisions;++x)
        {
            float x0=-16+x*.5f,y0=-16+y*.5f,z=(x+y)%5==0?-4:-14;
            foreach(var v in new[]{(x0,y0),(x0+.5f,y0),(x0+.5f,y0+.5f),(x0,y0),(x0+.5f,y0+.5f),(x0,y0+.5f)}){xyz[index++]=v.Item1;xyz[index++]=v.Item2;xyz[index++]=z;}
        }
        GL.BindVertexArray(mesh);GL.BindBuffer(BufferTarget.ArrayBuffer,vertices);GL.BufferData(BufferTarget.ArrayBuffer,xyz.Length*4,xyz,BufferUsageHint.StaticDraw);GL.EnableVertexAttribArray(0);GL.VertexAttribPointer(0,3,VertexAttribPointerType.Float,false,12,0);
        int opaque=GpuChecks.Texture(1,1,PixelInternalFormat.Rgba32f,new float[]{1,1,1,1});
        int shadowFbo=GL.GenFramebuffer(),colorFbo=GL.GenFramebuffer();float[] identity=new float[16];Mat4f.Identity(identity);
        int output=GpuChecks.Buffer(new float[40],7);GL.BindBufferBase(BufferRangeTarget.UniformBuffer,5,output);
        int query=GL.GenQuery();bool accepted=true;
        foreach(var dimensions in new[]{(1920,1080),(3840,2160)})
        {
            int width=dimensions.Item1,height=dimensions.Item2;float[] camera=GpuChecks.Camera(90,(float)width/height),inverse=new float[16];Mat4f.Invert(inverse,camera);
            float[] depths=new float[width*height];for(int y=0;y<height;++y)for(int x=0;x<width;++x)depths[x+y*width]=GpuChecks.DepthAt(x<width/4?4:14,camera);
            int depth=GpuChecks.Texture(width,height,values:depths),color=GpuChecks.Texture(width,height,PixelInternalFormat.Rgba32f);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer,colorFbo);GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,FramebufferAttachment.ColorAttachment0,TextureTarget.Texture2D,color,0);GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
            int tileBuffer=GpuChecks.Buffer(new float[((width+15)/16)*((height+15)/16)*16],6);
            foreach(int count in new[]{1,2,4,6,10})foreach(bool held in new[]{true,false})
            {
                int faceSize=MovingLightShadowRenderer.FaceSizeForDimensions(width,height),stride=faceSize+8;
                int columns=Math.Min(5,count),atlasW=columns*2*stride,atlasH=((count+columns-1)/columns)*3*stride;
                int atlas=GpuChecks.Texture(atlasW,atlasH,PixelInternalFormat.DepthComponent24);
                GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureCompareMode,(int)TextureCompareMode.CompareRefToTexture);GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureCompareFunc,(int)DepthFunction.Lequal);GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.Linear);GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)TextureMagFilter.Linear);
                int array=GL.GenTexture();GL.BindTexture(TextureTarget.Texture2DArray,array);GL.TexImage3D(TextureTarget.Texture2DArray,0,PixelInternalFormat.DepthComponent24,192,192,count*12,0,PixelFormat.DepthComponent,PixelType.UnsignedInt,IntPtr.Zero);
                GL.TexParameter(TextureTarget.Texture2DArray,TextureParameterName.TextureCompareMode,(int)TextureCompareMode.CompareRefToTexture);GL.TexParameter(TextureTarget.Texture2DArray,TextureParameterName.TextureCompareFunc,(int)DepthFunction.Lequal);GL.TexParameter(TextureTarget.Texture2DArray,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.Linear);GL.TexParameter(TextureTarget.Texture2DArray,TextureParameterName.TextureMagFilter,(int)TextureMagFilter.Linear);
                float[][] lights=new float[count][];float[] positions=new float[count*3],colors=new float[count*3],prepared=new float[count*4],ranges=new float[10],handLights=new float[8];int[] slots=new int[16],kinds=new int[10];Array.Fill(slots,-1);
                for(int i=0;i<count;++i)
                {
                    lights[i]=new float[]{i==0?.4f:i==1?-.4f:(i-2)%3*2-2,-.2f,i<2?0:-3};Array.Copy(lights[i],0,positions,i*3,3);
                    colors[i*3]=16;colors[i*3+1]=10;colors[i*3+2]=5;prepared[i*4]=1;prepared[i*4+1]=.625f;prepared[i*4+2]=.3125f;
                    prepared[i*4+3]=MathF.Sqrt(381);ranges[i]=prepared[i*4+3];slots[i]=i;
                    if(held&&i<2){kinds[i]=i+1;Array.Copy(lights[i],0,handLights,i*4,3);handLights[i*4+3]=ranges[i];}
                }
                foreach(int p in new[]{older,newer})
                {
                    GL.UseProgram(p);GL.Uniform1(GL.GetUniformLocation(p,"drtPointCount"),count);GL.Uniform3(GL.GetUniformLocation(p,"drtPointPos[0]"),count,positions);GL.Uniform3(GL.GetUniformLocation(p,"drtPointColor[0]"),count,colors);
                    GL.Uniform4(GL.GetUniformLocation(p,"drtPointRadiance[0]"),count,prepared);GL.Uniform1(GL.GetUniformLocation(p,"drtShadowCount"),count);GL.Uniform1(GL.GetUniformLocation(p,"drtShadowSlot[0]"),16,slots);GL.Uniform1(GL.GetUniformLocation(p,"drtShadowMaps"),15);
                    GL.Uniform1(GL.GetUniformLocation(p,"drtShadowKind[0]"),10,kinds);GL.Uniform1(GL.GetUniformLocation(p,"drtShadowRange[0]"),10,ranges);GL.Uniform4(GL.GetUniformLocation(p,"drtMovingAtlasInfo"),(float)atlasW,(float)atlasH,(float)faceSize,(float)stride);GL.Uniform1(GL.GetUniformLocation(p,"drtMovingAtlasColumns"),columns);
                    GL.Uniform1(GL.GetUniformLocation(p,"receiverDepth"),12);GL.Uniform2(GL.GetUniformLocation(p,"frameSize"),(float)width,(float)height);GL.UniformMatrix4(GL.GetUniformLocation(p,"inverseProjection"),1,false,inverse);
                }
                float[] view=new float[16],projection=new float[16],matrix=new float[16];
                void Frame(bool old)
                {
                    if(!old&&held)GpuChecks.DispatchFit(tile,final,depth,output,width,height,camera,handLights,faceSize*2);
                    GL.BindFramebuffer(FramebufferTarget.Framebuffer,shadowFbo);GL.DrawBuffer(DrawBufferMode.None);GL.ReadBuffer(ReadBufferMode.None);GL.Enable(EnableCap.DepthTest);GL.DepthFunc(DepthFunction.Less);GL.DepthMask(true);GL.ClearDepth(1);GL.Disable(EnableCap.CullFace);GL.Disable(EnableCap.ScissorTest);
                    GL.BindVertexArray(mesh);int p=old?shadowOld:shadowNew;GL.UseProgram(p);GL.Uniform1(GL.GetUniformLocation(p,"tex2d"),0);GL.UniformMatrix4(GL.GetUniformLocation(p,"drtHeldCameraMatrix"),1,false,identity);
                    GL.ActiveTexture(TextureUnit.Texture0);GL.BindTexture(TextureTarget.Texture2D,opaque);
                    if(!old){GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,FramebufferAttachment.DepthAttachment,TextureTarget.Texture2D,atlas,0);GL.Clear(ClearBufferMask.DepthBufferBit);}
                    for(int i=0;i<count;++i)
                    {
                        if(!old&&kinds[i]>0)
                        {GL.Viewport(i%columns*2*stride+4,i/columns*3*stride+4,faceSize*2,faceSize*2);GL.Uniform1(GL.GetUniformLocation(p,"drtHeldProjectionIndex"),kinds[i]-1);GL.DrawArrays(PrimitiveType.Triangles,0,xyz.Length/3);continue;}
                        GL.Uniform1(GL.GetUniformLocation(p,"drtHeldProjectionIndex"),-1);
                        for(int cascade=0;cascade<(old?2:1);++cascade)for(int f=0;f<6;++f)
                        {
                            if(old){GL.FramebufferTextureLayer(FramebufferTarget.Framebuffer,FramebufferAttachment.DepthAttachment,array,0,(i*2+cascade)*6+f);GL.Viewport(0,0,192,192);GL.Clear(ClearBufferMask.DepthBufferBit);}
                            else GL.Viewport(i%columns*2*stride+(f%2)*stride+4,i/columns*3*stride+(f/2)*stride+4,faceSize,faceSize);
                            MovingLightShadowRenderer.MakeLightMatrix(lights[i],MovingLightShadowRenderer.Directions[f],old?(cascade==0?16:48):ranges[i]+.5f,view,projection,matrix);
                            GL.UniformMatrix4(GL.GetUniformLocation(p,"mvpMatrix"),1,false,matrix);GL.DrawArrays(PrimitiveType.Triangles,0,xyz.Length/3);
                        }
                    }
                    GL.BindFramebuffer(FramebufferTarget.Framebuffer,colorFbo);GL.Viewport(0,0,width,height);GL.Disable(EnableCap.DepthTest);GL.BindVertexArray(fullscreen);GL.UseProgram(old?older:newer);
                    GL.ActiveTexture(TextureUnit.Texture12);GL.BindTexture(TextureTarget.Texture2D,depth);GL.ActiveTexture(TextureUnit.Texture15);GL.BindTexture(old?TextureTarget.Texture2DArray:TextureTarget.Texture2D,old?array:atlas);
                    GL.DrawArrays(PrimitiveType.Triangles,0,3);
                }
                for(int warm=0;warm<12;++warm){Frame(true);Frame(false);}GL.Finish();
                double[] oldTimes=new double[48],newTimes=new double[48];
                for(int sample=0;sample<48;++sample)foreach(bool old in sample%2==0?new[]{true,false}:new[]{false,true})
                {
                    GL.BeginQuery(QueryTarget.TimeElapsed,query);Frame(old);GL.EndQuery(QueryTarget.TimeElapsed);GL.GetQueryObject(query,GetQueryObjectParam.QueryResult,out long ns);(old?oldTimes:newTimes)[sample]=ns/1_000_000.0;
                }
                Array.Sort(oldTimes);Array.Sort(newTimes);double om=oldTimes[24],nm=newTimes[24],op=oldTimes[45],np=newTimes[45];
                bool ok=count<=2&&held?nm<om&&np<op:nm<=om+Math.Max(.05,om*.05)&&np<=op+Math.Max(.05,op*.05);accepted&=ok;
                Console.WriteLine($"BENCH {width}x{height} {(held?"held":"cube")} lights={count}: before median/p95 {om:F3}/{op:F3} ms; after {nm:F3}/{np:F3} ms; delta {(nm/om-1)*100:F1}% {(ok?"PASS":"FAIL")}");
                Program.Check(GL.GetError()==ErrorCode.NoError,"benchmark OpenGL state is valid");GL.DeleteTexture(atlas);GL.DeleteTexture(array);
            }
            GL.DeleteBuffer(tileBuffer);GL.DeleteTexture(depth);GL.DeleteTexture(color);
        }
        Program.Check(accepted,"combined default held improvement and sparse-regression threshold");
        GL.DeleteProgram(newer);GL.DeleteProgram(older);GL.DeleteProgram(shadowOld);GL.DeleteProgram(shadowNew);GL.DeleteProgram(tile);GL.DeleteProgram(final);GL.DeleteBuffer(output);GL.DeleteBuffer(vertices);GL.DeleteVertexArray(fullscreen);GL.DeleteVertexArray(mesh);GL.DeleteTexture(opaque);GL.DeleteFramebuffer(shadowFbo);GL.DeleteFramebuffer(colorFbo);GL.DeleteQuery(query);
    }
}
