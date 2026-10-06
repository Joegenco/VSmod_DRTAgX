using System;
using System.Collections.Generic;
using System.IO;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using Vintagestory.API.MathTools;
using static StaticCacheProbeFixture;

// Exercise the production comparison-coordinate path on GPU, and the real
// resident/pending owners while toggling culling with an unchanged camera.
internal static class ShadowStyleProbe
{
    internal static void Run(string deferredPath)
    {
        using var state = new ShadowGlState();
        string assets = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(deferredPath)!, "../.."));
        string modules = Path.Combine(assets, "drtagx/shaders/deferred");
        string moving = File.ReadAllText(Path.Combine(modules, "drtagx_deferred_dynamiclights.fsh"));
        string source = "#version 430 core\n" + ProbeShader.DeferredSource(Path.Combine(modules, "drtagx_deferred_uniforms.fsh")) + "\n" +
            File.ReadAllText(Path.Combine(modules, "drtagx_deferred_cube.fsh")) + "\n" + """
            uniform mat4 invModelViewMatrix;
            uniform vec3 probeReceiver, probeEmitter, probeNormal;
            vec3 captured = vec3(0);
            // Observe the actual point delivered to the filter. Depth/PCF is
            // covered by the existing moving-light GPU suite, independently.
            float drtMovingCubeFace(vec3 q, vec3 n, float d, int slot, int face) {
                captured = q; return 0.5;
            }
            float drtMovingPcf(vec3 shadow, vec2 gradient, int slot, int face, bool held) {
                vec4 point = inverse(drtHeldMatrices[0]) * vec4(shadow * 2.0 - 1.0, 1);
                captured = point.xyz / point.w - probeEmitter;
                return 0.5;
            }
            """ + ProbeShader.Function(moving, "float drtMovingVisibility(") + "\n" + """
            out vec4 color;
            void main() {
                float visibility = drtMovingVisibility(probeReceiver, probeEmitter, 0, probeNormal);
                // Undo only the established geometric bias, to measure the
                // selected surface point without repeating the grid algorithm.
                vec3 q = captured - probeNormal * 0.003;
                q *= 1.0 + 0.01 / max(length(q), 1e-6);
                color = vec4(mat3(invModelViewMatrix) * (probeEmitter + q) + drtSunGridCameraPhase, visibility);
            }
            """;
        const string vertex = "#version 430 core\nvoid main(){vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);gl_Position=vec4(p*2-1,0,1);}";
        int program = ProbeShader.Program((ShaderType.VertexShader, vertex), (ShaderType.FragmentShader, source));
        int texture = GL.GenTexture(), framebuffer = GL.GenFramebuffer(), vao = GL.GenVertexArray(), heldBuffer = GL.GenBuffer();
        try
        {
            GL.BindTexture(TextureTarget.Texture2D, texture);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba32f, 1, 1, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, texture, 0);
            GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
            GL.BindVertexArray(vao); GL.Viewport(0, 0, 1, 1); GL.Disable(EnableCap.DepthTest);
            GL.UseProgram(program);
            GL.Uniform1(GL.GetUniformLocation(program, "drtShadowCount"), 1);
            GL.Uniform1(GL.GetUniformLocation(program, "drtShadowSlot[0]"), 0);
            GL.Uniform1(GL.GetUniformLocation(program, "drtShadowRange[0]"), 22f);
            float[] identity = { 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1 };
            GL.UniformMatrix4(GL.GetUniformLocation(program, "invModelViewMatrix"), 1, false, identity);
            var phase = new Vector3(.375f, .25f, .625f);
            GL.Uniform3(GL.GetUniformLocation(program, "drtSunGridCameraPhase"), phase);
            float[] held = new float[40];
            Mat4f.Perspective(held, MathF.PI / 2, 1, .005f, 22.5f);
            Array.Copy(held, 0, held, 16, 16); held[32] = held[36] = 1;
            GL.BindBuffer(BufferTarget.UniformBuffer, heldBuffer);
            GL.BufferData(BufferTarget.UniformBuffer, 160, held, BufferUsageHint.StaticDraw);
            GL.BindBufferBase(BufferRangeTarget.UniformBuffer, 5, heldBuffer);

            Vector3 Draw(Vector3 position, Vector3 normal, Vector3 emitter, bool grid, bool projector = false)
            {
                GL.Uniform1(GL.GetUniformLocation(program, "drtShadowGridEnabled"), grid ? 1 : 0);
                GL.Uniform1(GL.GetUniformLocation(program, "drtShadowKind[0]"), projector ? 1 : 0);
                GL.Uniform3(GL.GetUniformLocation(program, "probeReceiver"), position - phase);
                GL.Uniform3(GL.GetUniformLocation(program, "probeNormal"), normal);
                GL.Uniform3(GL.GetUniformLocation(program, "probeEmitter"), emitter - phase);
                GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
                float[] pixel = new float[4]; GL.ReadPixels(0, 0, 1, 1, PixelFormat.Rgba, PixelType.Float, pixel);
                if (Math.Abs(pixel[3] - .5f) > 1e-6) throw new Exception("Probe did not reach the moving shadow filter");
                return new Vector3(pixel[0], pixel[1], pixel[2]);
            }
            foreach (var normal in new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ })
            {
                Vector3 tangent = Math.Abs(normal.X) > .5f ? Vector3.UnitY : Vector3.UnitX;
                var point = new Vector3(-.121f, .254f, -4.117f);
                Vector3 emitter = point + normal * 6;
                Vector3 a = Draw(point, normal, emitter, true), b = Draw(point + tangent * .003f, normal, emitter, true);
                Check((a - b).Length < 2e-5, "moving cube shadow is constant inside a 1/32 surface cell, normal " + normal);
                Check(Math.Abs(Vector3.Dot(a - point, normal)) < 2e-5, "moving grid preserves receiver-plane depth, normal " + normal);
                Vector3 neighbor = Draw(point + tangent / 32, normal, emitter, true);
                Check((neighbor - a - tangent / 32).Length < 2e-5, "moving grid advances by exactly one cell, normal " + normal);
                Vector3 off = Draw(point + tangent * .003f, normal, emitter, false);
                Check((off - point - tangent * .003f).Length < 2e-5, "grid off restores continuous moving comparisons, normal " + normal);
                Vector3 moved = Draw(point, normal, emitter + tangent * .241f, true);
                Check((a - moved).Length < 2e-5, "moving emitter does not drag the world shadow grid, normal " + normal);
            }
            Vector3 heldPoint = new(-.121f, .254f, -4.117f), heldEmitter = phase;
            Vector3 h0 = Draw(heldPoint, Vector3.UnitZ, heldEmitter, true, true);
            Vector3 h1 = Draw(heldPoint + Vector3.UnitX * .003f, Vector3.UnitZ, heldEmitter, true, true);
            Check((h0 - h1).Length < .001f, "held projector uses the same fixed surface cells");
            Vector3 hoff = Draw(heldPoint, Vector3.UnitZ, heldEmitter, false, true);
            Check((hoff - heldPoint).Length < .001f, "grid off restores continuous held projection");
            Vector3 slopeNormal = Vector3.Normalize(new Vector3(.3f, .6f, 1f));
            Vector3 slopeTangent = Vector3.Normalize(Vector3.Cross(slopeNormal, Vector3.UnitX));
            Vector3 slopeEmitter = heldPoint + slopeNormal * 6;
            Vector3 s0 = Draw(heldPoint, slopeNormal, slopeEmitter, true), s1 = Draw(heldPoint + slopeTangent * .002f, slopeNormal, slopeEmitter, true);
            Check((s0 - s1).Length < 2e-5 && Math.Abs(Vector3.Dot(s0 - heldPoint, slopeNormal)) < 2e-5,
                "sloped receiver retains its plane and stable projected cell");
            // Supply a rotated camera and a fractional phase taken from a far,
            // negative double-world origin. The grid must stay on world axes.
            float[] inverseView = new float[16], view = new float[16];
            Mat4f.RotateY(inverseView, identity, .73f);
            Mat4f.RotateX(inverseView, inverseView, -.31f);
            Mat4f.Invert(view, inverseView);
            GL.UniformMatrix4(GL.GetUniformLocation(program, "invModelViewMatrix"), 1, false, inverseView);
            double[] origin = { -10000000.8125, 9999999.25, -10000000.125 };
            var farPhase = new Vector3((float)(origin[0] - Math.Floor(origin[0])),
                (float)(origin[1] - Math.Floor(origin[1])), (float)(origin[2] - Math.Floor(origin[2])));
            GL.Uniform3(GL.GetUniformLocation(program, "drtSunGridCameraPhase"), farPhase);
            static Vector3 Rotate(float[] matrix, Vector3 v) => new(
                matrix[0] * v.X + matrix[4] * v.Y + matrix[8] * v.Z,
                matrix[1] * v.X + matrix[5] * v.Y + matrix[9] * v.Z,
                matrix[2] * v.X + matrix[6] * v.Y + matrix[10] * v.Z);
            Vector3 RotatedDraw(Vector3 point)
            {
                GL.Uniform1(GL.GetUniformLocation(program, "drtShadowGridEnabled"), 1);
                GL.Uniform1(GL.GetUniformLocation(program, "drtShadowKind[0]"), 0);
                GL.Uniform3(GL.GetUniformLocation(program, "probeReceiver"), Rotate(view, point - farPhase));
                GL.Uniform3(GL.GetUniformLocation(program, "probeEmitter"), Rotate(view, slopeEmitter - farPhase));
                GL.Uniform3(GL.GetUniformLocation(program, "probeNormal"), Rotate(view, slopeNormal));
                GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
                float[] pixel = new float[4]; GL.ReadPixels(0, 0, 1, 1, PixelFormat.Rgba, PixelType.Float, pixel);
                return new Vector3(pixel[0], pixel[1], pixel[2]);
            }
            Vector3 r0 = RotatedDraw(heldPoint), r1 = RotatedDraw(heldPoint + slopeTangent * .002f);
            Check((r0 - s0).Length < 2e-5 && (r1 - s1).Length < 2e-5,
                "camera rotation and far negative origins preserve the same world shadow cell");
            Check(GL.GetError() == ErrorCode.NoError, "moving style probe has no GL errors");
        }
        finally
        {
            GL.BindBufferBase(BufferRangeTarget.UniformBuffer, 5, 0);
            GL.BindBuffer(BufferTarget.UniformBuffer, 0);
            GL.DeleteBuffer(heldBuffer); GL.DeleteTexture(texture); GL.DeleteFramebuffer(framebuffer);
            GL.DeleteVertexArray(vao); GL.DeleteProgram(program);
        }
        CheckPlacedToggle(modules);
        CheckCulling();
    }

    private static void CheckPlacedToggle(string modules)
    {
        string staticSource = File.ReadAllText(Path.Combine(modules, "drtagx_deferred_staticshadows.fsh"));
        string source = "#version 430 core\n" + ProbeShader.DeferredSource(Path.Combine(modules, "drtagx_deferred_uniforms.fsh")) + "\n" +
            File.ReadAllText(Path.Combine(modules, "../lighting/drtagx_light_balance.ash")) + "\n" + """
            uniform mat4 invModelViewMatrix;
            uniform vec3 receiver;
            vec3 captured = vec3(0);
            const float STATIC_SHADOW_GRID_CELLS = 32.0;
            float drtStaticTwoSidedVisibility(vec3 q,vec3 n,int slot,int secondary,float blend){return 1.0;}
            float drtStaticTwoSidedVisibilityWithTolerance(vec3 q,vec3 n,int slot,int secondary,float blend,float tolerance){return 1.0;}
            float drtStaticVisibility(vec3 q, vec3 n, float d, float plane, int slot, int secondary, float blend) {
                captured = q + vec3(0.5); return 1.0;
            }
            """ + ProbeShader.Function(staticSource, "vec3 drtStaticGridOffset(") + "\n" +
            File.ReadAllText(Path.Combine(modules, "drtagx_deferred_placedlights.fsh")) + "\n" + """
            out vec4 color;
            void main() {
                float emitter; vec3 selfLight;
                vec4 placed = drtPlacedLights(receiver, vec3(0,0,1), true, false, emitter, selfLight);
                color = vec4(captured, placed.r);
            }
            """;
        const string vertex = "#version 430 core\nvoid main(){vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);gl_Position=vec4(p*2-1,0,1);}";
        int program = ProbeShader.Program((ShaderType.VertexShader, vertex), (ShaderType.FragmentShader, source));
        int output = GL.GenTexture(), framebuffer = GL.GenFramebuffer(), vao = GL.GenVertexArray();
        int sources = GL.GenBuffer(), tiles = GL.GenBuffer();
        try
        {
            GL.BindTexture(TextureTarget.Texture2D, output);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba32f, 1, 1, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, output, 0);
            GL.DrawBuffer(DrawBufferMode.ColorAttachment0); GL.BindVertexArray(vao); GL.UseProgram(program);
            GL.Viewport(0, 0, 1, 1); GL.Disable(EnableCap.DepthTest);
            float[] records = { .5f,.5f,.5f,20, 1,1,1,0, 1,0,-1,0, .5f,.5f,.5f,20 };
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, sources);
            GL.BufferData(BufferTarget.ShaderStorageBuffer, records.Length * sizeof(float), records, BufferUsageHint.StaticDraw);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 4, sources);
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, tiles);
            GL.BufferData(BufferTarget.ShaderStorageBuffer, 5 * sizeof(uint), new uint[] { 1,0,0,0,0 }, BufferUsageHint.StaticDraw);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 5, tiles);
            GL.Uniform1(GL.GetUniformLocation(program, "drtStaticCount"), 1);
            GL.Uniform1(GL.GetUniformLocation(program, "drtStaticTileWidth"), 1);
            GL.Uniform1(GL.GetUniformLocation(program, "drtStaticBlend"), 1f);
            float[] identity = { 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1 }, calibration = new float[64];
            Array.Fill(calibration, 1f);
            GL.UniformMatrix4(GL.GetUniformLocation(program, "invModelViewMatrix"), 1, false, identity);
            GL.Uniform2(GL.GetUniformLocation(program, "drtPlacedCalibration[0]"), 32, calibration);
            Vector3 Draw(float x, bool grid)
            {
                GL.Uniform1(GL.GetUniformLocation(program, "drtShadowGridEnabled"), grid ? 1 : 0);
                GL.Uniform3(GL.GetUniformLocation(program, "receiver"), x, .254f, -4.117f);
                GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
                float[] pixel = new float[4]; GL.ReadPixels(0, 0, 1, 1, PixelFormat.Rgba, PixelType.Float, pixel);
                if (!(pixel[3] > 0)) throw new Exception("Placed grid probe did not reach the filter");
                return new Vector3(pixel[0], pixel[1], pixel[2]);
            }
            Vector3 on = Draw(-.121f, true), nearby = Draw(-.118f, true);
            Check((on - nearby).Length < 2e-5, "shared switch gives placed shadows fixed 32-cell surface sampling");
            Vector3 off = Draw(-.121f, false), offNearby = Draw(-.118f, false);
            Check((off - new Vector3(-.121f, .254f, -4.117f)).Length < 2e-5 && Math.Abs(offNearby.X - off.X - .003f) < 2e-5,
                "shared switch off restores continuous placed shadow comparisons");
            Check((Draw(-.121f, true) - on).Length < 2e-5, "reenabling the shared style restores the same placed cell");
            Check(GL.GetError() == ErrorCode.NoError, "placed style probe has no GL errors");
        }
        finally
        {
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 4, 0);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 5, 0);
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
            GL.DeleteBuffer(sources); GL.DeleteBuffer(tiles); GL.DeleteTexture(output);
            GL.DeleteFramebuffer(framebuffer); GL.DeleteVertexArray(vao); GL.DeleteProgram(program);
        }
    }

    private static void CheckCulling()
    {
        using var fixture = new StaticCacheProbeFixture(2);
        var front = new StaticLightSources.Source(0, 0, -40, 0, 0, 20);
        var behind = new StaticLightSources.Source(0, 0, 40, 0, 0, 20);
        fixture.Source(front); fixture.Source(behind);
        fixture.Resident(front, 0); fixture.Resident(behind, 1); fixture.Settle();
        Check(fixture.Maps.Active.Count == 1 && fixture.Maps.ValidCount == 2,
            "default culling publishes visible volumes and retains offscreen depth");
        int admissions = fixture.Maps.Admissions;
        for (int toggle = 0; toggle < 4; toggle++)
        {
            fixture.Maps.ViewCullingEnabled = false; fixture.Settle();
            Check(fixture.Maps.Active.Count == 2 && fixture.Maps.FacesBakedThisFrame == 0,
                "culling off wakes a stationary cache and publishes both directions without rebaking");
            fixture.Maps.ViewCullingEnabled = true; fixture.Settle();
            Check(fixture.Maps.Active.Count == 1 && fixture.Maps.ValidCount == 2 && fixture.Maps.Admissions == admissions,
                "culling on restores view selection and preserves completed maps");
        }
        var queue = new PlacedLightPendingQueue();
        var residents = new Dictionary<StaticLightSources.Position, int>();
        queue.Update(fixture.Sources, fixture.Camera, fixture.View, fixture.Projection, residents, Now);
        Check(queue.VisibleCount == 1, "pending admissions honor default view culling");
        queue.ViewCullingEnabled = false;
        queue.Update(fixture.Sources, fixture.Camera, fixture.View, fixture.Projection, residents, Now);
        Check(queue.VisibleCount == 2, "stationary culling toggle reclassifies already queued offscreen sources");
        queue.ViewCullingEnabled = true;
        queue.Update(fixture.Sources, fixture.Camera, fixture.View, fixture.Projection, residents, Now);
        Check(queue.VisibleCount == 1, "pending view classification returns when culling is reenabled");
    }
}
