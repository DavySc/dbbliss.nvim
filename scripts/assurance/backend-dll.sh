#!/usr/bin/env bash
# Starts the backend from its build output (not the published single file) so that dotnet-coverage,
# which instruments .NET processes it launches, can measure it while the cancel suite and the
# end-to-end tests drive it. Used with: --backend scripts/assurance/backend-dll.sh / DBBLISS_BACKEND.
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
exec dotnet "$root/backend/src/Dbbliss.Backend/bin/Debug/net10.0/dbbliss-backend.dll" "$@"
