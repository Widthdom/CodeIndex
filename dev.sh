#!/usr/bin/env bash
set -euo pipefail

CONFIGURATION="${CONFIGURATION:-Release}"
FRAMEWORK="${FRAMEWORK:-net8.0}"
RESULTS_DIRECTORY="${RESULTS_DIRECTORY:-./TestResults}"

usage() {
  cat <<'USAGE'
Usage: ./dev.sh <task>

Tasks:
  build       Build the test project for FRAMEWORK.
  test        Run the test suite for FRAMEWORK.
  lint        Verify formatting without changing files.
  format      Apply dotnet format.
  coverage    Run tests with XPlat Code Coverage.
  mcp-smoke   Run a minimal MCP help/build smoke.
  clean       Clean build outputs and local test artifacts.
USAGE
}

task="${1:-}"
case "$task" in
  build)
    dotnet build tests/CodeIndex.Tests/CodeIndex.Tests.csproj \
      --configuration "$CONFIGURATION" \
      --framework "$FRAMEWORK"
    ;;
  test)
    dotnet test tests/CodeIndex.Tests/CodeIndex.Tests.csproj \
      --configuration "$CONFIGURATION" \
      --framework "$FRAMEWORK" \
      --settings tests/CodeIndex.Tests/CodeIndex.Tests.runsettings \
      --blame-crash \
      --blame-hang \
      --blame-hang-timeout 5m
    ;;
  lint)
    dotnet format whitespace CodeIndex.sln --verify-no-changes --no-restore --verbosity minimal
    ;;
  format)
    dotnet format whitespace CodeIndex.sln --verbosity minimal
    ;;
  coverage)
    dotnet test tests/CodeIndex.Tests/CodeIndex.Tests.csproj \
      --configuration "$CONFIGURATION" \
      --framework "$FRAMEWORK" \
      --settings tests/CodeIndex.Tests/CodeIndex.Tests.runsettings \
      --collect "XPlat Code Coverage" \
      --results-directory "$RESULTS_DIRECTORY"
    ;;
  mcp-smoke)
    dotnet build src/CodeIndex/CodeIndex.csproj --configuration "$CONFIGURATION"
    dotnet run --project src/CodeIndex -- mcp --help > /dev/null
    ;;
  clean)
    dotnet clean CodeIndex.sln --configuration "$CONFIGURATION"
    rm -rf "$RESULTS_DIRECTORY" publish
    ;;
  -h|--help|help|"")
    usage
    ;;
  *)
    usage >&2
    exit 2
    ;;
esac
