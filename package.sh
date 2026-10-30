#!/usr/bin/env bash
# Builds the plugin and produces Thunderstore and Nexus zips in ./dist
# Usage: ./package.sh        (SKIP_BUILD=1 ./package.sh to reuse an existing build)
set -euo pipefail
cd "$(dirname "$0")"

NAME=$(python3 -c "import json;print(json.load(open('manifest.json'))['name'])")
VERSION=$(python3 -c "import json;print(json.load(open('manifest.json'))['version_number'])")
PLUGIN_VERSION=$(grep -oP '"\K[0-9]+\.[0-9]+\.[0-9]+(?="\)\])' Plugin.cs | head -n1)
DLL="bin/Release/net472/SulfurNoGore.dll"

if [ "$VERSION" != "$PLUGIN_VERSION" ]; then
  echo "Version mismatch: manifest.json=$VERSION, Plugin.cs=$PLUGIN_VERSION" >&2
  exit 1
fi
if ! grep -q "^## $VERSION" CHANGELOG.md; then
  echo "CHANGELOG.md has no entry for $VERSION" >&2
  exit 1
fi
for f in manifest.json README.md CHANGELOG.md LICENSE icon.png; do
  [ -f "$f" ] || { echo "Missing $f" >&2; exit 1; }
done

if [ -z "${SKIP_BUILD:-}" ]; then
  dotnet build -c Release
fi
[ -f "$DLL" ] || { echo "Missing $DLL (build failed?)" >&2; exit 1; }

rm -rf dist
mkdir -p dist/ts/plugins "dist/nexus/BepInEx/plugins/$NAME"

# Thunderstore: manifest, README, CHANGELOG, icon at the root, DLL under plugins/
cp manifest.json README.md CHANGELOG.md LICENSE icon.png dist/ts/
cp "$DLL" dist/ts/plugins/
(cd dist/ts && zip -qr "../${NAME}-${VERSION}-thunderstore.zip" .)

# Nexus: extract-into-game-folder layout
cp "$DLL" README.md LICENSE "dist/nexus/BepInEx/plugins/$NAME/"
(cd dist/nexus && zip -qr "../${NAME}-${VERSION}-nexus.zip" .)

rm -rf dist/ts dist/nexus
echo "Created:"
ls -1 dist
