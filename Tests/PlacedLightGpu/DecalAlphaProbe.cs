using System;
using System.IO;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;

internal static class DecalAlphaProbe
{
    // These alpha/late-pass fixtures use an empty PLS cache. The real prepared
    // receiving path is covered by the terrain/all-pass lighting probes.
    private const string EmptyTerrainPlaced = """
        const int drtTerrainPlacedCount=0;
        const vec3 drtPlacedRestPos=vec3(0),drtTerrainPlacedRadiance=vec3(0);
        const float drtTerrainPlacedVisibility=1;
        void drtPrepareTerrainPlacedVisibility(vec3 p,bool foliage,float glow){}
        """;
    internal static void RunWaterPlants(string deferredPath)
    {
        using var state=new ShadowGlState();
        string assets=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(deferredPath)!,"../.."));
        string source=File.ReadAllText(Path.Combine(assets,"game/shaders/chunkopaque.fsh"));
        string surface=File.ReadAllText(Path.Combine(assets,"drtagx/shaders/lighting/drtagx_surface_lighting.fsh"));
        int start=source.IndexOf("    if (drtForwardDecalPass == 0",StringComparison.Ordinal);
        string condition=source[start..source.IndexOf('{',start)];
        int routeStart=source.IndexOf("    bool smDeferrable",StringComparison.Ordinal);
        string route=source[routeStart..(source.IndexOf(';',routeStart)+1)];
        string vertex="#version 430 core\nvoid main(){vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);gl_Position=vec4(p*2.-1.,0,1);}";
        int texture=GL.GenTexture(),fbo=GL.GenFramebuffer(),vao=GL.GenVertexArray();
        try
        {
            GL.ActiveTexture(TextureUnit.Texture0);GL.BindTexture(TextureTarget.Texture2D,texture);
            GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.Rgba32f,1,1,0,PixelFormat.Rgba,PixelType.Float,IntPtr.Zero);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer,fbo);GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,FramebufferAttachment.ColorAttachment0,TextureTarget.Texture2D,texture,0);
            GL.DrawBuffer(DrawBufferMode.ColorAttachment0);GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
            GL.BindVertexArray(vao);GL.Viewport(0,0,1,1);GL.Disable(EnableCap.DepthTest);GL.Disable(EnableCap.ScissorTest);GL.Disable(EnableCap.Blend);GL.ColorMask(true,true,true,true);
            foreach(int performance in new[]{0,1})
            {
                // Native lilies: OpaqueWaterPlant, Water wind mode (bits 25..28).
                // Execute the maintained producer, route and forward compositor.
                string fragment="#version 430 core\n#define SSAOLEVEL 0\n#define SHADOWQUALITY 0\n#define SHEYDER_DEFERRED "+performance+"\n"+"""
                    layout(location=0)out vec4 outColor;
                    layout(location=1)out vec4 outGlow;
                    uniform sampler2D specularTex;
                    uniform int deferredMode,drtForwardDecalPass;
                    uniform vec3 drtSunLight,drtLocalLight,drtVoxelLight,drtEmissionLight;
                    uniform float visibility=1,opacity=1;
                    const float alphaTest=.42,specularStrength=0,glowLevel=0,fogAmount=0,voxSunLight=1;
                    const int haxyFade=1,renderFlags=6<<25,WindModeBitMask=0x1e000000,WindModeLeavesMask=3<<25;
                    const float drtWindPresence=1;
                    const vec2 uv=vec2(0);
                    const vec4 rgba=vec4(1);
                    vec3 drtAppliedSunScale=vec3(1);
                    float drtPackDeferredSun(float s){return s*63.;}
                    """+EmptyTerrainPlaced+ProbeShader.Function(source,"void sm_deferredFill(")+
                    ProbeShader.Function(surface,"vec3 drtComposeRadiance(")+
                    ProbeShader.Function(surface,"vec3 drtVisibleLocalLight(")+
                    ProbeShader.Function(surface,"vec3 drtShadeSurface(")+"\nvoid main(){"+route+"""
                        const vec3 rawAlbedo=vec3(.3,.5,.1);
                        vec4 texColor=vec4(rawAlbedo*(drtSunLight+drtLocalLight),opacity);
                        if(texColor.a<alphaTest)discard;
                        """+condition+"{sm_deferredFill(texColor,rawAlbedo);}else{outColor=vec4(drtShadeSurface(texColor.rgb,visibility,vec3(1)),opacity);outGlow=vec4(0);}}";
                int program=ProbeShader.Program((ShaderType.VertexShader,vertex),(ShaderType.FragmentShader,fragment));
                try
                {
                    GL.UseProgram(program);GL.Uniform1(GL.GetUniformLocation(program,"deferredMode"),1);
                    float[] pixel=new float[4];
                    void Draw(int forward,float sun,float local,float voxel,float shade,float alpha)
                    {
                        GL.Uniform1(GL.GetUniformLocation(program,"drtForwardDecalPass"),forward);
                        GL.Uniform3(GL.GetUniformLocation(program,"drtSunLight"),sun,sun,sun);
                        GL.Uniform3(GL.GetUniformLocation(program,"drtLocalLight"),local,local,local);
                        GL.Uniform3(GL.GetUniformLocation(program,"drtVoxelLight"),voxel,voxel,voxel);
                        GL.Uniform1(GL.GetUniformLocation(program,"visibility"),shade);GL.Uniform1(GL.GetUniformLocation(program,"opacity"),alpha);
                        GL.ClearColor(0,0,0,0);GL.Clear(ClearBufferMask.ColorBufferBit);
                        GL.DrawArrays(PrimitiveType.Triangles,0,3);GL.ReadPixels(0,0,1,1,PixelFormat.Rgba,PixelType.Float,pixel);
                    }
                    Draw(0,1,0,0,1,1);
                    if(pixel[0]!=0||pixel[1]!=0||pixel[2]!=0)throw new Exception("Late water-plant fixture did not reproduce black daylight output");
                    Console.WriteLine($"PASS water-plant reproducer: daylight-only deferred output is black after relight, performance={performance}");
                    foreach(var sample in new[]{(sun:1f,local:0f,shade:1f),(sun:1f,local:0f,shade:.2f),(sun:.05f,local:.02f,shade:1f),(sun:0f,local:.8f,shade:0f),(sun:0f,local:0f,shade:1f)})
                    foreach(float alpha in new[]{.2f,.5f,1f})
                    {
                        Draw(1,sample.sun,sample.local,sample.local,sample.shade,alpha);
                        for(int c=0;c<4;++c)
                        {
                            float albedo=c==0?.3f:c==1?.5f:.1f;
                            float expected=alpha<.42f?0:c==3?alpha:albedo*(sample.local+sample.sun*sample.shade);
                            if(Math.Abs(pixel[c]-expected)>1e-5)throw new Exception($"Late water-plant radiance/coverage changed: {sample}, alpha={alpha}, channel={c}: {pixel[c]} vs {expected}");
                        }
                    }
                    Draw(0,1,.2f,.2f,1,1);
                    if(Math.Abs(pixel[0]-.06f)>1e-5)throw new Exception("Next frame no longer emits the original voxel-only G-buffer");
                    Console.WriteLine($"PASS water-plant lighting: daylight, sun shadows, night, local light, darkness, cutout/alpha and next-frame G-buffer, performance={performance}");
                }
                finally{GL.DeleteProgram(program);}
            }
            if(GL.GetError()!=ErrorCode.NoError)throw new Exception("Water-plant lighting probe GL error");
        }
        finally{GL.DeleteVertexArray(vao);GL.DeleteFramebuffer(fbo);GL.DeleteTexture(texture);}
    }

    internal static void Run(string deferredPath)
    {
        using var state=new ShadowGlState();
        string path=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(deferredPath)!,"../../game/shaders/chunkopaque.fsh"));
        string source=File.ReadAllText(path);
        // Exercise the maintained producer and its real runtime/performance
        // exclusion. Forward radiance is controlled here; its lighting math is
        // covered separately by ForwardLightingProbe.
        int start=source.IndexOf("    if (drtForwardDecalPass == 0",StringComparison.Ordinal);
        string condition=source[start..source.IndexOf('{',start)];
        string fill=ProbeShader.Function(source,"void sm_deferredFill(");
        int routeStart=source.IndexOf("    bool smDeferrable",StringComparison.Ordinal);
        string route=source[routeStart..(source.IndexOf(';',routeStart)+1)];
        string vertex="#version 430 core\nvoid main(){vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);gl_Position=vec4(p*2.-1.,0,1);}";
        int texture=GL.GenTexture(),fbo=GL.GenFramebuffer(),vao=GL.GenVertexArray();
        int src=GL.GetInteger(GetPName.BlendSrcRgb),dst=GL.GetInteger(GetPName.BlendDstRgb),srcA=GL.GetInteger(GetPName.BlendSrcAlpha),dstA=GL.GetInteger(GetPName.BlendDstAlpha);
        try
        {
            GL.ActiveTexture(TextureUnit.Texture0);GL.BindTexture(TextureTarget.Texture2D,texture);
            GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.Rgba32f,1,1,0,PixelFormat.Rgba,PixelType.Float,IntPtr.Zero);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer,fbo);GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,FramebufferAttachment.ColorAttachment0,TextureTarget.Texture2D,texture,0);
            GL.DrawBuffer(DrawBufferMode.ColorAttachment0);GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
            GL.BindVertexArray(vao);GL.Viewport(0,0,1,1);GL.Disable(EnableCap.DepthTest);GL.Disable(EnableCap.ScissorTest);GL.ColorMask(true,true,true,true);
            GL.Enable(EnableCap.Blend);GL.BlendFuncSeparate(BlendingFactorSrc.SrcAlpha,BlendingFactorDest.OneMinusSrcAlpha,BlendingFactorSrc.One,BlendingFactorDest.OneMinusSrcAlpha);
            float[] background={2.4f,1.2f,.6f,1},pixel=new float[4];
            foreach(int performance in new[]{0,1})
            {
                string fragment="#version 430 core\n#define SSAOLEVEL 0\n#define SHADOWQUALITY 0\n#define SHEYDER_DEFERRED "+performance+"\n"+"""
                    layout(location=0)out vec4 outColor;
                    layout(location=1)out vec4 outGlow;
                    uniform vec4 decal;
                    uniform sampler2D specularTex;
                    const vec2 uv=vec2(0);
                    uniform int deferredMode,drtForwardDecalPass;
                    uniform float alphaTest;
                    const float specularStrength=0,glowLevel=0,fogAmount=0,voxSunLight=1;
                    uniform int haxyFade;
                    const int renderFlags=0,WindModeBitMask=0,WindModeLeavesMask=1;
                    const float drtWindPresence=0; // Stationary decal geometry.
                    const vec4 rgba=vec4(1);
                    const vec3 drtVoxelLight=vec3(1),drtEmissionLight=vec3(0);
                    float drtPackDeferredSun(float s){return s*63.;}
                    """+EmptyTerrainPlaced+"\n"+fill+"\nvoid main(){if(decal.a<alphaTest)discard;\n"+route+condition+
                    "{sm_deferredFill(decal,decal.rgb);}else{outColor=decal;outGlow=vec4(0);}}";
                int program=ProbeShader.Program((ShaderType.VertexShader,vertex),(ShaderType.FragmentShader,fragment));
                try
                {
                    GL.UseProgram(program);
                    int mode=GL.GetUniformLocation(program,"deferredMode"),bypass=GL.GetUniformLocation(program,"drtForwardDecalPass"),color=GL.GetUniformLocation(program,"decal");
                    GL.Uniform1(GL.GetUniformLocation(program,"alphaTest"),.2f);
                    void Draw(float opacity,int runtime,int forward)
                    {
                        GL.ClearBuffer(ClearBuffer.Color,0,background);GL.Uniform1(mode,runtime);GL.Uniform1(bypass,forward);GL.Uniform4(color,.2f,.1f,.05f,opacity);
                        GL.DrawArrays(PrimitiveType.Triangles,0,3);GL.ReadPixels(0,0,1,1,PixelFormat.Rgba,PixelType.Float,pixel);
                    }
                    Draw(.5f,1,0);
                    if(Math.Abs(pixel[0]-.2f)>1e-5)throw new Exception("Decal fixture did not reproduce opaque deferred alpha");
                    Console.WriteLine($"PASS decal alpha reproducer: G-buffer makes 0.5 opaque, performance={performance}");
                    foreach(float opacity in new[]{0f,.1f,.25f,.5f,.75f,1f})
                    {
                        Draw(opacity,1,1);
                        float coverage=opacity<.2f?0:opacity;
                        for(int c=0;c<3;++c)
                        {
                            float decal=c==0?.2f:c==1?.1f:.05f;
                            float expected=background[c]*(1-coverage)+decal*coverage;
                            if(Math.Abs(pixel[c]-expected)>1e-5)throw new Exception($"Fractional decal blend lost alpha: {opacity}, {pixel[c]} vs {expected}");
                        }
                    }
                    if(performance==0)
                    {
                        Draw(.5f,0,0);if(Math.Abs(pixel[0]-1.3f)>1e-5)throw new Exception("Forward-only decal blend changed");
                    }
                    Console.WriteLine($"PASS decal replay: six opacity/cutout levels match HDR alpha blending, performance={performance}");
                    GL.Uniform1(GL.GetUniformLocation(program,"haxyFade"),1);
                    GL.Uniform1(GL.GetUniformLocation(program,"alphaTest"),.25f);
                    Draw(.5f,1,0);
                    if(Math.Abs(pixel[0]-1.3f)>1e-5)throw new Exception("Blended foliage with full vertex alpha entered the G-buffer");
                    Console.WriteLine($"PASS blended foliage stays forward with full vertex alpha, performance={performance}");
                }
                finally{GL.DeleteProgram(program);}
            }
            if(GL.GetError()!=ErrorCode.NoError)throw new Exception("Decal alpha probe GL error");
        }
        finally
        {
            GL.BlendFuncSeparate((BlendingFactorSrc)src,(BlendingFactorDest)dst,(BlendingFactorSrc)srcA,(BlendingFactorDest)dstA);
            GL.DeleteVertexArray(vao);GL.DeleteFramebuffer(fbo);GL.DeleteTexture(texture);
        }
    }
}
