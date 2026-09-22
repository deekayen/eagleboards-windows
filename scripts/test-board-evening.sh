#!/usr/bin/env bash
# ------------------------------------------------------------------------
# test-board-evening.sh — end-to-end regression test for a board evening.
#
# scripts/test-seat-conflicts.js pins down the composition RULES as pure
# functions. This pins down what the SERVER does with them across a whole
# evening: boards convening and starting, adults committed to one room and
# released when the review finishes, boards postponed and reset, and the
# rules that must hold even when the request does not come from our own UI.
#
#   dotnet build -c Release && bash scripts/test-board-evening.sh
#   EB_PORT=18096 bash scripts/test-board-evening.sh
#   EB_JAR=/path/to/eagleboardscheduler-*.jar bash scripts/test-board-evening.sh
#
# The last form runs the SAME checks against the Java version this was ported
# from. The script came over from that project unchanged apart from how the
# server is launched; passing against both is the evidence that the port's
# server behaves like the original.
#
# Everything runs against a throwaway data directory seeded with SYNTHETIC
# names on a spare port. It never reads Master_AdultHistory.csv and never
# touches a live instance -- see CLAUDE.md.
#
# Portability: bash, curl, awk and the .NET SDK (or a JDK with EB_JAR), no
# Python. Runs on Git-for-Windows and Linux.
#
# The scenario is the district's real shape, and deliberately short of chairs:
#
#   14 scouts (9 Final, 5 Project)     12 rooms (7 Final, 5 Project)
#   30 adults, of whom only FIVE may chair anything:
#       3 can chair a Final board, 3 a Project review, one of them both.
#
# So the evening is capped at five concurrent boards no matter how many rooms
# are free -- which is the constraint the scheduler actually has to survive.
# ------------------------------------------------------------------------

set -u

PORT="${EB_PORT:-18096}"
B="http://127.0.0.1:$PORT"
ROOT=$(cd "$(dirname "$0")/.." && pwd)
WORK="${TMPDIR:-/tmp}/eb-evening-$$"
SRV=""

PASS=0
FAIL=0

ok()  { echo "  ok  : $*"; PASS=$((PASS + 1)); }
bad() { echo "FAIL  : $*"; FAIL=$((FAIL + 1)); }

# chk <what> <got> <want>
chk() {
    if [ "$2" = "$3" ]; then
        ok "$1"
    else
        bad "$1"
        echo "          got:  '$2'"
        echo "          want: '$3'"
    fi
}

cleanup() {
    if [ -n "$SRV" ]; then
        kill "$SRV" 2>/dev/null
        wait "$SRV" 2>/dev/null
    fi
    # EB_KEEP=1 leaves the data files behind, e.g. to diff what two builds wrote.
    if [ -n "${EB_KEEP:-}" ]; then
        echo "kept: $WORK"
    else
        rm -rf "$WORK"
    fi
}
trap cleanup EXIT INT TERM

# ------------------------------------------------------------ 0. preflight
for t in curl awk; do
    command -v "$t" >/dev/null 2>&1 || { echo "MISSING: $t"; exit 1; }
done

if [ -n "${EB_JAR:-}" ]; then
    command -v java >/dev/null 2>&1 || { echo "MISSING: java (needed for EB_JAR)"; exit 1; }
    [ -f "$EB_JAR" ] || { echo "MISSING: $EB_JAR"; exit 1; }
    LAUNCH="java -jar"
    TARGET="$EB_JAR"
else
    command -v dotnet >/dev/null 2>&1 || { echo "MISSING: dotnet"; exit 1; }
    # Release if built, else Debug; the headless server takes the jar's options.
    TARGET=""
    for cfg in Release Debug; do
        d="$ROOT/src/EagleBoards.Server/bin/$cfg/net10.0/EagleBoards.Server.dll"
        if [ -z "$TARGET" ] && [ -f "$d" ]; then
            TARGET="$d"
        fi
    done
    if [ -z "$TARGET" ]; then
        echo "MISSING: no built server. Run 'dotnet build -c Release' first."
        exit 1
    fi
    LAUNCH="dotnet"
fi

echo "testing: ${TARGET#"$ROOT"/}"
echo

mkdir -p "$WORK"
cp "$ROOT/config.properties" "$WORK/config.properties"
# Synthetic, header-only. The real adult history is PII and never goes near
# a test run.
echo 'Type,ID,Last,First,Email,Phone,UnitType,Unit,UnitName,ProjectReview,FinalBoard,RegTime,Room,Flags,Sel,BoardHistory' \
    > "$WORK/AdultHistory.csv"

# $LAUNCH is deliberately unquoted: "java -jar" is two words.
# -bind 127.0.0.1 keeps the .NET server off the network (and clear of a
# Windows Firewall prompt); the Java jar finds no such interface and warns.
# shellcheck disable=SC2086
( cd "$WORK" && exec $LAUNCH "$TARGET" \
    -a AdultHistory.csv -c config.properties -port "$PORT" -d run -bind 127.0.0.1 ) > "$WORK/server.log" 2>&1 &
SRV=$!

if ! curl -sf --retry 40 --retry-delay 1 --retry-connrefused --retry-all-errors \
     -o /dev/null "$B/index.html"; then
    echo "server never became ready on port $PORT"
    cat "$WORK/server.log"
    exit 1
fi

SCOUTS="$WORK/run/scouts.csv"
ADULTS="$WORK/run/adults.csv"
ROOMS="$WORK/run/rooms.csv"

# ------------------------------------------------------------- 0a. helpers
# All the write handlers store() their records inside the same lock they
# answer from, so once curl returns, the CSVs on disk are already current --
# these read them directly rather than re-parsing the grid XML.
post() { curl -sf -X POST "$@" -o /dev/null; }

# act <path> [curl args...] -> "HTTPCODE|body". The server answers "OK." on
# success and a plain-text ERROR line on refusal, so the body is the verdict.
act() {
    _p=$1
    shift
    _out=$(curl -s -w '\n%{http_code}' -X POST "$@" "$B$_p")
    echo "$(echo "$_out" | tail -1)|$(echo "$_out" | sed '$d' | tr -d '\r\n')"
}

# Column positions in the CSVs the app writes.
#   scouts.csv  17 Room  18 Status  20 BoardChair  21 BoardChairID
#   adults.csv  10 ProjectReview  11 FinalBoard  13 Room  15 Sel
status_of()   { awk -F, -v i="$1" 'NR>1 && $2==i {print $18}' "$SCOUTS"; }
room_of()     { awk -F, -v i="$1" 'NR>1 && $2==i {print $17}' "$SCOUTS"; }
chair_of()    { awk -F, -v i="$1" 'NR>1 && $2==i {print $21}' "$SCOUTS"; }
adult_room()  { awk -F, -v i="$1" 'NR>1 && $2==i {print $13}' "$ADULTS"; }
busy_adults() { awk -F, 'NR>1 && $13!="" {n++} END {print n+0}' "$ADULTS"; }
n_status()    { awk -F, -v s="$1" 'NR>1 && $18==s {n++} END {print n+0}' "$SCOUTS"; }
empty_rooms() { awk -F, 'NR>1 && $5=="" {n++} END {print n+0}' "$ROOMS"; }

# Success is exactly what ebAction() in eb-data.js calls success: HTTP 200
# with a body starting "OK". Anything else is a refusal, and the handlers do
# not agree on wording -- /seat-board answers "ERROR: ..." while
# /complete-board answers "Invalid Scout Status ..." -- so these test the
# contract the UI actually reads rather than any one handler's phrasing.
refused() {
    _code=${2%%|*}
    _body=${2#*|}
    if [ "$_code" = "500" ]; then
        bad "$1 -- HTTP 500. That is a crash, not a rule."
        return
    fi
    case "$_body" in
        OK*) bad "$1 -- the server ACCEPTED it" ;;
        "")  bad "$1 -- refused with an empty body; the UI has nothing to show" ;;
        *)   ok "$1" ;;
    esac
}

accepted() {
    case "${2#*|}" in
        OK*) ok "$1" ;;
        *)   bad "$1 -- refused with '${2#*|}'" ;;
    esac
}

# seat <room> <scout> <chair> <member>... -- chair always sits on the board.
seat() {
    _rm=$1; _sc=$2; _ch=$3
    shift 3
    _ids=$_ch
    for _m in "$@"; do _ids="$_ids,$_m"; done
    act /seat-board \
        --data-urlencode "RoomID=ROOM:$_rm" \
        --data-urlencode "ScoutID=$_sc" \
        --data-urlencode "ChairID=$_ch" \
        --data-urlencode "MemberIDs=$_ids"
}

run_board() { # <room> <scout> -- convene, bring the scout in, record a result
    act /inprogress-board --data-urlencode "ScoutID=$2" >/dev/null
    act /complete-board \
        --data-urlencode "RoomID=ROOM:$1" \
        --data-urlencode "ScoutID=$2" \
        --data-urlencode "Result=Approved" >/dev/null
}

# ------------------------------------------------------------------ 1. seed
LAST="Abernathy Blackwood Castellano Duxbury Ellsworth Fairbanks Grimaldi Hollingsworth
      Ivanovic Jankowski Kaminski Lindqvist Montgomery Nakamura Oyelaran Pemberton
      Quintanilla Rasmussen Stavropoulos Thornbury Uddin Vandermeer Whitfield Xiong
      Yarborough Zeltser Ashworth Bellweather Crowninshield Devereaux"
FIRST="Anneliese Bartholomew Clementine Desmond Evangeline Fitzgerald Genevieve Horatio
       Isadora Jebediah Katarina Leopold Marguerite Nathaniel Ophelia Percival
       Quintessa Roderick Seraphina Thaddeus Ulyana Vivienne Wilhelmina Xavier
       Yolanda Zacharias Augustina Benedikt Cordelia Dashiell"

set -- $LAST;  LASTS="$*"
set -- $FIRST; FIRSTS="$*"

# nth <word-list> <1-based index>. Both arguments are read into locals FIRST:
# `set --` overwrites the positional parameters, so reading $2 after it would
# pick the second word of the LIST, not the index -- which silently gave every
# adult the same name and let this whole test pass on degenerate data.
nth() {
    _nth_list=$1
    _nth_idx=$2
    set -- $_nth_list
    shift $((_nth_idx - 1))
    echo "$1"
}

# adult <n> <unit> <project-role> <final-role>  -> echoes the record id
adult() {
    _l=$(nth "$LASTS" "$1")
    _f=$(nth "$FIRSTS" "$1")
    post --data "Last=$_l&First=$_f&Email=a$1@example.org&UnitType=Troop&Unit=$2&UnitName=Troop$2&ProjectReview=$3&FinalBoard=$4" \
        "$B/register-adult" || echo "seed failed: adult $_l" >&2
    echo "ADULT:$_l:$_f:$2"
}

for r in 101 102 103 104 105 106 107; do
    post --data "!nativeeditor_status=inserted&gr_id=ROOM:$r&Room=$r&BoardType=Final" "$B/room-update" \
        || echo "seed failed: room $r" >&2
done
for r in 200A 200B 201A 201B 202; do
    post --data "!nativeeditor_status=inserted&gr_id=ROOM:$r&Room=$r&BoardType=Project" "$B/room-update" \
        || echo "seed failed: room $r" >&2
done

# The five chair-qualified people. FC1 can chair either kind, so committing
# them to a Final board is what drops the evening to two Project chairs.
FC1=$(adult 1 2001 Chair       Chair)
FC2=$(adult 2 2002 Member      Chair)
FC3=$(adult 3 2003 Member      Chair)
PC1=$(adult 4 2004 Chair       Member)
PC2=$(adult 5 2005 Chair       Member)

MEMBERS=""
for i in 6 7 8 9 10 11 12 13 14 15 16 17 18 19 20 21 22 23 24 25; do
    MEMBERS="$MEMBERS $(adult "$i" "$((2000 + i))" Member Member)"
done
set -- $MEMBERS
M1=$1;  M2=$2;  M3=$3;  M4=$4;  M5=$5;  M6=$6;  M7=$7;  M8=$8
M9=$9;  shift 9
M10=$1; M11=$2; M12=$3; M13=$4; M14=$5; M15=$6

# Two who cannot do a project review, and three who share a unit with scouts
# 1-3 so the same-unit rule has something real to catch in the UI.
adult 26 2026 Unavailable Member  >/dev/null
adult 27 2027 Unavailable Member  >/dev/null
adult 28 1001 Member      Unavailable >/dev/null
adult 29 1002 Member      Unavailable >/dev/null
adult 30 1003 Member      Unavailable >/dev/null

SLAST="Aldridge Bram Carrington Dunmore Everly Fenwick Gallagher Harrington
       Iverson Jessup Kirkland Lockhart Merriweather Northcott"
SFIRST="Alexander Beauregard Cormac Dorian Emmett Finnegan Gideon Huckleberry
        Ignatius Jasper Kingston Lachlan Montgomery Nicodemus"
set -- $SLAST;  SLASTS="$*"
set -- $SFIRST; SFIRSTS="$*"

scout() { # <n> <board-type> -> echoes the record id
    _l=$(nth "$SLASTS" "$1")
    _f=$(nth "$SFIRSTS" "$1")
    _u=$((1000 + $1))
    post --data "Last=$_l&First=$_f&Email=s$1@example.org&UnitType=Troop&Unit=$_u&UnitName=Troop$_u&BoardType=$2" \
        "$B/register-youth" || echo "seed failed: scout $_l" >&2
    echo "SCOUT:$_l:$_f:$_u"
}

SF1=$(scout 1 Final); SF2=$(scout 2 Final); SF3=$(scout 3 Final)
SF4=$(scout 4 Final); SF5=$(scout 5 Final); SF6=$(scout 6 Final)
SF7=$(scout 7 Final); SF8=$(scout 8 Final); SF9=$(scout 9 Final)
SP1=$(scout 10 Project); SP2=$(scout 11 Project); SP3=$(scout 12 Project)
SP4=$(scout 13 Project); SP5=$(scout 14 Project)

echo "== 1. the evening as seeded =="
chk "12 rooms"  "$(awk 'NR>1' "$ROOMS"  | wc -l | tr -d ' ')" "12"
chk "30 adults" "$(awk 'NR>1' "$ADULTS" | wc -l | tr -d ' ')" "30"
chk "14 scouts" "$(awk 'NR>1' "$SCOUTS" | wc -l | tr -d ' ')" "14"
chk "only 5 adults may chair anything" \
    "$(awk -F, 'NR>1 && ($10=="Chair" || $11=="Chair") {n++} END {print n+0}' "$ADULTS")" "5"

# ------------------------------------------------- 2. rules the server keeps
# These bypass our UI entirely. process_seat.js refuses all of them too, but
# the browser is the normal way in, not the only one, and a board seated past
# the UI is one nobody finds out about until they read the result.
echo
echo "== 2. composition rules hold without the UI =="

refused "a Final board of 2 is refused (GTA 8.0.0.3 floor)" \
    "$(seat 101 "$SF1" "$FC1" "$M1")"
refused "a Final board of 7 is refused (GTA 8.0.0.3 ceiling)" \
    "$(seat 101 "$SF1" "$FC1" "$M1" "$M2" "$M3" "$M4" "$M5" "$M6")"
refused "a project review of 1 is refused" \
    "$(seat 200A "$SP1" "$PC1")"
refused "a project review of 7 is refused" \
    "$(seat 200A "$SP1" "$PC1" "$M1" "$M2" "$M3" "$M4" "$M5" "$M6")"

# Chair is binding. When the qualified chairs are all busy the answer is to
# promote someone on the Admin page, never to let a Member hold the gavel.
refused "a plain Member may not chair a Final board" \
    "$(seat 101 "$SF1" "$M1" "$M2" "$M3")"
refused "a Project chair may not chair a Final board" \
    "$(seat 101 "$SF1" "$PC1" "$M1" "$M2")"
refused "a plain Member may not chair a project review" \
    "$(seat 200A "$SP1" "$M1" "$M2")"
refused "the chair must be sitting on the board" \
    "$(act /seat-board --data-urlencode "RoomID=ROOM:101" --data-urlencode "ScoutID=$SF1" \
        --data-urlencode "ChairID=$FC2" --data-urlencode "MemberIDs=$M1,$M2,$M3")"
refused "an unknown chair id is refused" \
    "$(act /seat-board --data-urlencode "RoomID=ROOM:101" --data-urlencode "ScoutID=$SF1" \
        --data-urlencode "ChairID=NOBODY" --data-urlencode "MemberIDs=$M1,$M2,$M3")"
refused "a board with no members at all is refused" \
    "$(act /seat-board --data-urlencode "RoomID=ROOM:101" --data-urlencode "ScoutID=$SF1" \
        --data-urlencode "ChairID=$FC1" --data-urlencode "MemberIDs=")"

chk "none of that seated anyone" "$(n_status Seated)" "0"

# ------------------------------------------------------- 3. the chair ceiling
echo
echo "== 3. five chairs cap the evening at five concurrent boards =="

accepted "Final board 1, room 101" "$(seat 101 "$SF1" "$FC1" "$M1" "$M2")"
accepted "Final board 2, room 102" "$(seat 102 "$SF2" "$FC2" "$M3" "$M4")"
accepted "Final board 3, room 103" "$(seat 103 "$SF3" "$FC3" "$M5" "$M6")"
accepted "project review 1, room 200A" "$(seat 200A "$SP1" "$PC1" "$M7")"
accepted "project review 2, room 200B" "$(seat 200B "$SP2" "$PC2" "$M8")"

chk "5 boards convening"        "$(n_status Seated)" "5"
chk "13 adults committed"       "$(busy_adults)" "13"
chk "7 rooms sit empty for want of a chair" "$(empty_rooms)" "7"

# Seating convenes only -- the scout is still outside. GTA 8.0.3.0 #8.
chk "seating does not start the review" "$(n_status InProgress)" "0"

refused "a seated chair cannot take a second board" \
    "$(seat 104 "$SF4" "$FC1" "$M9" "$M10")"
refused "a seated member cannot take a second board" \
    "$(seat 104 "$SF4" "$FC1" "$M1" "$M9")"
chk "the sixth scout is still waiting" "$(status_of "$SF4")" "Registered"
chk "the double-booked chair is still in room 101" "$(adult_room "$FC1")" "101"

# ----------------------------------------------- 4. an adult who goes home
echo
echo "== 4. an adult who leaves is out of the pool until re-enabled =="

# The Disable button writes Room="N/A"; auto-select skips them and the grid
# hides them, but the server has to refuse them too.
post --data-urlencode "!nativeeditor_status=updated" --data-urlencode "gr_id=$M11" \
     --data-urlencode "Room=N/A" "$B/adult-update"
chk "adult marked disabled" "$(adult_room "$M11")" "N/A"
refused "a disabled adult cannot be seated" "$(seat 104 "$SF4" "$FC1" "$M11" "$M12")"

post --data-urlencode "!nativeeditor_status=updated" --data-urlencode "gr_id=$M11" \
     --data-urlencode "Room=" "$B/adult-update"
chk "and is available again once re-enabled" "$(adult_room "$M11")" ""

# ------------------------------------------------------ 5. convene, then run
echo
echo "== 5. convene, then review, then complete =="

r=$(act /complete-board --data-urlencode "RoomID=ROOM:101" --data-urlencode "ScoutID=$SF1" \
        --data-urlencode "Result=Approved")
refused "a result cannot be recorded while the board is still convening" "$r"
chk "the scout is still Seated" "$(status_of "$SF1")" "Seated"

accepted "Start Review brings the scout in" \
    "$(act /inprogress-board --data-urlencode "ScoutID=$SF1")"
chk "board is InProgress" "$(status_of "$SF1")" "InProgress"

accepted "and now the result can be recorded" \
    "$(act /complete-board --data-urlencode "RoomID=ROOM:101" --data-urlencode "ScoutID=$SF1" \
        --data-urlencode "Result=Approved")"
chk "board is Completed" "$(status_of "$SF1")" "Completed"

# The whole evening turns on this: finishing a review must hand the adults
# back, or the fifth board is the last board.
echo
echo "== 6. completing a review releases its adults =="
chk "the chair is free again"   "$(adult_room "$FC1")" ""
chk "member 1 is free again"    "$(adult_room "$M1")" ""
chk "member 2 is free again"    "$(adult_room "$M2")" ""
chk "10 adults still committed" "$(busy_adults)" "10"
chk "room 101 is back in the pool" \
    "$(awk -F, 'NR>1 && $3=="101" {print $5}' "$ROOMS")" ""

accepted "the freed chair seats the next scout" "$(seat 104 "$SF4" "$FC1" "$M1" "$M2")"
chk "scout 4 is convening in room 104" "$(room_of "$SF4")" "104"
chk "chair moved to room 104"          "$(adult_room "$FC1")" "104"

# ------------------------------------------------------- 7. postpone / reset
echo
echo "== 7. postpone and reset =="

accepted "a waiting scout can be postponed" \
    "$(act /postpone-board --data-urlencode "ScoutID=$SF9")"
chk "scout 9 Postponed" "$(status_of "$SF9")" "Postponed"

refused "a scout whose board has convened cannot be postponed" \
    "$(act /postpone-board --data-urlencode "ScoutID=$SF2")"
chk "scout 2 is still Seated" "$(status_of "$SF2")" "Seated"

busy_before=$(busy_adults)
accepted "a convening board can be reset" \
    "$(act /reset-board --data-urlencode "ScoutID=$SF2")"
chk "scout 2 back to Registered" "$(status_of "$SF2")" "Registered"
chk "and has no room"            "$(room_of "$SF2")" ""
chk "its chair was released"     "$(adult_room "$FC2")" ""
chk "its 2 members were released too" "$(busy_adults)" "$((busy_before - 3))"

accepted "a review already in progress can be reset" \
    "$(act /inprogress-board --data-urlencode "ScoutID=$SP1")"
accepted "reset from InProgress"  "$(act /reset-board --data-urlencode "ScoutID=$SP1")"
chk "project scout 1 back to Registered" "$(status_of "$SP1")" "Registered"
chk "its project chair was released"     "$(adult_room "$PC1")" ""

# --------------------------------------------------------- 8. run it to the end
echo
echo "== 8. the rest of the evening, five chairs at a time =="

# Explicit rather than greedy: a regression test should assert the outcome it
# expects, not whatever the scheduler managed on the day.
for pair in "103 $SF3" "200B $SP2" "104 $SF4"; do
    set -- $pair
    run_board "$1" "$2"
done

seat 101 "$SF2" "$FC1" "$M1" "$M2"   >/dev/null; run_board 101 "$SF2"
seat 102 "$SF5" "$FC2" "$M3" "$M4"   >/dev/null; run_board 102 "$SF5"
seat 103 "$SF6" "$FC3" "$M5" "$M6"   >/dev/null; run_board 103 "$SF6"
seat 104 "$SF7" "$FC1" "$M1" "$M2"   >/dev/null; run_board 104 "$SF7"
seat 105 "$SF8" "$FC2" "$M3" "$M4"   >/dev/null; run_board 105 "$SF8"
seat 200A "$SP1" "$PC1" "$M7"        >/dev/null; run_board 200A "$SP1"
seat 200B "$SP3" "$PC2" "$M8"        >/dev/null; run_board 200B "$SP3"
seat 201A "$SP4" "$PC1" "$M7"        >/dev/null; run_board 201A "$SP4"
seat 201B "$SP5" "$PC2" "$M8"        >/dev/null; run_board 201B "$SP5"

chk "13 scouts completed"      "$(n_status Completed)" "13"
chk "1 scout postponed"        "$(n_status Postponed)" "1"
chk "nobody left waiting"      "$(n_status Registered)" "0"
chk "no board left convening"  "$(n_status Seated)" "0"
chk "no review left running"   "$(n_status InProgress)" "0"
chk "every adult released"     "$(busy_adults)" "0"
chk "every room empty"         "$(empty_rooms)" "12"

# Every completed board must name a chair who was actually allowed to chair
# it -- the record is what the district keeps, so a wrong chair on it is the
# failure that outlives the evening.
bad_chairs=0
while IFS= read -r line; do
    [ -z "$line" ] && continue
    sid=${line%%|*}
    btype=${line##*|}
    cid=$(chair_of "$sid")
    if [ -z "$cid" ]; then
        bad_chairs=$((bad_chairs + 1))
        continue
    fi
    col=11
    [ "$btype" = "Project" ] && col=10
    role=$(awk -F, -v i="$cid" -v c="$col" 'NR>1 && $2==i {print $c}' "$ADULTS")
    [ "$role" = "Chair" ] || bad_chairs=$((bad_chairs + 1))
done <<EOF
$(awk -F, 'NR>1 && $18=="Completed" {print $2"|"$12}' "$SCOUTS")
EOF
chk "every completed board was chaired by a qualified chair" "$bad_chairs" "0"

# ------------------------------------------------------------------- verdict
echo
if [ "$FAIL" -gt 0 ]; then
    echo "BOARD EVENING: FAIL — $FAIL of $((PASS + FAIL)) checks failed"
    echo "--- server log (tail) ---"
    tail -30 "$WORK/server.log"
    exit 1
fi

echo "BOARD EVENING: PASS — $PASS checks"
