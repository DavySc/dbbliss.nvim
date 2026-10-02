#!/usr/bin/env bash
# Publishes the backend as a self-contained single file into bin/<rid>/, where the plugin looks for it.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
rid="${1:-linux-x64}"
dotnet publish "$root/backend/src/Dbbliss.Backend" -c Release -r "$rid" \
  --self-contained true -p:PublishSingleFile=true -o "$root/bin/$rid" --nologo
