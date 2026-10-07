using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

/// <summary>Released provider shaders, native setter declarations and the real atmosphere binder.</summary>
internal static class LodFogProbe
{
    internal static void Run(string deferredPath, string references)
    {
        string assets = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(deferredPath)!, "../.."));
        string install = Environment.GetEnvironmentVariable("VINTAGE_STORY")!;
        string[] directories = { Path.Combine(assets, "game/shaders"), Path.Combine(assets, "game/shaderincludes"),
            Path.Combine(assets, "drtagx/shaders/atmosphere"), Path.Combine(assets, "drtagx/shaders/lighting"),
            Path.Combine(assets, "drtagx/shaders/deferred"), Path.Combine(assets, "drtagx/shaders"),
            Path.Combine(assets, "sheydermod/shaders"), Path.Combine(install, "assets/game/shaderincludes"), Path.Combine(install, "assets/game/shaders") };
        using var sheyder = ZipFile.OpenRead(Path.Combine(Path.GetDirectoryName(install)!, "VintagestoryData/Mods/SheyderMod 1.1.3.zip"));
        string Expand(string source, int ssao) {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string Include(string name) {
                if (!seen.Add(name)) return "";
                string file = directories.Select(dir => Path.Combine(dir, name)).FirstOrDefault(File.Exists);
                string text;
                if (file != null) text = File.ReadAllText(file);
                else {
                    var entry = sheyder.GetEntry("assets/sheydermod/shaders/" + name) ?? throw new Exception("LOD include missing: " + name);
                    using var reader = new StreamReader(entry.Open()); text = reader.ReadToEnd();
                }
                return Lines(text, true);
            }
            string Lines(string text, bool include) => string.Join("\n", text.Split('\n').Select(line => {
                string trim = line.Trim();
                return trim.StartsWith("#include ", StringComparison.Ordinal) ? Include(trim[9..].Trim().Trim('"')) :
                    include && trim.StartsWith("#version ", StringComparison.Ordinal) ? "" : line;
            }));
            string expanded = Lines(source, false);
            int first = expanded.IndexOf('\n');
            return expanded[..(first + 1)] + $"#define SSAOLEVEL {ssao}\n#define SHADOWQUALITY 0\n#define DYNLIGHTS 0\n#define GODRAYS 0\n#define MINBRIGHT 0.0\n" + expanded[(first + 1)..];
        }
        void Check(bool valid, string label) { if (!valid) throw new Exception(label); }
        using var window = new GameWindow(GameWindowSettings.Default, new NativeWindowSettings {
            StartVisible = false, ClientSize = new Vector2i(16, 16), API = ContextAPI.OpenGL,
            APIVersion = new Version(4, 3), Profile = ContextProfile.Core });
        window.MakeCurrent(); GL.LoadBindings(new OpenTK.Windowing.GraphicsLibraryFramework.GLFWBindingsContext());
        int[] outputs = new int[4], nativeTextures = new int[8];
        int Scalar(float r, float g, float b) {
            int texture = GL.GenTexture(); GL.BindTexture(TextureTarget.Texture2D, texture);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba32f, 1, 1, 0, PixelFormat.Rgba, PixelType.Float, new[] { r, g, b, 1f });
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            return texture;
        }
        int fbo = GL.GenFramebuffer(), vao = GL.GenVertexArray(), ubo = GL.GenBuffer(), oldUbo = GL.GenBuffer();
        int sentinel = GL.GenSampler(), sentinelTexture = Scalar(.02f, .02f, .02f), skyTexture = Scalar(.8f, .6f, .4f), liquid = Scalar(1, 1, 1);
        int volume = AtmosphereCompute.Texture(1, 1, 1);
        GL.SamplerParameter(sentinel, SamplerParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.SamplerParameter(sentinel, SamplerParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        for (int i = 0; i < 8; i++) {
            GL.ActiveTexture(TextureUnit.Texture0 + i); nativeTextures[i] = Scalar(.4f, .5f, .3f); GL.BindSampler(i, sentinel);
        }
        int tint = GL.GenTexture(); GL.ActiveTexture(TextureUnit.Texture4); GL.BindTexture(TextureTarget.Texture2DArray, tint);
        GL.TexImage3D(TextureTarget.Texture2DArray, 0, PixelInternalFormat.Rgba32f, 1, 1, 1, 0, PixelFormat.Rgba, PixelType.Float, new[] { 1f, 1f, 1f, 1f });
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        for (int i = 0; i < outputs.Length; i++) {
            outputs[i] = Scalar(0, 0, 0);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0 + i, TextureTarget.Texture2D, outputs[i], 0);
        }
        GL.DrawBuffers(4, new[] { DrawBuffersEnum.ColorAttachment0, DrawBuffersEnum.ColorAttachment1, DrawBuffersEnum.ColorAttachment2, DrawBuffersEnum.ColorAttachment3 });
        Check(GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) == FramebufferErrorCode.FramebufferComplete, "LOD probe FBO");
        // Allocation above touched native/high units; establish explicit sentinels.
        for (int i = 0; i < 8; i++) { GL.ActiveTexture(TextureUnit.Texture0 + i); GL.BindTexture(TextureTarget.Texture2D, nativeTextures[i]); }
        for (int i = 10; i < 14; i++) {
            GL.ActiveTexture(TextureUnit.Texture0 + i); GL.BindTexture(TextureTarget.Texture2D, sentinelTexture);
            GL.BindTexture(TextureTarget.Texture3D, volume); GL.BindSampler(i, sentinel);
        }
        GL.BindVertexArray(vao); GL.Viewport(0, 0, 1, 1); GL.Disable(EnableCap.Blend); GL.Disable(EnableCap.DepthTest);
        float[] frame = new float[AtmosphereRenderer.FrameFloatCount];
        frame[0] = .2f; frame[1] = .3f; frame[2] = .4f; frame[3] = 1; frame[8] = 512; frame[9] = 200;
        frame[24] = frame[27] = 1; frame[32] = frame[33] = 1; frame[116] = .3f; frame[117] = frame[119] = 1; frame[118] = 4096;
        // Camera faces +world Z; standard perspective still looks down -view Z.
        // Publish native near=.1/far=512 matrices while synthetic LOD depth=.5.
        // The water test therefore detects use of compressed fragment depth.
        frame[52] = frame[62] = -1; frame[57] = frame[67] = 1;
        float projectionA = -(512 + .1f) / (512 - .1f), projectionB = -2 * 512 * .1f / (512 - .1f);
        frame[76] = frame[81] = 1; frame[86] = projectionA; frame[87] = -1; frame[90] = projectionB;
        frame[36] = frame[41] = 1; frame[47] = 1 / projectionB; frame[50] = -1; frame[51] = projectionA / projectionB;
        GL.BindBuffer(BufferTarget.UniformBuffer, oldUbo); GL.BufferData(BufferTarget.UniformBuffer, frame.Length * 4, frame, BufferUsageHint.DynamicDraw);
        ProbeAssets.Api([]); // Resolve optional native API signature dependencies before generating proxies.
        var buffers = new List<FrameBufferRef>(); for (int i = 0; i < 13; i++) buffers.Add(null);
        buffers[5] = new FrameBufferRef { DepthTextureId = liquid, Width = 1, Height = 1 };
        var render = SurfaceApiProxy.Make<IRenderAPI>((method, _) => method.Name == "get_FrameBuffers" ? buffers : throw new NotSupportedException(method.Name));
        var api = SurfaceApiProxy.Make<ICoreClientAPI>((method, _) => method.Name == "get_Render" ? render : throw new NotSupportedException(method.Name));
        var sky = new AtmosphereSkyResources(api);
        typeof(AtmosphereSkyResources).GetProperty("Previous", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(sky, skyTexture);
        typeof(AtmosphereSkyResources).GetProperty("Current", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(sky, skyTexture);
        using var bindings = new AtmosphereProgramBindings(api, sky, ubo) { Volume = volume };
        var bind = typeof(AtmosphereProgramBindings).GetMethod("Bind", BindingFlags.Instance | BindingFlags.NonPublic)!;
        string synthetic = """
            #version 330 core
            uniform vec3 probePos;
            uniform float probeClipDepth;
            out vec4 worldPos, rgbaFog, colorVarying, vCamPos;
            out vec3 vWorldAbs;
            out float yLevel, fogAmount, dist, nightVisionStrengthv, isHeightmapv;
            flat out int vTintType,vHasTex,vIsLeaf,vIsBillboard,vGrassFringe,vDirtBase,vIsSeam,vSeamTopY;
            out vec2 vTileUv;
            out vec4 vTileRect;
            void main() {
                vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);
                gl_Position=vec4(p*2.-1.,probeClipDepth,1);
                // Vary real position to keep provider derivative normals defined.
                worldPos=vec4(probePos+vec3((p-.5)*2.,0),1);
                vWorldAbs=worldPos.xyz+vec3(0,110,0); vCamPos=worldPos;
                colorVarying=vec4(.4,.5,.3,1); rgbaFog=vec4(.2,.3,.4,1);
                yLevel=110; fogAmount=.75; dist=worldPos.z/1000.;
                nightVisionStrengthv=0.; isHeightmapv=0.;
                vTintType=0;vHasTex=0;vIsLeaf=0;vIsBillboard=0;vGrassFringe=0;vDirtBase=0;vIsSeam=0;vSeamTopY=0;
                vTileUv=p;vTileRect=vec4(0,0,1,1);
            }
            """;
        string water = ProbeShader.DeferredSource(Path.Combine(assets, "game/shaders/underwatereffects.fsh"));
        int reference = ProbeShader.Program((ShaderType.VertexShader, synthetic), (ShaderType.FragmentShader,
            "#version 330 core\nuniform float zNear,zFar;\nvec4 applySpheresFog(vec4 c,float f,vec3 p){return c;}\n" + water +
            "\nuniform vec3 probePos;out vec4 outColor;void main(){outColor=vec4(drtSkyBackground(normalize(probePos)),1);}"));
        // Independent real-terrain reference: mapped material times the shared
        // full-access diffuse sky, followed by the existing surface transport.
        string lightBalance = File.ReadAllText(Path.Combine(assets, "drtagx/shaders/lighting/drtagx_light_balance.ash"));
        int terrainReference = ProbeShader.Program((ShaderType.VertexShader, synthetic), (ShaderType.FragmentShader,
            "#version 330 core\nuniform float zNear,zFar;\nvec4 applySpheresFog(vec4 c,float f,vec3 p){return c;}\n" + water + "\n" + lightBalance + """
            in vec4 worldPos;
            uniform vec3 probePos, probeMaterial;
            uniform float probeFaceShade;
            out vec4 outColor;
            void main() {
                vec3 radiance=probeMaterial*drtSunRadiance(1.0,drtAmbientSky(drtAmbientNative.rgb))*probeFaceShade;
                float receiverDepth=drtNativeReceiverDepth(worldPos.xyz,gl_FragCoord.z);
                outColor=drtApplySurfaceFog(vec4(radiance,1),worldPos.xyz,receiverDepth,true,1.0);
            }
            """));
        void SharedSky(Vector3 radiance) {
            frame[92] = frame[93] = frame[94] = frame[95] = 1;
            frame[100] = frame[101] = frame[102] = .5f;
            frame[104] = radiance.X; frame[105] = radiance.Y; frame[106] = radiance.Z; frame[107] = 1;
        }
        void Uniform(int program, string name, float value) => GL.Uniform1(GL.GetUniformLocation(program, name), value);
        void Setup(int program, float day, float nativeFar) {
            GL.UseProgram(program);
            foreach (var (name, unit) in new[] { ("terrainTex", 0), ("shadowTex", 1), ("chunkMaskTex", 2), ("tempField", 3), ("tintField", 4), ("glow", 6), ("sky", 7) })
                GL.Uniform1(GL.GetUniformLocation(program, name), unit);
            Uniform(program, "dayLight", day); Uniform(program, "farViewDistance", nativeFar); Uniform(program, "viewDistance", 512);
            Uniform(program, "lightLevelBias", .5f); Uniform(program, "fadeBias", .5f); Uniform(program, "tempFieldSpan", 1024);
            Uniform(program, "tintFieldSpan", 1024); Uniform(program, "shadowsEnabled", 0); Uniform(program, "gapFillEnabled", 0);
            GL.Uniform1(GL.GetUniformLocation(program, "seaLevel"), 110);
            GL.Uniform3(GL.GetUniformLocation(program, "sunPosition"), 0f, day > .5f ? 1f : -1f, 0f);
            GL.Uniform3(GL.GetUniformLocation(program, "sunColor"), 1f, 1f, 1f);
            GL.Uniform2(GL.GetUniformLocation(program, "frameSize"), 1f, 1f);
        }
        float[] Read(int program, float distance) {
            GL.UseProgram(program); GL.Uniform3(GL.GetUniformLocation(program, "probePos"), 0f, 0f, distance);
            GL.BindBufferBase(BufferRangeTarget.UniformBuffer, 10, oldUbo); GL.BindBuffer(BufferTarget.UniformBuffer, oldUbo);
            GL.ActiveTexture(TextureUnit.Texture7);
            bind.Invoke(bindings, new object[] { program });
            Check(GL.GetInteger(GetPName.ActiveTexture) == (int)TextureUnit.Texture7, "LOD binder preserves active texture");
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
            var pixel = new float[8]; GL.ReadBuffer(ReadBufferMode.ColorAttachment0); GL.ReadPixels(0, 0, 1, 1, PixelFormat.Rgba, PixelType.Float, pixel);
            if (program != reference && program != terrainReference) {
                var glow = new float[4]; GL.ReadBuffer(ReadBufferMode.ColorAttachment1); GL.ReadPixels(0, 0, 1, 1, PixelFormat.Rgba, PixelType.Float, glow);
                Array.Copy(glow, 0, pixel, 4, 4);
            }
            bindings.Restore();
            GL.GetInteger(GetIndexedPName.UniformBufferBinding, 10, out int restoredUbo);
            Check(restoredUbo == oldUbo && GL.GetInteger(GetPName.UniformBufferBinding) == oldUbo, "LOD binder restores UBO bindings");
            for (int i = 0; i < 8; i++) {
                GL.ActiveTexture(TextureUnit.Texture0 + i);
                Check(GL.GetInteger(GetPName.TextureBinding2D) == nativeTextures[i] && GL.GetInteger(GetPName.SamplerBinding) == sentinel, "LOD native units 0..7 preserved");
            }
            for (int i = 10; i < 14; i++) {
                GL.ActiveTexture(TextureUnit.Texture0 + i);
                Check(GL.GetInteger(GetPName.TextureBinding2D) == sentinelTexture && GL.GetInteger(GetPName.TextureBinding3D) == volume && GL.GetInteger(GetPName.SamplerBinding) == sentinel, "LOD atmosphere textures/samplers restored");
            }
            Check(pixel.All(float.IsFinite), "finite LOD radiance/metadata");
            return pixel;
        }
        try {
            foreach (bool chunkLod in new[] { true, false }) {
                string prefix = chunkLod ? "" : File.Exists(Path.Combine(references, "legacy-farseer-region.fsh")) ? "legacy-farseer-" : "farseer-";
                string original = File.ReadAllText(Path.Combine(references, prefix + "region.fsh"));
                Check(LodFogAssetPatch.TryPatchFragment(original, out string patched), "recognized released LOD fragment");
                if (chunkLod) {
                    Check(!patched.Contains("fract(52.9829189", StringComparison.Ordinal), "LOD shadow screen-pixel jitter removed");
                    Check(patched.Contains("float t = 2.0 + 0.5 * stepLen;", StringComparison.Ordinal), "LOD shadow fixed midpoint");
                }
                Check(LodFogAssetPatch.TryPatchFragment(patched, out string repeated) && repeated == patched, "LOD reload is idempotent");
                // Reapply to the previous in-memory compatibility patch, as on hot reload.
                string previous = patched.Replace("    drtDiscardLodNearBoundary(worldPos.xyz);\n", "", StringComparison.Ordinal);
                Check(LodFogAssetPatch.TryPatchFragment(previous, out string upgraded) && upgraded == patched, "existing LOD patch gains near exclusion once");
                string unknown = original.Replace("outGlow = mix(", "outGlow = customMix(", StringComparison.Ordinal);
                Check(!LodFogAssetPatch.TryPatchFragment(unknown, out string fallback) && fallback == unknown, "unknown LOD layout stays intact");
                string unknownLighting = original.Replace("terraColor.rgb *=", "terraColor.rgb +=", StringComparison.Ordinal);
                Check(!LodFogAssetPatch.TryPatchFragment(unknownLighting, out fallback) && fallback == unknownLighting, "unknown LOD lighting stays intact");
                foreach (int ssao in new[] { 0, 1 }) {
                    // GL may reuse deleted program IDs. Production clears these
                    // location caches on shader reload; model that lifecycle here.
                    bindings.Reload();
                    string actualVertex = Expand(File.ReadAllText(Path.Combine(references, prefix + "region.vsh")), ssao);
                    string fragment = Expand(patched, ssao);
                    int linked = ProbeShader.Program((ShaderType.VertexShader, actualVertex), (ShaderType.FragmentShader, fragment));
                    // The engine stores declared uniforms even when GL optimizes
                    // a location to -1. Protect setter keys, not needless activity.
                    foreach (Match declaration in Regex.Matches(Expand(original, ssao) + actualVertex, @"uniform\s+\w+\s+(\w+)"))
                        Check(Regex.IsMatch(fragment + actualVertex, @"uniform\s+\w+\s+" + declaration.Groups[1].Value + @"(?:\s|[=;\[])"), "native LOD uniform declaration retained: " + declaration.Groups[1].Value);
                    foreach (string active in new[] { "fogDensityIn", "fogMinIn", "rgbaFogIn", "farViewDistance" })
                        Check(GL.GetUniformLocation(linked, active) >= 0, "LOD native fallback remains linked: " + active);
                    GL.DeleteProgram(linked);
                    int program = ProbeShader.Program((ShaderType.VertexShader, synthetic), (ShaderType.FragmentShader, fragment));
                    LodOverlapProbe.Run(synthetic, Expand(previous, ssao), fragment,
                        p => Setup(p, 1, chunkLod ? 2048 : 4096),
                        p => bind.Invoke(bindings, new object[] { p }), () => bindings.Restore(),
                        frame, ubo, fbo, $"{(chunkLod ? "ChunkLOD" : "Farseer")} SSAO={ssao}");
                    bindings.Reload(); // GL can recycle the probe's deleted program IDs.
                    foreach (float day in new[] { 0f, 1f }) foreach (float density in new[] { 0f, .001f, .05f, .2f }) {
                        frame[13] = day > .5f ? 1 : -1; frame[15] = day; frame[4] = density;
                        SharedSky(day > .5f ? new Vector3(1.2f,1.4f,1.6f) : new Vector3(.05f,.1f,.15f));
                        GL.BindBuffer(BufferTarget.UniformBuffer, ubo); GL.BufferData(BufferTarget.UniformBuffer, frame.Length * 4, frame, BufferUsageHint.DynamicDraw);
                        // ChunkLOD's old half-range fog uniform stays native,
                        // while shared concealment follows full terrain coverage.
                        Setup(program, day, chunkLod ? 2048 : 4096); Setup(reference, day, 4096);
                        float[] near = Read(program, 600), end = Read(program, 4086), expected = Read(reference, 4086);
                        Check(near[3] == 1 && end[3] == 1, $"LOD solid coverage: chunk={chunkLod}, SSAO={ssao}, day={day}, rho={density}, near/end alpha={near[3]}/{end[3]}");
                        for (int c = 0; c < 3; c++) Check(Math.Abs(end[c] - expected[c]) < 5e-5f, "LOD endpoint matches shared exposed weather sky");
                        Check(end[4] == 0 && end[5] == 0 && end[6] == 0 && end[7] == 1, "LOD stays completed forward RGB with no deferred marker");
                        if (day == 1 && density == 0) Check(near[0] > .3f && near[1] > .3f && near[2] > .2f, "daytime LOD terrain is visible before concealment");
                    }
                    frame[4] = 0; frame[8] = 512;
                    foreach (var (nativeDay, solarY, sunStrength, skyRadiance) in new[] {
                        (.08f,.05f,.2f,new Vector3(1.2f,1.0f,.8f)),
                        (.9f,1f,1f,new Vector3(1.8f,1.7f,1.5f)),
                        (.2f,.02f,.1f,new Vector3(.4f,.25f,.15f)),
                        (.4f,-1f,0f,new Vector3(.02f,.035f,.05f)) }) {
                        frame[13] = solarY; frame[15] = sunStrength; SharedSky(skyRadiance);
                        GL.BindBuffer(BufferTarget.UniformBuffer,ubo); GL.BufferData(BufferTarget.UniformBuffer,frame.Length*4,frame,BufferUsageHint.DynamicDraw);
                        Setup(program,nativeDay,chunkLod?2048:4096); Setup(terrainReference,nativeDay,4096);
                        GL.UseProgram(terrainReference);
                        GL.Uniform3(GL.GetUniformLocation(terrainReference,"probeMaterial"),chunkLod?new Vector3(.4f,.5f,.3f):Vector3.One);
                        Uniform(terrainReference,"probeFaceShade",chunkLod?1f:.5f);
                        float[] actual=Read(program,600), expected=Read(terrainReference,600);
                        for(int c=0;c<3;c++) Check(Math.Abs(actual[c]-expected[c])<3e-5f,
                            $"LOD diffuse sky mismatch: channel={c}, actual={actual[c]:R}, expected={expected[c]:R}, solarY={solarY}, nativeRange={frame[8]}");
                        GL.UseProgram(program); Uniform(program,"dayLight",1-nativeDay);
                        GL.Uniform3(GL.GetUniformLocation(program,"sunColor"),.1f,.2f,.3f);
                        float[] changed=Read(program,600);
                        for(int c=0;c<3;c++) Check(Math.Abs(changed[c]-expected[c])<3e-5f,"legacy LOD day bias cannot override shared lighting");
                    }
                    Console.WriteLine($"PASS {(chunkLod?"ChunkLOD":"Farseer")} SSAO={ssao} shared morning/day/sunset/night radiance, material brightness, legacy dayLight/sunColor bypass");
                    if (chunkLod) {
                        // Exercise the released height-map raymarch, not a mocked
                        // visibility function. A constant tall map occludes the
                        // first ray step; a negative map leaves the ray clear.
                        Setup(program, 1, 2048); Setup(terrainReference, 1, 4096);
                        GL.UseProgram(program);
                        Uniform(program, "shadowsEnabled", 1); Uniform(program, "chunkLodFaceLighting", 1);
                        Uniform(program, "shadowTexSize", 1); Uniform(program, "shadowBlocksPerTexel", 1);
                        Uniform(program, "shadowNoHeight", -1); Uniform(program, "shadowMaxY", 1000);
                        GL.Uniform3(GL.GetUniformLocation(program, "sunPosition"), 0f, .6f, .8f);
                        GL.UseProgram(terrainReference);
                        GL.Uniform3(GL.GetUniformLocation(terrainReference, "probeMaterial"), .4f, .5f, .3f);
                        frame[13] = .6f; SharedSky(new Vector3(.8f, 1f, .6f));
                        foreach (bool occluded in new[] { false, true }) {
                            GL.ActiveTexture(TextureUnit.Texture1); GL.BindTexture(TextureTarget.Texture2D, nativeTextures[1]);
                            GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, 1, 1, PixelFormat.Rgba, PixelType.Float,
                                new[] { occluded ? 500f : -100f, 0f, 0f, 1f });
                            foreach (float sunStrength in new[] { 0f, 1f }) {
                                frame[15] = sunStrength;
                                GL.BindBuffer(BufferTarget.UniformBuffer, ubo);
                                GL.BufferData(BufferTarget.UniformBuffer, frame.Length * 4, frame, BufferUsageHint.DynamicDraw);
                                foreach (float distance in new[] { 600f, 1710f, 1950f }) {
                                    // +Z normal faces this sun with dot=.8. The
                                    // terrain contract uses maximum darkness;
                                    // its .65 cast strength fades over 1520..1900.
                                    float faceBrightness = Math.Max(.45f, Math.Min(.9f, 1 - .65f * sunStrength * .2f));
                                    float fade = distance < 1520 ? 1 : distance > 1900 ? 0 : .5f;
                                    float castBrightness = 1 - (occluded ? .65f * sunStrength * fade : 0);
                                    GL.UseProgram(terrainReference);
                                    Uniform(terrainReference, "probeFaceShade", Math.Min(faceBrightness, castBrightness));
                                    float[] actual = Read(program, distance), expected = Read(terrainReference, distance);
                                    for (int c = 0; c < 3; c++) Check(Math.Abs(actual[c] - expected[c]) < 3e-5f,
                                        "LOD height-map shadows retain shared darkness, overlap and range fade");
                                }
                            }
                        }
                        GL.ActiveTexture(TextureUnit.Texture1); GL.BindTexture(TextureTarget.Texture2D, nativeTextures[1]);
                        GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, 1, 1, PixelFormat.Rgba, PixelType.Float, new[] { .4f, .5f, .3f, 1f });
                        Console.WriteLine($"PASS ChunkLOD SSAO={ssao} actual height-map shadow visibility, shared strength, maximum overlapping darkness, retained distance fade");
                    }
                    // A submerged camera sees only twenty blocks of water,
                    // then a distant outdoor LOD surface in clear air. Real
                    // native depth must win over the provider's compressed Z.
                    // Keep the native range truthful: the 600-block receiver is
                    // beyond its 384-block exclusion, and the LOD endpoint is 4096.
                    frame[4] = 0; frame[13] = 1; frame[8] = 512;
                    GL.BindBuffer(BufferTarget.UniformBuffer, ubo); GL.BufferData(BufferTarget.UniformBuffer, frame.Length * 4, frame, BufferUsageHint.DynamicDraw);
                    Setup(program, 1, chunkLod ? 2048 : 4096);
                    GL.UseProgram(program); Uniform(program, "chunkLodFaceLighting", 0);
                    Setup(terrainReference, 1, 4096);
                    GL.UseProgram(terrainReference);
                    GL.Uniform3(GL.GetUniformLocation(terrainReference, "probeMaterial"), chunkLod ? new Vector3(.4f,.5f,.3f) : Vector3.One);
                    Uniform(terrainReference, "probeFaceShade", chunkLod ? 1f : .5f);
                    float interfaceDepth = .5f + .5f * (-projectionA + projectionB / 20);
                    GL.ActiveTexture(TextureUnit.Texture0); GL.BindTexture(TextureTarget.Texture2D, liquid);
                    GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, 1, 1, PixelFormat.Rgba, PixelType.Float, new[] { interfaceDepth, 1f, 1f, 1f });
                    GL.BindTexture(TextureTarget.Texture2D, nativeTextures[0]);
                    frame[10] = frame[35] = 1;
                    GL.BindBuffer(BufferTarget.UniformBuffer, ubo); GL.BufferData(BufferTarget.UniformBuffer, frame.Length * 4, frame, BufferUsageHint.DynamicDraw);
                    float[] wet = Read(program, 600);
                    float[] nativeWet = Read(terrainReference, 600);
                    // The truthful native range starts boundary haze before the
                    // LOD cutoff. Compare with native terrain's complete water+air
                    // transport instead of assuming a fog-free dry receiver.
                    for (int c = 0; c < 3; c++) {
                        Check(Math.Abs(wet[c] - nativeWet[c]) < .001f, "compressed LOD projection matches native terrain at the twenty-block water exit");
                    }
                    // Negative control: using the compressed raster depth must
                    // fail this same water test, proving the reference is sensitive
                    // to a lost native-depth handoff rather than matching trivially.
                    string wrongDepthFragment = fragment.Replace("drtNativeReceiverDepth(worldPos.xyz, gl_FragCoord.z)", "gl_FragCoord.z", StringComparison.Ordinal);
                    Check(wrongDepthFragment != fragment, "water negative control replaces the native-depth call");
                    int wrongDepth = ProbeShader.Program((ShaderType.VertexShader, synthetic), (ShaderType.FragmentShader, wrongDepthFragment));
                    Setup(wrongDepth, 1, chunkLod ? 2048 : 4096);
                    float[] compressedWet = Read(wrongDepth, 600);
                    Check(Enumerable.Range(0,3).Any(c => Math.Abs(compressedWet[c]-nativeWet[c]) > .01f), "water reference detects incorrect compressed receiver depth");
                    // State guards must capture a live program, not an active
                    // program pending deletion after this negative control.
                    GL.UseProgram(terrainReference);
                    GL.DeleteProgram(wrongDepth); bindings.Reload();
                    frame[10] = frame[35] = 0; frame[8] = 512;
                    GL.ActiveTexture(TextureUnit.Texture0); GL.BindTexture(TextureTarget.Texture2D, liquid);
                    GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, 1, 1, PixelFormat.Rgba, PixelType.Float, new[] { 1f, 1f, 1f, 1f });
                    GL.BindTexture(TextureTarget.Texture2D, nativeTextures[0]);
                    GL.DeleteProgram(program);
                    Console.WriteLine($"PASS {(chunkLod ? "ChunkLOD 1.2.0-dev.6" : "Farseer 1.4.0")} actual shader compile/link, native uniform keys/fallback, SSAO={ssao}, day/night/clear/dense sky merge, opaque alpha/forward glow, native water-exit depth, real binder restoration");
                }
            }
            if (File.Exists(Path.Combine(references, "lodterrain.fsh")))
            {
                // Use the same real binder/resources as the established LOD probes.
                bindings.Reload();
                DistantVistasProbe.Run(references, Expand, frame, ubo, fbo,
                    p => { bindings.Reload(); bind.Invoke(bindings, new object[] { p }); },
                    () => bindings.Restore(), Read, SharedSky);
            }
            Check(GL.GetError() == ErrorCode.NoError, "LOD probe GL error");
        } finally {
            bindings.Restore(); GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0); GL.BindVertexArray(0);
            GL.BindBufferBase(BufferRangeTarget.UniformBuffer, 10, 0);
            for (int i = 0; i < 14; i++) GL.BindSampler(i, 0);
            GL.DeleteSampler(sentinel); GL.DeleteProgram(reference); GL.DeleteProgram(terrainReference); GL.DeleteBuffer(ubo); GL.DeleteBuffer(oldUbo);
            GL.DeleteFramebuffer(fbo); GL.DeleteVertexArray(vao); sky.Dispose();
            foreach (int texture in outputs.Concat(nativeTextures).Concat(new[] { liquid, sentinelTexture, volume, tint })) GL.DeleteTexture(texture);
        }
    }
}
