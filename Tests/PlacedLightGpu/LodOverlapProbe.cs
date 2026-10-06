using System;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

// Actual provider fragments compete with an existing object pixel/depth. Small
// alternating depth offsets reproduce the visible instability of overlapping proxies.
internal static class LodOverlapProbe
{
    internal static void Run(string vertex, string before, string after, Action<int> setup,
        Action<int> bind, Action restore, float[] frame, int buffer, int framebuffer, string label)
    {
        using var state = new ShadowGlState();
        float[] preserved = (float[])frame.Clone(), oldClear = new float[4];
        GL.GetFloat(GetPName.ColorClearValue, oldClear);
        double oldClearDepth = GL.GetDouble(GetPName.DepthClearValue);
        int oldGeneric = GL.GetInteger(GetPName.UniformBufferBinding);
        GL.GetInteger(GetIndexedPName.UniformBufferBinding, AtmosphereProgramBindings.BufferBinding, out int oldIndexed);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
        int oldReadBuffer = GL.GetInteger(GetPName.ReadBuffer);
        int previousTexture = GL.GetInteger(GetPName.TextureBinding2D), depth = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, depth);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.DepthComponent32f, 1, 1, 0,
            PixelFormat.DepthComponent, PixelType.Float, IntPtr.Zero);
        GL.BindTexture(TextureTarget.Texture2D, previousTexture);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, depth, 0);
        if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete)
            throw new Exception("LOD overlap framebuffer incomplete");
        int original = ProbeShader.Program((ShaderType.VertexShader, vertex), (ShaderType.FragmentShader, before));
        int patched = ProbeShader.Program((ShaderType.VertexShader, vertex), (ShaderType.FragmentShader, after));
        float[] pixel = new float[4], depthPixel = new float[1];
        int groups = 0, reproduced = 0;
        void Require(bool condition, string message) { if (!condition) throw new Exception(label + ": " + message); }
        // Literal thresholds are independent of the shader expression: 384/768
        // blocks at native ranges 512/1024, and 192 at server-approved range 256.
        var cases = new[] {
            (512f,512f,Vector3.Zero,new Vector3(0,0,32),true,true),
            (512f,512f,Vector3.Zero,new Vector3(0,0,383.9f),true,true),
            (512f,512f,Vector3.Zero,new Vector3(0,0,384),false,true),
            (512f,512f,Vector3.Zero,new Vector3(0,0,384.1f),false,true),
            (512f,512f,Vector3.Zero,new Vector3(0,700,32),true,true),
            (512f,512f,new Vector3(2048,0,0),new Vector3(0,0,32),true,true),
            (1024f,1024f,Vector3.Zero,new Vector3(0,0,767.9f),true,true),
            (1024f,1024f,Vector3.Zero,new Vector3(0,0,768),false,true),
            (256f,512f,Vector3.Zero,new Vector3(0,0,191.9f),true,true),
            (256f,512f,Vector3.Zero,new Vector3(0,0,192),false,true),
            (1024f,512f,Vector3.Zero,new Vector3(0,0,32),true,false),
            (1024f,512f,Vector3.Zero,new Vector3(0,0,384),false,false)
        };
        try {
            GL.Enable(EnableCap.DepthTest); GL.DepthFunc(DepthFunction.Less); GL.DepthMask(true);
            GL.Disable(EnableCap.Blend); GL.Viewport(0, 0, 1, 1);
            foreach (var (nativeRange, settingRange, camera, position, excluded, ready) in cases)
            foreach (bool depthOnly in new[] { false, true })
            foreach (float clipDepth in new[] { -.0001f, .0001f }) {
                frame[3] = ready ? 1 : 0; frame[8] = nativeRange;
                frame[4] = frame[5] = frame[6] = frame[10] = 0;
                frame[12] = frame[14] = 0; frame[13] = frame[15] = 1;
                frame[20] = camera.X; frame[21] = camera.Y; frame[22] = camera.Z;
                GL.BindBuffer(BufferTarget.UniformBuffer, buffer);
                GL.BufferData(BufferTarget.UniformBuffer, frame.Length * sizeof(float), frame, BufferUsageHint.DynamicDraw);
                foreach (int program in new[] { original, patched }) {
                    setup(program); GL.UseProgram(program);
                    GL.Uniform1(GL.GetUniformLocation(program, "viewDistance"), settingRange);
                    GL.Uniform1(GL.GetUniformLocation(program, "gapFillEnabled"), 1f);
                    GL.Uniform1(GL.GetUniformLocation(program, "depthOnly"), depthOnly ? 1 : 0);
                    GL.Uniform1(GL.GetUniformLocation(program, "probeClipDepth"), clipDepth);
                    GL.Uniform3(GL.GetUniformLocation(program, "probePos"), camera + position);
                    // Clear values stand in for the native object's completed pixel.
                    // Proxy depth alternates in front/behind that same object's depth.
                    GL.ColorMask(true, true, true, true);
                    GL.ClearColor(.02f, .03f, .04f, 1); GL.ClearDepth(.5);
                    GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                    if (depthOnly) GL.ColorMask(false, false, false, false);
                    bind(program);
                    try { GL.DrawArrays(PrimitiveType.Triangles, 0, 3); }
                    finally { restore(); }
                    GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
                    GL.ReadPixels(0, 0, 1, 1, PixelFormat.Rgba, PixelType.Float, pixel);
                    GL.ReadPixels(0, 0, 1, 1, PixelFormat.DepthComponent, PixelType.Float, depthPixel);
                    bool expectedWrite = clipDepth < 0 && (program == original || !excluded);
                    Require(expectedWrite ? depthPixel[0] < .5f : depthPixel[0] == .5f,
                        $"proxy depth write mismatch: range={nativeRange}, pos={position}, ready={ready}, depthOnly={depthOnly}, patched={program==patched}");
                    if (!expectedWrite || depthOnly) {
                        Require(Math.Abs(pixel[0]-.02f)<1e-7 && Math.Abs(pixel[1]-.03f)<1e-7 && Math.Abs(pixel[2]-.04f)<1e-7 && pixel[3]==1,
                            "rejected/depth-only proxy altered native object color");
                        if (program == patched && excluded) {
                            // The near gate must also preserve glow and SSAO metadata.
                            for (int attachment = 1; attachment < 4; ++attachment) {
                                GL.ReadBuffer(ReadBufferMode.ColorAttachment0 + attachment);
                                GL.ReadPixels(0, 0, 1, 1, PixelFormat.Rgba, PixelType.Float, pixel);
                                Require(Math.Abs(pixel[0]-.02f)<1e-7 && Math.Abs(pixel[1]-.03f)<1e-7 && Math.Abs(pixel[2]-.04f)<1e-7 && pixel[3]==1,
                                    "rejected proxy altered an auxiliary attachment");
                            }
                        }
                    } else {
                        Require(Math.Abs(pixel[0]-.02f)>1e-5 || Math.Abs(pixel[1]-.03f)>1e-5 || Math.Abs(pixel[2]-.04f)>1e-5,
                            "baseline/distant proxy did not replace native object color");
                    }
                    if (program == original && expectedWrite && excluded) ++reproduced;
                }
                ++groups;
            }
            Require(GL.GetError() == ErrorCode.NoError, "overlap probe GL error");
            Console.WriteLine($"PASS {label}: {groups} overlap groups, {reproduced} near-proxy depth conflicts reproduced before; zero protected color/depth/metadata writes after; cutoff, approved range, camera origin, height, fallback and depth-only checks pass");
        } finally {
            restore(); preserved.CopyTo(frame, 0);
            GL.BindBuffer(BufferTarget.UniformBuffer, buffer);
            GL.BufferData(BufferTarget.UniformBuffer, frame.Length * sizeof(float), frame, BufferUsageHint.DynamicDraw);
            GL.BindBufferBase(BufferRangeTarget.UniformBuffer, AtmosphereProgramBindings.BufferBinding, oldIndexed);
            GL.BindBuffer(BufferTarget.UniformBuffer, oldGeneric);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, 0, 0);
            GL.ReadBuffer((ReadBufferMode)oldReadBuffer);
            GL.ClearColor(oldClear[0],oldClear[1],oldClear[2],oldClear[3]); GL.ClearDepth(oldClearDepth);
            GL.DeleteTexture(depth); GL.DeleteProgram(original); GL.DeleteProgram(patched);
        }
    }
}
