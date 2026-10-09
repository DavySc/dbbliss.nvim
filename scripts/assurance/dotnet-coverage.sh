#!/usr/bin/env bash
# Statement and decision (branch) coverage of the backend, merged over the suites that drive it:
# the protocol tests (in-process) and the cancel suite (which starts the backend as a child process
# from its build output, scripts/assurance/backend-dll.sh, so that it is instrumented too).
# Linux only. Needs `dotnet-coverage` (dotnet tool install -g dotnet-coverage) and the databases
# of the cancel suite (DBBLISS_TEST_PG, DBBLISS_TEST_MSSQL; engines without a variable are skipped).
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."
out="coverage"
mkdir -p "$out"
if [ -z "${SKIP_PROTOCOL:-}" ]; then rm -f "$out"/protocol.cobertura.xml; fi
rm -f "$out"/cancel.cobertura.xml

dotnet build backend/src/Dbbliss.Backend -c Debug -v q
dotnet build backend/tests/Dbbliss.ProtocolTests -c Debug -v q
dotnet build backend/tests/Dbbliss.CancelTests -c Debug -v q

if [ -z "${SKIP_PROTOCOL:-}" ]; then
  dotnet-coverage collect -f cobertura -o "$out/protocol.cobertura.xml" \
    "dotnet backend/tests/Dbbliss.ProtocolTests/bin/Debug/net10.0/Dbbliss.ProtocolTests.dll"
fi
dotnet-coverage collect -f cobertura -o "$out/cancel.cobertura.xml" \
  "dotnet backend/tests/Dbbliss.CancelTests/bin/Debug/net10.0/Dbbliss.CancelTests.dll --backend scripts/assurance/backend-dll.sh ${NVIM:+--nvim $NVIM} --report $out/cancel-report.md"
