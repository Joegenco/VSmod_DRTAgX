using System;
using System.Linq.Expressions;
using HarmonyLib;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using AssetLocation = Vintagestory.API.Common.AssetLocation;
using ProviderShaderType = OpenTK.Graphics.OpenGL.ShaderType;

// Domain folders share the root namespace; Harmony binds these parameter names.
// ReSharper disable CheckNamespace, InconsistentNaming
namespace DRTAgX;

/// <summary>Optional GhostChunks raw-GL shader bridge; native draw and cache ownership are retained.</summary>
internal sealed class GhostChunksCompatibility : IDisposable
{
    private const string PatchId = "drtagx.atmosphere.ghostchunks";
    private static GhostChunksCompatibility? _current;
    private readonly ICoreClientAPI _api;
    private Harmony? _harmony;
    private readonly Func<Vec3d, double>? _range;
    private readonly string _original = string.Empty, _patched = string.Empty;
    private readonly Action? _compile, _delete;
    private readonly Func<int>? _getProgram;
    private int _program;
    private bool _bound, _warned, _candidate, _retry;

    internal GhostChunksCompatibility(ICoreClientAPI api)
    {
        _api = api;
        if (!api.ModLoader.IsModEnabled("ghostchunks")) return;
        try
        {
            // Only the installed optional mod's fields/methods are reflected.
            // It starts on PlayerJoin, after this client-start installation.
            object? system = api.ModLoader.GetModSystem("GhostChunks.GhostChunksMod");
            Type? type = system?.GetType().Assembly.GetType("GhostChunks.GhostChunkRenderer");
            if (system == null || type == null) throw new MissingMemberException("GhostChunks renderer");
            // GhostChunks uses OpenTK's legacy OpenGL namespace. Its ShaderType
            // is a distinct CLR enum from OpenGL4.ShaderType even though both
            // contain the same GL values; method lookup and Harmony must match it.
            var stage = AccessTools.Method(type, "CompileStage", new[] { typeof(ProviderShaderType), typeof(string) });
            var compile = AccessTools.Method(type, "CompileShader", Type.EmptyTypes);
            var begin = AccessTools.Method(type, "BeginDraw");
            var end = AccessTools.Method(type, "EndDraw", Type.EmptyTypes);
            var frame = AccessTools.Method(type, "OnRenderFrame", new[] { typeof(float), typeof(EnumRenderStage) });
            var delete = AccessTools.Method(type, "DeleteShader", Type.EmptyTypes);
            if (stage?.ReturnType != typeof(int) || compile == null || begin == null || end == null || frame == null ||
                delete == null || AccessTools.Field(type, "glProgram")?.FieldType != typeof(int))
                throw new MissingMemberException("GhostChunks shader lifecycle");
            object? renderer = AccessTools.Field(system.GetType(), "Renderer")?.GetValue(system);
            if (renderer == null || renderer.GetType() != type)
                throw new MissingMemberException("GhostChunks renderer instance");
            _compile = compile.CreateDelegate<Action>(renderer);
            _delete = delete.CreateDelegate<Action>(renderer);
            _getProgram = Expression.Lambda<Func<int>>(Expression.Field(Expression.Constant(renderer), "glProgram"))
                .Compile();
            string vertex = AccessTools.Field(type, "VSH")?.GetRawConstantValue() as string ?? string.Empty;
            _original = AccessTools.Field(type, "FSH")?.GetRawConstantValue() as string ?? string.Empty;

            string Read(string path) => api.Assets.Get(new AssetLocation("drtagx", "shaders/" + path)).ToText();
            // Raw GL does not expand the game's #include assets. Inline maintained
            // shared helpers once; dead lighting functions are removed by GLSL.
            string helper = Read("atmosphere/drtagx_atmosphere_sampling.fsh").Replace(
                                "#include drtagx_fog_transport.ash", Read("atmosphere/drtagx_fog_transport.ash"),
                                StringComparison.Ordinal) +
                            "\n" + Read("lighting/drtagx_light_balance.ash") +
                            "\n#define viewDistance drtAtmosphereView.x\n" + Read("lighting/drtagx_lod_lighting.fsh");
            if (!GhostChunksShaderPatch.TryPatch(_original, helper, out _patched))
                throw new NotSupportedException("unrecognized GhostChunks shader source");
            if (!GhostChunksShaderPatch.Validate(vertex, _patched, out string error))
                throw new InvalidOperationException("candidate shader rejected: " + error);
            _range = CreateRange(system);
            _harmony = new Harmony(PatchId);
            _current = this;
            _harmony.Patch(stage, prefix: Hook(nameof(BeforeStage)));
            _harmony.Patch(compile, postfix: Hook(nameof(AfterCompile)));
            _harmony.Patch(begin, postfix: Hook(nameof(AfterBegin)));
            _harmony.Patch(end, prefix: Hook(nameof(BeforeEnd)));
            // Restore only our added bindings even if a provider draw throws.
            _harmony.Patch(frame, finalizer: Hook(nameof(AfterFrame)));
            _harmony.Patch(delete, prefix: Hook(nameof(BeforeDelete)));
            api.Logger.Notification("[DRT AgX] GhostChunks shared lighting/fog compatibility ready.");
        }
        catch (Exception ex)
        {
            Dispose();
            Warn("Compatibility unavailable; original provider retained: " + ex.Message);
        }
    }

    private static HarmonyMethod Hook(string name) => new(typeof(GhostChunksCompatibility), name);

    private static void BeforeStage(ProviderShaderType __0, ref string __1)
    {
        var hook = _current;
        if (hook == null || hook._retry || __0 != ProviderShaderType.FragmentShader) return;
        // Another mod/version changing this source keeps its own complete program.
        hook._candidate = string.Equals(__1, hook._original, StringComparison.Ordinal);
        if (hook._candidate) __1 = hook._patched;
        else hook.Warn("Changed shader source; original provider retained.");
    }

    private static void AfterCompile(int ___glProgram)
    {
        var hook = _current;
        if (hook == null || hook._retry || !hook._candidate) return;
        if (___glProgram == 0)
        {
            // A later compiler transform can invalidate a preflighted program.
            // The provider has already deleted its failed handle; retry native once.
            hook._retry = true;
            try
            {
                hook._compile?.Invoke();
            }
            catch (Exception ex)
            {
                hook.Warn("Original shader retry failed: " + ex.Message);
            }
            finally
            {
                hook._retry = false;
            }

            hook.Warn("Adapted program rejected; retried original provider shader.");
            return;
        }

        GL.GetProgram(___glProgram, GetProgramParameterName.LinkStatus, out int linked);
        if (linked != 0 && GL.GetUniformBlockIndex(___glProgram, "DrtAtmosphere") >= 0)
        {
            // Ghost normally assigns these only while external shadows are on.
            // Reserve its verified units before atmosphere sampler caching so a
            // later ReLight toggle cannot collide with an already assigned LUT.
            GL.ProgramUniform1(___glProgram, GL.GetUniformLocation(___glProgram, "extShadow0"), 13);
            GL.ProgramUniform1(___glProgram, GL.GetUniformLocation(___glProgram, "extShadow1"), 14);
            hook._program = ___glProgram;
        }
    }

    private static void AfterBegin(int ___glProgram)
    {
        var hook = _current;
        if (hook == null || hook._program != ___glProgram || ___glProgram == 0) return;
        hook._bound = true;
        AtmosphereProgramBindings.BindExternal(___glProgram);
    }

    private static void BeforeEnd() => _current?.Restore();
    private static void AfterFrame() => _current?.Restore();

    private static void BeforeDelete(int ___glProgram)
    {
        var hook = _current;
        if (hook == null || hook._program != ___glProgram) return;
        hook.Restore();
        AtmosphereProgramBindings.ForgetExternal(___glProgram);
        hook._program = 0;
    }

    private void Restore()
    {
        if (!_bound) return;
        AtmosphereProgramBindings.RestoreExternal();
        _bound = false;
    }

    internal static float Endpoint
    {
        get
        {
            var hook = _current;
            var camera = hook?._api.World?.Player?.Entity?.CameraPos;
            if (hook == null || hook._program == 0 || camera == null) return 0f;
            double value = hook._range?.Invoke(camera) ?? 0.0;
            return double.IsFinite(value) ? (float)Math.Clamp(value, 0.0, float.MaxValue) : 0f;
        }
    }

    internal static Func<Vec3d, double> CreateRange(object system)
    {
        // Startup-only reflection compiles live null-checked reads. Ghost radius
        // is additional to the approved view; whole-world coverage may be larger.
        var root = Expression.Constant(system);
        var config = Expression.PropertyOrField(root, "Config");
        var world = Expression.PropertyOrField(root, "World");
        var camera = Expression.Parameter(typeof(Vec3d), "camera");
        var chunks = Expression.Call(root, "GetViewDistanceChunks", Type.EmptyTypes);
        var radius = Expression.PropertyOrField(config, "GhostRadiusChunks");
        var ghost = Expression.Multiply(Expression.Add(Expression.Convert(chunks, typeof(double)),
            Expression.Convert(radius, typeof(double))), Expression.Constant(32.0));
        var worldReady = Expression.AndAlso(Expression.PropertyOrField(config, "ShowWholeWorld"),
            Expression.AndAlso(Expression.NotEqual(world, Expression.Constant(null, world.Type)),
                Expression.PropertyOrField(world, "HasGrid")));
        var far = Expression.Condition(worldReady, Expression.Call(world, "FarDistance", Type.EmptyTypes, camera),
            Expression.Constant(0.0));
        var enabled = Expression.AndAlso(Expression.NotEqual(config, Expression.Constant(null, config.Type)),
            Expression.PropertyOrField(config, "Enabled"));
        var maximum =
            Expression.Call(typeof(Math).GetMethod(nameof(Math.Max), new[] { typeof(double), typeof(double) })!, ghost,
                far);
        return Expression
            .Lambda<Func<Vec3d, double>>(Expression.Condition(enabled, maximum, Expression.Constant(0.0)), camera)
            .Compile();
    }

    private void Warn(string message)
    {
        if (_warned) return;
        _warned = true;
        _api.Logger.Warning("[DRT AgX] GhostChunks: " + message);
    }

    public void Dispose()
    {
        Restore();
        bool owned = _program != 0 && _getProgram?.Invoke() == _program;
        if (_program != 0) AtmosphereProgramBindings.ForgetExternal(_program);
        _program = 0;
        _harmony?.UnpatchAll(PatchId);
        _harmony = null;
        if (_current == this) _current = null;
        if (!owned) return;
        try
        {
            // The shared UBO/LUT owner is disposed next. Restore only our live
            // program through the provider's own deletion/compile/setter lifecycle.
            // A later provider replacement belongs to that provider and is untouched.
            _delete?.Invoke();
            _compile?.Invoke();
        }
        catch (Exception ex)
        {
            Warn("Native shader restore failed: " + ex.Message);
        }
    }
}