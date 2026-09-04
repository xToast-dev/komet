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

## What is actually in here

Copied from a 1.22.7 installation (`/opt/vintagestory`) on 2026-09-05 and compared against the
source afterwards: all 21 files byte-identical. 18 MB in total, three quarters of it
OpenTK.Graphics, VSSurvivalMod, VintagestoryLib and VintagestoryAPI.

The checksums of the files as committed. Run this from inside `.github/vintagestory` to confirm
that what is here is still what was verified - it prints nothing and exits 0 when everything
matches, and names every file that does not:

```bash
sha256sum --check <<'EOF'
64b8b8f926efe5b926b832aaedb93e73de34af2e84d9f8f5d51c6fb70ab61149  Lib/0Harmony.dll
a8d3645f639a9d02b1f0220b8e24135f799868b30a253cefa9769918eb2c8af0  Lib/cairo-sharp.dll
831dca77470d85cb6ffbea3072daa7a3df5b7c9fcfd9c3f43674a9be99d4bfcf  Lib/Mono.Cecil.dll
ac3f32bfd44aab83abf71abdff6dde548d57b7c0f8a1fe6d8964e348b4eeafb1  Lib/MonoMod.Backports.dll
c5a4827d583d4c0c2c46edbada84f20a0e04f8ffd9c5fd12bf45ffec6cc05059  Lib/MonoMod.Core.dll
580633335b974b49633425ba642e8d64efb5f461c729138f2f87808c590417bf  Lib/MonoMod.Iced.dll
fdd0e3538340fd78b8f521e62b8cac1ebb7683ac1f27f6aaedfc1044b14bf4bb  Lib/MonoMod.ILHelpers.dll
fc61ea42e74933a7fbc6299418bd6d2b434c5d8320e538aa722377eb56eceac6  Lib/MonoMod.Utils.dll
a28c251dfe36d881e9e2462e171441b8b0ec156fe3f452602c9149b1b9efe05b  Lib/Newtonsoft.Json.dll
00e86fd2aa3ec0a518294ac210f2377078570120dfc22889654843e5ccb9e71c  Lib/OpenTK.Core.dll
5b57957ca9e4c5f7bfb7ec495b991e6b88cfac52863a19e405492ab8c33cb1d2  Lib/OpenTK.Graphics.dll
10172a71d9590960bf594fb397e65ee5ebb9fff5d08bd5d358ab047b0a6efff0  Lib/OpenTK.Mathematics.dll
2f0c74b795c664db5ca8a4d8d426efafda2fa48986ae5e2e0ee62dfaf1da660e  Lib/OpenTK.Windowing.Common.dll
cd759959d5924a44e5d943693ec63dd65dacbca5fe209546df1f932a0e3ae8ae  Lib/OpenTK.Windowing.Desktop.dll
b54ec149aa11635dcd09bcc2f0bd2acbd6584f67303b21065935e566d00605cb  Lib/OpenTK.Windowing.GraphicsLibraryFramework.dll
31e6ab89f21dac236bd63dc6f01c8fa9d77b9b3c51e34fb717d4b461ae084e52  Lib/protobuf-net.dll
3df1b742b4b2cdbe2b29b676123be4ab75b4078751c27fc171d3de9d49767be4  Lib/SkiaSharp.dll
a28565c5c9181f8cc84b98a2b7457ab824b8ecb7763714448da1f7c245aecd6e  Mods/VSEssentials.dll
d67b48a321403b2052b33c7d0caa99611f92350ffac73ea72672901dd87ccd7a  Mods/VSSurvivalMod.dll
034283e7e9d98eae45ee63005576fd89badc3c995b531cc4c3fe46f3eb2d3296  VintagestoryAPI.dll
e08f22b493b92feaf0aaeb79d22437ea0f7efc38aa7f72a04a47f98bc0e40df0  VintagestoryLib.dll
EOF
```

The same block also checks a fresh drop before you copy it in: run it in the game folder
(`cd /opt/vintagestory`) instead. A line that fails there is a file whose version differs from
the one this repository was verified against - which is exactly the case where
`Guard/EngineFingerprint.cs` has to be regenerated too (see below).

## Which version

The version here decides what CI verifies against, and it must be the version
`Guard/EngineFingerprint.cs` was generated for - currently **1.22.7**. The last check in the
suite hashes the IL of every patched engine method and compares it with that file, so a
mismatched drop fails the build by name instead of silently verifying against the wrong engine.

After a game update: replace the files here, run `./build.sh fingerprint` locally against the
same version, and commit both together.
