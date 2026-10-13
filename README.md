# DRTAgX 2.1.1

Client-side graphics mod for **Vintage Story 1.22.7**, by **Joegen**.
It builds on the game's rendering and SheyderMod rather than replacing the engine.

- AgX tone mapping, HDR bloom and automatic exposure.
- Atmospheric sky, ambient lighting, clouds, room-aware fog and underwater light absorption.
- Cached placed-light lighting and shadows, prioritizing visible surfaces and local edits.
- Shadows from held, dropped and moving lights, including animated entities.
- Lighting fixes for foliage, particles, transparent materials and night vision.
- Optional compatibility with ChunkLod, Distant Vistas, GhostChunks, SMAA, RainCurtains,
  AstraTerra, AstraExtera and VS Roofing.

Install DRTAgX alongside **SheyderMod 1.1.3**. Press **O** for DRTAgX settings and
**F8** for Sheyder's other effects. Deferred rendering stays enabled in normal play.
Versioned defaults handle both mods' settings; outdated settings reset on upgrade.
Performance mode trades shadow/effect detail for speed. The hardware target is
an OpenGL 4.6-capable GPU with an 8 GB VRAM budget.

## Build from source

Install the **.NET 10 SDK** and set `VINTAGE_STORY` to your game installation
(the directory containing `VintagestoryAPI.dll`, `Lib/` and `Mods/`).

```powershell
$env:VINTAGE_STORY = 'C:\path\to\Vintagestory'
dotnet build DRTAgX.sln -c Release
```

```sh
export VINTAGE_STORY='/path/to/vintagestory'
dotnet build DRTAgX.sln -c Release
```

Output: `DRTAgX/bin/Release/Mods/mod/`. Assets are copied alongside the DLL on every
platform. Copy that folder into your client Mods directory, or ZIP its contents
with `modinfo.json` at the archive root. Remove any other DRTAgX version first.
The game supplies all referenced libraries; they are not included in this repo.

## Find your way around

- `Core/`: lifecycle, settings, dependency defaults and moving-light sources.
- `Atmosphere/`: sky, ambient light, fog and distant-terrain/weather compatibility.
- `HDR/`: scene buffers, bloom, exposure and final composition.
- `Lighting/` and `Shadows/`: placed-light discovery/caches and moving shadow maps.
- `Rendering/`: native hooks, deferred composition, GPU resources and uniforms.
- `UI/`: in-game settings. `assets/`: GLSL and textures.

These folders are inside `DRTAgX/`. C# changes need a rebuild and client restart;
shaders can use the game's **Alt+F1** reload. This source tree contains the Release
implementation, without tests, probes or private development notes.

## License

[WTFPL v2](LICENSE): do whatever the fuck you want with the project, except the
contents of `DRTAgX/assets/sheydermod/`. Those retain their original licensing
terms and copyright holders' permissions. Fork it or use snippets freely within
that scope.
