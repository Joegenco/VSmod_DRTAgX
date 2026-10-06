using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

/// <summary>
/// Test-only native shader adapters. The registered fixture objects use real
/// owned HDR GLSL programs; all other native objects retain their normal methods.
/// No native method body is inspected and no shim enters the release assembly.
/// </summary>
internal sealed class HdrShaderFixture : IDisposable
{
    private sealed record Entry(string Name, int Program);
    private readonly Dictionary<object, Entry> _programs = new(ReferenceEqualityComparer.Instance);
    private readonly Harmony _harmony = new("drtagx.tests.hdr.shaders");
    private readonly string _directory;
    private static HdrShaderFixture _current;
    internal IShaderProgram Active;
    internal string Failure;
    internal IShaderAPI Api { get; }

    internal HdrShaderFixture(string directory)
    {
        _directory = directory;
        _current = this;
        Patch("Use", Type.EmptyTypes);
        Patch("Stop", Type.EmptyTypes);
        Patch("Dispose", Type.EmptyTypes);
        Patch("BindTexture2D", [typeof(string), typeof(int), typeof(int)]);
        Patch("Uniform", [typeof(string), typeof(int)]);
        Patch("Uniform", [typeof(string), typeof(float)]);
        Patch("Uniform", [typeof(string), typeof(float), typeof(float)]);
        var compile = AccessTools.Method(typeof(ShaderProgram), "Compile", Type.EmptyTypes)
            ?? throw new MissingMethodException("ShaderProgram.Compile");
        compile = AccessTools.DeclaredMethod(compile.DeclaringType, "Compile", Type.EmptyTypes);
        _harmony.Patch(compile, prefix: new HarmonyMethod(typeof(HdrShaderFixture), nameof(CompilePrefix)));
        Api = SurfaceApiProxy.Make<IShaderAPI>((method, args) => {
            if (method.Name == "NewShaderProgram") {
                var shader = (ShaderProgram)RuntimeHelpers.GetUninitializedObject(typeof(ShaderProgram));
                _programs.Add(shader, new("pending", 0));
                return shader;
            }
            if (method.Name == "RegisterFileShaderProgram") {
                string name = (string)args[0];
                int program = ProbeShader.Program(
                    (ShaderType.VertexShader, File.ReadAllText(Path.Combine(_directory, name + ".vsh"))),
                    (ShaderType.FragmentShader, File.ReadAllText(Path.Combine(_directory, name + ".fsh"))));
                _programs[args[1]] = new(name, program);
                // Production owners cache locations by the native program ID.
                ((ShaderProgram)args[1]).ProgramId = program;
                return method.ReturnType == typeof(bool) ? true : method.ReturnType == typeof(int) ? program : null;
            }
            throw new NotSupportedException(method.Name);
        });
    }

    private void Patch(string name, Type[] arguments)
    {
        var method = AccessTools.Method(typeof(ShaderProgram), name, arguments)
            ?? throw new MissingMethodException("ShaderProgram." + name);
        // Harmony needs the declaring type's implemented metadata, rather than
        // an inherited MethodInfo reflected through the concrete shader type.
        method = AccessTools.DeclaredMethod(method.DeclaringType, name, arguments);
        _harmony.Patch(method, prefix: new HarmonyMethod(typeof(HdrShaderFixture), nameof(CallPrefix)));
    }

    private static bool CompilePrefix(object __instance, ref bool __result)
    {
        if (_current == null || !_current._programs.ContainsKey(__instance)) return true;
        __result = true; return false;
    }

    private static bool CallPrefix(object __instance, object[] __args, MethodBase __originalMethod)
    {
        var fixture = _current;
        if (fixture == null || !fixture._programs.TryGetValue(__instance, out var entry)) return true;
        string method = __originalMethod.Name;
        switch (method) {
            case "Use": fixture.Active = (IShaderProgram)__instance; GL.UseProgram(entry.Program); break;
            case "Stop": fixture.Active = null; GL.UseProgram(0); break;
            case "Dispose": GL.DeleteProgram(entry.Program); fixture._programs.Remove(__instance); break;
            case "BindTexture2D":
                GL.Uniform1(GL.GetUniformLocation(entry.Program, (string)__args[0]), (int)__args[2]);
                GL.ActiveTexture(TextureUnit.Texture0 + (int)__args[2]);
                GL.BindTexture(TextureTarget.Texture2D, (int)__args[1]);
                break;
            case "Uniform":
                int location = GL.GetUniformLocation(entry.Program, (string)__args[0]);
                if (__args.Length == 3) GL.Uniform2(location, (float)__args[1], (float)__args[2]);
                else if (__args[1] is int integer) GL.Uniform1(location, integer);
                else GL.Uniform1(location, (float)__args[1]);
                break;
            default: throw new NotSupportedException(method);
        }
        // Fail after the operation has changed tracking/GL, reproducing partial
        // native calls rather than an exception before any state was touched.
        if (fixture.Failure == entry.Name + ":" + method)
            throw new InvalidOperationException("Injected HDR " + fixture.Failure);
        return false;
    }

    public void Dispose()
    {
        _harmony.UnpatchAll(_harmony.Id);
        foreach (var entry in _programs.Values) if (entry.Program != 0) GL.DeleteProgram(entry.Program);
        _programs.Clear();
        _current = null;
    }
}
