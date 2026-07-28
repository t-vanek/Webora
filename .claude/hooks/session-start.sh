#!/bin/bash
# SessionStart hook for Claude Code on the web.
# Prepares the D3Parking dev environment: .NET 10 SDK, the dotnet-ef tool, restored
# NuGet packages, and the Playwright browser.
#
# The app runs against Microsoft SQL Server, which this environment does not
# host: point ConnectionStrings__SqlServer at a reachable instance before
# running the app or the E2E suite. Building and unit-level work need no
# database.
set -euo pipefail

# This setup only applies to the remote (web) environment; locally the
# developer manages their own toolchain.
if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

REPO_DIR="${CLAUDE_PROJECT_DIR:-$(pwd)}"
cd "$REPO_DIR"

log() { echo "[session-start] $*"; }

# --- .NET 10 SDK -----------------------------------------------------------
DOTNET_DIR="$HOME/.dotnet"
if [ ! -x "$DOTNET_DIR/dotnet" ]; then
  log "Installing .NET SDK 10..."
  curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  chmod +x /tmp/dotnet-install.sh
  /tmp/dotnet-install.sh --channel 10.0 --install-dir "$DOTNET_DIR"
else
  log ".NET SDK already present ($("$DOTNET_DIR/dotnet" --version))."
fi

export DOTNET_ROOT="$DOTNET_DIR"
export PATH="$DOTNET_DIR:$DOTNET_DIR/tools:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

# Persist the toolchain on PATH for the rest of the session.
if [ -n "${CLAUDE_ENV_FILE:-}" ]; then
  {
    echo "export DOTNET_ROOT=\"$DOTNET_DIR\""
    echo "export PATH=\"$DOTNET_DIR:$DOTNET_DIR/tools:\$PATH\""
    echo "export DOTNET_CLI_TELEMETRY_OPTOUT=1"
    echo "export DOTNET_NOLOGO=1"
  } >> "$CLAUDE_ENV_FILE"
fi

# --- .NET tools + package restore ------------------------------------------
log "Restoring the dotnet-ef tool..."
dotnet tool restore
log "Restoring NuGet packages..."
dotnet restore D3Parking.slnx

# --- Playwright ------------------------------------------------------------
# The base image preinstalls a browser under $PLAYWRIGHT_BROWSERS_PATH; this is
# an idempotent no-op when it is already there.
if command -v playwright >/dev/null 2>&1; then
  log "Ensuring the Playwright chromium browser is installed..."
  playwright install chromium || log "WARNING: 'playwright install' failed (non-fatal)."
fi

log "Environment ready."
