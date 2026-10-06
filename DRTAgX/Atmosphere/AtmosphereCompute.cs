using System;
using System.Text;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace DRTAgX;

/// <summary>Small GL 4.3 loader; compilation occurs only on initialization/reload.</summary>
internal static class AtmosphereCompute
{
    internal static int Load(ICoreClientAPI api, string name)
    {
        string Expand(string file)
        {
            string source = api.Assets.Get(new AssetLocation("drtagx", "shaders/atmosphere/" + file)).ToText();
            var result = new StringBuilder();
            foreach (string line in source.Split('\n'))
            {
                string trimmed = line.Trim();
                result.AppendLine(trimmed.StartsWith("#include ", StringComparison.Ordinal)
                    ? Expand(trimmed[9..].Trim()) : line);
            }
            return result.ToString();
        }
        int shader = GL.CreateShader(ShaderType.ComputeShader), program = 0;
        try
        {
            GL.ShaderSource(shader, Expand(name + ".csh"));
            GL.CompileShader(shader);
            GL.GetShader(shader, ShaderParameter.CompileStatus, out int compiled);
            if (compiled == 0) throw new InvalidOperationException(name + ": " + GL.GetShaderInfoLog(shader));
            program = GL.CreateProgram();
            GL.AttachShader(program, shader);
            GL.LinkProgram(program);
            GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int linked);
            if (linked == 0) throw new InvalidOperationException(name + ": " + GL.GetProgramInfoLog(program));
            return program;
        }
        catch { if (program != 0) GL.DeleteProgram(program); throw; }
        finally { GL.DeleteShader(shader); }
    }

    internal static int Texture(int width, int height, int depth = 0)
    {
        int texture = GL.GenTexture();
        TextureTarget target = depth == 0 ? TextureTarget.Texture2D : TextureTarget.Texture3D;
        GL.BindTexture(target, texture);
        if (depth == 0) GL.TexStorage2D(TextureTarget2d.Texture2D, 1, SizedInternalFormat.Rgba16f, width, height);
        else GL.TexStorage3D(TextureTarget3d.Texture3D, 1, SizedInternalFormat.R16f, width, height, depth);
        GL.TexParameter(target, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(target, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(target, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(target, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(target, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
        return texture;
    }

    internal static void Dispatch2D(int program, int texture, int width, int height)
    {
        GL.UseProgram(program);
        GL.BindImageTexture(0, texture, 0, false, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);
        GL.DispatchCompute((width + 7) / 8, (height + 7) / 8, 1);
        GL.MemoryBarrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit | MemoryBarrierFlags.TextureFetchBarrierBit);
    }
}
