#!/usr/bin/env bash
# Linux mode: prepare the D3Parking toolchain and local services for Codex.
# Operational logs go to stderr so SessionStart only adds the concise final
# instructions on stdout to the agent context.
set -euo pipefail

REPO_DIR="$(git rev-parse --show-toplevel)"
cd "$REPO_DIR"

log() { printf '[codex-setup] %s\n' "$*" >&2; }

if ! command -v dotnet >/dev/null 2>&1; then
  log "ERROR: .NET 10 SDK is not installed or is not on PATH."
  exit 1
fi

DOTNET_MAJOR="$(dotnet --version | cut -d. -f1)"
if [ "$DOTNET_MAJOR" -ne 10 ]; then
  log "ERROR: D3Parking requires .NET 10; found $(dotnet --version)."
  exit 1
fi

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

BACKEND="${D3PARKING_DEV_BACKEND:-auto}"
BACKEND="${BACKEND,,}"
SQL_OVERRIDE="${D3PARKING_SQL_CONNECTION:-${ConnectionStrings__SqlServer:-}}"
MSSQL_CONN=""
SMTP_PORT="${Smtp__Port:-}"
CONTAINER_ENGINE=""

if [ "$BACKEND" = "auto" ] && [ -n "$SQL_OVERRIDE" ]; then
  BACKEND="external"
fi

if [ "$BACKEND" = "auto" ] || [ "$BACKEND" = "containers" ]; then
  if command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
    CONTAINER_ENGINE="docker"
  elif command -v podman >/dev/null 2>&1 && podman info >/dev/null 2>&1; then
    CONTAINER_ENGINE="podman"
  elif [ "$BACKEND" = "containers" ]; then
    log "ERROR: container mode needs an accessible Docker daemon or rootless Podman."
    exit 1
  else
    log "ERROR: no container engine is available. Set D3PARKING_SQL_CONNECTION and use the external backend."
    exit 1
  fi
  BACKEND="containers"
fi

if [ "$BACKEND" = "external" ]; then
  if [ -z "$SQL_OVERRIDE" ]; then
    log "ERROR: external mode requires D3PARKING_SQL_CONNECTION."
    exit 1
  fi
  MSSQL_CONN="$SQL_OVERRIDE"
  log "Using the externally managed SQL Server connection."
elif [ "$BACKEND" = "containers" ]; then
  MSSQL_CONTAINER="d3parking-mssql"
  MSSQL_PASSWORD='D3Parking!Passw0rd'
  MSSQL_CONN="Server=localhost,1433;Database=D3Parking;User Id=sa;Password=${MSSQL_PASSWORD};TrustServerCertificate=True"
  SMTP_CONTAINER="d3parking-smtp"

  if ! "$CONTAINER_ENGINE" container inspect "$MSSQL_CONTAINER" >/dev/null 2>&1; then
    log "Creating SQL Server 2022 Developer container with $CONTAINER_ENGINE..."
    "$CONTAINER_ENGINE" run -d --name "$MSSQL_CONTAINER" \
      --restart unless-stopped \
      -e ACCEPT_EULA=Y \
      -e MSSQL_PID=Developer \
      -e MSSQL_SA_PASSWORD="$MSSQL_PASSWORD" \
      -p 127.0.0.1:1433:1433 \
      mcr.microsoft.com/mssql/server:2022-latest >/dev/null
  else
    "$CONTAINER_ENGINE" start "$MSSQL_CONTAINER" >/dev/null 2>&1 || true
    log "Reusing the existing SQL Server container."
  fi

  log "Waiting for SQL Server..."
  MSSQL_READY=false
  for _ in $(seq 1 30); do
    if "$CONTAINER_ENGINE" exec "$MSSQL_CONTAINER" \
        /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_PASSWORD" \
        -C -Q "SELECT 1" >/dev/null 2>&1; then
      MSSQL_READY=true
      break
    fi
    sleep 2
  done

  if [ "$MSSQL_READY" != true ]; then
    "$CONTAINER_ENGINE" logs --tail 80 "$MSSQL_CONTAINER" >&2 || true
    log "ERROR: SQL Server did not become ready within 60 seconds."
    exit 1
  fi

  # Rootless containers cannot bind host port 25, so the app uses 2525.
  if ! "$CONTAINER_ENGINE" container inspect "$SMTP_CONTAINER" >/dev/null 2>&1; then
    log "Creating smtp4dev mail catcher with $CONTAINER_ENGINE..."
    "$CONTAINER_ENGINE" run -d --name "$SMTP_CONTAINER" \
      --restart unless-stopped \
      -p 127.0.0.1:2525:25 \
      -p 127.0.0.1:5000:80 \
      docker.io/rnwood/smtp4dev:latest >/dev/null
  else
    "$CONTAINER_ENGINE" start "$SMTP_CONTAINER" >/dev/null 2>&1 || true
  fi
  SMTP_PORT="2525"
else
  log "ERROR: unsupported backend '$BACKEND'; use auto, containers, or external."
  exit 1
fi

# This file is ignored by *.env. It lets later tool calls opt into the same
# database without committing credentials or modifying application settings.
umask 077
{
  printf 'export ConnectionStrings__SqlServer=%q\nexport D3PARKING_DESIGN_CONNECTION=%q\n' \
    "$MSSQL_CONN" "$MSSQL_CONN"
  if [ -n "$SMTP_PORT" ]; then
    printf 'export Smtp__Port=%q\n' "$SMTP_PORT"
  fi
} > .codex/dev.env

if [ "${D3PARKING_SKIP_RESTORE:-false}" != "true" ]; then
  log "Restoring local .NET tools and NuGet packages..."
  dotnet tool restore >&2
  dotnet restore D3Parking.slnx >&2
fi

printf '%s\n' \
  "D3Parking Linux development environment is ready (backend: $BACKEND)." \
  'For app, EF, or test commands that need SQL Server, prefix the command with `.codex/run-with-dev-env.sh`.' \
  'The helper exports the selected SQL settings; containers are optional.'
