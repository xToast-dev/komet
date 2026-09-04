# Komet

Client-side performance mod for **Vintage Story 1.22.0+** (built and verified against
1.22.7), based on Harmony patches.
It reduces main-thread CPU time in the render loop — visibility sweep, occlusion
culling, draw-range merging, chunk loading, shadows, VRAM pooling — and ships an
F7 performance HUD with per-renderer timings, a hitch log and a built-in stress test.
Gains grow with view distance.

All patches are applied in memory when the game loads. No game files are modified,
no assets are replaced; remove the mod and the game is untouched.

## Installation (players)

Download the release zip from the ModDB page and drop it into your `Mods` folder —
that's it. The page lists a SHA-256 for the file; `sha256sum Komet_v<version>.zip`
(PowerShell: `Get-FileHash`) proves that what you downloaded is what was uploaded.
In game:

```
F7               toggle the performance HUD
.komet           the same numbers, compact, in chat
.komet report    full diagnostic block written to client-main.log
```

## Building from source

### Prerequisites

| Requirement | Notes |
|---|---|
| .NET SDK 10.0 | `dotnet --version` |
| Vintage Story 1.22.x | the game assemblies are the compile references; 1.22.7 is the verified reference |
| Linux + bash | `build.sh`, the verify harness (`/proc`) and the bench target Linux; on other platforms `dotnet build` of the individual projects works with `-p:VsInstall=<game dir>` |
| `bsdtar` (libarchive) | only for `./build.sh release` |

The game location defaults to `/opt/vintagestory` and can be overridden everywhere
via the environment variable `VS_INSTALL` (or the MSBuild property `VsInstall`).
The game data folder defaults to `~/.config/VintagestoryData` (`VS_DATA` / `VsData`).
Nothing else outside this repository is required.

### Build

```bash
./build.sh            # build both mods + run all checks (patch application, behaviour, equivalence, benchmark)
./build.sh deploy     # the above, then copy Komet.dll + KometBaseline.dll into $VS_DATA/Mods
./build.sh bench      # just the throughput/equivalence benchmark
./build.sh preview    # print the HUD text without starting the game
./build.sh config     # regenerate dist/komet.json from the real config class
./build.sh release    # full checks, then pack dist/Komet_v<version>.zip for ModDB
                      # (+ a .sha256 next to each zip, to publish on the download page)
```

`./build.sh` with no argument is the full check suite; a release candidate is
whatever survived it. The `verify` harness applies the real Harmony patches to the
real game assemblies, forces JIT compilation and checks behaviour — without
starting the game. The `bench` harness proves the optimised culling produces
byte-identical results to vanilla before measuring throughput.

## Project structure

```
KometModSystem.cs  loading, config, the .komet command
Culling/           visibility: frustum sweep, occlusion pass, ray traversal
Runtime/           threads, budgets, pools, samplers under the patches
Guard/             self-check: foreign patches, engine fingerprint, task codes
Patches/           Harmony entry points, one file per subsystem
assets/komet/lang/ en.json, de.json - the HUD and the .komet replies in the
                   player's language; logs and reports stay English
Measure/           measurement code (HUD, frame stats, hitch log) — compiled into
                   both mods so their numbers mean exactly the same thing
KometBaseline/          KometBaseline: the same HUD with none of the optimisations,
                   a measuring stick for vanilla; stands down when Komet is active
verify/            patch + behaviour checks against the real game assemblies
bench/             equivalence + throughput benchmark
moddb/             the ModDB page description (paste-ready HTML)
docs/TECHNIK.md    the full technical write-up (German): what each patch does,
                   why, and what it measured
build.sh           build, verify, bench, deploy, release
```

## Documentation

The complete technical documentation lives in [docs/TECHNIK.md](docs/TECHNIK.md)
(German). It explains every optimisation with the vanilla call path it replaces,
the measured numbers, and the failure modes that shaped the design — including the
things that were deliberately *not* done.
