using System;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;

/// <summary>Partially completed native-like Use/Uniform/Stop calls must restore shader ownership.</summary>
internal static class ShaderExceptionProbe
{
    internal static void Run()
    {
        const string vertex = "#version 430 core\nvoid main(){gl_Position=vec4(0);}";
        const string fragment = "#version 430 core\nuniform float Exposure;out vec4 color;void main(){color=vec4(exp2(Exposure));}";
        int target = ProbeShader.Program((ShaderType.VertexShader, vertex), (ShaderType.FragmentShader, fragment));
        int incoming = ProbeShader.Program((ShaderType.VertexShader, vertex), (ShaderType.FragmentShader, fragment));
        int previous = GL.GetInteger(GetPName.CurrentProgram);
        try
        {
            foreach (bool hadManagedShader in new[] { false, true })
            foreach (string failingCall in new[] { "Use", "Uniform", "Stop" })
            {
                IShaderProgram current = null, final = null, active = null;
                IShaderProgram Shader(int program) => SurfaceApiProxy.Make<IShaderProgram>((method, args) =>
                {
                    switch (method.Name)
                    {
                        case "get_Disposed": case "get_LoadError": return false;
                        case "HasUniform": return (string)args[0] == "Exposure";
                        case "Use": current = program == target ? final : active; GL.UseProgram(program); break;
                        case "Stop": current = null; GL.UseProgram(0); break;
                        case "Uniform": GL.Uniform1(GL.GetUniformLocation(program, "Exposure"), (float)args[1]); break;
                        default: throw new NotSupportedException(method.Name);
                    }
                    if (program == target && method.Name == failingCall) throw new InvalidOperationException("Injected shader failure");
                    return null;
                });
                final = Shader(target); active = Shader(incoming);
                var render = SurfaceApiProxy.Make<IRenderAPI>((method, _) => method.Name switch {
                    "GetEngineShader" => final, "get_CurrentActiveShader" => current,
                    _ => throw new NotSupportedException(method.Name)
                });
                var api = SurfaceApiProxy.Make<ICoreClientAPI>((method, _) => method.Name == "get_Render" ? render : throw new NotSupportedException(method.Name));
                current = hadManagedShader ? active : null;
                // A mod can bind raw GL without updating native shader tracking.
                // Deliberately seed different managed and raw entry states.
                GL.UseProgram(0);
                bool failed = false;
                try { AgxShaderUniforms.Apply(api, new AgxConfig { Exposure = .75f }); }
                catch (InvalidOperationException) { failed = true; }
                if (!failed || !ReferenceEquals(current, hadManagedShader ? active : null) || GL.GetInteger(GetPName.CurrentProgram) != 0)
                    throw new Exception("Shader restoration failed after " + failingCall);
            }
            if (GL.GetError() != ErrorCode.NoError) throw new Exception("Shader exception probe GL error");
            Console.WriteLine("PASS shader upload failures: partial Use, Uniform, Stop; managed/null shader and independent raw program restored");
        }
        finally { GL.UseProgram(previous); GL.DeleteProgram(target); GL.DeleteProgram(incoming); }
    }
}
