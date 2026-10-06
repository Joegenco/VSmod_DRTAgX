#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.Common;
using OpenTK.Mathematics;

string root = Path.GetFullPath(args[0]);
bool nativeStandard = args.Skip(1).Contains("--native-standard", StringComparer.Ordinal);
bool performance = args.Skip(1).Contains("--performance", StringComparer.Ordinal);
bool nativeBootstrap = args.Skip(1).Contains("--native-bootstrap", StringComparer.Ordinal);
bool nativeIncludes = false;
int captureArgument = Array.IndexOf(args, "--capture-terrain");
string? captureDirectory = captureArgument >= 0 ? Path.GetFullPath(args[captureArgument + 1]) : null;
if (captureDirectory != null) Directory.CreateDirectory(captureDirectory);
string nativeIncludeDir = Path.Combine(Environment.GetEnvironmentVariable("VINTAGE_STORY")!, "assets/game/shaderincludes");
string[] dirs =
[
    Path.Combine(root, "game", "shaderincludes"),
    Path.Combine(root, "game", "shaders"),
    Path.Combine(root, "drtagx", "shaders"),
    Path.Combine(root, "drtagx", "shaders", "deferred"),
    Path.Combine(root, "drtagx", "shaders", "lighting"),
    Path.Combine(root, "drtagx", "shaders", "atmosphere"),
    Path.Combine(root, "sheydermod", "shaders"),
    nativeIncludeDir,
    Path.Combine(Environment.GetEnvironmentVariable("VINTAGE_STORY")!, "assets/game/shaders")
];
// Read third-party shader includes from the developer's installed archive.
using var sheyderZip = ZipFile.OpenRead(Environment.GetEnvironmentVariable("SHEYDER_MOD_ZIP") ??
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VintagestoryData/Mods/SheyderMod 1.1.3.zip"));
HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

string Expand(string name, bool include = false)
{
    // Match the game's one-include-per-stage expansion for shared shader helpers.
    if (!seen.Add(name)) return "";
    string? path = dirs.Select(dir => Path.Combine(dir, name)).FirstOrDefault(File.Exists);
    // Bootstrap standard compiles before Sheyder's include loader is installed.
    // Exercise the actual native helpers as well as the maintained mod helpers.
    if (include && nativeIncludes && File.Exists(Path.Combine(nativeIncludeDir, name)))
        path = Path.Combine(nativeIncludeDir, name);
    string[] lines;
    if (path != null) lines = File.ReadAllLines(path);
    else
    {
        var entry = sheyderZip.GetEntry("assets/sheydermod/shaders/" + name)
            ?? throw new FileNotFoundException(name);
        using var reader = new StreamReader(entry.Open());
        lines = reader.ReadToEnd().Split('\n');
    }
    return string.Join("\n", lines.Select(line =>
    {
        string trimmed = line.Trim();
        if (trimmed.StartsWith("#include "))
            return Expand(trimmed[9..].Trim().Trim('"'), true);
        return include && trimmed.StartsWith("#version ") ? "" : line;
    }));
}

var settings = new NativeWindowSettings
{
    StartVisible = false, ClientSize = new Vector2i(32, 32),
    API = ContextAPI.OpenGL, APIVersion = new Version(4, 3), Profile = ContextProfile.Core
};
using var window = new GameWindow(GameWindowSettings.Default, settings);
window.MakeCurrent();
GL.LoadBindings(new OpenTK.Windowing.GraphicsLibraryFramework.GLFWBindingsContext());

foreach (var (name, defines) in new[]
{
    ("sky", "SSAOLEVEL 1\nSHADOWQUALITY 2\nDYNLIGHTS 16\nGODRAYS 1"),
    ("sky", "SSAOLEVEL 0\nSHADOWQUALITY 0\nDYNLIGHTS 0\nGODRAYS 0"),
    ("nightsky", "SSAOLEVEL 1\nSHADOWQUALITY 2\nDYNLIGHTS 16"),
    ("nightsky", "SSAOLEVEL 0\nSHADOWQUALITY 0\nDYNLIGHTS 0"),
    ("clouds", "SSAOLEVEL 1\nSHADOWQUALITY 2\nDYNLIGHTS 16\nUSEOIT 1"),
    ("cloudvolumetric", "USEOIT 1"),
    ("cloudmap", "DYNLIGHTS 0"),
    ("cloudmap", "DYNLIGHTS 16"),
    ("particlescube", "SSAOLEVEL 1\nSHADOWQUALITY 2\nDYNLIGHTS 16"),
    ("particlesquad", "SSAOLEVEL 1\nSHADOWQUALITY 2\nDYNLIGHTS 16\nUSEOIT 1"),
    ("chunkopaque", "SSAOLEVEL 0\nSHADOWQUALITY 0\nDYNLIGHTS 0\nSHEYDER_DEFERRED 0"),
    ("chunkopaque", "SSAOLEVEL 1\nSHADOWQUALITY 1\nDYNLIGHTS 4\nSHEYDER_DEFERRED 0"),
    ("chunkopaque", "SSAOLEVEL 1\nSHADOWQUALITY 1\nDYNLIGHTS 4\nSHEYDER_DEFERRED 1"),
    ("chunktopsoil", "SSAOLEVEL 0\nSHADOWQUALITY 0\nDYNLIGHTS 0\nSHEYDER_DEFERRED 0"),
    ("chunktopsoil", "SSAOLEVEL 1\nSHADOWQUALITY 1\nDYNLIGHTS 4\nSHEYDER_DEFERRED 0"),
    ("chunktopsoil", "SSAOLEVEL 1\nSHADOWQUALITY 1\nDYNLIGHTS 4\nSHEYDER_DEFERRED 1"),
    ("chunkliquid", "SSAOLEVEL 1\nSHADOWQUALITY 1\nDYNLIGHTS 4\nUSEOIT 1"),
    ("chunktransparent", "SSAOLEVEL 1\nSHADOWQUALITY 1\nDYNLIGHTS 4\nUSEOIT 1"),
    ("standard", "SSAOLEVEL 0\nSHADOWQUALITY 0\nDYNLIGHTS 0\nBLOOM 0"),
    ("standard", "SSAOLEVEL 1\nSHADOWQUALITY 2\nDYNLIGHTS 16\nBLOOM 1\nGLOWSUB 1"),
    ("standard", "SSAOLEVEL 2\nSHADOWQUALITY 3\nDYNLIGHTS 25\nBLOOM 1\nSHINYEFFECT 1\nUSEOIT 1\nALLOWDEPTHOFFSET 1\nNORMALVIEW 0"),
    ("standard", "SSAOLEVEL 0\nSHADOWQUALITY 0\nDYNLIGHTS 0\nBLOOM 0\nUSEOIT 1\nGLOWSUB 1\nNORMALVIEW 0"),
    ("entityanimated", "SSAOLEVEL 1\nSHADOWQUALITY 2\nDYNLIGHTS 16\nMAXANIMATEDELEMENTS 75\nUSEOIT 0\nNORMALVIEW 0"),
    ("entityanimated", "SSAOLEVEL 1\nSHADOWQUALITY 2\nDYNLIGHTS 16\nMAXANIMATEDELEMENTS 75\nUSEOIT 1\nNORMALVIEW 0"),
    ("entityanimated", "SSAOLEVEL 0\nSHADOWQUALITY 0\nDYNLIGHTS 0\nMAXANIMATEDELEMENTS 75\nUSEOIT 1\nNORMALVIEW 0"),
    ("chunkopaque", "SSAOLEVEL 1\nSHADOWQUALITY 2\nDYNLIGHTS 16\nSHEYDER_DEFERRED 1\nUSESSBO 1"),
    ("chunktopsoil", "SSAOLEVEL 1\nSHADOWQUALITY 2\nDYNLIGHTS 16\nSHEYDER_DEFERRED 1\nUSESSBO 1"),
    ("chunktransparent", "SSAOLEVEL 0\nSHADOWQUALITY 0\nDYNLIGHTS 0\nUSEOIT 1"),
    ("chunkliquid", "SSAOLEVEL 0\nSHADOWQUALITY 0\nDYNLIGHTS 0\nUSEOIT 1"),
    ("chunkopaque", "SSAOLEVEL 1\nSHADOWQUALITY 2\nDYNLIGHTS 16\nSHEYDER_DEFERRED 0\nSHINYEFFECT 1"),
    ("chunktopsoil", "SSAOLEVEL 1\nSHADOWQUALITY 2\nDYNLIGHTS 16\nSHEYDER_DEFERRED 0\nSHINYEFFECT 1"),
    ("standard", "SSAOLEVEL 1\nSHADOWQUALITY 2\nDYNLIGHTS 16\nBLOOM 0\nGLOWSUB 1\nSHINYEFFECT 1"),
    ("entityanimated", "SSAOLEVEL 1\nSHADOWQUALITY 2\nDYNLIGHTS 16\nMAXANIMATEDELEMENTS 75\nUSEOIT 1\nNORMALVIEW 0\nSHINYEFFECT 1"),
    ("chunktransparent", "SSAOLEVEL 1\nSHADOWQUALITY 2\nDYNLIGHTS 16\nUSEOIT 1\nSHINYEFFECT 1"),
    ("chunktransparent", "SSAOLEVEL 2\nSHADOWQUALITY 3\nDYNLIGHTS 25\nUSEOIT 1\nSHINYEFFECT 1\nUSESSBO 1"),
    ("chunktransparent", "SSAOLEVEL 0\nSHADOWQUALITY 0\nDYNLIGHTS 0\nUSEOIT 1\nUSESSBO 1"),
    ("chunkliquid", "SSAOLEVEL 1\nSHADOWQUALITY 2\nDYNLIGHTS 16\nUSEOIT 1\nSHINYEFFECT 1\nFOAMEFFECT 1\nWAVYEFFECT 1"),
    ("chunkshadowmap", "USESSBO 0"),
    ("chunkshadowmap", "USESSBO 1"),
    ("chunkshadowmap", "USESSBO 0\nWAVINGSTUFF 1"),
    ("chunkshadowmap", "USESSBO 1\nWAVINGSTUFF 1"),
    ("chunkopaque", "USESSBO 0\nWAVINGSTUFF 1\nSSAOLEVEL 1\nSHADOWQUALITY 2\nDYNLIGHTS 16\nSHEYDER_DEFERRED 0"),
    ("chunkopaque", "USESSBO 1\nWAVINGSTUFF 1\nSSAOLEVEL 2\nSHADOWQUALITY 3\nDYNLIGHTS 26\nSHEYDER_DEFERRED 1"),
    ("deferredlighting", "SSAOLEVEL 1\nSHADOWQUALITY 1\nDYNLIGHTS 4"),
    ("deferredlighting", "SSAOLEVEL 0\nSHADOWQUALITY 0\nDYNLIGHTS 0"),
    ("deferredlighting", "SSAOLEVEL 1\nSHADOWQUALITY 2\nDYNLIGHTS 16"),
    ("celestialobject", "SSAOLEVEL 0\nSHADOWQUALITY 0\nDYNLIGHTS 0"),
    ("celestialobject", "SSAOLEVEL 1\nSHADOWQUALITY 1\nDYNLIGHTS 4"),
    ("final", "SSAOLEVEL 0\nBLOOM 0\nFXAA 0\nGODRAYS 0"),
    ("vfscatter", "SSAOLEVEL 0\nSHADOWQUALITY 2\nDYNLIGHTS 0"),
    ("ssrcomposite", "SSAOLEVEL 1"),
    ("hdr_downsample", "SSAOLEVEL 0"),
    ("ao_reconstruct", "SSAOLEVEL 0"),
    // Keep the replacement god-ray program valid even when native callers
    // retain its compatibility uniforms with the effect enabled or disabled.
    ("godrays", "SSAOLEVEL 0\nGODRAYS 0"),
    ("godrays", "SSAOLEVEL 1\nGODRAYS 1"),
    ("final", "SSAOLEVEL 1\nBLOOM 1\nFXAA 1\nGODRAYS 1"),
    ("ssao", "SSAOLEVEL 1"),
    ("ssao", "SSAOLEVEL 2"),
    ("cloudvolumetric", "SSAOLEVEL 1\nSHADOWQUALITY 2\nDYNLIGHTS 4\nUSEOIT 1"),
    ("ssr", "SSAOLEVEL 1")
})
{
    int program = GL.CreateProgram();
    nativeIncludes = nativeStandard && name == "standard" ||
        nativeBootstrap && (name == "standard" || name == "entityanimated" || name == "particlescube" || name == "particlesquad");
    string preamble = string.Join("\n", defines.Split('\n').Select(x => "#define " + x)) + "\n";
    if (performance) preamble += "#define DRT_PERFORMANCE_NO_PLS 1\n";
    foreach (var (extension, type) in new[] { ("vsh", ShaderType.VertexShader), ("fsh", ShaderType.FragmentShader) })
    {
        seen.Clear();
        string source = Expand(name + "." + extension);
        if (defines.Contains("USESSBO 1") && extension == "vsh")
            source = source.Replace("#version 330 core", "#version 430 core");
        int versionStart = source.IndexOf("#version", StringComparison.Ordinal);
        int firstLine = source.IndexOf('\n', versionStart);
        source = source[..(firstLine + 1)] + preamble + source[(firstLine + 1)..];
        // Feed the coverage probe current maintained terrain code, rather than
        // a stale live capture from before these shader/include changes.
        if (captureDirectory != null && name == "chunkopaque" &&
            defines == "SSAOLEVEL 1\nSHADOWQUALITY 1\nDYNLIGHTS 4\nSHEYDER_DEFERRED 0")
            File.WriteAllText(Path.Combine(captureDirectory, "live-terrain." + extension), source);
        int shader = GL.CreateShader(type);
        GL.ShaderSource(shader, source);
        GL.CompileShader(shader);
        GL.GetShader(shader, ShaderParameter.CompileStatus, out int status);
        if (status == 0) throw new Exception(name + "." + extension + " (" + defines.Replace('\n', ',') + "): " + GL.GetShaderInfoLog(shader));
        GL.AttachShader(program, shader);
        GL.DeleteShader(shader);
    }
    GL.LinkProgram(program);
    GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int linked);
    if (linked == 0) throw new Exception(name + " link (" + defines.Replace('\n', ',') + "): " + GL.GetProgramInfoLog(program));
    if (name == "standard" && defines.Contains("USEOIT 1")) {
        // FpHands sets USEOIT=1 but draws into four scene targets with ordinary
        // SrcAlpha blending. The native standard shader deliberately ignores it.
        string[] outputs = defines.Contains("SSAOLEVEL 0")
            ? ["outColor", "outGlow"] : ["outColor", "outGlow", "outGNormal", "outGPosition"];
        for (int location = 0; location < outputs.Length; ++location) {
            int actual = GL.GetFragDataLocation(program, outputs[location]);
            if (actual != location)
                throw new Exception($"standard forward output {outputs[location]}: {actual} != native slot {location}");
        }
        foreach (string oitOutput in new[] { "OITreveal", "outReveal", "OITaccumulation0", "OITaccumulation1", "OITaccumulation2" })
            if (GL.GetFragDataLocation(program, oitOutput) >= 0)
                throw new Exception("standard forward exposes OIT output " + oitOutput);
        Console.WriteLine("PASS held-item standard uses native forward color/glow/SSAO output layout");
    }
    GL.DeleteProgram(program);
    Console.WriteLine(name + " " + defines.Replace('\n', ',') + " compiled and linked");
}

// Current PLS storage is a D24 array: up to 128 resident six-face maps plus
// one six-face staging transaction, at the shipping 96-pixel face resolution.
// Check both ends of the allocation rather than the retired 2D packed atlas.
int residentMaps = Math.Min(128, GL.GetInteger(GetPName.MaxArrayTextureLayers) / 6 - 1);
if (residentMaps < 1) throw new Exception("No capacity for resident and staging shadow layers");
int atlasLayers = (residentMaps + 1) * 6;
int atlas = GL.GenTexture();
GL.BindTexture(TextureTarget.Texture2DArray, atlas);
GL.TexStorage3D(TextureTarget3d.Texture2DArray, 1, SizedInternalFormat.DepthComponent24,
    96, 96, atlasLayers);
int atlasFbo = GL.GenFramebuffer();
GL.BindFramebuffer(FramebufferTarget.Framebuffer, atlasFbo);
GL.DrawBuffer(DrawBufferMode.None);
GL.ReadBuffer(ReadBufferMode.None);
GL.Disable(EnableCap.ScissorTest);
GL.DepthMask(true);
GL.ClearDepth(1.0);
foreach (int layer in new[] { 0, atlasLayers - 1 }) {
    GL.FramebufferTextureLayer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
        atlas, 0, layer);
    var atlasStatus = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
    if (atlasStatus != FramebufferErrorCode.FramebufferComplete)
        throw new Exception("placed-light array layer framebuffer: " + atlasStatus);
    GL.Clear(ClearBufferMask.DepthBufferBit);
    float[] clearedDepth = new float[1];
    GL.ReadPixels(0, 0, 1, 1, PixelFormat.DepthComponent, PixelType.Float, clearedDepth);
    if (clearedDepth[0] != 1f) throw new Exception("placed-light array layer clear failed");
}
var atlasError = GL.GetError();
Console.WriteLine($"placed-light D24 array: {residentMaps} residents + staging, {atlasLayers} layers at 96x96; GL error {atlasError}");
if (atlasError != ErrorCode.NoError)
    throw new Exception("placed-light atlas creation/clear failed");
GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
GL.DeleteFramebuffer(atlasFbo);
GL.DeleteTexture(atlas);
