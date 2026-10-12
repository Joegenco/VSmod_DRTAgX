using System;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;

namespace DRTAgX
{
    internal static class AgxShaderUniforms
    {
        // Called before the native final pass; restore the shader active on entry.
        internal static void Apply(ICoreClientAPI api, AgxConfig config)
        {
            IShaderProgram? targetShader = api.Render.GetEngineShader(EnumShaderProgram.Final);
            if (targetShader == null || targetShader.Disposed || targetShader.LoadError) return;

            // Preserve incoming active shader so other mods and engine passes remain unaffected
            IShaderProgram? activeShader = api.Render.CurrentActiveShader;
            int previousProgram = GL.GetInteger(GetPName.CurrentProgram);
            try
            {
                targetShader.Use();

                if (targetShader.HasUniform("AgxMinEv")) targetShader.Uniform("AgxMinEv", config.AgxMinEv);
                if (targetShader.HasUniform("AgxMaxEv")) targetShader.Uniform("AgxMaxEv", config.AgxMaxEv);
                if (targetShader.HasUniform("GreyPoint")) targetShader.Uniform("GreyPoint", config.GreyPoint);
                if (targetShader.HasUniform("INV_GREY_POINT"))
                    targetShader.Uniform("INV_GREY_POINT", 1.0f / Math.Max(config.GreyPoint, 0.0001f));
                // Apply the configured exposure directly, independently of PLS.
                if (targetShader.HasUniform("Exposure"))
                    targetShader.Uniform("Exposure", config.Exposure);

                // Native gameplay strength drives the final scene tint and its
                // fade. Publish every frame, independently of HDR readiness.
                if (targetShader.HasUniform("drtNightVisionStrength"))
                {
                    var strength = api.Render.ShaderUniforms.NightVisionStrength;
                    targetShader.Uniform("drtNightVisionStrength",
                        float.IsFinite(strength) ? Math.Clamp(strength, 0f, 1f) : 0f);
                }
            }
            finally
            {
                // Uniform setters can throw during reload. Preserve both the
                // engine's managed shader and raw GL state, even if Stop fails.
                try
                {
                    try
                    {
                        targetShader.Stop();
                    }
                    finally
                    {
                        activeShader?.Use();
                    }
                }
                finally
                {
                    GL.UseProgram(previousProgram);
                }
            }
        }
    }
}