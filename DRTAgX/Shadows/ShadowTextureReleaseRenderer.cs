using System;
using Vintagestory.API.Client;

namespace DRTAgX;

// Runs after Sheyder's deferred relight so sampler unit 15 remains private to one pass.
internal sealed class ShadowTextureReleaseRenderer(MovingLightShadowRenderer maps,
    StaticLightTileBindings staticTiles) : IRenderer
{
    public double RenderOrder => 1.01;
    public int RenderRange => 0;
    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        maps.ReleaseTextureBinding();
        staticTiles.Release();
    }
    public void Dispose() { }
}
