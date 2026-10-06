using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;

namespace DRTAgX;

/// <summary>Metadata-only inventory. No pixel readback; borrowed GL objects remain untouched.</summary>
internal static class FrameResourceInventory
{
    internal static void Write(ICoreClientAPI api, string directory)
    {
        var buffers = new Dictionary<int, List<string>>();
        var rows = new List<object>();
        var known2D = new HashSet<int>();
        var errors = new List<string>();
        void Check(string location)
        {
            var error = GL.GetError();
            if (error != ErrorCode.NoError) errors.Add(location + ": " + error);
        }
        Check("entry (pre-existing)");
        void Add(FrameBufferRef? buffer, string owner)
        {
            if (buffer == null || buffer.Disposed || buffer.FboId <= 0) return;
            if (!buffers.TryGetValue(buffer.FboId, out var owners)) buffers.Add(buffer.FboId, owners = new());
            owners.Add(owner);
            if (buffer.ColorTextureIds != null) foreach (int id in buffer.ColorTextureIds) if (id > 0) known2D.Add(id);
            if (buffer.DepthTextureId > 0) known2D.Add(buffer.DepthTextureId);
        }
        for (int i = 0; i < api.Render.FrameBuffers.Count; ++i) Add(api.Render.FrameBuffers[i], "native-slot" + i);
        object? system = api.ModLoader.GetModSystem("SheyderMod.SheyderModSystem");
        if (system != null && AccessTools.Field(system.GetType(), "_renderers")?.GetValue(system) is IEnumerable renderers)
            foreach (object renderer in renderers)
                foreach (FieldInfo field in renderer.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                    if (field.FieldType == typeof(FrameBufferRef)) Add(field.GetValue(renderer) as FrameBufferRef, renderer.GetType().Name + "/" + field.Name);
        // Water owns additional unattached/history FBOs and is registered outside _renderers in 1.1.3.
        if (system != null && AccessTools.Field(system.GetType(), "_waterSsr")?.GetValue(system) is object water)
            foreach (FieldInfo field in water.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                if (field.FieldType == typeof(FrameBufferRef)) Add(field.GetValue(water) as FrameBufferRef, "SheyderWaterSSR/" + field.Name);
        GL.GetInteger(GetPName.DrawFramebufferBinding, out int draw);
        GL.GetInteger(GetPName.ActiveTexture, out int active);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.GetInteger(GetPName.TextureBinding2D, out int texture);
        GL.GetInteger(GetPName.RenderbufferBinding, out int renderbuffer);
        GL.GetInteger(GetPName.MaxColorAttachments, out int maxColors);
        Check("save state");
        try
        {
            foreach (var pair in buffers)
            {
                GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, pair.Key);
                Check("bind FBO " + pair.Key);
                for (int i = -2; i < maxColors; ++i)
                {
                    var attachment = i == -2 ? FramebufferAttachment.DepthAttachment : i == -1 ? FramebufferAttachment.StencilAttachment : FramebufferAttachment.ColorAttachment0 + i;
                    GL.GetFramebufferAttachmentParameter(FramebufferTarget.DrawFramebuffer, attachment, FramebufferParameterName.FramebufferAttachmentObjectType, out int type);
                    Check("attachment type " + pair.Key + "/" + i);
                    if (type == 0) continue;
                    GL.GetFramebufferAttachmentParameter(FramebufferTarget.DrawFramebuffer, attachment, FramebufferParameterName.FramebufferAttachmentObjectName, out int id);
                    if (type == (int)All.Texture)
                    {
                        if (!known2D.Contains(id))
                        {
                            // An extra OIT attachment can be an array texture. GL 4.3 cannot query its target
                            // by name; never guess by rebinding it to 2D. Its ownership remains unresolved.
                            GL.GetFramebufferAttachmentParameter(FramebufferTarget.DrawFramebuffer, attachment, FramebufferParameterName.FramebufferAttachmentLayered, out int layered);
                            rows.Add(new { Fbo = pair.Key, Owners = pair.Value, Attachment = attachment.ToString(), Kind = "texture (unresolved target)", Id = id, Layered = layered });
                            continue;
                        }
                        // Enumerated FrameBufferRef scene attachments are 2D; cube/array owners need their declared targets.
                        GL.BindTexture(TextureTarget.Texture2D, id);
                        Check("bind texture " + id);
                        int Param(GetTextureParameter parameter) { GL.GetTexLevelParameter(TextureTarget.Texture2D, 0, parameter, out int value); return value; }
                        rows.Add(new { Fbo = pair.Key, Owners = pair.Value, Attachment = attachment.ToString(), Kind = "texture2D", Id = id,
                            Width = Param(GetTextureParameter.TextureWidth), Height = Param(GetTextureParameter.TextureHeight),
                            Format = Param(GetTextureParameter.TextureInternalFormat), R = Param(GetTextureParameter.TextureRedSize),
                            G = Param(GetTextureParameter.TextureGreenSize), B = Param(GetTextureParameter.TextureBlueSize),
                            A = Param(GetTextureParameter.TextureAlphaSize), Depth = Param(GetTextureParameter.TextureDepthSize) });
                        Check("texture metadata " + id);
                    }
                    else if (type == (int)All.Renderbuffer)
                    {
                        GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, id);
                        int Param(RenderbufferParameterName parameter) { GL.GetRenderbufferParameter(RenderbufferTarget.Renderbuffer, parameter, out int value); return value; }
                        rows.Add(new { Fbo = pair.Key, Owners = pair.Value, Attachment = attachment.ToString(), Kind = "renderbuffer", Id = id,
                            Width = Param(RenderbufferParameterName.RenderbufferWidth), Height = Param(RenderbufferParameterName.RenderbufferHeight),
                            Format = Param(RenderbufferParameterName.RenderbufferInternalFormat), Samples = Param(RenderbufferParameterName.RenderbufferSamples) });
                    }
                }
            }
        }
        finally
        {
            // Restore the only bindings changed by this audit; do not touch pixel-unpack or sampled contents.
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, draw);
            GL.BindTexture(TextureTarget.Texture2D, texture);
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, renderbuffer);
            GL.ActiveTexture((TextureUnit)active);
        }
        Check("restore");
        File.WriteAllText(Path.Combine(directory, "resource-inventory.json"), JsonConvert.SerializeObject(rows, Formatting.Indented));
        File.WriteAllText(Path.Combine(directory, "resource-inventory-errors.json"), JsonConvert.SerializeObject(errors, Formatting.Indented));
        WriteShaderEvidence(api, directory);
        DeclaredFrameResources.Write(api, directory);
    }

    private static void WriteShaderEvidence(ICoreClientAPI api, string directory)
    {
        var uniforms = new Dictionary<string, object>();
        foreach (var kind in new[] { EnumShaderProgram.Final, EnumShaderProgram.Chunkopaque })
        {
            var shader = api.Render.GetEngineShader(kind);
            if (shader == null || shader.Disposed || shader.ProgramId <= 0) continue;
            GL.GetProgram(shader.ProgramId, GetProgramParameterName.AttachedShaders, out int count);
            var attached = new int[count];
            GL.GetAttachedShaders(shader.ProgramId, count, out int actual, attached);
            for (int i = 0; i < actual; ++i)
            {
                GL.GetShader(attached[i], ShaderParameter.ShaderType, out int type);
                GL.GetShader(attached[i], ShaderParameter.ShaderSourceLength, out int length);
                GL.GetShaderSource(attached[i], length, out _, out string source);
                File.WriteAllText(Path.Combine(directory, kind + "-" + (ShaderType)type + ".glsl"), source);
            }
            foreach (string name in new[] { "deferredMode", "drtForwardDecalPass", "haxyFade", "drtTerrainPlacedCount", "shadowMapWidthInv", "shadowMapHeightInv", "shadowRangeNear", "shadowRangeFar", "toShadowMapSpaceMatrixFar", "toShadowMapSpaceMatrixNear" })
            {
                int location = GL.GetUniformLocation(shader.ProgramId, name);
                if (location < 0) continue;
                if (name.StartsWith("toShadow", StringComparison.Ordinal))
                {
                    var matrix = new float[16]; GL.GetUniform(shader.ProgramId, location, matrix);
                    uniforms[kind + "/" + name] = matrix;
                }
                else if (name is "deferredMode" or "drtForwardDecalPass" or "drtTerrainPlacedCount" or "haxyFade")
                {
                    // Routing and source counts are integer uniforms; preserve their native type.
                    GL.GetUniform(shader.ProgramId, location, out int value);
                    uniforms[kind + "/" + name] = value;
                }
                else { GL.GetUniform(shader.ProgramId, location, out float value); uniforms[kind + "/" + name] = value; }
            }
        }
        // Public native range values also survive when a deferred forward
        // variant optimizes out the corresponding GL uniform. Observe only.
        var native = api.Render.ShaderUniforms;
        if (native != null)
        {
            uniforms["Native/ShadowRangeNear"] = native.ShadowRangeNear;
            uniforms["Native/ShadowRangeFar"] = native.ShadowRangeFar;
        }
        File.WriteAllText(Path.Combine(directory, "shader-routing-and-sun-matrices.json"), JsonConvert.SerializeObject(uniforms, Formatting.Indented));
    }
}
