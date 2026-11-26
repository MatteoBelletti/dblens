#!/usr/bin/env bash
# Runs the MCP server from this working tree, so that --plugin-dir and the evals exercise the code
# under development, not the package NuGet lists. The build's output goes to stderr: stdout is the
# protocol.
set -euo pipefail
project="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../src/DbLens.Mcp" && pwd)"
dotnet build "$project" -nologo -v q -c Debug 1>&2
# The built DLL, not `dotnet run`: the server must answer within a second or two of the client starting.
exec dotnet "$project/bin/Debug/net10.0/DbLens.Mcp.dll"
