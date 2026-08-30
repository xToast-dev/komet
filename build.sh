#!/usr/bin/env bash
# Build, verify and (optionally) deploy both mods.
#   ./build.sh            build both + run all checks
#   ./build.sh deploy     the above, then copy both dlls into VintagestoryData/Mods
#   ./build.sh bench      just the throughput benchmark
#   ./build.sh preview    print the HUD text without starting the game
#   ./build.sh config     regenerate dist/komet.json from the real config class
#   ./build.sh release    full checks, then pack dist/Komet_v<version>.zip for ModDB
set -euo pipefail
cd "$(dirname "$0")"

VS_INSTALL="${VS_INSTALL:-/opt/vintagestory}"
VS_DATA="${VS_DATA:-$HOME/.config/VintagestoryData}"
BASELINE="baseline"
# Mod and baseline get the same build stamp from one clock read, so a side by side
# comparison cannot end up with two builds that look a minute apart. An inherited value
# wins so the release target and its inner check run agree on one stamp too.
KOMET_BUILD="${KOMET_BUILD:-$(date +%y%m%d.%H%M)}"

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
  release)
    # A release candidate is whatever survived the full check suite - build, verify, bench.
    KOMET_BUILD="$KOMET_BUILD" "$0" check

    # modinfo.json is generated from AssemblyInfo.cs, never hand written: the attribute is
    # what the running mod reports as its version (HUD title), so the zip metadata cannot
    # disagree with the binary. The csproj Version must match too, or the two are drifting.
    VERSION="$(sed -n 's/.*Version = "\([^"]*\)".*/\1/p' src/AssemblyInfo.cs)"
    CSPROJ_VERSION="$(sed -n 's|.*<Version>\(.*\)</Version>.*|\1|p' Komet.csproj)"
    DESCRIPTION="$(sed -n 's/.*Description = "\([^"]*\)".*/\1/p' src/AssemblyInfo.cs)"
    if [[ -z "$VERSION" || "$VERSION" != "$CSPROJ_VERSION" ]]; then
      echo "FEHLER: Versionsdrift - AssemblyInfo.cs sagt '$VERSION', Komet.csproj sagt '$CSPROJ_VERSION'" >&2
      exit 1
    fi

    STAGE="dist/release-stage"
    rm -rf "$STAGE"
    mkdir -p "$STAGE"
    cp bin/Release/Komet.dll "$STAGE/"
    cp modicon.png "$STAGE/"
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
    (cd "$STAGE" && bsdtar -a -cf "../$ZIP" modinfo.json modicon.png Komet.dll)
    rm -rf "$STAGE"

    # The baseline ships as its own zip: it must be a SEPARATE mod entry so the mod
    # manager can disable Komet and enable the baseline independently - the whole
    # measuring-stick workflow depends on that. Same drift assert as the main mod.
    B_VERSION="$(sed -n 's/.*Version = "\([^"]*\)".*/\1/p' baseline/src/AssemblyInfo.cs)"
    B_CSPROJ_VERSION="$(sed -n 's|.*<Version>\(.*\)</Version>.*|\1|p' baseline/KometBaseline.csproj)"
    B_DESCRIPTION="$(sed -n 's/.*Description = "\([^"]*\)".*/\1/p' baseline/src/AssemblyInfo.cs)"
    if [[ -z "$B_VERSION" || "$B_VERSION" != "$B_CSPROJ_VERSION" ]]; then
      echo "FEHLER: Versionsdrift Baseline - AssemblyInfo sagt '$B_VERSION', csproj sagt '$B_CSPROJ_VERSION'" >&2
      exit 1
    fi
    STAGE="dist/release-stage"
    rm -rf "$STAGE"
    mkdir -p "$STAGE"
    cp baseline/bin/Release/KometBaseline.dll "$STAGE/"
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
    (cd "$STAGE" && bsdtar -a -cf "../$B_ZIP" modinfo.json modicon.png KometBaseline.dll)
    rm -rf "$STAGE"

    echo
    echo "== release candidate: dist/$ZIP + dist/$B_ZIP (v$VERSION b$KOMET_BUILD) =="
    bsdtar -tvf "dist/$ZIP"
    bsdtar -tvf "dist/$B_ZIP"
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
    dotnet build -c Release -v q --nologo -p:KometBuild="$KOMET_BUILD" -p:VsInstall="$VS_INSTALL"
    dotnet build "$BASELINE" -c Release -v q --nologo -p:KometBuild="$KOMET_BUILD" -p:VsInstall="$VS_INSTALL"
    echo
    echo "== patch + behaviour checks =="
    dotnet build verify -c Release -v q --nologo -p:VsInstall="$VS_INSTALL"
    dotnet verify/bin/Release/net10.0/KometVerify.dll
    echo
    echo "== equivalence + throughput =="
    dotnet build bench -c Release -v q --nologo -p:VsInstall="$VS_INSTALL"
    dotnet bench/bin/Release/net10.0/KometBench.dll
    ;;
esac
