using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;

/// <summary>Actual animated vertex lighting through packed normals and native joint/model/view matrices.</summary>
internal static class AnimatedPlacedProbe
{
    internal static void Run(string deferredPath)
    {
        using var state = new ShadowGlState();
        string assets = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(deferredPath)!, "../.."));
        string[] dirs = { "game/shaders", "game/shaderincludes", "drtagx/shaders/lighting", "drtagx/shaders/atmosphere" };
        var seen = new HashSet<string>();
        string Expand(string name)
        {
            if (!seen.Add(name)) return "";
            string path = dirs.Select(d => Path.Combine(assets, d, name)).FirstOrDefault(File.Exists)
                ?? Path.Combine(Environment.GetEnvironmentVariable("VINTAGE_STORY")!, "assets/game/shaderincludes", name);
            return string.Join('\n', File.ReadAllLines(path).Select(line => line.Trim().StartsWith("#include ") ? Expand(line.Trim()[9..]) : line));
        }
        string vertex = Expand("entityanimated.vsh").Replace("#version 330 core",
            "#version 330 core\n#define MAXANIMATEDELEMENTS 1\n#define SSAOLEVEL 0\n#define SHADOWQUALITY 0\n#define DYNLIGHTS 0");
        int entity = Program(vertex), generic = Program(vertex.Replace("#define DRT_ANIMATED_PLACED_FACING 1", ""));
        int vao = GL.GenVertexArray(); GL.BindVertexArray(vao);
        GL.VertexAttrib3(0, 0f, 0f, 0f); GL.VertexAttrib2(1, .5f, .5f); GL.VertexAttrib4(2, 1f, 1f, 1f, 1f);
        // Normal sign Z is bit 21; its three magnitude bits are 22..24.
        // +Z with magnitude 7 exercises the native packed-normal decoder.
        GL.VertexAttribI1(3, 7 << 22); GL.VertexAttrib1(4, 0f); GL.VertexAttribI1(5, 0);
        int animation = GL.GenBuffer(); GL.BindBuffer(BufferTarget.UniformBuffer, animation);
        float[] identity = Matrix(0, 0, 0, 0);
        GL.BufferData(BufferTarget.UniformBuffer, 64, identity, BufferUsageHint.DynamicDraw);
        GL.GetInteger(GetIndexedPName.UniformBufferBinding, 0, out int previousAnimation);
        GL.BindBufferBase(BufferRangeTarget.UniformBuffer, 0, animation);
        int atmosphere = GL.GenBuffer(); GL.BindBuffer(BufferTarget.UniformBuffer, atmosphere);
        var frame = new float[AtmosphereRenderer.FrameFloatCount];
        GL.BufferData(BufferTarget.UniformBuffer, frame.Length * 4, frame, BufferUsageHint.StaticDraw);
        GL.GetInteger(GetIndexedPName.UniformBufferBinding, 10, out int previousAtmosphere);
        GL.BindBufferBase(BufferRangeTarget.UniformBuffer, 10, atmosphere);
        int result = GL.GenBuffer(), feedback = GL.GenTransformFeedback();
        GL.GetInteger(GetPName.TransformFeedbackBinding, out int previousFeedback);
        GL.BindTransformFeedback(TransformFeedbackTarget.TransformFeedback, feedback);
        GL.BindBuffer(BufferTarget.TransformFeedbackBuffer, result);
        GL.BufferData(BufferTarget.TransformFeedbackBuffer, 48, IntPtr.Zero, BufferUsageHint.DynamicRead);
        GL.BindBufferBase(BufferRangeTarget.TransformFeedbackBuffer, 0, result);
        bool previousDiscard = GL.IsEnabled(EnableCap.RasterizerDiscard); GL.Enable(EnableCap.RasterizerDiscard);
        foreach (int p in new[] { entity, generic })
        {
            GL.UseProgram(p); GL.UniformBlockBinding(p, GL.GetUniformBlockIndex(p, "Animation"), 0);
            GL.UniformBlockBinding(p, GL.GetUniformBlockIndex(p, "DrtAtmosphere"), 10);
            GL.Uniform4(GL.GetUniformLocation(p, "renderColor"), 1f, 1f, 1f, 1f);
            GL.Uniform1(GL.GetUniformLocation(p, "viewDistance"), 512f);
            GL.UniformMatrix4(GL.GetUniformLocation(p, "projectionMatrix"), 1, false, identity);
            GL.UniformMatrix4(GL.GetUniformLocation(p, "viewMatrix"), 1, false, identity);
            GL.Uniform2(GL.GetUniformLocation(p, "drtPlacedCalibration[20]"), 1.7f, 1f);
            GL.Uniform4(GL.GetUniformLocation(p, "drtEntityPlacedSources[0]"), 0f, 0f, 0f, 22f);
            GL.Uniform4(GL.GetUniformLocation(p, "drtEntityPlacedColors[0]"), 1f, .3f, .08f, 20f);
            GL.Uniform4(GL.GetUniformLocation(p, "drtEntityPlacedFades"), 1f, 0f, 0f, 0f);
            GL.Uniform1(GL.GetUniformLocation(p, "drtEntityPlacedCount"), 1);
        }
        int checks = 0, draws = 0;
        void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
        void Near(float actual, float expected, string message) => Check(float.IsFinite(actual) && Math.Abs(actual - expected) < 2e-5f * Math.Max(1, Math.Abs(expected)), $"{message}: {actual} vs {expected}");
        float[] Draw(int program, float distance, float angle, float voxel, float sun = 0, int glow = 0, float camera = 0, float scale = 1)
        {
            GL.UseProgram(program);
            float[] model = Matrix(0, 0, 0, -distance); model[0] = model[5] = model[10] = scale;
            GL.UniformMatrix4(GL.GetUniformLocation(program, "modelMatrix"), 1, false, model);
            GL.UniformMatrix4(GL.GetUniformLocation(program, "viewMatrix"), 1, false, Matrix(camera, 0, 0, 0));
            GL.Uniform4(GL.GetUniformLocation(program, "rgbaLightIn"), voxel, voxel * .7f, voxel * .2f, sun);
            GL.Uniform1(GL.GetUniformLocation(program, "extraGlow"), glow);
            GL.BindBuffer(BufferTarget.UniformBuffer, animation);
            GL.BufferSubData(BufferTarget.UniformBuffer, IntPtr.Zero, 64, Matrix(angle, 0, 0, 0));
            GL.BeginTransformFeedback(TransformFeedbackPrimitiveType.Points);
            GL.DrawArrays(PrimitiveType.Points, 0, 1); GL.EndTransformFeedback(); draws++;
            GL.MemoryBarrier(MemoryBarrierFlags.TransformFeedbackBarrierBit | MemoryBarrierFlags.BufferUpdateBarrierBit);
            var data = new float[12]; GL.BindBuffer(BufferTarget.TransformFeedbackBuffer, result);
            GL.GetBufferSubData(BufferTarget.TransformFeedbackBuffer, IntPtr.Zero, 48, data);
            Check(data.All(float.IsFinite), "finite actual animated vertex output"); return data;
        }
        try
        {
            var sampleFront = Draw(entity, 5.5f, 0, .5f); var sampleBack = Draw(entity, 5.5f, 180, .5f);
            Console.WriteLine($"Animated placed GPU front={sampleFront[0]:F6}, back={sampleBack[0]:F6}, ratio={sampleBack[0]/sampleFront[0]:F6}");
            foreach (float distance in new[] { 1f, 4f, 5.5f, 10f, 17f })
            foreach (float voxel in new[] { .0001f, .02f, .5f })
            {
                var front = Draw(entity, distance, 0, voxel); var oldFront = Draw(generic, distance, 0, voxel);
                var back = Draw(entity, distance, 180, voxel);
                for (int c = 0; c < 3; c++)
                {
                    // Shipping animated lighting has half the generic placed gain
                    // and a 3% away-facing floor. These authored values predate
                    // the ranking optimization and are checked independently.
                    Near(front[c], .5f * oldFront[c], "authored half-gain animated head-on calibration retained");
                    Near(back[c], .03f * front[c], "authored animated away-facing floor retained");
                }
                foreach (float angle in new[] { 30f, 60f, 89f, 90f, 91f, 120f })
                {
                    float facing = .03f + .97f * Math.Max(MathF.Cos(angle * MathF.PI / 180), 0);
                    var actual = Draw(entity, distance, angle, voxel);
                    for (int c = 0; c < 3; c++) Near(actual[c], front[c] * facing, "actual joint normal controls smooth signed facing");
                }
            }
            var cameraReference = Draw(entity, 5.5f, 180, .5f);
            foreach (float camera in new[] { 30f, 90f, 180f })
            {
                var actual = Draw(entity, 5.5f, 180, .5f, camera: camera);
                for (int c = 0; c < 3; c++) Near(actual[c], cameraReference[c], "view rotation preserves source/normal directionality");
            }
            foreach (float scale in new[] { .1f, 3f, 12f })
            {
                var actual = Draw(entity, 5.5f, 180, .5f, scale: scale);
                for (int c = 0; c < 3; c++) Near(actual[c], cameraReference[c], "scaled animated normals preserve facing");
            }
            // Opposing colored lamps must average voxel facing by their original
            // radiance, not by the already shaded brightness of the nearer face.
            GL.UseProgram(entity); GL.Uniform1(GL.GetUniformLocation(entity, "drtEntityPlacedCount"), 2);
            GL.Uniform4(GL.GetUniformLocation(entity, "drtEntityPlacedSources[1]"), 0f, 0f, -11f, 22f);
            GL.Uniform4(GL.GetUniformLocation(entity, "drtEntityPlacedColors[1]"), .08f, .7f, 1f, 20f);
            GL.Uniform4(GL.GetUniformLocation(entity, "drtEntityPlacedFades"), 1f, 1f, 0f, 0f);
            var overlap = Draw(entity, 5.5f, 0, .5f);
            float strength = 1.7f * .75f * (1 / (1 + .25f * 5.5f * 5.5f) + .33f);
            float firstY = .2126f + .3f * .7152f + .08f * .0722f;
            float secondY = .08f * .2126f + .7f * .7152f + .0722f;
            // The weaker away-facing source occupies rank two (weight 1/2).
            // Weight its unshaded directional ownership by that same rank,
            // then apply the authored half gain to voxel and direct energy.
            float voxelFacing = (firstY + .5f * .03f * secondY) / (firstY + .5f * secondY);
            for (int c = 0; c < 3; c++)
                Near(overlap[c], .25f * (new[] { .5f, .35f, .1f }[c] * voxelFacing + strength * (new[] { 1f, .3f, .08f }[c] + .5f * .03f * new[] { .08f, .7f, 1f }[c])), "colored overlaps use unshaded energy weights");
            GL.Uniform1(GL.GetUniformLocation(entity, "drtEntityPlacedCount"), 1);
            GL.Uniform4(GL.GetUniformLocation(entity, "drtEntityPlacedFades"), 1f, 0f, 0f, 0f);
            // Readiness fades only the covered share; uncovered voxel illumination remains.
            var readyFront = Draw(entity, 5.5f, 0, .5f); var readyBack = Draw(entity, 5.5f, 180, .5f);
            GL.UseProgram(entity); GL.Uniform1(GL.GetUniformLocation(entity, "drtEntityPlacedCount"), 0);
            var fallback = Draw(entity, 5.5f, 180, .5f);
            for (int c = 0; c < 3; c++) Near(fallback[c], new[] { .5f, .35f, .1f }[c], "missing source retains voxel fallback");
            GL.Uniform1(GL.GetUniformLocation(entity, "drtEntityPlacedCount"), 1);
            foreach (float fade in new[] { 0f, .25f, .5f, 1f })
            {
                GL.Uniform4(GL.GetUniformLocation(entity, "drtEntityPlacedFades"), fade, 0f, 0f, 0f);
                var front = Draw(entity, 5.5f, 0, .5f); var back = Draw(entity, 5.5f, 180, .5f);
                for (int c = 0; c < 3; c++) { Near(front[c], fallback[c] + fade * (readyFront[c] - fallback[c]), "front readiness is continuous"); Near(back[c], fallback[c] + fade * (readyBack[c] - fallback[c]), "back readiness is continuous"); }
            }
            var dark = Draw(entity, 5.5f, 180, 0);
            for (int c = 0; c < 3; c++) Near(dark[c], 0, "zero voxel envelope prevents through-wall source leaks");
            GL.Uniform4(GL.GetUniformLocation(entity, "drtEntityPlacedColors[0]"), 0f, 0f, 0f, 20f);
            var empty = Draw(entity, 5.5f, 180, .5f);
            for (int c = 0; c < 3; c++) Near(empty[c], fallback[c], "empty cached source retains voxel fallback");
            GL.Uniform4(GL.GetUniformLocation(entity, "drtEntityPlacedColors[0]"), 1f, .3f, .08f, 20f);
            var beyond = Draw(entity, 23, 180, .5f);
            for (int c = 0; c < 3; c++) Near(beyond[c], fallback[c], "outside source reach retains voxel fallback");
            foreach (float angle in new[] { 0f, 180f })
            {
                var lit = Draw(entity, 5.5f, angle, .5f, .5f, 64);
                for (int c = 0; c < 3; c++) { Near(lit[3 + c], new[] { .025f, .05f, .075f }[c], "sunlight remains independent"); Near(lit[6 + c], 1, "emission remains independent"); }
                var emissionOnly = Draw(entity, 5.5f, angle, 0, 0, 64);
                for (int c = 0; c < 3; c++) Near(emissionOnly[c], 1, "placed response does not darken emission");
            }
            Console.WriteLine($"PASS animated placed directionality: {checks} checks, {draws} actual vertex captures; joint/view rotation, RGB/calibration, source fades, voxel fallback/occlusion, independent sun/emission");
        }
        finally
        {
            if (!previousDiscard) GL.Disable(EnableCap.RasterizerDiscard);
            GL.BindTransformFeedback(TransformFeedbackTarget.TransformFeedback, previousFeedback);
            GL.BindBufferBase(BufferRangeTarget.UniformBuffer, 0, previousAnimation); GL.BindBufferBase(BufferRangeTarget.UniformBuffer, 10, previousAtmosphere);
            GL.DeleteProgram(entity); GL.DeleteProgram(generic); GL.DeleteVertexArray(vao); GL.DeleteTransformFeedback(feedback);
            foreach (int b in new[] { animation, atmosphere, result }) GL.DeleteBuffer(b);
        }
        Check(GL.GetError() == ErrorCode.NoError, "animated placed GL NoError");
    }
    private static float[] Matrix(float angle, float x, float y, float z)
    {
        float a = angle * MathF.PI / 180, c = MathF.Cos(a), s = MathF.Sin(a);
        return new[] { c,0,s*-1,0, 0,1f,0,0, s,0,c,0, x,y,z,1f };
    }
    private static int Program(string vertex)
    {
        int shader = GL.CreateShader(ShaderType.VertexShader); GL.ShaderSource(shader, vertex); GL.CompileShader(shader);
        GL.GetShader(shader, ShaderParameter.CompileStatus, out int compiled);
        if (compiled == 0) throw new Exception(GL.GetShaderInfoLog(shader));
        int program = GL.CreateProgram(); GL.AttachShader(program, shader);
        GL.TransformFeedbackVaryings(program, 4, new[] { "drtLocalLight", "drtSunLight", "drtEmissionLight", "normal" }, TransformFeedbackMode.InterleavedAttribs);
        GL.LinkProgram(program); GL.DeleteShader(shader);
        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int linked);
        if (linked == 0) throw new Exception(GL.GetProgramInfoLog(program)); return program;
    }
}
