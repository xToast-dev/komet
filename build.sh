#!/usr/bin/env bash
# Build, verify and (optionally) deploy both mods.
#   ./build.sh            build both + run all checks
#   ./build.sh deploy     the above, then copy both dlls into VintagestoryData/Mods
#   ./build.sh bench      just the throughput benchmark
#   ./build.sh preview    print the HUD text without starting the game
#   ./build.sh config     regenerate dist/komet.json from the real config class
#   ./build.sh fingerprint  regenerate Guard/EngineFingerprint.cs (IL hashes of every engine
#                           method the patches touch, against VS_INSTALL) - after a game update
#   ./build.sh release    full checks, then pack dist/Komet_v<version>.zip for ModDB,
#                           each zip with a .sha256 next to it to publish alongside it
# Environment: VS_INSTALL points at the game (default /opt/vintagestory), KOMET_SKIP_BENCH=1
# leaves out the throughput benchmark (CI: it gates nothing and dominates the runtime).
set -euo pipefail
cd "$(dirname "$0")"

VS_INSTALL="${VS_INSTALL:-/opt/vintagestory}"
VS_DATA="${VS_DATA:-$HOME/.config/VintagestoryData}"
BASELINE="KometBaseline"
# Mod and baseline get the same build stamp from one clock read, so a side by side
# comparison cannot end up with two builds that look a minute apart. An inherited value
# wins so the release target and its inner check run agree on one stamp too.
KOMET_BUILD="${KOMET_BUILD:-$(date +%y%m%d.%H%M)}"

# Deterministic zip. Three things had to stop moving, each one measured rather than assumed:
#
#   1. bsdtar (and Info-ZIP) write an extended-timestamp extra field carrying the file's ctime,
#      which nothing can set - two runs of the same source differed in exactly 8 bytes.
#      Python's zipfile writes only what it is asked for.
#   2. The entry timestamps, pinned to the commit (see the release target).
#   3. The compression. Identical data does NOT give identical deflate streams: the runner has
#      stock zlib, an Arch machine has zlib-ng, and the archives differed in every entry while
#      every entry's CONTENT was identical. Deflate output is not specified, so the only way to
#      make the container itself reproducible is to not compress at all. That costs about
#      2.5x download size (179 -> 447 KB) and buys a checksum anyone can recompute.
# Arguments: <stage dir> <zip> <epoch> <entry> [entry ...]
pack_zip() {
  python3 - "$@" <<'PY'
import os, sys, time, zipfile

stage, out, epoch = sys.argv[1], sys.argv[2], int(sys.argv[3])
stamp = time.gmtime(epoch)[:6]

def files(root, entry):
    path = os.path.join(root, entry)
    if os.path.isfile(path):
        yield entry
        return
    for dirpath, dirnames, filenames in os.walk(path):
        dirnames.sort()
        for name in sorted(filenames):
            yield os.path.relpath(os.path.join(dirpath, name), root)

with zipfile.ZipFile(out, "w", zipfile.ZIP_STORED) as z:
    for entry in sys.argv[4:]:
        for rel in files(stage, entry):
            info = zipfile.ZipInfo(rel.replace(os.sep, "/"), date_time=stamp)
            info.compress_type = zipfile.ZIP_STORED
            info.create_system = 3          # unix, so the mode below is read back as one
            info.external_attr = 0o644 << 16
            with open(os.path.join(stage, rel), "rb") as f:
                z.writestr(info, f.read())
PY
}

case "${1:-check}" in
  bench)
    dotnet build bench -c Release -v q --nologo
    exec dotnet bench/bin/Release/net10.0/KometBench.dll "${2:-}"
    ;;
  preview)
    dotnet build verify -c Release -v q --nologo -p:VsInstall="$VS_INSTALL"
    exec dotnet verify/bin/Release/net10.0/KometVerify.dll preview
    ;;
  config)
    dotnet build verify -c Release -v q --nologo -p:VsInstall="$VS_INSTALL"
    exec dotnet verify/bin/Release/net10.0/KometVerify.dll config "${2:-dist/komet.json}"
    ;;
  fingerprint)
    # Runs the whole check suite first (that is what applies every patch), then hashes the
    # patched engine methods. A failing check means an incomplete patch set - nothing is written.
    dotnet build verify -c Release -v q --nologo -p:VsInstall="$VS_INSTALL"
    exec dotnet verify/bin/Release/net10.0/KometVerify.dll fingerprint "${2:-Guard/EngineFingerprint.cs}"
    ;;
  release)
    # Reproducible: the same commit must produce the same bytes, so the sha256 printed at the
    # end can be recomputed by anyone from the same source. Two things have to stop moving.
    #
    # The build stamp, which normally is the minute of the build (that is what a dev build
    # wants), becomes the minute of the COMMIT - in UTC, so it does not depend on who builds
    # it where. A published artefact identifies its source, not its build machine.
    if git rev-parse --git-dir >/dev/null 2>&1; then
      KOMET_BUILD="${KOMET_BUILD_OVERRIDE:-$(TZ=UTC git log -1 --format=%cd --date=format-local:%y%m%d.%H%M)}"
      SOURCE_DATE_EPOCH="$(git log -1 --format=%ct)"
      if [[ -n "$(git status --porcelain)" ]]; then
        echo "WARNUNG: Arbeitsverzeichnis nicht sauber - der sha256 gehört dann zu keinem Commit" >&2
      fi
    else
      # no repository around (a source drop): fall back to the clock and say so
      SOURCE_DATE_EPOCH="$(date +%s)"
      echo "WARNUNG: kein git-Repo - Build ist nicht reproduzierbar" >&2
    fi
    export SOURCE_DATE_EPOCH
    # ... and the compilation itself: no pdb path in the debug directory, normalised source
    # paths (see the KometReproducible block in the csproj files).
    export KOMET_REPRODUCIBLE=true

    # A release candidate is whatever survived the full check suite - build, verify, bench.
    KOMET_BUILD="$KOMET_BUILD" "$0" check

    # modinfo.json is generated from AssemblyInfo.cs, never hand written: the attribute is
    # what the running mod reports as its version (HUD title), so the zip metadata cannot
    # disagree with the binary. The csproj Version must match too, or the two are drifting.
    VERSION="$(sed -n 's/.*Version = "\([^"]*\)".*/\1/p' AssemblyInfo.cs)"
    CSPROJ_VERSION="$(sed -n 's|.*<Version>\(.*\)</Version>.*|\1|p' Komet.csproj)"
    DESCRIPTION="$(sed -n 's/.*Description = "\([^"]*\)".*/\1/p' AssemblyInfo.cs)"
    if [[ -z "$VERSION" || "$VERSION" != "$CSPROJ_VERSION" ]]; then
      echo "FEHLER: Versionsdrift - AssemblyInfo.cs sagt '$VERSION', Komet.csproj sagt '$CSPROJ_VERSION'" >&2
      exit 1
    fi

    STAGE="dist/release-stage"
    rm -rf "$STAGE"
    mkdir -p "$STAGE"
    cp bin/Release/Komet.dll "$STAGE/"
    # '.komet alloctrace' attaches the runtime's own EventPipe to the process through the
    # diagnostics client library; pure managed, loaded from the mod folder like Komet.dll.
    for dep in Microsoft.Diagnostics.NETCore.Client Microsoft.Extensions.Logging.Abstractions Microsoft.Extensions.DependencyInjection.Abstractions; do
      cp "bin/Release/$dep.dll" "$STAGE/"
    done
    cp modicon.png "$STAGE/"
    # assets/komet/lang/{en,de}.json - the HUD and the .komet replies in the player's
    # language. Logs stay English whatever is in here (see Measure/Loc.cs).
    cp -r assets "$STAGE/"
    cat > "$STAGE/modinfo.json" <<EOF
{
  "type": "code",
  "name": "Komet",
  "modid": "komet",
  "version": "$VERSION",
  "description": "$DESCRIPTION",
  "authors": [ "xToast" ],
  "side": "Universal",
  "requiredOnClient": false,
  "requiredOnServer": false,
  "dependencies": { "game": "1.22.0" }
}
EOF
    ZIP="Komet_v${VERSION}.zip"
    # zip stores a modification time per entry, so the staged copies would carry the moment
    # they were copied. Pinned to the commit's timestamp, in UTC, and the entries are listed
    # in a fixed order - that is what makes two runs produce the same archive.
    pack_zip "$STAGE" "dist/$ZIP" "$SOURCE_DATE_EPOCH" modinfo.json modicon.png Komet.dll \
      Microsoft.Diagnostics.NETCore.Client.dll Microsoft.Extensions.Logging.Abstractions.dll \
      Microsoft.Extensions.DependencyInjection.Abstractions.dll assets
    rm -rf "$STAGE"

    # The baseline ships as its own zip: it must be a SEPARATE mod entry so the mod
    # manager can disable Komet and enable the baseline independently - the whole
    # measuring-stick workflow depends on that. Same drift assert as the main mod.
    B_VERSION="$(sed -n 's/.*Version = "\([^"]*\)".*/\1/p' KometBaseline/AssemblyInfo.cs)"
    B_CSPROJ_VERSION="$(sed -n 's|.*<Version>\(.*\)</Version>.*|\1|p' KometBaseline/KometBaseline.csproj)"
    B_DESCRIPTION="$(sed -n 's/.*Description = "\([^"]*\)".*/\1/p' KometBaseline/AssemblyInfo.cs)"
    if [[ -z "$B_VERSION" || "$B_VERSION" != "$B_CSPROJ_VERSION" ]]; then
      echo "FEHLER: Versionsdrift Baseline - AssemblyInfo sagt '$B_VERSION', csproj sagt '$B_CSPROJ_VERSION'" >&2
      exit 1
    fi
    STAGE="dist/release-stage"
    rm -rf "$STAGE"
    mkdir -p "$STAGE"
    cp KometBaseline/bin/Release/KometBaseline.dll "$STAGE/"
    cp modicon.png "$STAGE/"
    cat > "$STAGE/modinfo.json" <<EOF
{
  "type": "code",
  "name": "Komet Baseline",
  "modid": "kometbase",
  "version": "$B_VERSION",
  "description": "$B_DESCRIPTION",
  "authors": [ "xToast" ],
  "side": "Client",
  "requiredOnClient": false,
  "requiredOnServer": false,
  "dependencies": { "game": "1.22.0" }
}
EOF
    B_ZIP="KometBaseline_v${B_VERSION}.zip"
    pack_zip "$STAGE" "dist/$B_ZIP" "$SOURCE_DATE_EPOCH" modinfo.json modicon.png KometBaseline.dll
    rm -rf "$STAGE"

    # One checksum per published file, written next to it and printed here. It is meant to
    # go on the ModDB page so a downloader can prove that the zip they got is the zip that
    # was uploaded - the only thing a hash can honestly promise.
    #
    # It identifies the COMMIT: build stamp, compilation and archive timestamps are all
    # derived from it, so anyone who checks out the same commit and runs this target gets the
    # same bytes and the same number. A dirty working tree breaks that and says so above.
    (cd dist && sha256sum "$ZIP" > "$ZIP.sha256" && sha256sum "$B_ZIP" > "$B_ZIP.sha256")

    echo
    echo "== release candidate: dist/$ZIP + dist/$B_ZIP (v$VERSION b$KOMET_BUILD) =="
    python3 -m zipfile -l "dist/$ZIP"
    python3 -m zipfile -l "dist/$B_ZIP"
    # The changelog for the ModDB page carries the checksum, and the checksum cannot live in
    # the repository: writing it into a tracked file would change the commit and with it the
    # checksum. So moddb/ holds the template and the finished text is written here, next to
    # the files it describes.
    CHANGELOG=""
    for candidate in "moddb/changelog-${VERSION}.html" "moddb/changelog-${VERSION}.md"; do
      [[ -f "$candidate" ]] && CHANGELOG="$candidate"
    done
    if [[ -n "$CHANGELOG" ]]; then
      sed -e "s|{{SHA256_KOMET}}|$(cut -d' ' -f1 < "dist/$ZIP.sha256")|g" \
          -e "s|{{SHA256_BASELINE}}|$(cut -d' ' -f1 < "dist/$B_ZIP.sha256")|g" \
          -e "s|{{COMMIT}}|$(git rev-parse --short=7 HEAD 2>/dev/null || echo unknown)|g" \
          "$CHANGELOG" > "dist/$(basename "$CHANGELOG")"
      FILLED="dist/$(basename "$CHANGELOG")"
    else
      FILLED=""
    fi

    echo
    echo "== sha256 (for the ModDB page) =="
    cat "dist/$ZIP.sha256" "dist/$B_ZIP.sha256"
    if [[ -n "$FILLED" ]]; then
      echo "changelog with those checksums filled in: $FILLED"
    else
      # Silence here would mean uploading a page whose checksum belongs to an older build.
      echo "note: no moddb/changelog-${VERSION}.{html,md} - nothing to fill the checksums into"
    fi
    ;;
  deploy)
    # Die Mod hiess bis 1.51.8 VsPerf - eine liegengebliebene alte DLL wuerde doppelt laden.
    rm -f "$VS_DATA/Mods/VsPerf.dll" "$VS_DATA/Mods/VsPerfBaseline.dll"
    dotnet build -c Release -v q --nologo -p:KometBuild="$KOMET_BUILD" -p:DeployToGame=true -p:VsInstall="$VS_INSTALL" -p:VsData="$VS_DATA"
    dotnet build "$BASELINE" -c Release -v q --nologo -p:KometBuild="$KOMET_BUILD" -p:DeployToGame=true -p:VsInstall="$VS_INSTALL" -p:VsData="$VS_DATA"
    echo "deployed Komet.dll + KometBaseline.dll (b$KOMET_BUILD) -> $VS_DATA/Mods"
    ;;
  *)
    echo "== building both mods (b$KOMET_BUILD) =="
    REPRO="-p:KometReproducible=${KOMET_REPRODUCIBLE:-false}"
    dotnet build -c Release -v q --nologo -p:KometBuild="$KOMET_BUILD" -p:VsInstall="$VS_INSTALL" $REPRO
    dotnet build "$BASELINE" -c Release -v q --nologo -p:KometBuild="$KOMET_BUILD" -p:VsInstall="$VS_INSTALL" $REPRO
    echo
    echo "== patch + behaviour checks =="
    dotnet build verify -c Release -v q --nologo -p:VsInstall="$VS_INSTALL"
    dotnet verify/bin/Release/net10.0/KometVerify.dll
    # The benchmark measures throughput, it does not gate anything (it has no failing
    # exit code). On a shared CI runner its numbers are noise and it is by far the longest
    # step, so KOMET_SKIP_BENCH=1 leaves it out there - the checks above are the gate.
    if [[ "${KOMET_SKIP_BENCH:-0}" != "1" ]]; then
      echo
      echo "== equivalence + throughput =="
      dotnet build bench -c Release -v q --nologo -p:VsInstall="$VS_INSTALL"
      dotnet bench/bin/Release/net10.0/KometBench.dll
    fi
    ;;
esac
