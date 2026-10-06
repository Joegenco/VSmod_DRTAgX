using System;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace DRTAgX;

public partial class DrtagxModSystem
{
    private const int EntityPlacedLightLimit = 8;
    // The single cache replacement can publish two fading records for one rank.
    private const int EntityPlacedCandidateLimit = EntityPlacedLightLimit + 1;
    private readonly float[] _entityPlacedSources = new float[EntityPlacedCandidateLimit * 4];
    private readonly float[] _entityPlacedColors = new float[EntityPlacedCandidateLimit * 4];
    private static readonly EnumShaderProgram[] ForwardPlacedPrograms =
        [EnumShaderProgram.Entityanimated, EnumShaderProgram.Entityanimated_Oit,
         EnumShaderProgram.Standard, EnumShaderProgram.Particlesquad, EnumShaderProgram.Particlescube];
    private readonly int[] _entityPlacedPrograms = new int[5];
    private readonly int[] _entityPlacedCounts = new int[5];
    private readonly int[] _entityPlacedPositions = new int[5];
    private readonly int[] _entityPlacedColorLocations = new int[5];
    private readonly int[] _entityPlacedFadeLocations = new int[5];
    private readonly int[] _entityPlacedExtraFadeLocations = new int[5];
    private readonly int[] _entityPlacedOverflowFadeLocations = new int[5];
    private readonly int[] _entityPlacedPairLocations = new int[5];

    // Render-thread upload: nine nearby candidates feed at most eight light ranks.
    // A cache replacement's old/incoming records share one rank. Animated models
    // share terrain's placed-light calibration/falloff with forward materials,
    // without sampling the static atlas or adding another render pass.
    private void UploadEntityPlacedNormals(ICoreClientAPI api, Vec3d cameraPosition, bool ready)
    {
        Span<int> chosen = stackalloc int[EntityPlacedCandidateLimit];
        Span<double> distances = stackalloc double[EntityPlacedCandidateLimit];
        int count = 0;
        var lights = _publishedPlacedMaps.ReadyLights;
        if (ready && api.World is ClientMain)
        {
            for (int i = 0; i < lights.Count; i++)
            {
                var light = lights[i];
                if (light.Fade <= 0f) continue;
                double dx = light.Source.X + 0.5 - cameraPosition.X;
                double dy = light.Source.Y + StaticTerrainShadowMaps.SourceHeight - cameraPosition.Y;
                double dz = light.Source.Z + 0.5 - cameraPosition.Z;
                double distance = dx * dx + dy * dy + dz * dz;
                int insert = 0;
                while (insert < count && distances[insert] <= distance) insert++;
                if (insert >= EntityPlacedCandidateLimit) continue;
                if (count < EntityPlacedCandidateLimit) count++;
                for (int slot = count - 1; slot > insert; slot--)
                {
                    chosen[slot] = chosen[slot - 1];
                    distances[slot] = distances[slot - 1];
                }
                chosen[insert] = i;
                distances[insert] = distance;
            }
        }

        Span<float> fades = stackalloc float[EntityPlacedCandidateLimit];
        fades.Clear();
        int pairMask = 0;
        if (count > 0) {
            double[] view = ((ClientMain)api.World).CurrentModelViewMatrixd;
            for (int i = 0; i < count; ++i) {
                var light = lights[chosen[i]];
                double x = light.Source.X + 0.5;
                double y = light.Source.Y + StaticTerrainShadowMaps.SourceHeight;
                double z = light.Source.Z + 0.5;
                int offset = i * 4;
                _entityPlacedSources[offset] = (float)(view[0] * x + view[4] * y + view[8] * z + view[12]);
                _entityPlacedSources[offset+1] = (float)(view[1] * x + view[5] * y + view[9] * z + view[13]);
                _entityPlacedSources[offset+2] = (float)(view[2] * x + view[6] * y + view[10] * z + view[14]);
                _entityPlacedSources[offset+3] = PlacedLightView.Reach(light.Source);
                StaticLightGpuRecord.Rgb(light.Source.Hue, light.Source.Saturation, Config.PlsSaturation,
                    out _entityPlacedColors[offset], out _entityPlacedColors[offset+1], out _entityPlacedColors[offset+2]);
                _entityPlacedColors[offset+3] = light.Source.Level;
                fades[i] = Math.Clamp(light.Fade, 0f, 1f);
                if (light.ReplacementPair) pairMask |= 1 << i;
            }
        }
        // These names are the actual public native enum slots, including OIT.
        // Both counts are cleared on failure/disable, with cached locations and no per-frame strings.
        int previousProgram = GL.GetInteger(GetPName.CurrentProgram);
        try {
            for (int index = 0; index < ForwardPlacedPrograms.Length; ++index) {
                IShaderProgram? entity = api.Render.GetEngineShader(ForwardPlacedPrograms[index]);
                if (entity == null || entity.Disposed || entity.LoadError) continue;
                int program = entity.ProgramId;
                if (_entityPlacedPrograms[index] != program) {
                    _entityPlacedPrograms[index] = program;
                    _entityPlacedCounts[index] = GL.GetUniformLocation(program, "drtEntityPlacedCount");
                    _entityPlacedPositions[index] = GL.GetUniformLocation(program, "drtEntityPlacedSources[0]");
                    _entityPlacedColorLocations[index] = GL.GetUniformLocation(program, "drtEntityPlacedColors[0]");
                    _entityPlacedFadeLocations[index] = GL.GetUniformLocation(program, "drtEntityPlacedFades");
                    _entityPlacedExtraFadeLocations[index] = GL.GetUniformLocation(program, "drtEntityPlacedFadesExtra");
                    _entityPlacedOverflowFadeLocations[index] = GL.GetUniformLocation(program, "drtEntityPlacedFadeOverflow");
                    _entityPlacedPairLocations[index] = GL.GetUniformLocation(program, "drtEntityPlacedPairMask");
                }
                GL.UseProgram(program);
                if (_entityPlacedCounts[index] >= 0) GL.Uniform1(_entityPlacedCounts[index], count);
                if (count > 0 && _entityPlacedPositions[index] >= 0)
                    GL.Uniform4(_entityPlacedPositions[index], count, _entityPlacedSources);
                if (count > 0 && _entityPlacedColorLocations[index] >= 0)
                    GL.Uniform4(_entityPlacedColorLocations[index], count, _entityPlacedColors);
                if (_entityPlacedFadeLocations[index] >= 0)
                    GL.Uniform4(_entityPlacedFadeLocations[index], fades[0], fades[1], fades[2], fades[3]);
                if (_entityPlacedExtraFadeLocations[index] >= 0)
                    GL.Uniform4(_entityPlacedExtraFadeLocations[index], fades[4], fades[5], fades[6], fades[7]);
                if (_entityPlacedOverflowFadeLocations[index] >= 0)
                    GL.Uniform1(_entityPlacedOverflowFadeLocations[index], fades[8]);
                if (_entityPlacedPairLocations[index] >= 0)
                    GL.Uniform1(_entityPlacedPairLocations[index], pairMask);
            }
        }
        finally { GL.UseProgram(previousProgram); }
    }
}
