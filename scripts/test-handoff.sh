#!/usr/bin/env bash
# ------------------------------------------------------------------------
# test-handoff.sh — the Java and Windows versions taking turns on one event.
#
# The two versions read and write the same files, so if one crashes mid-event
# the other should pick up where it left off. This runs part of an event on
# one server, kills it as if it crashed, carries on with the other on the
# same folder and port, then brings the first back to read what the second
# wrote. Both directions: Java then Windows, and Windows then Java.
#
#   dotnet build -c Release
#   EB_JAR=/path/to/eagleboardscheduler-*.jar bash scripts/test-handoff.sh
#
# Build the jar from the Java project's source (./mvnw -DskipTests package);
# never take one from a checkout that holds live data. Everything here runs
# on a throwaway folder with SYNTHETIC names, bound to 127.0.0.1.
#
# Portability: bash, curl, awk, the .NET SDK and a JDK. No Python.
# ------------------------------------------------------------------------

set -u

# Non-ASCII values are percent-encoded here, byte by byte, before curl sees
# them: Git Bash on Windows hands curl's arguments over in the ANSI code
# page, so "Ångström" would arrive as Latin-1, which no browser sends.
urlenc() { printf '%s' "$1" | od -An -tx1 -v | tr -d ' \n' | sed 's/\(..\)/%\1/g'; }

PORT="${EB_PORT:-18097}"
B="http://127.0.0.1:$PORT"
ROOT=$(cd "$(dirname "$0")/.." && pwd)
WORK="${TMPDIR:-/tmp}/eb-handoff-$$"
SRV=""

PASS=0
FAIL=0

ok()  { echo "  ok  : $*"; PASS=$((PASS + 1)); }
bad() { echo "FAIL  : $*"; FAIL=$((FAIL + 1)); }

chk() {
    if [ "$2" = "$3" ]; then
        ok "$1"
    else
        bad "$1"
        echo "          got:  '$2'"
        echo "          want: '$3'"
    fi
}

stop_server() {
    if [ -n "$SRV" ]; then
        kill "$SRV" 2>/dev/null
        wait "$SRV" 2>/dev/null
        SRV=""
    fi
    for _ in $(seq 1 20); do
        curl -s -o /dev/null "$B/index.html" || return 0
        sleep 1
    done
}

cleanup() {
    stop_server
    if [ -n "${EB_KEEP:-}" ]; then echo "kept: $WORK"; else rm -rf "$WORK"; fi
}
trap cleanup EXIT INT TERM

# ------------------------------------------------------------ 0. preflight
for t in curl awk java dotnet; do
    command -v "$t" >/dev/null 2>&1 || { echo "MISSING: $t"; exit 1; }
done
[ -n "${EB_JAR:-}" ] && [ -f "$EB_JAR" ] || { echo "MISSING: EB_JAR=<path to eagleboardscheduler-*.jar>"; exit 1; }
DLL=""
for cfg in Release Debug; do
    d="$ROOT/src/EagleBoards.Server/bin/$cfg/net10.0/EagleBoards.Server.dll"
    if [ -z "$DLL" ] && [ -f "$d" ]; then DLL="$d"; fi
done
[ -n "$DLL" ] || { echo "MISSING: no built server. Run 'dotnet build -c Release' first."; exit 1; }

echo "java:    $EB_JAR"
echo "windows: ${DLL#"$ROOT"/}"

# start_server <java|windows> <folder>: the same options either way, the Java
# version's own, as a district's launcher passes them.
start_server() {
    if [ "$1" = java ]; then
        LAUNCH="java -jar"
        TARGET="$EB_JAR"
    else
        LAUNCH="dotnet"
        TARGET="$DLL"
    fi

    # shellcheck disable=SC2086
    ( cd "$2" && exec $LAUNCH "$TARGET" \
        -a AdultHistory.csv -c config.properties -port "$PORT" -d run -bind 127.0.0.1 ) >> "$2/server.log" 2>&1 &
    SRV=$!
    if ! curl -sf --retry 40 --retry-delay 1 --retry-connrefused --retry-all-errors -o /dev/null "$B/index.html"; then
        echo "the $1 server never became ready on port $PORT"
        cat "$2/server.log"
        exit 1
    fi
}

post() { curl -sf -X POST "$@" -o /dev/null; }

act() {
    _p=$1
    shift
    _out=$(curl -s -w '\n%{http_code}' -X POST "$@" "$B$_p")
    echo "$(echo "$_out" | tail -1)|$(echo "$_out" | sed '$d' | tr -d '\r\n')"
}

accepted() {
    case "${2#*|}" in
        OK*) ok "$1" ;;
        *)   bad "$1 -- refused with '${2#*|}'" ;;
    esac
}

refused() {
    case "${2#*|}" in
        OK*) bad "$1 -- the server ACCEPTED it" ;;
        "")  bad "$1 -- refused with an empty body" ;;
        *)   ok "$1" ;;
    esac
}

seat() {
    _rm=$1; _sc=$2; _ch=$3
    shift 3
    _ids=$_ch
    for _m in "$@"; do _ids="$_ids,$_m"; done
    act /seat-board --data "RoomID=$(urlenc "ROOM:$_rm")&ScoutID=$(urlenc "$_sc")&ChairID=$(urlenc "$_ch")&MemberIDs=$(urlenc "$_ids")"
}
start()    { act /inprogress-board --data-urlencode "ScoutID=$1"; }
complete() { act /complete-board --data-urlencode "ScoutID=$1" --data-urlencode "Result=$2" --data-urlencode "Notes=${3:-}"; }
postpone() { act /postpone-board --data-urlencode "ScoutID=$1"; }

adult() { # last first unit project final
    post --data-urlencode "Last=$1" --data-urlencode "First=$2" \
        --data "Email=a$3@example.org&UnitType=Troop&Unit=$3&UnitName=Troop$3&ProjectReview=$4&FinalBoard=$5" "$B/register-adult"
}
youth() { # last first unit type
    post --data-urlencode "Last=$1" --data-urlencode "First=$2" \
        --data "Email=s$3@example.org&UnitType=Troop&Unit=$3&UnitName=Troop$3&BoardType=$4" "$B/register-youth"
}
room() { post --data "!nativeeditor_status=inserted&gr_id=ROOM:$1&Room=$1&BoardType=$2" "$B/room-update"; }

# Column positions in the CSVs both versions write.
#   scouts.csv  3 RegNum  4 Last  17 Room  18 Status  19 Result  24 Notes
#   adults.csv  3 Last  4 First  13 Room
status_of()  { awk -F, -v i="$1" 'NR>1 && $2==i {print $18}' "$SCOUTS"; }
field_of()   { awk -F, -v i="$1" -v f="$2" 'NR>1 && $2==i {print $f}' "$SCOUTS"; }
adult_room() { awk -F, -v i="$1" 'NR>1 && $2==i {print $13}' "$ADULTS"; }
busy()       { awk -F, 'NR>1 && $13!="" && $13!="N/A" {n++} END {print n+0}' "$ADULTS"; }
rows()       { awk 'NR>1' "$1" | wc -l | tr -d ' '; }

A() { echo "ADULT:$1:$2:$3"; }
S() { echo "SCOUT:$1:$2:$3"; }

# ------------------------------------------------------------ the hand-off
handoff() {
    FIRST=$1
    SECOND=$2
    DIR="$WORK/$FIRST-then-$SECOND"
    mkdir -p "$DIR"
    cp "$ROOT/config.properties" "$DIR/config.properties"
    echo 'Type,ID,Last,First,Email,Phone,UnitType,Unit,UnitName,ProjectReview,FinalBoard,RegTime,Room,Flags,Sel,BoardHistory' \
        > "$DIR/AdultHistory.csv"
    SCOUTS="$DIR/run/scouts.csv"
    ADULTS="$DIR/run/adults.csv"

    echo
    echo "== $FIRST runs the first part of the event =="
    start_server "$FIRST" "$DIR"
    room 101 Final; room 102 Final; room 201 Project
    adult Abernathy Anneliese 2001 Member Chair
    adult Blackwood Bartholomew 2002 Member Chair
    adult Castellano Clementine 2003 Chair Member
    adult Duxbury Desmond 2004 Member Member
    adult Ellsworth Evangeline 2005 Member Member
    adult Grimaldi Genevieve 2006 Member Member
    adult Hollingsworth Horatio 2007 Member Member
    adult Kaminski Katarina 2008 Member Member
    # A comma in a name (saved as "~") and an accent (UTF-8), both written by
    # one version and read by the other.
    adult "Whitmore, Jr." Lysander 2009 Member Member
    # Sent as a browser would: UTF-8, percent-encoded. (Passing "Ångström" to
    # curl from Git Bash on Windows goes out in the ANSI code page instead.)
    post --data "Last=%C3%85ngstr%C3%B6m&First=Zo%C3%AB&Email=a2010@example.org&UnitType=Troop&Unit=2010&UnitName=Troop2010&ProjectReview=Member&FinalBoard=Member"         "$B/register-adult"
    youth Aldridge Alexander 1001 Final
    youth Bram Beauregard 1002 Final
    youth Fenwick Finnegan 1003 Project
    youth Everly Emmett 1004 Final
    youth Gallagher Gideon 1005 Final

    FC1=$(A Abernathy Anneliese 2001); FC2=$(A Blackwood Bartholomew 2002); PC1=$(A Castellano Clementine 2003)
    M1=$(A Duxbury Desmond 2004); M2=$(A Ellsworth Evangeline 2005); M3=$(A Grimaldi Genevieve 2006)
    M4=$(A Hollingsworth Horatio 2007); M5=$(A Kaminski Katarina 2008)
    JR=$(awk -F, 'NR>1 && $4=="Lysander" {print $2}' "$ADULTS")
    ZOE=$(awk -F, 'NR>1 && $8=="2010" {print $2}' "$ADULTS")
    S1=$(S Aldridge Alexander 1001); S2=$(S Bram Beauregard 1002); S3=$(S Fenwick Finnegan 1003)
    S4=$(S Everly Emmett 1004); S5=$(S Gallagher Gideon 1005)

    accepted "a board is seated"                 "$(seat 101 "$S1" "$FC1" "$M1" "$M2")"
    accepted "and its review started"            "$(start "$S1")"
    accepted "a second board is seated"          "$(seat 102 "$S2" "$FC2" "$M3" "$ZOE")"
    accepted "a project review is seated"        "$(seat 201 "$S3" "$PC1" "$M5")"
    start "$S3" >/dev/null
    accepted "and completed, notes with a comma" "$(complete "$S3" Approved "Well prepared, strong project")"
    accepted "a youth is postponed"              "$(postpone "$S4")"
    scouts=$(rows "$SCOUTS")
    adults=$(rows "$ADULTS")
    notes=$(field_of "$S3" 24)

    echo
    echo "== $FIRST crashes; $SECOND carries on with the same folder and port =="
    stop_server
    start_server "$SECOND" "$DIR"
    chk "no youth lost or added"                  "$(rows "$SCOUTS")" "$scouts"
    chk "no adult lost or added"                  "$(rows "$ADULTS")" "$adults"
    chk "the running review is still running"     "$(status_of "$S1")" "InProgress"
    chk "the convening board is still convening"  "$(status_of "$S2")" "Seated"
    chk "the finished review is still Approved"   "$(field_of "$S3" 19)" "Approved"
    chk "its notes are unchanged"                 "$(field_of "$S3" 24)" "$notes"
    chk "the postponed youth is still postponed"  "$(status_of "$S4")" "Postponed"
    chk "the waiting youth is still waiting"      "$(status_of "$S5")" "Registered"
    chk "both boards' adults are still committed" "$(busy)" "6"
    # Compared as bytes: "Ångström" in UTF-8, whatever this shell's locale.
    chk "the accented adult's name read back as UTF-8" \
        "$(awk -F, -v i="$ZOE" 'NR>1 && $2==i {printf "%s", $3}' "$ADULTS" | od -An -tx1 | tr -d ' \n')" \
        "c3856e67737472c3b66d"
    refused "a committed chair can't be double-booked" "$(seat 201 "$S5" "$FC1" "$M5")"

    youth Holloway Harriet 1006 Final
    S6=$(S Holloway Harriet 1006)
    chk "a new walk-in continues the numbering"   "$(field_of "$S6" 3)" "W6"
    accepted "the running review completes"       "$(complete "$S1" Approved)"
    accepted "the convening board starts"         "$(start "$S2")"
    accepted "the comma-named adult can be seated" "$(seat 101 "$S6" "$FC1" "$M1" "$JR")"
    chk "in room 101"                              "$(adult_room "$JR")" "101"

    echo
    echo "== $SECOND stops; $FIRST comes back and reads what $SECOND wrote =="
    stop_server
    start_server "$FIRST" "$DIR"
    chk "the review $SECOND completed is Approved" "$(status_of "$S1")" "Completed"
    chk "the board $SECOND started is running"     "$(status_of "$S2")" "InProgress"
    chk "the board $SECOND seated is convening"    "$(status_of "$S6")" "Seated"
    accepted "and $FIRST can complete it"         "$(complete "$S2" Approved)"
    accepted "and start the other"                 "$(start "$S6")"
    accepted "and complete it"                     "$(complete "$S6" Approved)"
    chk "every adult is released at the end"       "$(busy)" "0"
    stop_server
}

handoff java windows
handoff windows java

echo
if [ "$FAIL" -eq 0 ]; then
    echo "HAND-OFF: PASS — $PASS checks"
else
    echo "HAND-OFF: FAIL — $FAIL of $((PASS + FAIL)) checks failed"
    exit 1
fi
