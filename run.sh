#!/bin/bash
# Build the feed test and run it. All arguments are passed through; see ./run.sh --help.
#
#   ./run.sh                                         # BWF 20261108962633r1 vs its int fixture, newest build*/ lib
#   ./run.sh --lib ../ucpp-table-parse-lib/build-gcc7/LIBRARY/TABLE_PARSER/libTableParser.so
#
# Exit code: 0 match, 1 differences, 2 bad arguments, 3 pipeline error, 134 native abort in libTableParser.so.

set -e
SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"

dotnet build "${SCRIPT_DIR}/TableParseFeedTest.csproj" -c Release -nologo -v quiet -clp:ErrorsOnly
set +e
dotnet "${SCRIPT_DIR}/bin/Release/net8.0/TableParseFeedTest.dll" "$@"
