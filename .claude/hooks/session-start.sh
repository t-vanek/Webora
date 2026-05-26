#!/bin/bash
# SessionStart hook for Claude Code on the web.
# Prepares the Webora dev environment: .NET 10 SDK, the Docker daemon and the
# backing services (Postgres, Redis, RabbitMQ, smtp4dev), the dotnet-ef tool,
# restored NuGet packages, and the Playwright browser.
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

# --- Docker daemon ---------------------------------------------------------
# The web base image ships the Docker CLI but does not start the daemon, so
# bring it up by hand (the init script trips over ulimit under this sandbox).
if ! docker info >/dev/null 2>&1; then
  log "Starting Docker daemon..."
  nohup dockerd >/tmp/dockerd.log 2>&1 &
  for _ in $(seq 1 30); do
    docker info >/dev/null 2>&1 && break
    sleep 1
  done
fi
if docker info >/dev/null 2>&1; then
  log "Docker daemon is up."
else
  log "WARNING: Docker daemon did not start (see /tmp/dockerd.log); skipping backing services."
fi

# --- Backing services (Postgres, Redis, RabbitMQ, smtp4dev) ----------------
if docker info >/dev/null 2>&1; then
  log "Starting backing services..."
  docker compose up -d postgres redis rabbitmq smtp4dev
fi

# --- .NET tools + package restore ------------------------------------------
log "Restoring the dotnet-ef tool..."
dotnet tool restore
log "Restoring NuGet packages..."
dotnet restore Webora.slnx

# --- Playwright ------------------------------------------------------------
# The base image preinstalls a browser under $PLAYWRIGHT_BROWSERS_PATH; this is
# an idempotent no-op when it is already there.
if command -v playwright >/dev/null 2>&1; then
  log "Ensuring the Playwright chromium browser is installed..."
  playwright install chromium || log "WARNING: 'playwright install' failed (non-fatal)."
fi

log "Environment ready."
