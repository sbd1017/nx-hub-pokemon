#!/usr/bin/env bash
# Installs or upgrades the NX Hub trade bot from an extracted release folder (Raspberry Pi or any systemd Linux).
#
#   tar -xzf nxhub-pokemon-<version>-linux-arm64.tar.gz
#   cd nxhub-pokemon-<version>-linux-arm64
#   sudo ./install.sh                                   # listens on port 8820
#   sudo ./install.sh --port 8830                       # another port (systemd drop-in)
#   sudo ./install.sh --nxhub http://127.0.0.1:8810 --switch-id sw_abc123 --nxhub-token-file /var/lib/nxhub/admin-token
#                                                       # hand the Switch over from NX Hub while the bot runs
#
# What it does (nothing else):
#   - copies the application to /opt/nxhub-pokemon
#   - installs /etc/systemd/system/nxhub-pokemon.service and starts it (the bot itself stays stopped
#     until you press Start in the web page)
#   - with --nxhub: writes /etc/nxhub-pokemon/nxhub.env (root only), holding NX Hub's token
# Data (config.json, admin token, history) lives in /var/lib/nxhub-pokemon and is kept across upgrades.
set -euo pipefail

APP_DIR=/opt/nxhub-pokemon
UNIT=/etc/systemd/system/nxhub-pokemon.service
DROPIN_DIR=/etc/systemd/system/nxhub-pokemon.service.d
ENV_DIR=/etc/nxhub-pokemon
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PORT=""
NXHUB=""
SWITCH_ID=""
TOKEN_FILE=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --port) PORT="${2:-}"; shift 2 ;;
    --nxhub) NXHUB="${2:-}"; shift 2 ;;
    --switch-id) SWITCH_ID="${2:-}"; shift 2 ;;
    --nxhub-token-file) TOKEN_FILE="${2:-}"; shift 2 ;;
    -h|--help) sed -n '2,17p' "$0"; exit 0 ;;
    *) echo "Unknown option: $1" >&2; exit 1 ;;
  esac
done

if [[ $EUID -ne 0 ]]; then
  echo "Run with sudo: sudo ./install.sh" >&2
  exit 1
fi

if [[ -n "$PORT" ]] && ! [[ "$PORT" =~ ^[0-9]+$ && "$PORT" -ge 1024 && "$PORT" -le 65535 ]]; then
  echo "--port must be a number between 1024 and 65535." >&2
  exit 1
fi

if [[ -n "$NXHUB" || -n "$SWITCH_ID" || -n "$TOKEN_FILE" ]]; then
  if [[ -z "$NXHUB" || -z "$SWITCH_ID" || -z "$TOKEN_FILE" ]]; then
    echo "--nxhub, --switch-id and --nxhub-token-file go together." >&2
    exit 1
  fi
  if ! [[ "$NXHUB" =~ ^https?:// ]]; then
    echo "--nxhub must be a URL such as http://127.0.0.1:8810" >&2
    exit 1
  fi
  if ! [[ "$SWITCH_ID" =~ ^[A-Za-z0-9_-]+$ ]]; then
    echo "--switch-id looks wrong (NX Hub ids look like sw_abc123)." >&2
    exit 1
  fi
  if [[ ! -s "$TOKEN_FILE" ]]; then
    echo "Cannot read the NX Hub token at $TOKEN_FILE." >&2
    exit 1
  fi
fi

if [[ ! -x "$HERE/app/nxhub-pokemon" ]]; then
  echo "Cannot find app/nxhub-pokemon next to this script. Run it from the extracted release folder." >&2
  exit 1
fi

arch="$(uname -m)"
case "$arch" in
  aarch64|arm64) expected=arm64 ;;
  x86_64) expected=x64 ;;
  *) echo "Unsupported architecture: $arch (needs 64-bit ARM or x64)." >&2; exit 1 ;;
esac
if [[ -f "$HERE/TARGET" ]] && ! grep -q "linux-$expected" "$HERE/TARGET"; then
  echo "This package is for $(cat "$HERE/TARGET") but this machine is linux-$expected." >&2
  exit 1
fi

if command -v dpkg >/dev/null && ! dpkg -s libssl3 >/dev/null 2>&1 && ! dpkg -s libssl3t64 >/dev/null 2>&1; then
  echo "Missing dependency: libssl3. Install it first:  sudo apt install libssl3" >&2
  exit 1
fi

WRITE_DROPIN=0
if [[ -n "$PORT" ]]; then
  WRITE_DROPIN=1
elif [[ -f "$DROPIN_DIR/10-listen.conf" ]]; then
  # Keep the port chosen at a previous install.
  PORT="$(sed -n 's/.*:\([0-9]*\)$/\1/p' "$DROPIN_DIR/10-listen.conf")"
fi
PORT="${PORT:-8820}"

if systemctl is-active --quiet nxhub-pokemon; then
  echo "Stopping the running trade bot..."
  systemctl stop nxhub-pokemon
fi

echo "Installing to $APP_DIR..."
rm -rf "$APP_DIR.new"
cp -a "$HERE/app" "$APP_DIR.new"
chown -R root:root "$APP_DIR.new"
chmod -R u=rwX,go=rX "$APP_DIR.new"
rm -rf "$APP_DIR.old"
if [[ -d "$APP_DIR" ]]; then mv "$APP_DIR" "$APP_DIR.old"; fi
mv "$APP_DIR.new" "$APP_DIR"
rm -rf "$APP_DIR.old"

install -o root -g root -m 0644 "$HERE/nxhub-pokemon.service" "$UNIT"
if [[ "$WRITE_DROPIN" == 1 ]]; then
  install -d -m 0755 "$DROPIN_DIR"
  printf '[Service]\nEnvironment=Urls=http://0.0.0.0:%s\n' "$PORT" > "$DROPIN_DIR/10-listen.conf"
fi

if [[ -n "$NXHUB" ]]; then
  install -d -o root -g root -m 0700 "$ENV_DIR"
  umask 077
  {
    echo "NxHubPokemon__NxHub__BaseUrl=$NXHUB"
    echo "NxHubPokemon__NxHub__SwitchId=$SWITCH_ID"
    echo "NxHubPokemon__NxHub__Token=$(tr -d '\r\n' < "$TOKEN_FILE")"
  } > "$ENV_DIR/nxhub.env"
  chmod 0600 "$ENV_DIR/nxhub.env"
  echo "Linked to NX Hub at $NXHUB (Switch $SWITCH_ID)."
fi

systemctl daemon-reload
systemctl enable --now nxhub-pokemon >/dev/null

for _ in $(seq 1 30); do
  if command -v curl >/dev/null && curl -fsS "http://127.0.0.1:$PORT/api/health" >/dev/null 2>&1; then
    break
  fi
  sleep 1
done

echo
echo "Trade bot installed and running on port $PORT. The bot itself is stopped until you press Start."
echo "Admin token:  sudo cat /var/lib/nxhub-pokemon/admin-token"
echo "Open:         http://$(hostname).local:$PORT"
