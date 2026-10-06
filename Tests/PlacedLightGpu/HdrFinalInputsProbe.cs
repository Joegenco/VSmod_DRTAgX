using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

/// <summary>Final inputs stay borrowed through native composition and restore on failure.</summary>
internal static class HdrFinalInputsProbe
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(HdrPostProcessor owner, HdrShaderFixture shaders, AgxConfig config)
    {
        const string vertex = "#version 430 core\nvoid main(){gl_Position=vec4(0);}";
        const string fragment = """
            #version 430 core
            uniform int drtBloomReady, drtExposureEnabled;
            uniform float drtBloomStrength;
            uniform sampler2D drtBloomTex, drtExposureTex;
            out vec4 color;
            void main(){color=vec4(texture(drtBloomTex,vec2(.5)).rgb*float(drtBloomReady)*drtBloomStrength+
                texture(drtExposureTex,vec2(.5)).rrr*float(drtExposureEnabled),1);}
            """;
        int program = ProbeShader.Program((ShaderType.VertexShader, vertex), (ShaderType.FragmentShader, fragment));
        int incomingProgram = ProbeShader.Program((ShaderType.VertexShader, vertex), (ShaderType.FragmentShader, fragment));
        int activeUnit = GL.GetInteger(GetPName.ActiveTexture);
        int[] oldTextures = new int[2], oldSamplers = new int[2], textures = new int[2], samplers = new int[2];
        var currentField = typeof(HdrFinalHook).GetField("_current", BindingFlags.Static | BindingFlags.NonPublic);
        object previousHook = currentField.GetValue(null);
        try {
            for (int i = 0; i < 2; i++) {
                GL.ActiveTexture(TextureUnit.Texture8 + i);
                oldTextures[i] = GL.GetInteger(GetPName.TextureBinding2D); oldSamplers[i] = GL.GetInteger(GetPName.SamplerBinding);
                textures[i] = GL.GenTexture(); samplers[i] = GL.GenSampler();
            }
            string failure = null;
            IShaderProgram final = null, incoming = null;
            final = SurfaceApiProxy.Make<IShaderProgram>((method, args) => {
                switch (method.Name) {
                    case "get_Disposed": case "get_LoadError": return false;
                    case "HasUniform": return GL.GetUniformLocation(program, (string)args[0]) >= 0;
                    case "Use": shaders.Active = final; GL.UseProgram(program); break;
                    case "Stop": shaders.Active = null; GL.UseProgram(0); break;
                    case "Uniform":
                        int location = GL.GetUniformLocation(program, (string)args[0]);
                        if (args[1] is int value) GL.Uniform1(location, value); else GL.Uniform1(location, (float)args[1]);
                        break;
                    case "BindTexture2D":
                        GL.Uniform1(GL.GetUniformLocation(program, (string)args[0]), (int)args[2]);
                        GL.ActiveTexture(TextureUnit.Texture0 + (int)args[2]); GL.BindTexture(TextureTarget.Texture2D, (int)args[1]);
                        break;
                    default: throw new NotSupportedException(method.Name);
                }
                if (failure == method.Name) throw new InvalidOperationException("Injected final " + failure);
                return null;
            });
            incoming = SurfaceApiProxy.Make<IShaderProgram>((method, _) => {
                if (method.Name == "Use") { shaders.Active = incoming; GL.UseProgram(incomingProgram); return null; }
                if (method.Name == "Stop") { shaders.Active = null; GL.UseProgram(0); return null; }
                throw new NotSupportedException(method.Name);
            });
            var render = SurfaceApiProxy.Make<IRenderAPI>((method, _) => method.Name switch {
                "GetEngineShader" => final, "get_CurrentActiveShader" => shaders.Active, _ => throw new NotSupportedException(method.Name)
            });
            var logger = SurfaceApiProxy.Make<ILogger>((_, _) => null);
            var api = SurfaceApiProxy.Make<ICoreClientAPI>((method, _) => method.Name switch {
                "get_Render" => render, "get_Logger" => logger, _ => throw new NotSupportedException(method.Name)
            });
            // Isolate the binding lifecycle from hook installation on the game.
            var hook = (HdrFinalHook)RuntimeHelpers.GetUninitializedObject(typeof(HdrFinalHook));
            Set(hook, "_api", api); Set(hook, "_processor", owner); Set(hook, "_config", (Func<AgxConfig>)(() => config));
            currentField.SetValue(null, hook);
            config.BloomEnabled = true; owner.Render(config, .1f);
            if (!owner.Ready) throw new Exception("HDR final fixture owner not ready");
            Seed(true);
            typeof(HdrFinalHook).GetMethod("BindFinalInputs", Private).Invoke(hook, null);
            if (!ReferenceEquals(shaders.Active, incoming) || GL.GetInteger(GetPName.CurrentProgram) != 0)
                throw new Exception("Final inputs changed incoming shader state");
            for (int i = 0; i < 2; i++) {
                GL.ActiveTexture(TextureUnit.Texture8 + i);
                if (GL.GetInteger(GetPName.TextureBinding2D) != (i == 0 ? owner.BloomTexture : owner.ExposureTexture) || GL.GetInteger(GetPName.SamplerBinding) != 0)
                    throw new Exception("Final texture released before native draw");
            }
            Postfix(); AssertRestored(true);
            foreach (bool managed in new[] { false, true })
            foreach (string call in new[] { "Use", "Uniform", "BindTexture2D", "Stop" }) {
                Seed(managed); failure = call;
                typeof(HdrFinalHook).GetMethod("FinalPrefix", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
                AssertRestored(managed);
            }
            failure = null;
            if (GL.GetError() != ErrorCode.NoError) throw new Exception("HDR final input GL error");
            Console.WriteLine("PASS final HDR lifecycle: textures 8/9 retained through native draw; partial Use/Uniform/Bind/Stop failures restore textures, samplers, active unit and managed/raw shader");

            void Seed(bool managed) {
                for (int i = 0; i < 2; i++) {
                    GL.ActiveTexture(TextureUnit.Texture8 + i); GL.BindTexture(TextureTarget.Texture2D, textures[i]); GL.BindSampler(8 + i, samplers[i]);
                }
                GL.ActiveTexture(TextureUnit.Texture14); shaders.Active = managed ? incoming : null; GL.UseProgram(0);
            }
            void AssertRestored(bool managed) {
                if (!ReferenceEquals(shaders.Active, managed ? incoming : null) || GL.GetInteger(GetPName.CurrentProgram) != 0 || GL.GetInteger(GetPName.ActiveTexture) != (int)TextureUnit.Texture14)
                    throw new Exception("Final prefix leaked shader or active texture after " + failure);
                for (int i = 0; i < 2; i++) {
                    GL.ActiveTexture(TextureUnit.Texture8 + i);
                    if (GL.GetInteger(GetPName.TextureBinding2D) != textures[i] || GL.GetInteger(GetPName.SamplerBinding) != samplers[i])
                        throw new Exception("Final prefix leaked texture/sampler after " + failure);
                }
                GL.ActiveTexture(TextureUnit.Texture14);
            }
        }
        finally {
            Postfix(); currentField.SetValue(null, previousHook);
            for (int i = 0; i < 2; i++) {
                GL.ActiveTexture(TextureUnit.Texture8 + i); GL.BindTexture(TextureTarget.Texture2D, oldTextures[i]); GL.BindSampler(8 + i, oldSamplers[i]);
                GL.DeleteTexture(textures[i]); GL.DeleteSampler(samplers[i]);
            }
            GL.ActiveTexture((TextureUnit)activeUnit); GL.UseProgram(0); shaders.Active = null;
            GL.DeleteProgram(program); GL.DeleteProgram(incomingProgram);
        }
    }
    private static void Postfix() => typeof(HdrFinalHook).GetMethod("FinalPostfix", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
    private static void Set(object target, string field, object value) => target.GetType().GetField(field, Private).SetValue(target, value);
}
