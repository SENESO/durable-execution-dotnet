#!/usr/bin/env bash
# The evidence command (foundation §7.2): from a clean checkout, this one
# command builds the solution and runs the suite — the crash-injection
# scenarios across real process kills, plus the store/identity/host unit
# tests. A phase advances only when this runs green.
#
# Provider coverage (§6.5): the suite runs per provider. The file provider
# always runs. The SQL Server provider runs when DE_SQLSERVER_CONNECTION
# names a test SQL Server (a master connection under a login that may
# create and drop scratch databases; the suite provisions and drops one
# database per test). Without it, the SQL Server classes are excluded by
# trait — so the file evidence stays reproducible on any machine, and the
# SQL evidence is claimed only where it actually ran.
set -Eeuo pipefail
cd "$(dirname "$0")"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="dotnet"
mkdir -p /tmp/nupkg-out 2>/dev/null || true

echo "== building (Release) =="
"$DOTNET" build DurableExecution.sln -c Release --nologo -v q

TEST_DLL="tests/DurableExecution.Tests/bin/Release/net8.0/DurableExecution.Tests.dll"
RUNNER="$HOME/workspace/.xunit-runner/pkg/tools/net6.0/xunit.console.dll"

if [ -n "${DE_SQLSERVER_CONNECTION:-}" ]; then
  echo "== running the suite (file + SQL Server providers) =="
  TRAIT_ARGS=()
else
  echo "== running the suite (file provider; SQL Server classes excluded — DE_SQLSERVER_CONNECTION not set) =="
  TRAIT_ARGS=(-notrait "Provider=SqlServer")
fi

if [ -f "$RUNNER" ]; then
  # Console-runner path (used where the vstest host cannot run).
  "$DOTNET" exec --roll-forward LatestMajor "$RUNNER" "$TEST_DLL" "${TRAIT_ARGS[@]}"
else
  if [ -n "${DE_SQLSERVER_CONNECTION:-}" ]; then
    "$DOTNET" test DurableExecution.sln -c Release --no-build
  else
    "$DOTNET" test DurableExecution.sln -c Release --no-build --filter "Provider!=SqlServer"
  fi
fi
