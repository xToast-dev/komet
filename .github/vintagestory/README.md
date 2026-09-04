# Game assemblies for the CI build

The four projects compile against Vintage Story's own assemblies. They are not on a public
NuGet feed, so the ones the build needs live here, with the kind permission of Anego Studios.

`.github/workflows/build.yml` points `VS_INSTALL` at this folder, and every `.csproj` resolves
its references from `$(VsInstall)`. Locally nothing changes: `VS_INSTALL` defaults to
`/opt/vintagestory`, i.e. a normal game installation.

## Expected layout

Copy these 21 files out of the game folder, keeping the subfolders:

```
.github/vintagestory/
  VintagestoryAPI.dll
  VintagestoryLib.dll
  Lib/
    0Harmony.dll
    cairo-sharp.dll
    Newtonsoft.Json.dll
    OpenTK.Core.dll
    OpenTK.Graphics.dll
    OpenTK.Mathematics.dll
    OpenTK.Windowing.Common.dll
    OpenTK.Windowing.Desktop.dll
    OpenTK.Windowing.GraphicsLibraryFramework.dll
    protobuf-net.dll
    SkiaSharp.dll
    Mono.Cecil.dll
    MonoMod.Backports.dll
    MonoMod.Core.dll
    MonoMod.Iced.dll
    MonoMod.ILHelpers.dll
    MonoMod.Utils.dll
  Mods/
    VSEssentials.dll
    VSSurvivalMod.dll
```

The six MonoMod/Cecil assemblies are not referenced by any `.csproj` either - they are what
`0Harmony.dll` itself needs. MSBuild finds them because they sit next to it and copies them to
the output; without them the build succeeds and the verify run then dies on the first
`AccessTools.FieldRefAccess` with a missing-assembly exception.

The two under `Mods/` are not compile-time references: the verify suite loads them at runtime
(`Assembly.LoadFrom`) because the entity and firepit patches resolve their targets by name from
the content mods. Without them those checks fail rather than being skipped.

## Which version

The version here decides what CI verifies against, and it must be the version
`Guard/EngineFingerprint.cs` was generated for - currently **1.22.7**. The last check in the
suite hashes the IL of every patched engine method and compares it with that file, so a
mismatched drop fails the build by name instead of silently verifying against the wrong engine.

After a game update: replace the files here, run `./build.sh fingerprint` locally against the
same version, and commit both together.
