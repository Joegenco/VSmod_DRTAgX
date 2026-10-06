using System;
using Vintagestory.API.Client;

namespace DRTAgX;

// Render-thread callback immediately after native point-light publication.
internal sealed class NativeLightMatrixCaptureRenderer(Action capture) : IRenderer
{
    public double RenderOrder => 0.11;
    public int RenderRange => 0;
    public void OnRenderFrame(float deltaTime, EnumRenderStage stage) => capture();
    public void Dispose() { }
}
