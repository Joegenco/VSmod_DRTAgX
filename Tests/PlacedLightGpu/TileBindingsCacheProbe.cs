using System;
using System.Collections.Generic;
using System.Text;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using static StaticCacheProbeFixture;

// Real production publication/compute owner, without native relighting. Verify
// cache invalidation and shared GL state, including newly exposed receivers.
internal static class TileBindingsCacheProbe
{
    internal static void Run(string computePath)
    {
        using var saved = new ShadowGlState();
        using var fixture = new StaticCacheProbeFixture(computePath: computePath);
        fixture.RequireViewMatrix();
        int program = ProbeShader.Program((ShaderType.VertexShader, "#version 430 core\nvoid main(){gl_Position=vec4(0.0);}"),
            (ShaderType.FragmentShader, """
            #version 430 core
            uniform int drtStaticCount, drtStaticTileWidth;
            uniform float drtStaticBlend;
            uniform sampler2DArrayShadow drtStaticMaps;
            out vec4 color;
            void main(){color=vec4(drtStaticCount,drtStaticTileWidth,drtStaticBlend,
                texture(drtStaticMaps,vec4(.5,.5,0,.5)));}
            """));
        var shader = SurfaceApiProxy.Make<IShaderProgram>((method, _) => method.Name == "get_ProgramId"
            ? program : throw new NotSupportedException(method.Name));
        using var owner = new StaticLightTileBindings { LightRevision = 1 };
        var source = new StaticLightSources.Source(0, 0, -10, 0, 0, 20);
        var bounds = new PlacedLightView.Bounds(-1, 1, -1, 1, 0, 0, 0, 10);
        var lights = new List<StaticTerrainShadowMaps.ActiveLight> { new(source, 0, 1, 0) { Bounds = bounds } };
        int generic = GL.GetInteger(GetPName.ShaderStorageBufferBinding);
        GL.GetInteger(GetIndexedPName.ShaderStorageBufferBinding, 4, out int old4);
        GL.GetInteger(GetIndexedPName.ShaderStorageBufferBinding, 5, out int old5);
        GL.GetInteger(GetIndexedPName.ShaderStorageBufferBinding, 6, out int old6);
        int active = GL.GetInteger(GetPName.ActiveTexture), previousProgram = GL.GetInteger(GetPName.CurrentProgram);
        int sentinel = GL.GenBuffer(), sampler = GL.GenSampler(), sentinelTexture = GL.GenTexture();
        int rangeAlignment = GL.GetInteger(GetPName.ShaderStorageBufferOffsetAlignment);
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, sentinel);
        GL.BufferData(BufferTarget.ShaderStorageBuffer, rangeAlignment * 4 + 64, IntPtr.Zero, BufferUsageHint.StaticDraw);
        for (int binding = 4; binding <= 6; binding++)
            GL.BindBufferRange(BufferRangeTarget.ShaderStorageBuffer, binding, sentinel, (IntPtr)(rangeAlignment * (binding - 3)), (IntPtr)64);
        GL.ActiveTexture(TextureUnit.Texture14); GL.BindTexture(TextureTarget.Texture2DArray, sentinelTexture); GL.BindSampler(14, sampler);
        GL.ActiveTexture((TextureUnit)active);
        int depth = GL.GenTexture();
        var pixels = new float[32 * 32]; Array.Fill(pixels, 1f);
        GL.BindTexture(TextureTarget.Texture2D, depth);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.DepthComponent24, 32, 32, 0, PixelFormat.DepthComponent, PixelType.Float, pixels);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        try
        {
            void Prepare() => owner.Prepare(fixture.Api, shader, lights, fixture.Texture, true, 1f/60);
            void Restore()
            {
                owner.Release();
                for (int binding = 4; binding <= 6; binding++) {
                    GL.GetInteger(GetIndexedPName.ShaderStorageBufferBinding, binding, out int buffer);
                    GL.GetInteger64(GetIndexedPName.ShaderStorageBufferStart, binding, out long offset);
                    GL.GetInteger64(GetIndexedPName.ShaderStorageBufferSize, binding, out long size);
                    if (buffer != sentinel || offset != rangeAlignment * (binding - 3) || size != 64)
                        throw new Exception("Cached binder leaked indexed range " + binding);
                }
                if (GL.GetInteger(GetPName.CurrentProgram) != previousProgram ||
                    GL.GetInteger(GetPName.ShaderStorageBufferBinding) != sentinel || GL.GetInteger(GetPName.ActiveTexture) != active)
                    throw new Exception("Cached binder leaked program/generic buffer/active unit");
                GL.ActiveTexture(TextureUnit.Texture14);
                if (GL.GetInteger(GetPName.TextureBinding2DArray) != sentinelTexture || GL.GetInteger(GetPName.SamplerBinding) != sampler)
                    throw new Exception("Cached binder leaked placed texture/sampler");
                GL.ActiveTexture((TextureUnit)active);
            }
            Prepare();
            Check(owner.UploadedRecordsThisFrame && owner.GeneratedMasksThisFrame && !owner.DepthCulling,
                "first placed publication uploads records and generates coarse masks");
            Check(ReadMasks(fixture.Width, fixture.Height)[0] == 1, "first coarse mask contains its published source");
            Restore(); lights[0] = lights[0] with { Bounds = new PlacedLightView.Bounds(-1.05f,-.97f,-1,1,1,0,0,10) }; owner.LightRevision++;
            Prepare();
            Check(ReadMasks(fixture.Width, fixture.Height)[0] == 1,
                "fringe source with conservative screen overlap retains its border tile");
            Restore(); lights[0] = lights[0] with { Bounds = bounds }; owner.LightRevision++;
            Prepare();
            Restore(); Prepare();
            Check(!owner.UploadedRecordsThisFrame && !owner.GeneratedMasksThisFrame,
                "unchanged sparse view reuses both GPU source records and coarse masks");
            // Exercise the same quality gate used by the native render callback.
            // A dormant mode must clear relighting without destroying warm storage.
            for (int toggle = 0; toggle < 12; ++toggle)
            {
                Restore();
                owner.Prepare(fixture.Api, shader, lights, fixture.Texture,
                    new FrameQuality(true).PlacedLights(true), 1f/60);
                GL.GetUniform(program, GL.GetUniformLocation(program, "drtStaticCount"), out int count);
                GL.GetUniform(program, GL.GetUniformLocation(program, "drtStaticBlend"), out float blend);
                Check(count == 0 && blend == 0 && owner.PublishedCount == 0 && !owner.IsBound &&
                    !owner.UploadedRecordsThisFrame && !owner.GeneratedMasksThisFrame,
                    "Performance clears deferred PLS and performs no upload/dispatch");
                Restore(); Prepare();
                Check(owner.PublishedCount == 1 && owner.IsBound &&
                    !owner.UploadedRecordsThisFrame && !owner.GeneratedMasksThisFrame,
                    "Normal restores unchanged PLS records/masks without rebuilding");
            }
            Restore(); lights[0] = lights[0] with { Source = source with { Hue = 12 }, Fade = .7f }; owner.LightRevision++;
            Prepare();
            Check(owner.UploadedRecordsThisFrame && !owner.GeneratedMasksThisFrame && ReadRecords(1)[8] == .7f,
                "colour/fade revision updates records while equal rectangles reuse masks");
            Restore(); fixture.View[12] = -.25; Prepare();
            Check(owner.UploadedRecordsThisFrame && Math.Abs(ReadRecords(1)[0] - .25f) < 1e-6,
                "in-place native camera mutation refreshes eye-space source data");
            Restore(); fixture.Origin[0] = .95f; Prepare();
            Check(owner.UploadedRecordsThisFrame, "in-place render-origin matrix mutation refreshes shared source transform");
            Restore(); fixture.Width = 17; fixture.Height = 19; Prepare();
            Check(owner.UploadedRecordsThisFrame && owner.GeneratedMasksThisFrame && ReadMasks(17, 19)[15] == 1,
                "viewport resize invalidates mask storage and complete tile coverage");
            Restore(); fixture.Width = fixture.Height = 32;
            while (lights.Count < 16) lights.Add(new(source with { X = lights.Count % 4 }, lights.Count, 1, 0) { Bounds = bounds });
            owner.LightRevision++;
            fixture.Buffers.Add(new FrameBufferRef { Width = 32, Height = 32, DepthTextureId = depth });
            Prepare();
            Check(owner.DepthCulling && owner.GeneratedMasksThisFrame && ReadMasks(32, 32)[0] == 0,
                "dense view depth masks reject clear receiver tiles");
            Restore(); Array.Fill(pixels, .5f);
            GL.BindTexture(TextureTarget.Texture2D, depth);
            GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, 32, 32, PixelFormat.DepthComponent, PixelType.Float, pixels);
            Prepare();
            Check(owner.DepthCulling && !owner.UploadedRecordsThisFrame && owner.GeneratedMasksThisFrame &&
                ReadMasks(32, 32)[0] == 65535, "changed receiver depth is never hidden by cached light/camera inputs");
            Restore(); fixture.Projection[14] = 0; Prepare();
            Check(!owner.DepthCulling && owner.GeneratedMasksThisFrame && ReadMasks(32, 32)[0] == 65535,
                "singular projection replaces preceding depth masks with conservative coarse coverage");
            Restore(); Prepare();
            Check(!owner.UploadedRecordsThisFrame && !owner.GeneratedMasksThisFrame,
                "unchanged singular projection retains coarse fallback without repeated dispatch");
            Restore(); fixture.Projection[14] = -1; owner.Reload(); Prepare();
            Check(owner.UploadedRecordsThisFrame && owner.GeneratedMasksThisFrame && owner.DepthCulling,
                "shader reload rebuilds cached publication and compute state");
            Restore(); owner.Dispose(); Prepare();
            Check(owner.UploadedRecordsThisFrame && owner.GeneratedMasksThisFrame,
                "released buffer storage cannot inherit a prior cache-valid flag");
            Restore();
            lights.RemoveRange(1, lights.Count - 1); owner.LightRevision++; Prepare();
            uint[] sparse = ReadMasks(32, 32);
            Check(!owner.DepthCulling && owner.GeneratedMasksThisFrame && sparse[0] == 1 && sparse[4] == 0,
                "shrinking published count replaces dense masks and clears unused words");
            Restore(); owner.Prepare(fixture.Api, shader, lights, fixture.Texture, false, 1f/60);
            GL.GetUniform(program, GL.GetUniformLocation(program, "drtStaticCount"), out int disabledCount);
            Check(!owner.IsBound && disabledCount == 0 && owner.PublishedCount == 0,
                "disabled placed shadows cannot expose cached records or masks");
            Check(GL.GetError() == ErrorCode.NoError, "cached publication and lifecycle restore GL state without errors");
            FirstCompileFailure(shader, computePath, lights[0]);
            ScaledReceiverMasks(shader, computePath);
        }
        finally
        {
            owner.Release();
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 4, old4);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 5, old5);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 6, old6);
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, generic);
            GL.ActiveTexture(TextureUnit.Texture14); GL.BindSampler(14, 0); GL.BindTexture(TextureTarget.Texture2DArray, 0);
            GL.ActiveTexture((TextureUnit)active);
            GL.DeleteBuffer(sentinel); GL.DeleteSampler(sampler); GL.DeleteTexture(sentinelTexture); GL.DeleteTexture(depth); GL.DeleteProgram(program);
        }
    }

    private static void FirstCompileFailure(IShaderProgram shader, string computePath, StaticTerrainShadowMaps.ActiveLight light)
    {
        using var fixture = new StaticCacheProbeFixture();
        fixture.RequireViewMatrix();
        using var owner = new StaticLightTileBindings { LightRevision = 1 };
        var assetsApi = ProbeAssets.Api(Encoding.UTF8.GetBytes("invalid compute shader"));
        var api = SurfaceApiProxy.Make<ICoreClientAPI>((method, _) => method.Name switch {
            "get_World" => fixture.Api.World, "get_Render" => fixture.Api.Render,
            "get_Assets" => assetsApi.Assets, "get_Logger" => assetsApi.Logger,
            _ => throw new NotSupportedException(method.Name)
        });
        bool failed = false;
        try { owner.Prepare(api, shader, new[] { light }, fixture.Texture, true, 1f/60); }
        catch (InvalidOperationException) { failed = true; }
        GL.GetUniform(shader.ProgramId, GL.GetUniformLocation(shader.ProgramId, "drtStaticCount"), out int count);
        Check(failed && !owner.IsBound && count == 0 && owner.PublishedCount == 0,
            "first coarse compile failure preserves vanilla lighting in that same frame");
        owner.Reload();
        using var valid = new StaticCacheProbeFixture(computePath: computePath);
        valid.RequireViewMatrix();
        owner.Prepare(valid.Api, shader, new[] { light }, valid.Texture, true, 1f/60);
        Check(owner.IsBound && owner.GeneratedMasksThisFrame, "compute failure recovers after reload with valid assets");
        owner.Release();
    }

    private static void ScaledReceiverMasks(IShaderProgram shader, string computePath)
    {
        using var fixture = new StaticCacheProbeFixture(computePath: computePath);
        fixture.RequireViewMatrix();
        fixture.Width = 256; fixture.Height = 144;
        var primary = new FrameBufferRef();
        fixture.Buffers.Add(primary);
        using var owner = new StaticLightTileBindings { LightRevision = 1 };
        // All four emitters stay beyond a viewport edge while their spheres
        // reach real receivers. Movement must preserve those receiver bits.
        var sources = new[] {
            new StaticLightSources.Source(105, 0, -100, 0, 0, 7),
            new StaticLightSources.Source(-106, 0, -100, 0, 0, 7),
            new StaticLightSources.Source(0, 105, -100, 0, 0, 7),
            new StaticLightSources.Source(0, -106, -100, 0, 0, 7)
        };
        var lights = new List<StaticTerrainShadowMaps.ActiveLight>();
        foreach (float scale in new[] { .75f, 1f, 1.5f, 2f })
        {
            primary.Width = (int)(fixture.Width * scale);
            primary.Height = (int)(fixture.Height * scale);
            int reached = 0;
            foreach (int movement in new[] { 0, -4, -2, -1, 1, 2, 4, 0 })
            {
                fixture.View[12] = -movement; fixture.View[13] = movement;
                lights.Clear();
                for (int i = 0; i < sources.Length; i++)
                {
                    var bounds = PlacedLightView.Project(sources[i], fixture.View, fixture.Projection);
                    Check(bounds.Class == 0, $"scaled movement {scale}/{movement}: offscreen emitter sphere reaches viewport {i}");
                    lights.Add(new(sources[i], 0, 1, 0) { Bounds = bounds });
                }
                owner.LightRevision++;
                owner.Prepare(fixture.Api, shader, lights, fixture.Texture, true, 1f / 60);
                GL.GetUniform(shader.ProgramId, GL.GetUniformLocation(shader.ProgramId, "drtStaticTileWidth"), out int stride);
                float[] records = ReadRecords(sources.Length);
                GL.GetInteger(GetIndexedPName.ShaderStorageBufferBinding, 5, out int buffer);
                int generic = GL.GetInteger(GetPName.ShaderStorageBufferBinding);
                GL.BindBuffer(BufferTarget.ShaderStorageBuffer, buffer);
                GL.GetBufferParameter(BufferTarget.ShaderStorageBuffer, BufferParameterName.BufferSize, out int bytes);
                var masks = new uint[bytes / sizeof(uint)];
                GL.GetBufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, bytes, masks);
                GL.BindBuffer(BufferTarget.ShaderStorageBuffer, generic);
                // Independent visible plane oracle: use the render target's
                // pixel centres, not the owner's projected tile rectangles.
                for (int y = 0; y < primary.Height; y++)
                for (int x = 0; x < primary.Width; x++)
                {
                    float rx = ((x + .5f) / primary.Width * 2f - 1f) * 99.5f;
                    float ry = ((y + .5f) / primary.Height * 2f - 1f) * 99.5f;
                    int word = ((y / 16) * stride + x / 16) * 5;
                    for (int i = 0; i < sources.Length; i++)
                    {
                        float dx = records[i * 16] - rx, dy = records[i * 16 + 1] - ry;
                        if (dx * dx + dy * dy >= records[i * 16 + 3] * records[i * 16 + 3]) continue;
                        reached++;
                        if (word >= masks.Length || (masks[word] & (1u << i)) == 0)
                            throw new Exception($"Scaled placed mask lost visible receiver: scale={scale}, movement={movement}, pixel={x},{y}, source={i}, stride={stride}");
                    }
                }
                Check(stride == (primary.Width + 15) / 16 &&
                    bytes == stride * ((primary.Height + 15) / 16) * 5 * sizeof(uint),
                    $"scaled movement {scale}/{movement}: tile stride/storage match render target");
                owner.Release();
                owner.Prepare(fixture.Api, shader, lights, fixture.Texture, true, 1f / 60);
                Check(!owner.UploadedRecordsThisFrame && !owner.GeneratedMasksThisFrame,
                    $"scaled movement {scale}/{movement}: settled inputs reuse records/masks");
                owner.Release();
            }
            Check(reached > 0, $"scaled viewport {scale}: {reached} visible offscreen-light receiver pairs retained");
        }
        // A window resize without a render-target resize changes no receiver
        // coordinates. Conversely, changing only the target must invalidate.
        fixture.Width += 11; fixture.Height += 7;
        owner.Prepare(fixture.Api, shader, lights, fixture.Texture, true, 1f / 60);
        Check(!owner.UploadedRecordsThisFrame && !owner.GeneratedMasksThisFrame,
            "window-only resize reuses placed masks for an unchanged render target");
        owner.Release(); primary.Width++; primary.Height++;
        owner.Prepare(fixture.Api, shader, lights, fixture.Texture, true, 1f / 60);
        Check(owner.UploadedRecordsThisFrame && owner.GeneratedMasksThisFrame,
            "render-target-only resize invalidates placed mask storage and records");
        owner.Release();
        // Dense depth masks must use the same scaled pixels as relighting;
        // changing receiver depth alone still needs a current-frame dispatch.
        int depth = GL.GenTexture();
        try
        {
            var pixels = new float[primary.Width * primary.Height]; Array.Fill(pixels, 1f);
            GL.BindTexture(TextureTarget.Texture2D, depth);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.DepthComponent24,
                primary.Width, primary.Height, 0, PixelFormat.DepthComponent, PixelType.Float, pixels);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            primary.DepthTextureId = depth;
            lights.Clear();
            for (int i = 0; i < 16; i++) lights.Add(new(new(0, 0, -10, 0, 0, 20), 0, 1, 0) {
                Bounds = new(-1, 1, -1, 1, 0, 0, 0, 10)
            });
            owner.LightRevision++;
            owner.Prepare(fixture.Api, shader, lights, fixture.Texture, true, 1f / 60);
            Check(owner.DepthCulling && Array.TrueForAll(ReadMasks(primary.Width, primary.Height), mask => mask == 0),
                "scaled dense viewport uses current primary depth and clears empty receiver masks");
            owner.Release(); Array.Fill(pixels, .5f);
            GL.BindTexture(TextureTarget.Texture2D, depth);
            GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, primary.Width, primary.Height,
                PixelFormat.DepthComponent, PixelType.Float, pixels);
            owner.Prepare(fixture.Api, shader, lights, fixture.Texture, true, 1f / 60);
            uint[] masks = ReadMasks(primary.Width, primary.Height);
            for (int i = 0; i < masks.Length; i++)
                if (masks[i] != (i % 5 == 0 ? 65535u : 0u))
                    throw new Exception("Scaled depth masks lost newly exposed receivers");
            Check(owner.DepthCulling && !owner.UploadedRecordsThisFrame && owner.GeneratedMasksThisFrame,
                "scaled depth changes preserve all overlapping lights without reuploading source records");
        }
        finally { owner.Release(); GL.DeleteTexture(depth); }
    }

    private static uint[] ReadMasks(int width, int height)
    {
        GL.GetInteger(GetIndexedPName.ShaderStorageBufferBinding, 5, out int buffer);
        int generic = GL.GetInteger(GetPName.ShaderStorageBufferBinding);
        var data = new uint[((width + 15) / 16) * ((height + 15) / 16) * 5];
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, buffer);
        GL.GetBufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, data.Length * sizeof(uint), data);
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, generic); return data;
    }
    private static float[] ReadRecords(int count)
    {
        GL.GetInteger(GetIndexedPName.ShaderStorageBufferBinding, 4, out int buffer);
        int generic = GL.GetInteger(GetPName.ShaderStorageBufferBinding);
        var data = new float[count * 16]; GL.BindBuffer(BufferTarget.ShaderStorageBuffer, buffer);
        GL.GetBufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, data.Length * sizeof(float), data);
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, generic); return data;
    }
}
