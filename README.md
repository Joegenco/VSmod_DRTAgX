# DRT AgX

DRT AgX is a client-side graphics mod for Vintage Story. It adds an AgX display
transform, HDR bloom and exposure, atmospheric lighting and fog, and shadows for
placed and moving lights through the game's existing rendering pipeline.

Current version: **2.0.2**. Author: **Joegen**.

## Requirements

- Vintage Story **1.22.7**.
- SheyderMod **1.1.3** installed in the client Mods directory.
- A graphics driver supporting **OpenGL 4.3 core**.
- The **.NET 10 SDK** to build from source.

Engine and dependency binaries are read from your own game installation and
are not distributed in this repository.

## Build

Set `VINTAGE_STORY` to the installation directory containing `VintagestoryAPI.dll`
and `Lib/`, then build the solution:

```powershell
# PowerShell: replace this path with your installation.
$env:VINTAGE_STORY = 'C:\path\to\Vintagestory'
dotnet build DRTAgX.sln -c Release
```

```sh
# Linux/macOS: replace this path with your installation.
export VINTAGE_STORY='/path/to/vintagestory'
dotnet build DRTAgX.sln -c Release
```

The mod output is `DRTAgX/bin/Release/Mods/mod/`. Ordinary builds copy the shader
and texture assets alongside the DLL on every platform.

To create an installable ZIP and SHA-256 checksum:

```powershell
./build.ps1
```

```sh
sh ./build.sh
```

The packaging command restores Cake build dependencies from NuGet and writes
`Releases/drtagx_2.0.2.zip` and its checksum. It packages only the mod DLL,
metadata, and runtime assets. Existing archives are preserved.

## Install and configure

Copy the generated ZIP into your Vintage Story data directory's `Mods/` folder,
alongside SheyderMod, and restart the client. Avoid installing multiple DRT AgX
versions together.

Press **O** to open the mod settings. Settings are saved in
`ModConfig/DRTAgX.json` in the game's data directory.

## Source layout

| Directory | Responsibility |
| --- | --- |
| `DRTAgX/Core/` | Lifecycle, settings, and moving/entity light discovery |
| `DRTAgX/Atmosphere/` | Atmospheric sky, ambient lighting, fog, and LOD integration |
| `DRTAgX/HDR/` | HDR scene buffers, bloom, exposure, and final composition |
| `DRTAgX/Lighting/` | Placed light indexing, GPU tiles, and shader bindings |
| `DRTAgX/Shadows/` | Moving and placed light shadow rendering and caches |
| `DRTAgX/Rendering/` | Native rendering integration, uniforms, and profiling |
| `DRTAgX/UI/` | In-game configuration dialog |
| `DRTAgX/assets/` | Runtime GLSL shaders and textures |
| `CakeBuild/` | Release packaging and archive verification |
| `Tests/` | CPU checks and OpenGL regression harnesses |

See [Tests/README.md](Tests/README.md) for check commands. C# changes require a
rebuild and client restart; shader changes can use the game's shader reload.
Windows developers may opt into live source assets with
`dotnet build DRTAgX/DRTAgX.csproj -c Debug -p:UseAssetJunction=true` in a fresh
output directory. The build preserves any existing directory or unexpected link.

[AGENTS.md](AGENTS.md) provides a few optional instructions for coding assistants.

## License

[WTFPL v2](LICENSE): do whatever the fuck you want with this project, except
the contents of `DRTAgX/assets/sheydermod/` (packaged as `assets/sheydermod/`).
Those contents are excluded and retain their original licensing terms and
copyright holders' permissions.
