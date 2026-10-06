using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using DRTAgX;
using HarmonyLib;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

/// <summary>
/// The real frame owner uploads into a real UBO. Only scene capture and compute
/// consumers are controlled, isolating upload order from atmospheric visual math.
/// </summary>
internal static class AtmosphereUploadProbe
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static AtmosphereRenderer _owner;
    private static AtmosphereFogVolume _volume;
    private static AtmosphereAmbientResources _ambient;
    private static readonly List<string> Calls = new();
    private static bool _failVolume;

    internal static void Run()
    {
        ProbeAssets.Api([]);
        using var saved = new HdrPassState();
        var player = HdrFixturePlayer.Create((EntityPlayer)RuntimeHelpers.GetUninitializedObject(typeof(EntityPlayer)));
        var world = SurfaceApiProxy.Make<IClientWorldAccessor>((method, _) => method.Name == "get_Player" ? player : throw new NotSupportedException(method.Name));
        var events = SurfaceApiProxy.Make<IClientEventAPI>((_, _) => null);
        var loader = SurfaceApiProxy.Make<IModLoader>((method, _) => method.Name == "GetModSystem" ? null : throw new NotSupportedException(method.Name));
        int errors = 0;
        var logger = SurfaceApiProxy.Make<ILogger>((method, _) => { if (method.Name == "Error") errors++; return null; });
        var api = SurfaceApiProxy.Make<ICoreClientAPI>((method, _) => method.Name switch {
            "get_World" => world, "get_Event" => events, "get_ModLoader" => loader, "get_Logger" => logger,
            _ => throw new NotSupportedException(method.Name)
        });
        var harmony = new Harmony("drtagx.tests.atmosphere.upload");
        try {
            _owner = new AtmosphereRenderer(api);
            _volume = (AtmosphereFogVolume)Get(_owner, "_volume"); _ambient = (AtmosphereAmbientResources)Get(_owner, "_ambient");
            Set(_owner, "_world", world); Set(_owner, "_pending", false); Set(_owner, "_ambientAvailable", true);
            var sky = (AtmosphereSkyResources)Get(_owner, "_sky"); SetReady(sky, true);
            var bridge = (AtmosphereSheyderBridge)Get(_owner, "_sheyder");
            typeof(AtmosphereSheyderBridge).GetProperty("LegacyAvailable", Private).SetValue(bridge, true);
            harmony.Patch(AccessTools.Method(typeof(AtmosphereRenderer), "Capture"), prefix: new HarmonyMethod(typeof(AtmosphereUploadProbe), nameof(Capture)));
            harmony.Patch(AccessTools.Method(typeof(AtmosphereRenderer), "Upload"), postfix: new HarmonyMethod(typeof(AtmosphereUploadProbe), nameof(Uploaded)));
            harmony.Patch(AccessTools.Method(typeof(AtmosphereFogVolume), "Render"), prefix: new HarmonyMethod(typeof(AtmosphereUploadProbe), nameof(Volume)));
            harmony.Patch(AccessTools.Method(typeof(AtmosphereAmbientResources), "Render"), prefix: new HarmonyMethod(typeof(AtmosphereUploadProbe), nameof(Ambient)));
            foreach (bool fallback in new[] { false, true }) {
                Set(_owner, "_volumeAvailable", fallback); SetReady(_volume, false); Calls.Clear();
                _owner.OnRenderFrame(.1f, EnumRenderStage.Opaque);
                string expected = fallback ? "upload,volume,upload,ambient" : "upload,ambient";
                if (string.Join(',', Calls) != expected) throw new Exception("Atmosphere upload order: " + string.Join(',', Calls));
                float[] final = Read();
                int result = AtmosphereAmbientResources.ResultOffset;
                if (final[result] != .2f || final[result + 1] != .4f || final[result + 2] != .6f || final[result + 3] != 1 ||
                    final[25] != (fallback ? 1 : 0) || final[34] != (fallback ? 1 : 0))
                    throw new Exception("Atmosphere final upload overwrote ambient output or volume flags");
            }
            // A failing optional volume still receives its intermediate frame,
            // then publishes the analytical fallback before ambient compute.
            _failVolume = true; Set(_owner, "_volumeAvailable", true); Calls.Clear();
            _owner.OnRenderFrame(.1f, EnumRenderStage.Opaque);
            if (string.Join(',', Calls) != "upload,volume,upload,ambient" || Read()[34] != 0 || errors != 1)
                throw new Exception("Atmosphere volume failure lost fallback/upload ordering");
            _failVolume = false; Calls.Clear();
            _owner.OnRenderFrame(.1f, EnumRenderStage.AfterFinalComposition);
            if (string.Join(',', Calls) != "upload" || Read()[3] != 0) throw new Exception("UI stage retained world atmosphere");
            if (GL.GetError() != ErrorCode.NoError) throw new Exception("Atmosphere upload GL error");
            Console.WriteLine("PASS atmosphere frame uploads: healthy path uploads once; volume path twice; final upload precedes ambient output, volume failure and UI stage preserve fallback flags");
        }
        finally {
            harmony.UnpatchAll(harmony.Id); _owner?.Dispose();
            _owner = null; _volume = null; _ambient = null; Calls.Clear(); _failVolume = false;
        }
    }

    private static bool Capture(AtmosphereRenderer __instance)
    {
        if (!ReferenceEquals(__instance, _owner)) return true;
        float[] frame = (float[])Get(_owner, "_frame"); Array.Clear(frame); frame[3] = 1;
        return false;
    }
    private static void Uploaded(AtmosphereRenderer __instance) { if (ReferenceEquals(__instance, _owner)) Calls.Add("upload"); }
    private static bool Volume(AtmosphereFogVolume __instance)
    {
        if (!ReferenceEquals(__instance, _volume)) return true;
        Calls.Add("volume");
        float[] frame = Read();
        if (frame[3] != 1 || frame[24] != 1 || frame[25] != 0) throw new Exception("Volume received stale frame data");
        if (_failVolume) throw new InvalidOperationException("Injected optional volume failure");
        SetReady(_volume, true); return false;
    }
    private static bool Ambient(AtmosphereAmbientResources __instance)
    {
        if (!ReferenceEquals(__instance, _ambient)) return true;
        Calls.Add("ambient");
        float[] before = Read();
        if (before[AtmosphereAmbientResources.ResultOffset + 3] != 0) throw new Exception("Ambient received an uncleared result-valid flag");
        int previous = GL.GetInteger(GetPName.UniformBufferBinding);
        GL.BindBuffer(BufferTarget.UniformBuffer, (int)Get(_owner, "_buffer"));
        GL.BufferSubData(BufferTarget.UniformBuffer, (IntPtr)(AtmosphereAmbientResources.ResultOffset * sizeof(float)), 4 * sizeof(float), new[] { .2f, .4f, .6f, 1f });
        GL.BindBuffer(BufferTarget.UniformBuffer, previous); return false;
    }
    private static float[] Read()
    {
        int previous = GL.GetInteger(GetPName.UniformBufferBinding);
        GL.BindBuffer(BufferTarget.UniformBuffer, (int)Get(_owner, "_buffer"));
        float[] data = new float[AtmosphereRenderer.FrameFloatCount];
        GL.GetBufferSubData(BufferTarget.UniformBuffer, IntPtr.Zero, data.Length * sizeof(float), data);
        GL.BindBuffer(BufferTarget.UniformBuffer, previous); return data;
    }
    private static void SetReady(object target, bool value) => target.GetType().GetProperty("Ready", Private).SetValue(target, value);
    private static object Get(object target, string field) => target.GetType().GetField(field, Private).GetValue(target);
    private static void Set(object target, string field, object value) => target.GetType().GetField(field, Private).SetValue(target, value);
}
