using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

// Hidden-context checks of the maintained GLSL grid and real D24 contact filter.
internal static class StaticGridProbe
{
    internal static void Run(string maintained)
    {
        using var state = new ShadowGlState();
        string constants = string.Join("\n", maintained.Split('\n').Where(line => line.StartsWith("const float STATIC_")));
        float cells = float.Parse(Regex.Match(constants, @"STATIC_SHADOW_GRID_CELLS = ([\d.]+)").Groups[1].Value, CultureInfo.InvariantCulture);
        float step = 1 / cells;
        string vertex = "#version 430 core\nvoid main(){vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);gl_Position=vec4(p*2-1,0,1);}";
        string fragment = """
            #version 430 core
            uniform sampler2DArrayShadow drtStaticMaps;
            uniform vec3 receiver, anchor, normal;
            uniform int mode;
            out vec4 color;
            """ + "\n" + constants + "\n" +
            ProbeShader.Function(maintained, "vec3 drtStaticGridOffset(") + "\n" +
            ProbeShader.Function(maintained, "int drtCubeFace(") + "\n" +
            ProbeShader.Function(maintained, "float drtStaticFaceVisibility(") + "\n" +
            ProbeShader.Function(maintained, "float drtStaticVisibilityAtSlot(") + "\n" +
            ProbeShader.Function(maintained, "float drtStaticVisibility(") + "\n" + """
            void main() {
                vec3 offset = drtStaticGridOffset(receiver - anchor, normal);
                vec3 q = receiver + offset;
                if (mode == 0) color = vec4(q, dot(offset, normal));
                else {
                    vec3 receivingNormal = mode == 2 ? -normalize(q) : normal;
                    color = vec4(drtStaticVisibility(q, receivingNormal, length(q), dot(receivingNormal, q), 0, 0, 0));
                }
            }
            """;
        int program = ProbeShader.Program((ShaderType.VertexShader, vertex), (ShaderType.FragmentShader, fragment));
        int wide = ProbeShader.Program((ShaderType.VertexShader, vertex), (ShaderType.FragmentShader,
            fragment.Replace("STATIC_SHADOW_FILTER_RADIUS = 0.5", "STATIC_SHADOW_FILTER_RADIUS = 1.5")));
        int output = GL.GenTexture(), depth = GL.GenTexture(), fbo = GL.GenFramebuffer(), vao = GL.GenVertexArray();
        int oldActive = GL.GetInteger(GetPName.ActiveTexture);
        GL.ActiveTexture(TextureUnit.Texture14);
        int old2D = GL.GetInteger(GetPName.TextureBinding2D), oldArray = GL.GetInteger(GetPName.TextureBinding2DArray);
        int oldSampler = GL.GetInteger(GetPName.SamplerBinding);
        try
        {
            // Keep texture unit 14 and all raster state private to this probe.
            GL.BindSampler(14, 0); GL.BindTexture(TextureTarget.Texture2D, output);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba32f, 1, 1, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, output, 0);
            GL.DrawBuffer(DrawBufferMode.ColorAttachment0); GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
            if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete)
                throw new Exception("Grid probe framebuffer incomplete");
            GL.BindVertexArray(vao); GL.Viewport(0, 0, 1, 1);
            GL.Disable(EnableCap.DepthTest); GL.Disable(EnableCap.Blend); GL.Disable(EnableCap.ScissorTest); GL.ColorMask(true, true, true, true);
            float[] pixel = new float[4];
            Vector4 Draw(Vector3 q, Vector3 n, Vector3 a = default, int mode = 0, bool broad = false)
            {
                int active = broad ? wide : program;
                GL.UseProgram(active);
                GL.Uniform1(GL.GetUniformLocation(active, "drtStaticMaps"), 14);
                GL.Uniform1(GL.GetUniformLocation(active, "mode"), mode);
                GL.Uniform3(GL.GetUniformLocation(active, "receiver"), q);
                GL.Uniform3(GL.GetUniformLocation(active, "normal"), n);
                GL.Uniform3(GL.GetUniformLocation(active, "anchor"), a);
                GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
                GL.ReadPixels(0, 0, 1, 1, PixelFormat.Rgba, PixelType.Float, pixel);
                if (pixel.Any(value => !float.IsFinite(value))) throw new Exception("Nonfinite grid sample");
                return new(pixel[0], pixel[1], pixel[2], pixel[3]);
            }
            void Check(bool valid, string message)
            {
                if (!valid) throw new Exception(message);
                Console.WriteLine("PASS " + message);
            }
            foreach (Vector3 n in new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ })
            {
                Vector3 q = new(.123f, -.234f, .345f);
                Vector4 sample = Draw(q, n);
                Vector3 delta = sample.Xyz - q;
                Check(Math.Abs(Vector3.Dot(delta, n)) < 1e-6 && Math.Abs(sample.W) < 1e-6,
                    "grid preserves receiver plane for normal " + n);
            }
            Vector4 first = Draw(new(.05f * step, -4.5f, .1f * step), Vector3.UnitY);
            Vector4 second = Draw(new(.9f * step, -4.5f, .8f * step), Vector3.UnitY);
            Check((first - second).Length < 1e-6 && Math.Abs(first.X - .5f * step) < 1e-6,
                "all pixels in a surface cell use its fixed shadow centre");
            Vector4 negative = Draw(new(-.1f * step, -4.5f, -.8f * step), Vector3.UnitY);
            Check(Math.Abs(negative.X + .5f * step) < 1e-6 && Math.Abs(negative.Z + .5f * step) < 1e-6,
                "negative coordinates retain the block-aligned cell phase");
            Vector3 slope = Vector3.Normalize(new Vector3(.2f, .7f, .3f));
            Check(Math.Abs(Draw(new(.123f, -.234f, .345f), slope).W) < 1e-6,
                "sloped receivers keep their actual plane while snapping");
            // Represent global positions relative to moving cameras, just like
            // the prepared source records, without uploading large float worlds.
            foreach (double world in new[] { 0.0, 10000000.0, -10000000.0 })
            {
                Vector3 expected = new(.390625f, -4.5f, -.078125f);
                foreach (double camera in new[] { -.456, .123, .789 })
                {
                    Vector3 q = new((float)((world + .4) - (world + camera)), -4.5f, -.08f);
                    Vector3 a = new((float)((world + .5) - (world + camera)), .5f, .5f);
                    Vector4 sample = Draw(q, Vector3.UnitY, a);
                    Vector3 globalRelative = sample.Xyz + new Vector3((float)camera, 0, 0);
                    Vector4 changedAnchor = Draw(q, Vector3.UnitY, a + new Vector3(31, -12, 17));
                    // Expected cell centres follow whichever 16/32 constant is active.
                    expected.X = (MathF.Floor(.4f * cells) + .5f) / cells;
                    expected.Z = (MathF.Floor(-.08f * cells) + .5f) / cells;
                    if ((globalRelative - expected).Length > 3e-5 || (changedAnchor - sample).Length > 3e-5)
                        throw new Exception("Camera/source changes moved the world shadow grid");
                }
            }
            Check(true, "grid survives camera movement, source-anchor changes and +/-10M world origins");

            int size = StaticTerrainShadowMaps.FaceSize;
            GL.BindTexture(TextureTarget.Texture2DArray, depth);
            GL.TexStorage3D(TextureTarget3d.Texture2DArray, 1, SizedInternalFormat.DepthComponent24, size, size, 6);
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureCompareMode, (int)TextureCompareMode.CompareRefToTexture);
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureCompareFunc, (int)DepthFunction.Lequal);
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            float[] data = new float[size * size];
            for (int face = 0; face < 6; face++)
            {
                for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
                {
                    float u = ((x + .5f) / size - .5f) / .47f, v = ((y + .5f) / size - .5f) / .47f;
                    Vector3 ray = face switch { 0 => new(1, -v, u), 1 => new(-1, -v, -u), 2 => new(-u, 1, v),
                        3 => new(-u, -1, -v), 4 => new(-u, -v, 1), _ => new(u, -v, -1) };
                    // Light at (.5,4.5,.5), ground y=0 and wall x=3. Intersect
                    // true planes independently, then apply native projected depth.
                    double hit = ray.Y < 0 ? -4.5 / ray.Y : double.PositiveInfinity;
                    if (ray.X > 0)
                    {
                        double wall = 2.5 / ray.X;
                        if (wall * ray.Y >= -4.5) hit = Math.Min(hit, wall);
                    }
                    data[y * size + x] = hit >= .1 && hit <= 22 ? (float)(22.0 / 21.9 - 2.2 / (21.9 * hit)) : 1;
                }
                GL.TexSubImage3D(TextureTarget.Texture2DArray, 0, 0, 0, face, size, size, 1, PixelFormat.DepthComponent, PixelType.Float, data);
            }
            // A low lamp's grass tip is above its emitter, while the root is below.
            // The static scene has no blocker along either of these rays.
            Check(Draw(new(1, .4f, 0), Vector3.UnitY, mode: 1).X < .001f,
                "solid one-sided receiving plane rejects a grass tip above the emitter");
            Check(Draw(new(1, -.4f, 0), Vector3.UnitY, mode: 2).X > .999f &&
                Draw(new(1, .4f, 0), Vector3.UnitY, mode: 2).X > .999f,
                "thin grass roots and tips both receive unobstructed placed light");
            Check(Draw(new(3, -.4f, 0), Vector3.UnitY, mode: 2).X < .001f,
                "thin grass still receives real static wall occlusion");
            Vector3 ground = new(2.4f, -4.5f, 0);
            float tightVisibility = Draw(ground, Vector3.UnitY, mode: 1).X;
            float wideVisibility = Draw(ground, Vector3.UnitY, mode: 1, broad: true).X;
            Check(tightVisibility > wideVisibility + .15f,
                $"narrow contact filter reduces false wall shadow: wide={wideVisibility:F6}, narrow={tightVisibility:F6}");
            Check(Math.Abs(Draw(new(2.392f, -4.5f, .002f), Vector3.UnitY, mode: 1).X -
                Draw(new(2.406f, -4.5f, .020f), Vector3.UnitY, mode: 1).X) < 1e-6,
                "real D24 contact visibility stays constant within the same 32-cell surface patch");
            Check(Draw(new(2.1f, -4.5f, 0), Vector3.UnitY, mode: 1).X > .999f &&
                Draw(new(3.0f, -4.5f, 0), Vector3.UnitY, mode: 1).X < .001f,
                "grid contact filter preserves clear ground and real wall occlusion");
            Check(GL.GetError() == ErrorCode.NoError, "grid/filter probe has no GL errors");
        }
        finally
        {
            GL.ActiveTexture(TextureUnit.Texture14);
            GL.BindTexture(TextureTarget.Texture2D, old2D); GL.BindTexture(TextureTarget.Texture2DArray, oldArray); GL.BindSampler(14, oldSampler);
            GL.ActiveTexture((TextureUnit)oldActive);
            GL.DeleteFramebuffer(fbo); GL.DeleteVertexArray(vao); GL.DeleteTexture(output); GL.DeleteTexture(depth);
            GL.DeleteProgram(program); GL.DeleteProgram(wide);
        }
    }
}
