#!/usr/bin/env bash
# ------------------------------------------------------------------------
# smoke-test-exe.sh — prove a published EagleBoards.exe actually works.
#
#   bash scripts/smoke-test-exe.sh publish/EagleBoards.exe [port]
#
# A packaging mistake (a check-in page left out of the bundle, a dependency
# trimmed away) shows up only in the published single-file exe, never in the
# dotnet build the other tests run against. So this starts the exe itself on a
# throwaway data folder with synthetic data, serves loopback only, and checks
# the sign-in pages load and a sign-in lands on disk.
#
# Used by both workflows. The scheduler window opens on the CI runner's
# desktop, which nobody sees; don't run this on a desk someone is using.
# ------------------------------------------------------------------------

set -eu

EXE=$(cd "$(dirname "$1")" && pwd)/$(basename "$1")
PORT="${2:-18777}"
B="http://127.0.0.1:$PORT"
ROOT=$(cd "$(dirname "$0")/.." && pwd)
WORK="${RUNNER_TEMP:-${TMPDIR:-/tmp}}/eb-smoke-$$"
APP=""

stop() {
    if [ -n "$APP" ]; then
        # The exe is a native Windows process: taskkill by its Windows PID.
        taskkill //F //PID "$(cat "/proc/$APP/winpid" 2>/dev/null)" >/dev/null 2>&1 || kill "$APP" 2>/dev/null || true
    fi
}
trap stop EXIT

[ -f "$EXE" ] || { echo "MISSING: $EXE"; exit 1; }
mkdir -p "$WORK"
cp "$ROOT/config.properties" "$WORK/"
echo 'Type,ID,Last,First,Email,Phone,UnitType,Unit,UnitName,ProjectReview,FinalBoard,RegTime,Room,Flags,Sel,BoardHistory' \
    > "$WORK/AdultHistory.csv"

cd "$WORK"
"$EXE" -d run -a AdultHistory.csv -c config.properties -port "$PORT" -bind 127.0.0.1 &
APP=$!

ready=0
for _ in $(seq 1 60); do
    if curl -sf -o index.html "$B/"; then
        ready=1
        break
    fi
    sleep 1
done
if [ "$ready" != 1 ]; then
    echo "FAIL: the exe never served the check-in page"
    cat run/eagleboards.log 2>/dev/null || true
    exit 1
fi

# Fetch to files, then grep: `curl | grep -q` under pipefail fails exactly
# when grep matches early and curl takes the SIGPIPE.
curl -sf -o youth.html "$B/youth_register"
curl -sf -o adult.html "$B/adult_register"
curl -sf -o data.js "$B/eb-data.js"
grep -q "Please Sign In" index.html
grep -q "register-youth" youth.html
grep -q "register-adult" adult.html
grep -q "ebFetchRows" data.js

curl -sf -X POST --data "Last=Smoke&First=Test&UnitType=Troop&Unit=1&BoardType=Final" "$B/register-youth"
grep -q "SCOUT:Smoke:Test:1" run/scouts.csv

echo "published exe OK: serves the check-in pages and records a sign-in"
