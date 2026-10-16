using System;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace DRTAgX
{
    internal static class FogAndLightAssetBridge
    {
        // Asset data is shared with the shader registry before shader compilation.
        internal static void Apply(ICoreClientAPI api)
        {
            Mirror(api, "fogandlight.vsh");
            Mirror(api, "fogandlight.fsh");
            Mirror(api, "underwatereffects.fsh");
            Mirror(api, "skycolor.fsh");
            // The registry expands includes before GLSL conditionals. Remove
            // the optional file reference before release programs are loaded.
            IAsset? deferred = api.Assets.TryGet(new AssetLocation("sheydermod", "shaders/deferredlighting.fsh"));
            if (deferred != null)
                deferred.Data = Encoding.UTF8.GetBytes(ReleaseDeferredSource(deferred.ToText()));
            InstallStandardReceiver(api);
            LodFogAssetPatch.Apply(api);
        }

        // Shared by release asset-layout verification; no source file or live
        // asset on disk is changed by this in-memory compatibility bridge.
        internal static string ReleaseDeferredSource(string source) => source.Replace(
            "#include drtagx_deferred_debug.fsh", string.Empty, StringComparison.Ordinal);

        internal static void InstallLoadedModelReceiver(ICoreClientAPI api, string passName, IShader shader)
        {
            const string marker = "// DRT_MODEL_RECEIVER_INSERT";
            if (!passName.Equals("standard", StringComparison.OrdinalIgnoreCase) &&
                !passName.Equals("entityanimated", StringComparison.OrdinalIgnoreCase) &&
                !passName.Equals("entityanimated_oit", StringComparison.OrdinalIgnoreCase)) return;
            var source = shader.Code;
            if (string.IsNullOrEmpty(source) || !source.Contains(marker, StringComparison.Ordinal) ||
                !source.Contains("#define DRT_EXPLICIT_SURFACE_LIGHTING", StringComparison.Ordinal) ||
                source.Contains("#define DRT_PLACED_RECEIVER_FALLBACK", StringComparison.Ordinal)) return;

            // Native shader reloads can replace asset data before compiling and
            // notify ReloadShader subscribers afterwards. Repair the actual
            // loaded stage at this boundary, rather than only its cached asset.
            // These two owned dependencies are leaves; all other lighting math
            // was already expanded by the native loader. No native include
            // resolver or global deduplication policy is replaced here.
            var helper = api.Assets.Get(new AssetLocation("drtagx",
                "shaders/lighting/drtagx_placed_receiver_fallback.ash")).ToText();
            var cube = api.Assets.Get(new AssetLocation("drtagx",
                "shaders/deferred/drtagx_deferred_cube.fsh")).ToText();
            var shadows = api.Assets.Get(new AssetLocation("drtagx",
                "shaders/deferred/drtagx_deferred_staticshadows.fsh")).ToText();
            helper = helper.Replace("#include drtagx_deferred_cube.fsh", cube, StringComparison.Ordinal)
                .Replace("#include drtagx_deferred_staticshadows.fsh", shadows, StringComparison.Ordinal);
            shader.Code = source.Replace(marker, helper + "\n" + marker, StringComparison.Ordinal);
        }

        private static void InstallStandardReceiver(ICoreClientAPI api)
        {
            InstallModelReceiver(api, "standard.fsh");
            InstallModelReceiver(api, "entityanimated.fsh");
        }

        private static void InstallModelReceiver(ICoreClientAPI api, string filename)
        {
            var standard = api.Assets.TryGet(new AssetLocation("game", "shaders/" + filename));
            if (standard == null) return;
            const string include = "#include drtagx_placed_receiver_fallback.ash";
            var source = standard.ToText();
            if (source.Contains(include, StringComparison.Ordinal)) return;
            // FpHands compiles native model shaders before custom includes are indexed.
            // Add the helper only after asset registration, on each owner alone:
            // the native resolver deduplicates includes before GLSL conditionals.
            const string anchor = "#include fogandlight.fsh";
            if (!source.Contains(anchor, StringComparison.Ordinal)) return;
            standard.Data = Encoding.UTF8.GetBytes(source.Replace(anchor, anchor + "\n" + include,
                StringComparison.Ordinal));
        }

        private static void Mirror(ICoreClientAPI api, string filename)
        {
            // The game-domain shader is the maintained overwrite; mirror it
            // into alternate include locations before shader compilation.
            IAsset? source = api.Assets.TryGet(new AssetLocation("game", "shaders/" + filename));
            if (source == null)
            {
                api.Logger.Error("[DRT AgX] Dynamic light include source asset was not found.");
                return;
            }

            byte[] shaderData = source.Data;
            IAsset? nativeInclude = api.Assets.TryGet(new AssetLocation("game", "shaderincludes/" + filename));
            if (nativeInclude != null) nativeInclude.Data = shaderData;

            IAsset? sheyderInclude = api.Assets.TryGet(new AssetLocation("sheydermod", "shaderincludes/" + filename));
            if (sheyderInclude != null) sheyderInclude.Data = shaderData;

            IAsset? sheyderShader = api.Assets.TryGet(new AssetLocation("sheydermod", "shaders/" + filename));
            if (sheyderShader != null) sheyderShader.Data = shaderData;
            // Terrain shaders and deferredlighting are loaded directly from their
            // canonical asset paths, including when the shader registry reloads.
            // DRTAgX deferred modules use unique drtagx_deferred_* filenames in
            // assets/drtagx/shaders/deferred; the registry indexes them normally.
            api.Logger.Notification(
                $"[DRT AgX] Shared {filename} installed: sheyder={sheyderInclude != null}/{sheyderShader != null}.");
        }
    }
}