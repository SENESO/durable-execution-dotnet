#!/usr/bin/env bash
# The evidence command (foundation §7.2): from a clean checkout, this one
# command builds the solution and runs the suite — the crash-injection
# scenarios across real process kills, plus the store/identity/host unit
# tests. A phase advances only when this runs green.
#
# Provider coverage (§6.5): the suite runs per provider. The file and
# SQLite providers always run (SQLite is embedded; it needs no server).
# The SQL Server provider runs when DE_SQLSERVER_CONNECTION names a test
# SQL Server, and the PostgreSQL provider runs when DE_POSTGRES_CONNECTION
# names a test PostgreSQL server (in both cases a master connection under
# a login that may create and drop scratch databases; the suite provisions
# and drops one database per test). Without a variable, that provider's
# classes are excluded by trait — so the local evidence stays reproducible
# on any machine, and a server provider's evidence is claimed only where
# it actually ran.
set -Eeuo pipefail
cd "$(dirname "$0")"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="dotnet"
mkdir -p /tmp/nupkg-out 2>/dev/null || true

echo "== building (Release) =="
"$DOTNET" build DurableExecution.sln -c Release --nologo -v q

TEST_DLL="tests/DurableExecution.Tests/bin/Release/net8.0/DurableExecution.Tests.dll"
RUNNER="$HOME/workspace/.xunit-runner/pkg/tools/net6.0/xunit.console.dll"

TRAIT_ARGS=()
FILTER_PARTS=()
PROVIDERS_RAN="file + SQLite"

if [ -z "${DE_SQLSERVER_CONNECTION:-}" ]; then
  TRAIT_ARGS+=(-notrait "Provider=SqlServer")
  FILTER_PARTS+=("Provider!=SqlServer")
else
  PROVIDERS_RAN+=", SQL Server"
fi

if [ -z "${DE_POSTGRES_CONNECTION:-}" ]; then
  TRAIT_ARGS+=(-notrait "Provider=PostgreSql")
  FILTER_PARTS+=("Provider!=PostgreSql")
else
  PROVIDERS_RAN+=", PostgreSQL"
fi

echo "== running the suite (providers: $PROVIDERS_RAN) =="

if [ -f "$RUNNER" ]; then
  # Console-runner path (used where the vstest host cannot run). The test
  # assembly's own runtimeconfig rides along: it names both shared
  # frameworks the suite needs (NETCore for the engine tests,
  # AspNetCore for the dashboard's in-process TestServer) — the runner's
  # own runtimeconfig names only the first.
  RUNTIMECONFIG="tests/DurableExecution.Tests/bin/Release/net8.0/DurableExecution.Tests.runtimeconfig.json"
  "$DOTNET" exec --roll-forward LatestMajor --runtimeconfig "$RUNTIMECONFIG" "$RUNNER" "$TEST_DLL" "${TRAIT_ARGS[@]}"
else
  if [ "${#FILTER_PARTS[@]}" -eq 0 ]; then
    "$DOTNET" test DurableExecution.sln -c Release --no-build
  else
    FILTER="$(IFS='&'; echo "${FILTER_PARTS[*]}")"
    "$DOTNET" test DurableExecution.sln -c Release --no-build --filter "$FILTER"
  fi
fi
