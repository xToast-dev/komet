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

Download the release zip and drop it into your `Mods` folder — that's it. Two places
carry it, and both publish the same SHA-256: the ModDB page and the
[GitHub releases](https://github.com/xToast-dev/komet/releases), where the checksum is
written by the build job rather than typed in by hand.
`sha256sum Komet_v<version>.zip` (PowerShell: `Get-FileHash`) proves that what you
downloaded is what was uploaded — see [reproducible builds](#reproducible-builds) for
what else that number is good for.

In game:

```
F7               toggle the performance HUD
.komet           the same numbers, compact, in chat
.komet report    full diagnostic block written to client-main.log
.komet safemode  every optimisation that changes what is drawn, off at once —
                 settles in seconds whether a visual glitch comes from this mod
```

The HUD and the `.komet` replies follow the game language; English and German ship with
the mod. Logs, reports and hitch lines stay English on purpose: they end up in bug
reports and have to be readable by whoever helps you.

## Building from source

### Prerequisites

| Requirement | Notes |
|---|---|
| .NET SDK 10.0.111 | pinned in `global.json` — a different patch level compiles a different assembly, which is what would break reproducibility |
| Vintage Story 1.22.x | the game assemblies are the compile references; 1.22.7 is the verified reference |
| Linux + bash | `build.sh`, the verify harness (`/proc`) and the bench target Linux; on other platforms `dotnet build` of the individual projects works with `-p:VsInstall=<game dir>` |
| `python3` | only for `./build.sh release` (packs the zips reproducibly) |

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
`KOMET_SKIP_BENCH=1` leaves the benchmark out: it gates nothing and dominates the
runtime on a shared machine.

### Reproducible builds

`./build.sh release` produces the same bytes from the same commit, on any machine — so
the checksum on a download page can be recomputed instead of merely trusted. Three
things had to be pinned for that, each one found by measuring rather than assuming: the
build stamp comes from the commit instead of the clock, the SDK version is fixed in
`global.json`, and the zip is stored rather than compressed (identical data does not
give identical deflate streams across zlib implementations). The last one costs about
2.5x download size. Building from a dirty working tree voids the promise and says so —
the hash then belongs to no commit.

### Continuous integration

Every push to `main` or `nightly` builds both mods on GitHub Actions, runs the full
patch and behaviour suite against the real game assemblies, and attaches the packaged
zips and their checksums to the run. `main` produces a **draft** release tagged
`v<version>` for a human to look at and publish; other branches publish a
**prerelease** tagged `preview-<sha>`, of which only the newest few are kept.
The game assemblies CI compiles against live in `.github/vintagestory/`, with the kind
permission of Anego Studios; that folder's README lists them with their checksums.

## Project structure

```
KometModSystem.cs   loading, config, the .komet command
Culling/            visibility: frustum sweep, occlusion pass, ray traversal
Runtime/            threads, budgets, pools, samplers under the patches
Guard/              self-check: foreign patches, engine fingerprint, task codes
Patches/            Harmony entry points, one file per subsystem
Measure/            measurement code (HUD, frame stats, hitch log) — compiled into
                    both mods so their numbers mean exactly the same thing
assets/komet/lang/  en.json, de.json — the HUD and the .komet replies in the
                    player's language
KometBaseline/      the same HUD with none of the optimisations, a measuring stick
                    for vanilla; stands down when Komet is active
verify/             patch + behaviour checks against the real game assemblies
bench/              equivalence + throughput benchmark
.github/            the build workflow, and the game assemblies it compiles against
moddb/              the ModDB page description and changelogs (paste-ready HTML)
docs/TECHNIK.md     the full technical write-up (German): what each patch does,
                    why, and what it measured
global.json         the pinned SDK version
build.sh            build, verify, bench, deploy, release
```

Folder and namespace line up: `Culling/` is `Komet.Culling`, `Measure/` is
`Komet.Measure`, the root is `Komet`.

## Documentation

- [CHANGELOG.md](CHANGELOG.md) — what changed per release, and why.
- [docs/TECHNIK.md](docs/TECHNIK.md) (German) — the complete technical documentation.
  It explains every optimisation with the vanilla call path it replaces, the measured
  numbers, and the failure modes that shaped the design — including the things that
  were deliberately *not* done.
