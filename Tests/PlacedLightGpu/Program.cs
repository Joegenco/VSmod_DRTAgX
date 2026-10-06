using System;
using System.IO;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;

string shaderPath = args[0];
if (args.Length > 1 && args[1] == "--release-safety") {
    using var releaseWindow = new GameWindow(GameWindowSettings.Default, new NativeWindowSettings {
        StartVisible = false, ClientSize = new Vector2i(16, 16), API = ContextAPI.OpenGL,
        APIVersion = new Version(4, 3), Profile = ContextProfile.Core });
    releaseWindow.MakeCurrent(); GL.LoadBindings(new OpenTK.Windowing.GraphicsLibraryFramework.GLFWBindingsContext());
    HdrStateProbe.Run();
    AoFormatProbe.Run();
    HdrPipelineProbe.Run(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(shaderPath)!, "../../drtagx/shaders")));
    ShadowSafetyProbe.Run();
    ReleaseBindingsProbe.Run();
    AtmosphereUploadProbe.Run();
    LensFlareProbe.Run(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(shaderPath)!, "../..")));
    GtaoResolutionProbe.Run(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(shaderPath)!, "../../drtagx/shaders")));
    VolumetricNativeProbe.Run(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(shaderPath)!, "../..")));
    WaterSchedulingProbe.Run();
    HdrFilterFootprintProbe.Run(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(shaderPath)!, "../../drtagx/shaders")));
    return;
}
if (args.Length > 1 && args[1] == "--lod-fog") {
    LodFogProbe.Run(shaderPath, args[2]);
    return;
}
if (args.Length > 1 && args[1] == "--atmosphere-bindings") {
    // Isolate native GL state ownership from unrelated atmosphere visual math.
    using var stateWindow = new GameWindow(GameWindowSettings.Default, new NativeWindowSettings {
        StartVisible = false, ClientSize = new Vector2i(16, 16), API = ContextAPI.OpenGL,
        APIVersion = new Version(4, 3), Profile = ContextProfile.Core });
    stateWindow.MakeCurrent(); GL.LoadBindings(new OpenTK.Windowing.GraphicsLibraryFramework.GLFWBindingsContext());
    int stateBuffer = GL.GenBuffer();
    float[] stateFrame = new float[120]; stateFrame[3] = 1;
    GL.BindBuffer(BufferTarget.UniformBuffer, stateBuffer);
    GL.BufferData(BufferTarget.UniformBuffer, stateFrame.Length * sizeof(float), stateFrame, BufferUsageHint.StaticDraw);
    AtmosphereBindingsProbe.Run(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(shaderPath)!, "../../drtagx/shaders/atmosphere")), stateBuffer);
    GL.DeleteBuffer(stateBuffer);
    return;
}
if (args.Length > 1 && args[1] == "--opaque-coverage") {
    OpaqueCoverageProbe.Run(args[2]);
    return;
}
if (args.Length > 1 && args[1] == "--final-dither") {
    FinalDitherProbe.Run(args[2]);
    return;
}
if (args.Length > 1 && args[1] == "--smaa-hdr") {
    SmaaHdrProbe.Run(args[2]);
    return;
}
if (args.Length > 1 && args[1] == "--placed-performance") {
    PlacedPerformanceProbe.Run(shaderPath, args[2]);
    return;
}
if (args.Length > 1 && args[1] == "--deferred-gradients") {
    DeferredGradientProbe.Run(shaderPath, args.Length > 2 ? args[2] : null);
    return;
}
if (args.Length > 1 && args[1] == "--atmosphere") {
    AtmosphereProbe.Run(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(shaderPath)!, "../../drtagx/shaders/atmosphere")));
    return;
}
if (args.Length > 1 && args[1] == "--cloud-horizon") {
    using var cloudWindow = new GameWindow(GameWindowSettings.Default, new NativeWindowSettings {
        StartVisible = false, ClientSize = new Vector2i(16,16), API = ContextAPI.OpenGL,
        APIVersion = new Version(4,3), Profile = ContextProfile.Core });
    cloudWindow.MakeCurrent(); GL.LoadBindings(new OpenTK.Windowing.GraphicsLibraryFramework.GLFWBindingsContext());
    CloudBalanceProbe.Run(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(shaderPath)!, "../../drtagx/shaders/atmosphere")));
    CloudStackingProbe.Run(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(shaderPath)!, "../../drtagx/shaders/atmosphere")));
    return;
}
if (args.Length > 1 && args[1] == "--fog-visibility") {
    FogVisibilityProbe.Run(shaderPath);
    LodFogProbe.Run(shaderPath, args[2]);
    return;
}
string original = ProbeShader.DeferredSource(shaderPath);

string Extract(string signature)
{
    int start = original.IndexOf(signature, StringComparison.Ordinal);
    if (start < 0) throw new Exception("Missing shader function: " + signature);
    int open = original.IndexOf('{', start), depth = 0;
    for (int i = open; i < original.Length; i++)
    {
        if (original[i] == '{') depth++;
        if (original[i] == '}' && --depth == 0) return original[start..(i + 1)];
    }
    throw new Exception("Unclosed shader function: " + signature);
}

string fragment = """
    #version 430 core
    layout(std430, binding = 4) readonly buffer Sources { vec4 drtStaticSourceData[]; };
    layout(std430, binding = 5) readonly buffer Tiles { uint drtStaticTileData[]; };
    uniform int drtStaticCount;
    uniform int drtShadowGridEnabled = 1;
    uniform int drtStaticTileWidth;
    uniform float drtStaticBlend;
    uniform vec2 drtPlacedCalibration[32];
    uniform mat4 invModelViewMatrix;
    uniform vec3 receiver;
    uniform bool evaluateDirect;
    uniform bool grassReceiver;
    uniform int probeMode;
    float shadowCalls = 0.0;
    out vec4 color;
    float drtStaticVisibility(vec3 a, vec3 b, float c, float d, int e, int f, float g) { shadowCalls += 1.0; return 1.0; }
    """ + "\n" + File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(shaderPath)!, "../../drtagx/shaders/lighting/drtagx_light_balance.ash"))) +
    "\n" + string.Join("\n", Array.FindAll(original.Split('\n'), line => line.StartsWith("const float STATIC_"))) +
    "\n" + Extract("vec3 drtStaticGridOffset(") +
    "\nconst int TILE_STRIDE = 5;\n" + Extract("int drtNextStaticLight(") + "\n" + Extract("vec3 drtSourceRgb(") + "\n" +
    Extract("vec4 drtPlacedLights(") + "\n" + Extract("float drtPlacedWeight(") + "\n" + """
    void main() {
        float emitter;
        vec3 selfLight;
        vec4 placed = drtPlacedLights(receiver, grassReceiver ? vec3(0,1,0) : vec3(0,0,1), evaluateDirect, grassReceiver, emitter, selfLight);
        color = probeMode == 1 ? vec4(shadowCalls, placed.a, emitter, 1.0) :
            probeMode == 2 ? vec4(selfLight, emitter) :
            vec4(placed.rg, placed.a, drtPlacedWeight(receiver, 0.0, 0.0));
    }
    """;
string vertex = """
    #version 430 core
    void main() {
        vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
        gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
    }
    """;

using var window = new GameWindow(GameWindowSettings.Default, new NativeWindowSettings
{
    StartVisible = false, ClientSize = new Vector2i(16, 16),
    API = ContextAPI.OpenGL, APIVersion = new Version(4, 3), Profile = ContextProfile.Core
});
window.MakeCurrent();
GL.LoadBindings(new OpenTK.Windowing.GraphicsLibraryFramework.GLFWBindingsContext());
if (args.Length > 1 && args[1] == "--water-plants") {
    DecalBridgeProbe.Run();
    DecalAlphaProbe.RunWaterPlants(shaderPath);
    DecalAlphaProbe.Run(shaderPath);
    return;
}
if (args.Length > 1 && args[1] == "--light-budget") {
    LightBudgetProbe.Run(shaderPath);
    return;
}
if (args.Length > 1 && args[1] == "--shadow-style") {
    ShadowStyleProbe.Run(shaderPath);
    return;
}
if (args.Length > 1 && args[1] == "--cache-runtime") {
    string computePath = Path.Combine(Path.GetDirectoryName(shaderPath)!, "../../drtagx/shaders/staticlighttiles.csh");
    GeometryUploadProbe.Run();
    TileBindingsCacheProbe.Run(computePath);
    SchedulerCacheProbe.Run();
    ShadowEditProbe.Run(); CameraBakeProbe.Run();
    return;
}
if (args.Length > 1 && args[1] == "--sun-shadows") {
    SunShadowProbe.Run(shaderPath);
    return;
}
if (args.Length > 1 && args[1] == "--sun-foliage") {
    SunFoliageProbe.Run(shaderPath, args.Length > 2 ? args[2] : null);
    DecalAlphaProbe.Run(shaderPath);
    return;
}
if (args.Length > 1 && args[1] == "--sun-wind-lighting") {
    SunShadowProbe.Run(shaderPath);
    SunWindLightingProbe.Run(shaderPath);
    ForwardLightingProbe.Run(shaderPath);
    SurfaceBindingsProbe.Run();
    return;
}
if (args.Length > 1 && args[1] == "--static-grid") {
    StaticGridProbe.Run(original);
    return;
}
if (args.Length > 1 && args[1] == "--placed-foliage") {
    PlacedFoliageProbe.Run(shaderPath, args.Length > 2 ? args[2] : null);
    return;
}
if (args.Length > 1 && args[1] == "--sun-cascades") {
    SunShadowProbe.RunCascadeCoverage(args[2],args[3]);
    return;
}
if (args.Length > 1 && args[1] == "--all-pass-occlusion") {
    AllPassPlacedOcclusionProbe.Run(args[2]);
    return;
}
if (args.Length > 1 && args[1] == "--opaque-nocull") {
    PlacedFoliageProbe.Run(shaderPath, args.Length > 2 ? args[2] : null, staticNoCullOnly: true);
    return;
}
if (args.Length > 1 && args[1] == "--foliage-wind") {
    PlacedFoliageProbe.Run(shaderPath, args.Length > 2 ? args[2] : null, windTemporalOnly: true);
    return;
}
if (args.Length > 1 && args[1] == "--animated-placed") {
    AnimatedPlacedProbe.Run(shaderPath);
    return;
}
if (args.Length > 1 && args[1] == "--exposure-bindings") {
    ExposureBindingsProbe.Run();
    return;
}
if (args.Length > 1 && args[1] == "--hdr-ssr") {
    SsrGlowProbe.Run(shaderPath);
    return;
}
if (args.Length > 1 && args[1] == "--volumetric") {
    VolumetricFogProbe.Run(shaderPath);
    VolumetricGroundProbe.Run(shaderPath);
    VolumetricBridgeProbe.Run();
    VolumetricNativeProbe.Run(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(shaderPath)!, "../..")));
    return;
}


int Compile(ShaderType type, string source)
{
    int shader = GL.CreateShader(type);
    GL.ShaderSource(shader, source);
    GL.CompileShader(shader);
    GL.GetShader(shader, ShaderParameter.CompileStatus, out int success);
    if (success == 0) throw new Exception(GL.GetShaderInfoLog(shader));
    return shader;
}

int vs = Compile(ShaderType.VertexShader, vertex), fs = Compile(ShaderType.FragmentShader, fragment);
int program = GL.CreateProgram();
GL.AttachShader(program, vs);
GL.AttachShader(program, fs);
GL.LinkProgram(program);
GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int linked);
if (linked == 0) throw new Exception(GL.GetProgramInfoLog(program));
GL.DeleteShader(vs);
GL.DeleteShader(fs);

int colorTexture = GL.GenTexture();
GL.BindTexture(TextureTarget.Texture2D, colorTexture);
GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba32f,
    1, 1, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
int framebuffer = GL.GenFramebuffer();
GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
    TextureTarget.Texture2D, colorTexture, 0);
if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete)
    throw new Exception("Pixel probe framebuffer incomplete");
int vao = GL.GenVertexArray();
GL.BindVertexArray(vao);
int sourceBuffer = GL.GenBuffer(), tileBuffer = GL.GenBuffer();
float[] calibration = new float[64];
for (int level = 1; level < 32; ++level) { calibration[level * 2] = 8f; calibration[level * 2 + 1] = 0.5f; }
float[] sourceData = new float[129 * 16];
uint[] tileData = new uint[5];

void SetSource(int record, float z, float hue, float fade, bool pair)
{
    int offset = record * 16;
    sourceData[offset + 2] = z;
    sourceData[offset + 3] = 22f;
    DRTAgX.StaticLightGpuRecord.Rgb((int)hue, 8, out sourceData[offset + 4], out sourceData[offset + 5], out sourceData[offset + 6]);
    sourceData[offset + 7] = record;
    sourceData[offset + 8] = fade;
    sourceData[offset + 9] = -1f;
    sourceData[offset + 10] = -1f;
    sourceData[offset + 11] = pair ? 1f : 0f;
    sourceData[offset + 14] = z; // identity inverse-view fixture
    sourceData[offset + 15] = 20;
}

float[] Draw(float receiverZ, int count, bool direct = true, int mode = 0, bool stagingOnly = false, float receiverY = 0, bool grass = false)
{
    Array.Clear(tileData);
    for (int i = 0; i < count; i++) tileData[i >> 5] |= 1u << (i & 31);
    if (stagingOnly) { Array.Clear(tileData); tileData[4] = 1; }
    GL.BindBuffer(BufferTarget.ShaderStorageBuffer, sourceBuffer);
    GL.BufferData(BufferTarget.ShaderStorageBuffer, sourceData.Length * sizeof(float), sourceData, BufferUsageHint.StreamDraw);
    GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 4, sourceBuffer);
    GL.BindBuffer(BufferTarget.ShaderStorageBuffer, tileBuffer);
    GL.BufferData(BufferTarget.ShaderStorageBuffer, tileData.Length * sizeof(int), tileData, BufferUsageHint.StreamDraw);
    GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 5, tileBuffer);
    GL.Viewport(0, 0, 1, 1);
    GL.UseProgram(program);
    GL.Uniform1(GL.GetUniformLocation(program, "evaluateDirect"), direct ? 1 : 0);
    GL.Uniform1(GL.GetUniformLocation(program, "grassReceiver"), grass ? 1 : 0);
    GL.Uniform1(GL.GetUniformLocation(program, "probeMode"), mode);
    GL.Uniform1(GL.GetUniformLocation(program, "drtStaticCount"), count);
    GL.Uniform1(GL.GetUniformLocation(program, "drtStaticTileWidth"), 1);
    GL.Uniform1(GL.GetUniformLocation(program, "drtStaticBlend"), 1f);
    GL.Uniform2(GL.GetUniformLocation(program, "drtPlacedCalibration[0]"), 32, calibration);
    GL.Uniform3(GL.GetUniformLocation(program, "receiver"), 0f, receiverY, receiverZ);
    GL.UniformMatrix4(GL.GetUniformLocation(program, "invModelViewMatrix"), 1, false,
        new float[] { 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1 });
    GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
    float[] result = new float[4];
    GL.ReadPixels(0, 0, 1, 1, PixelFormat.Rgba, PixelType.Float, result);
    if (GL.GetError() != ErrorCode.NoError) throw new Exception("OpenGL pixel probe failed");
    return result;
}

void Equal(float[] a, float[] b, string name)
{
    for (int i = 0; i < 4; i++)
        if (MathF.Abs(a[i] - b[i]) > 0.00001f) throw new Exception(name + " channel " + i);
    Console.WriteLine("PASS " + name);
}

SetSource(0, -8f, 0f, 1f, false);
float[] established = Draw(-10f, 1);
SetSource(1, -8f, 21f, 0f, false);
Equal(established, Draw(-10f, 2), "zero-fade overlap has no effect");
SetSource(1, -8f, 21f, 1f, false);
float[] forward = Draw(-10f, 2);
// Mask enumeration has canonical order: permute source records themselves.
for (int i = 0; i < 16; i++) (sourceData[i], sourceData[16 + i]) = (sourceData[16 + i], sourceData[i]);
Equal(forward, Draw(-10f, 2), "overlap order independent");
SetSource(0, -8f, 0f, 0.25f, true);
SetSource(1, -8f, 0f, 0.75f, true);
Equal(established, Draw(-10f, 2), "replacement pair retains coverage and radiance");
float near = Draw(-112f, 0)[3], middle = Draw(-120f, 0)[3], far = Draw(-128f, 0)[3];
if (MathF.Abs(near - 1f) > 0.00001f || MathF.Abs(middle - 0.5f) > 0.00001f ||
    MathF.Abs(far) > 0.00001f) throw new Exception("112-128 receiver handoff");
Console.WriteLine("PASS 112-128 receiver handoff");
SetSource(0, -130f, 0f, 1f, false);
float[] halo = Draw(-120f, 1);
if (halo[2] < 0.99f || halo[3] < 0.499f)
    throw new Exception("source beyond 128 reaches visible receiver");
Console.WriteLine("PASS source beyond 128 reaches visible receiver");
for (int i = 0; i < 129; i++)
{
    SetSource(i, -8f, 0f, 1f, false);
}
float[] crowded = Draw(-10f, 129);
if (crowded[2] < 0.99f || crowded[0] <= established[0])
    throw new Exception("129-record tile capacity");
Console.WriteLine("PASS 129-record tile capacity");

// Use production calibration with the maintained placed-light traversal.
float[] referenceTable = new float[32];
for (int i = 0; i < 32; ++i) referenceTable[i] = i / 31f;
DRTAgX.SurfaceLightBindings.Calibrate(referenceTable, calibration);
foreach (int level in new[] { 1, 14, 15, 18, 20 }) {
    SetSource(0, 0f, 0f, 1f, false);
    sourceData[3] = Math.Min(1.4f * level, 22f);
    sourceData[15] = level;
    sourceData[4] = sourceData[5] = sourceData[6] = 1; // neutral native chroma
    float dref = Math.Min(5.5f, sourceData[3] / 2);
    float[] direct = Draw(-dref, 1);
    // Shipping solid PLS uses a 0.75 directional gain plus a 0.2 isotropic
    // shoulder. Keep this independent numerical reference aligned with tuning.
    float native = 0.5f * (level-dref) / 31 * (0.75f + 0.2f * (1 + 0.25f * dref * dref));
    if (Math.Abs(direct[0]-native) > 1e-6 || Math.Abs(direct[1]-native) > 1e-6)
        throw new Exception("Production placed-light calibration/traversal mismatch");
    Console.WriteLine($"PASS calibrated level {level} matches native unoccluded reference in GPU traversal");
}

// Symmetric roots/tips around a low light have the same attenuation. The
// production foliage path must give them equal orientation-independent energy.
SetSource(0, 0, 0, 1, false); sourceData[1] = sourceData[13] = .5f;
float[] grassRoot = Draw(-4, 1, receiverY: 0, grass: true);
float[] grassTip = Draw(-4, 1, receiverY: 1, grass: true);
if (grassRoot[0] <= 0 || Math.Abs(grassRoot[0] - grassTip[0]) > 1e-6)
    throw new Exception("Grass root/tip lighting mismatch");
Console.WriteLine("PASS production placed-light traversal gives equal root/tip energy at equal distance");
sourceData[1] = sourceData[13] = 0;
// Exact zero gate avoids all map calls while retaining coverage/self-light.
SetSource(0, -8f, 21f, 1f, false);
float[] enabledCalls = Draw(-10f, 1, true, 1), disabledCalls = Draw(-10f, 1, false, 1);
if (enabledCalls[0] != 1 || disabledCalls[0] != 0 || enabledCalls[1] != disabledCalls[1])
    throw new Exception("Zero-weight direct gate/coverage");
Equal(Draw(-8f, 1, true, 2), Draw(-8f, 1, false, 2), "zero-weight gate preserves owning-voxel RGB/self-light");
if (Draw(-8f, 1, false, 2)[3] != 1) throw new Exception("Owning voxel wasn't classified");
SetSource(128, -8f, 21f, 1f, false);
Equal(Draw(-10f, 1), Draw(-10f, 129, true, 0, true), "fifth-word-only tile keeps staging record 128");
if (Draw(-10f, 0, true, 1)[0] != 0) throw new Exception("Empty tile performed shadow work");
Console.WriteLine("PASS direct-weight gate skips map calls and retains coverage; empty tiles skip shadows");
DepthTileProbe.Run(Path.Combine(Path.GetDirectoryName(shaderPath)!, "../../drtagx/shaders/staticlighttiles.csh"));
GpuTimerProbe.Run(Path.Combine(Path.GetDirectoryName(shaderPath)!, "../../drtagx/shaders/staticlighttilestats.csh"));
SourceTransformProbe.Run();
TileMaskProbe.Run(Path.Combine(Path.GetDirectoryName(shaderPath)!, "../../drtagx/shaders/staticlighttiles.csh"));
DepthArrayProbe.Run(original);
GL.UseProgram(program);
StaticGrazingProbe.Run(original);
StaticReceiverPlaneProbe.Run(original);
StaticGridProbe.Run(original);
ShadowStateProbe.Run();
PlacementPublishProbe.Run();
GeometryUploadProbe.Run();
TileBindingsCacheProbe.Run(Path.Combine(Path.GetDirectoryName(shaderPath)!, "../../drtagx/shaders/staticlighttiles.csh"));
SchedulerCacheProbe.Run();
ShadowEditProbe.Run(); CameraBakeProbe.Run();
bool placedShadowsOnly = args.Length > 1 && args[1] == "--placed-shadows";
if (!placedShadowsOnly)
{
SunShadowProbe.Run(shaderPath);
LightingBalanceProbe.Run(shaderPath);
ForwardLightingProbe.Run(shaderPath);
DecalAlphaProbe.Run(shaderPath);
DecalBridgeProbe.Run();
VolumetricFogProbe.Run(shaderPath);
VolumetricGroundProbe.Run(shaderPath);
VolumetricBridgeProbe.Run();
SsrGlowProbe.Run(shaderPath);
TransparentLightingProbe.Run(shaderPath);
SurfaceBindingsProbe.Run();
}

GL.DeleteBuffer(sourceBuffer);
GL.DeleteBuffer(tileBuffer);
GL.DeleteVertexArray(vao);
GL.DeleteFramebuffer(framebuffer);
GL.DeleteTexture(colorTexture);
GL.DeleteProgram(program);
