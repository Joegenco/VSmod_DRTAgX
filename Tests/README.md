# Checks

These are console regression harnesses: run them with `dotnet run`, rather than
`dotnet test`. Set `VINTAGE_STORY` as described in the root README. Run the
commands below from the repository root.

## CPU checks

```sh
dotnet run --project Tests/PlacedLightTests -c Release
dotnet run --project Tests/PlacedLightTests -c Release -- --cache-regressions
dotnet run --project Tests/MovingLightTests -c Release
```

## Shader and GPU checks

GPU checks create a hidden OpenGL 4.3 window and require a working display and
graphics driver. On Windows, the projects copy the game's `glfw3.dll` to their
build output. Other platforms require their native GLFW library to be available.

Set `SHEYDER_MOD_ZIP` to your SheyderMod 1.1.3 archive. On Windows it defaults to
`%APPDATA%/VintagestoryData/Mods/SheyderMod 1.1.3.zip`.

```powershell
$env:SHEYDER_MOD_ZIP = 'C:\path\to\SheyderMod 1.1.3.zip'
```

```sh
export SHEYDER_MOD_ZIP='/path/to/SheyderMod 1.1.3.zip'
dotnet run --project Tests/ShaderVariants -c Release -- DRTAgX/assets
dotnet run --project Tests/ShaderVariants -c Release -- DRTAgX/assets --native-bootstrap
dotnet run --project Tests/MovingLightTests -c Release -- --gpu DRTAgX/assets
dotnet run --project Tests/PlacedLightGpu -c Release -- DRTAgX/assets/sheydermod/shaders/deferredlighting.fsh --release-safety
```

`PlacedLightGpu/Program.cs` lists additional focused modes. Some compatibility
modes accept paths to separately installed mods; provide those dependencies
explicitly. For the `--smaa-hdr` mode, set `SMAA_MOD_ZIP` to the VS-SMAA archive.
No dependency DLLs or archives are stored in source control.

The optional historical deferred-coverage comparison uses shader files from
`DRTAGX_BASELINE_DIR`; without that environment variable, it reports a skip.
Cloud checks can also compare `cloudvolumetric.fsh` from that directory if present.
Generated numerical reports go to the ignored `Tests/results/` directory.

The broad historical suites include lighting and atmosphere balance assertions
that may disagree with current artistic tuning. A successful build or focused
GPU check does not establish live-world appearance; report the exact commands
and any failures when validating a change.
