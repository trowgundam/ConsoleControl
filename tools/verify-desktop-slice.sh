#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

dotnet build "$repo_root/ConsoleControl.slnx" --no-restore -m:1
dotnet run \
  --project "$repo_root/tests/ConsoleControl.IntegrationTests" \
  --no-build \
  --no-restore
