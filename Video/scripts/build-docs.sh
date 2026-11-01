#!/usr/bin/env bash
# Builds the user guide and API reference (docs/site) into docs/site/_site.
# Usage: scripts/build-docs.sh [--serve]   (--serve: browse at http://localhost:8080)
# The output is a static site; browse it offline through --serve, not from file://.
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet tool restore
args=(docfx docs/site/docfx.json --warningsAsErrors)
if [[ "${1:-}" == "--serve" ]]; then args+=(--serve); fi
EnableWindowsTargeting=true dotnet "${args[@]}"
