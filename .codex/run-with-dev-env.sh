#!/usr/bin/env bash
# Linux mode: run a command with the D3Parking development services configured.
set -euo pipefail

REPO_DIR="$(git rev-parse --show-toplevel)"
ENV_FILE="$REPO_DIR/.codex/dev.env"

if [ "${1:-}" = "--backend" ]; then
  if [ -z "${2:-}" ]; then
    echo "--backend requires auto, containers, or external" >&2
    exit 2
  fi
  export D3PARKING_DEV_BACKEND="$2"
  shift 2
fi

if [ "$#" -eq 0 ]; then
  echo "usage: $0 [--backend MODE] command [args...]" >&2
  exit 2
fi

D3PARKING_SKIP_RESTORE=true bash "$REPO_DIR/.codex/hooks/session-start.sh" >/dev/null

# shellcheck disable=SC1090
source "$ENV_FILE"
export ConnectionStrings__SqlServer
export D3PARKING_DESIGN_CONNECTION
if [ -n "${Smtp__Port:-}" ]; then
  export Smtp__Port
fi

exec "$@"
