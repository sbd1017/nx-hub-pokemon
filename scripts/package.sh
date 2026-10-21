#!/usr/bin/env bash
# Publishes a self-contained trade bot and packs a release folder + tarball.
#
#   scripts/package.sh                 # linux-arm64 (Raspberry Pi 5)
#   scripts/package.sh linux-x64
#
# Output: artifacts/nxhub-pokemon-<version>-<rid>.tar.gz
set -euo pipefail

RID="${1:-linux-arm64}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props")"
NAME="nxhub-pokemon-$VERSION-$RID"
OUT="$ROOT/artifacts/$NAME"

if [[ ! -f "$ROOT/external/SysBot.NET/SysBot.Pokemon/SysBot.Pokemon.csproj" ]]; then
  echo "SysBot.NET is missing. Run: git submodule update --init" >&2
  exit 1
fi

echo "Publishing for $RID..."
rm -rf "$OUT"
dotnet publish "$ROOT/src/NxHub.Pokemon" -c Release -r "$RID" --self-contained true -o "$OUT/app"
rm -f "$OUT/app/"*.pdb

cp "$ROOT/deploy/install.sh" "$OUT/install.sh"
cp "$ROOT/deploy/systemd/nxhub-pokemon.service" "$OUT/nxhub-pokemon.service"
cp "$ROOT/LICENSE" "$ROOT/THIRD-PARTY.md" "$OUT/"
cp "$ROOT/LICENSE" "$ROOT/THIRD-PARTY.md" "$OUT/app/"
# AGPL-3.0: the exact SysBot.NET revision this build used.
git -C "$ROOT/external/SysBot.NET" rev-parse HEAD > "$OUT/SYSBOT-NET-REVISION"
echo "$RID" > "$OUT/TARGET"
chmod +x "$OUT/install.sh"

COPYFILE_DISABLE=1 tar --no-xattrs -C "$ROOT/artifacts" -czf "$ROOT/artifacts/$NAME.tar.gz" "$NAME" 2>/dev/null \
  || tar -C "$ROOT/artifacts" -czf "$ROOT/artifacts/$NAME.tar.gz" "$NAME"
(cd "$ROOT/artifacts" && shasum -a 256 "$NAME.tar.gz" > "$NAME.tar.gz.sha256")
echo "Created artifacts/$NAME.tar.gz"
