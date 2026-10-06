using System;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;

// Exercise the production final-pass binder against real GL uniforms. The
// manual exposure remains independent of the PLS toggle and saved configuration.
internal static class ExposureBindingsProbe
{
    internal static void Run()
    {
        ProbeAssets.Api([]); // Resolve optional native UI signature types before proxy creation.
        using var saved = new ShadowGlState();
        const string vertex = "#version 430 core\nvoid main(){gl_Position=vec4(0);}";
        const string fragment = "#version 430 core\nuniform float Exposure;out vec4 color;void main(){color=vec4(exp2(Exposure));}";
        int target = ProbeShader.Program((ShaderType.VertexShader, vertex), (ShaderType.FragmentShader, fragment));
        int incoming = ProbeShader.Program((ShaderType.VertexShader, vertex), (ShaderType.FragmentShader, fragment));
        int previous = GL.GetInteger(GetPName.CurrentProgram);
        try
        {
            IShaderProgram Shader(int program) => SurfaceApiProxy.Make<IShaderProgram>((method, args) =>
            {
                switch (method.Name)
                {
                    case "get_Disposed": case "get_LoadError": return false;
                    case "Use": GL.UseProgram(program); return null;
                    case "Stop": GL.UseProgram(0); return null;
                    case "HasUniform": return GL.GetUniformLocation(program, (string)args[0]) >= 0;
                    case "Uniform": GL.Uniform1(GL.GetUniformLocation(program, (string)args[0]), (float)args[1]); return null;
                    default: throw new NotSupportedException(method.Name);
                }
            });
            var final = Shader(target); var active = Shader(incoming);
            var render = SurfaceApiProxy.Make<IRenderAPI>((method, args) => method.Name switch
            {
                "GetEngineShader" when (EnumShaderProgram)args[0] == EnumShaderProgram.Final => final,
                "get_CurrentActiveShader" => active,
                _ => throw new NotSupportedException(method.Name)
            });
            var api = SurfaceApiProxy.Make<ICoreClientAPI>((method, _) => method.Name == "get_Render" ? render : throw new NotSupportedException(method.Name));
            int checks = 0;
            // Include repeated toggle cycles and both auto-exposure states: no
            // cache-readiness gate or accumulated correction is permitted here.
            foreach (float manual in new[] { -2.25f, 0f, 1.75f })
            foreach (bool auto in new[] { false, true })
            {
                var config = new AgxConfig { Exposure = manual, AutoExposureEnabled = auto };
                foreach (bool placed in new[] { false, true, false, true })
                {
                    config.StaticLightShadows = placed; GL.UseProgram(incoming);
                    AgxShaderUniforms.Apply(api, config);
                    GL.GetUniform(target, GL.GetUniformLocation(target, "Exposure"), out float actual);
                    if (actual != manual || config.Exposure != manual ||
                        GL.GetInteger(GetPName.CurrentProgram) != incoming)
                        throw new Exception($"PLS exposure binding/save/state mismatch: {manual}, auto={auto}, placed={placed}, actual={actual}");
                    checks++;
                }
            }
            if (GL.GetError() != ErrorCode.NoError) throw new Exception("Exposure binding GL error");
            Console.WriteLine($"PASS exposure: {checks} production binding checks; direct manual EV, auto/manual, PLS toggle cycles, saved EV and incoming shader preserved");
            ShaderExceptionProbe.Run();
        }
        finally { GL.UseProgram(previous); GL.DeleteProgram(target); GL.DeleteProgram(incoming); }
    }
}
