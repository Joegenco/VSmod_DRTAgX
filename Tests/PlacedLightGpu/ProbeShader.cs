using System;
using System.Collections.Generic;
using System.IO;
using OpenTK.Graphics.OpenGL4;

internal static class ProbeShader
{
    // The native registry indexes owned includes by unique filename. Expand
    // those same maintained modules for isolated shader-function probes.
    internal static string DeferredSource(string path)
    {
        // Accept an owned module directly as well as the canonical relight
        // entry point; both resolve includes from the same maintained directory.
        string parent = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string directory = Path.GetFileName(parent) == "deferred" ? parent :
            Path.GetFullPath(Path.Combine(parent, "../../drtagx/shaders/deferred"));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string Expand(string file)
        {
            if (!seen.Add(Path.GetFileName(file))) return "";
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (!line.StartsWith("#include drtagx_", StringComparison.Ordinal)) continue;
                string name = line[9..].Trim();
                if (Path.GetFileName(name) != name) throw new Exception("Owned include must use its unique filename");
                string owned = Path.Combine(directory, name);
                if (!File.Exists(owned)) owned = Path.Combine(directory, "../lighting", name);
                if (!File.Exists(owned)) owned = Path.Combine(directory, "../atmosphere", name);
                if (!File.Exists(owned)) owned = Path.Combine(directory, "..", name);
                lines[i] = Expand(owned);
            }
            return string.Join("\n", lines);
        }
        return Expand(Path.GetFullPath(path));
    }

    internal static int Program(params (ShaderType Type, string Source)[] stages)
    {
        int program = GL.CreateProgram();
        foreach (var stage in stages)
        {
            int shader = GL.CreateShader(stage.Type);
            GL.ShaderSource(shader, stage.Source);
            GL.CompileShader(shader);
            GL.GetShader(shader, ShaderParameter.CompileStatus, out int compiled);
            if (compiled == 0) throw new Exception(GL.GetShaderInfoLog(shader));
            GL.AttachShader(program, shader);
            GL.DeleteShader(shader);
        }
        GL.LinkProgram(program);
        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int linked);
        if (linked == 0) throw new Exception(GL.GetProgramInfoLog(program));
        return program;
    }

    internal static string Function(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0) throw new Exception("Missing maintained function: " + signature);
        int depth = 0;
        for (int i = source.IndexOf('{', start); i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            if (source[i] == '}' && --depth == 0) return source[start..(i + 1)];
        }
        throw new Exception("Unclosed maintained function: " + signature);
    }
}
