#!/usr/bin/env bash
# Starts Postgres (docker) and the ranked backend locally. Settings: ranked/.env + backend/appsettings.json
set -euo pipefail
cd "$(dirname "$0")"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
[ -f .env ] || { echo "Missing ranked/.env - copy .env.example and fill it in."; exit 1; }
docker compose up -d db >/dev/null
cd backend
exec dotnet run --no-launch-profile
