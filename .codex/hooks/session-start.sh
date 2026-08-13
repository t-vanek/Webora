#!/bin/bash
# SessionStart hook for Claude Code on the web.
# Prepares the D3Parking dev environment: .NET 10 SDK, the dotnet-ef tool, restored
# NuGet packages, the Playwright browser, and a Microsoft SQL Server 2022
# container (Docker), with ConnectionStrings__SqlServer exported for the whole
# session so `dotnet run` and the E2E suite work out of the box.
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

# --- SQL Server (Docker) ---------------------------------------------------
# The app and the E2E suite need Microsoft SQL Server; the remote container
# ships the Docker CLI but no running daemon. Start one, run SQL Server 2022,
# and export the connection string. Every step is non-fatal: without a
# database the session can still build, edit and unit-test.
#
# Dev-only throwaway credentials for the ephemeral container — nothing outside
# this sandboxed session can reach it.
MSSQL_PASSWORD='D3Parking!Passw0rd'
MSSQL_CONN="Server=localhost,1433;Database=D3Parking;User Id=sa;Password=$MSSQL_PASSWORD;TrustServerCertificate=True"

if command -v docker >/dev/null 2>&1; then
  if ! docker info >/dev/null 2>&1; then
    log "Starting the Docker daemon..."
    (dockerd > /var/log/dockerd.log 2>&1 &)
    for _ in $(seq 1 30); do
      docker info >/dev/null 2>&1 && break
      sleep 1
    done
  fi

  if docker info >/dev/null 2>&1; then
    if [ -z "$(docker ps -aq -f name='^mssql$')" ]; then
      log "Starting SQL Server 2022 in Docker (the first image pull can take a minute)..."
      docker run -d --name mssql \
        -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD="$MSSQL_PASSWORD" \
        -p 1433:1433 mcr.microsoft.com/mssql/server:2022-latest >/dev/null \
        || log "WARNING: could not start the SQL Server container (non-fatal)."
    else
      docker start mssql >/dev/null 2>&1 || true
      log "Reusing the existing SQL Server container."
    fi

    log "Waiting for SQL Server to accept connections..."
    MSSQL_READY=false
    for _ in $(seq 1 90); do
      if docker exec mssql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa \
           -P "$MSSQL_PASSWORD" -C -Q "SELECT 1" >/dev/null 2>&1; then
        MSSQL_READY=true
        break
      fi
      sleep 2
    done

    if [ "$MSSQL_READY" = true ]; then
      log "SQL Server is ready on localhost,1433."
      if [ -n "${CLAUDE_ENV_FILE:-}" ]; then
        echo "export ConnectionStrings__SqlServer=\"$MSSQL_CONN\"" >> "$CLAUDE_ENV_FILE"
      fi
    else
      log "WARNING: SQL Server did not become ready; running the app or the E2E suite needs a reachable instance (non-fatal)."
    fi
  else
    log "WARNING: the Docker daemon did not start; skipping SQL Server (non-fatal)."
  fi
else
  log "WARNING: Docker is unavailable; skipping SQL Server (non-fatal)."
fi

log "Environment ready."
