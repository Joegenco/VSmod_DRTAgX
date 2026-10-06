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
            LodFogAssetPatch.Apply(api);
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
            api.Logger.Notification($"[DRT AgX] Shared {filename} installed: sheyder={sheyderInclude != null}/{sheyderShader != null}.");
        }
    }
}
