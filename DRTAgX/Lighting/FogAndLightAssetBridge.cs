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

        private static void InstallStandardReceiver(ICoreClientAPI api)
        {
            var standard = api.Assets.TryGet(new AssetLocation("game", "shaders/standard.fsh"));
            if (standard == null) return;
            const string include = "#include drtagx_placed_receiver_fallback.ash";
            var source = standard.ToText();
            if (source.Contains(include, StringComparison.Ordinal)) return;
            // FpHands compiles Standard before custom includes are indexed.
            // Add the helper only after asset registration, on this owner alone:
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