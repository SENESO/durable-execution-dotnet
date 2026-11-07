#!/usr/bin/env bash
# The P0 evidence command (foundation §7.2): from a clean checkout, this one
# command builds the solution and runs the full suite — the 11 crash-injection
# scenarios across real process kills, plus the store/identity/host unit
# tests. A phase advances only when this runs green.
set -Eeuo pipefail
cd "$(dirname "$0")"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="dotnet"
mkdir -p /tmp/nupkg-out 2>/dev/null || true

echo "== building (Release) =="
"$DOTNET" build DurableExecution.sln -c Release --nologo -v q

TEST_DLL="tests/DurableExecution.Tests/bin/Release/net8.0/DurableExecution.Tests.dll"
RUNNER="$HOME/workspace/.xunit-runner/pkg/tools/net6.0/xunit.console.dll"

echo "== running the suite =="
if [ -f "$RUNNER" ]; then
  # Console-runner path (used where the vstest host cannot run).
  "$DOTNET" exec --roll-forward LatestMajor "$RUNNER" "$TEST_DLL"
else
  "$DOTNET" test DurableExecution.sln -c Release --no-build
fi
