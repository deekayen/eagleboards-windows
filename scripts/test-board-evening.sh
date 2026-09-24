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
#
# Sections 9-19 then work through what goes wrong on the night: malformed
# and replayed requests, every out-of-order step, adults and scouts signing
# in twice, boards moved between rooms, a room renamed or deleted under a
# board, a name with a comma in it, two operators seating the same chair at
# once, the server restarting mid-evening, a room switched between
# Project and Final, a recorded result corrected on the Admin page, and
# what an adult says at sign-in (Wood Badge, "no thanks", whom they support).
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

# ===================================================================
# The rest is what goes wrong on the night: late arrivals, a button
# pressed twice, a room that turns out to be locked, the laptop that
# reboots. Every section starts and ends with nobody committed, so a
# failure points at the section that caused it.
# ===================================================================

# xscout <last> <first> <unit> <board-type> -> echoes the record id
xscout() {
    post --data "Last=$1&First=$2&Email=x$3@example.org&UnitType=Troop&Unit=$3&UnitName=Troop$3&BoardType=$4" \
        "$B/register-youth" || echo "seed failed: scout $1" >&2
    echo "SCOUT:$1:$2:$3"
}

# resign_adult <id> -- the same adult taps through the sign-in page again,
# which is what happens when someone isn't sure the first one took. They
# pick the same roles as before; the form has no way to know they are seated.
resign_adult() {
    _rest=${1#ADULT:}
    _l=${_rest%%:*};  _rest=${_rest#*:}
    _f=${_rest%%:*};  _u=${_rest#*:}
    _pr=$(awk -F, -v i="$1" 'NR>1 && $2==i {print $10}' "$ADULTS")
    _fb=$(awk -F, -v i="$1" 'NR>1 && $2==i {print $11}' "$ADULTS")
    post --data "Last=$_l&First=$_f&Email=again@example.org&UnitType=Troop&Unit=$_u&ProjectReview=$_pr&FinalBoard=$_fb" \
        "$B/register-adult"
}

start()    { act /inprogress-board --data-urlencode "ScoutID=$1"; }
complete() { act /complete-board --data-urlencode "ScoutID=$1" --data-urlencode "Result=$2"; }
reset()    { act /reset-board --data-urlencode "ScoutID=$1"; }
postpone() { act /postpone-board --data-urlencode "ScoutID=$1"; }
room_scout() { awk -F, -v r="$1" 'NR>1 && $2==r {print $5}' "$ROOMS"; }

LATE1=$(xscout Okonkwo Barnaby 3101 Final)
LATE2=$(xscout Pellegrino Casimir 3102 Final)
LATE3=$(xscout Quarshie Dmitri 3103 Project)
LATE4=$(xscout Rourke Eamon 3104 Final)
LATE5=$(xscout Sandoval Florian 3105 Final)
LATE6=$(xscout Trevelyan Gustav 3106 Final)
LATE7=$(xscout Umarov Hamish 3107 Final)
LATE8=$(xscout Valdivia Ignatius 3108 Final)
LATE9=$(xscout Wojcik Jericho 3109 Final)
LATE10=$(xscout Yamamoto Kasimir 3110 Final)
LATE11=$(xscout Zabala Leander 3111 Final)

# --------------------------------------------- 9. malformed and replayed
echo
echo "== 9. requests the UI would never send =="

# A bookmark, a stale tab, a replay -- each must be a refusal the operator
# can read, never a 500 and never a silent OK.
for ep in /seat-board /inprogress-board /complete-board /postpone-board /reset-board /room-change; do
    refused "$ep with no parameters at all" "$(act "$ep")"
done
refused "an unknown scout cannot be started" "$(start SCOUT:Nobody:Here:0)"
refused "an unknown scout cannot be completed" "$(complete SCOUT:Nobody:Here:0 Approved)"
refused "an unknown room is refused" "$(seat 999 "$LATE1" "$FC1" "$M1" "$M2")"
refused "an unknown member id is refused" "$(seat 101 "$LATE1" "$FC1" "$M1" ADULT:Nobody:Here:0)"

# The size rules count entries, so without a duplicate check the same person
# twice made two people into a legal board of three.
refused "one adult listed twice cannot make up a Final board's numbers" \
    "$(seat 101 "$LATE1" "$FC1" "$M1" "$M1")"
refused "the chair listed twice cannot make up a project review's numbers" \
    "$(seat 200A "$LATE3" "$PC1" "$PC1")"
chk "none of that seated anyone" "$(n_status Seated)" "0"
chk "or committed anyone"        "$(busy_adults)" "0"

accepted "a real board seats in room 101" "$(seat 101 "$LATE1" "$FC1" "$M1" "$M2")"
refused  "and nobody else can be seated in room 101" "$(seat 101 "$LATE2" "$FC2" "$M3" "$M4")"
refused  "the same scout cannot be seated twice" "$(seat 102 "$LATE1" "$FC2" "$M3" "$M4")"
chk "the second scout is still waiting" "$(status_of "$LATE2")" "Registered"

# ------------------------------------------------- 10. every wrong step
echo
echo "== 10. each step only from the status before it =="

refused "a Completed scout cannot be seated again"  "$(seat 102 "$SF1" "$FC2" "$M3" "$M4")"
refused "a Postponed scout cannot be seated"        "$(seat 102 "$SF9" "$FC2" "$M3" "$M4")"
refused "a waiting scout cannot be started"         "$(start "$LATE2")"
refused "a Completed scout cannot be started"       "$(start "$SF1")"
refused "a waiting scout cannot be completed"       "$(complete "$LATE2" Approved)"
refused "a Completed scout cannot be completed again" "$(complete "$SF1" NotApproved)"
refused "a waiting scout has nothing to reset"      "$(reset "$LATE2")"
refused "a Completed scout cannot be reset"         "$(reset "$SF1")"
refused "a Postponed scout cannot be reset"         "$(reset "$SF9")"
refused "a Completed scout cannot be postponed"     "$(postpone "$SF1")"
chk "scout 1's result survived all of that" \
    "$(awk -F, -v i="$SF1" 'NR>1 && $2==i {print $18}' "$SCOUTS")" "Completed"

accepted "Start Review" "$(start "$LATE1")"
refused  "pressing Start Review twice is refused" "$(start "$LATE1")"
refused  "a review under way cannot be postponed" "$(postpone "$LATE1")"

# The dialog offers exactly three results; anything else is not a result.
refused "a made-up result is refused"         "$(complete "$LATE1" Maybe)"
refused "a misspelled result is refused"      "$(complete "$LATE1" Approvd)"
refused "a result in the wrong case is refused" "$(complete "$LATE1" approved)"
refused "an empty result is refused"          "$(complete "$LATE1" "")"
# Postponed is decided before any board meets the scout (the Postpone
# button); once a board has met them, its decision is one of the three.
refused "Postponed is not a board result"     "$(complete "$LATE1" Postponed)"
chk "and the review is still running" "$(status_of "$LATE1")" "InProgress"
chk "with its board still in the room" "$(adult_room "$FC1")" "101"

accepted "Adjourned is a result" "$(complete "$LATE1" Adjourned)"
chk "and is what was recorded" \
    "$(awk -F, -v i="$LATE1" 'NR>1 && $2==i {print $19}' "$SCOUTS")" "Adjourned"

seat 200A "$LATE3" "$PC1" "$M7" >/dev/null
start "$LATE3" >/dev/null
accepted "NotApproved is a result" "$(complete "$LATE3" NotApproved)"
chk "nobody committed after section 10" "$(busy_adults)" "0"

# ----------------------------------------------- 11. signing in twice
echo
echo "== 11. signing in again mid-board changes nothing =="

seat 102 "$LATE2" "$FC2" "$M3" "$M4" >/dev/null
start "$LATE2" >/dev/null
resign_adult "$M3"
resign_adult "$FC2"
chk "a member who signs in again stays in their room" "$(adult_room "$M3")" "102"
chk "so does the chair"                               "$(adult_room "$FC2")" "102"
chk "and the chair is still qualified to chair" \
    "$(awk -F, -v i="$FC2" 'NR>1 && $2==i {print $11}' "$ADULTS")" "Chair"
post --data "Last=Pellegrino&First=Casimir&Email=again@example.org&UnitType=Troop&Unit=3102&BoardType=Final" \
    "$B/register-youth"
chk "a scout who signs in again is still under review" "$(status_of "$LATE2")" "InProgress"
chk "in the same room"                                "$(room_of "$LATE2")" "102"
chk "and is still one scout, not two" \
    "$(awk -F, -v i="$LATE2" 'NR>1 && $2==i {n++} END {print n+0}' "$SCOUTS")" "1"
accepted "their board still completes" "$(complete "$LATE2" Approved)"
chk "and releases the re-signed member" "$(adult_room "$M3")" ""
chk "nobody committed after section 11" "$(busy_adults)" "0"

# ----------------------------------------------------- 12. moving rooms
echo
echo "== 12. a board moves to another room =="

seat 103 "$LATE4" "$FC3" "$M5" "$M6" >/dev/null
accepted "swap an occupied room with an empty one" \
    "$(act /room-change --data-urlencode "RmID1=ROOM:103" --data-urlencode "RmID2=ROOM:106")"
chk "the scout moved"       "$(room_of "$LATE4")" "106"
chk "the chair moved"       "$(adult_room "$FC3")" "106"
chk "the members moved"     "$(adult_room "$M5")$(adult_room "$M6")" "106106"
chk "the old room is free"  "$(room_scout ROOM:103)" ""
chk "the new room names the scout" "$(room_scout ROOM:106)" "Eamon Rourke"

accepted "the old room takes the next board" "$(seat 103 "$LATE5" "$FC1" "$M1" "$M2")"
accepted "swap two occupied rooms" \
    "$(act /room-change --data-urlencode "RmID1=ROOM:103" --data-urlencode "RmID2=ROOM:106")"
chk "each scout took the other's room" "$(room_of "$LATE4") $(room_of "$LATE5")" "103 106"
chk "and each chair went with their own board" "$(adult_room "$FC3") $(adult_room "$FC1")" "103 106"
refused "swapping with a room that does not exist is refused" \
    "$(act /room-change --data-urlencode "RmID1=ROOM:103" --data-urlencode "RmID2=ROOM:nope")"
chk "and moved nobody" "$(adult_room "$FC3")" "103"

start "$LATE4" >/dev/null
accepted "a moved board completes in its new room" "$(complete "$LATE4" Approved)"
chk "releasing only its own adults" "$(adult_room "$FC3") $(adult_room "$FC1")" " 106"
start "$LATE5" >/dev/null; complete "$LATE5" Approved >/dev/null
chk "nobody committed after section 12" "$(busy_adults)" "0"

# -------------------------------------- 13. the room changes under a board
echo
echo "== 13. a room renamed or deleted on the Admin page mid-board =="

# The Admin page can edit rooms at any time. The review still happened, so
# its result must be recordable and its adults must come back.
seat 104 "$LATE6" "$FC1" "$M1" "$M2" >/dev/null
start "$LATE6" >/dev/null
post --data-urlencode "!nativeeditor_status=updated" --data-urlencode "gr_id=ROOM:104" \
     --data-urlencode "Room=104 Annex" "$B/room-update"
accepted "a board whose room was renamed still completes" "$(complete "$LATE6" Approved)"
chk "its adults are released" "$(adult_room "$FC1")$(adult_room "$M1")$(adult_room "$M2")" ""
chk "and the renamed room is free again" "$(room_scout ROOM:104)" ""
accepted "and takes the next board" "$(seat 104 "$LATE7" "$FC1" "$M1" "$M2")"
reset "$LATE7" >/dev/null

seat 105 "$LATE8" "$FC2" "$M3" "$M4" >/dev/null
start "$LATE8" >/dev/null
post --data-urlencode "!nativeeditor_status=deleted" --data-urlencode "gr_id=ROOM:105" "$B/room-update"
accepted "a board whose room was deleted still completes" "$(complete "$LATE8" Approved)"
chk "its adults are released" "$(adult_room "$FC2")$(adult_room "$M3")$(adult_room "$M4")" ""

seat 107 "$LATE9" "$FC3" "$M5" "$M6" >/dev/null
post --data-urlencode "!nativeeditor_status=deleted" --data-urlencode "gr_id=ROOM:107" "$B/room-update"
accepted "a board whose room was deleted can be reset" "$(reset "$LATE9")"
chk "and its adults are not left committed to a room that is gone" \
    "$(adult_room "$FC3")$(adult_room "$M5")$(adult_room "$M6")" ""
chk "nobody committed after section 13" "$(busy_adults)" "0"

# ------------------------------------------------ 14. names with commas
echo
echo "== 14. a name with a comma in it =="

# MemberIDs is a comma-separated list, so an id with a comma in it split in
# two and that adult could never be seated.
post --data "Last=Whitmore%2C+Jr.&First=Lysander&Email=a31@example.org&UnitType=Troop&Unit=2031&ProjectReview=Member&FinalBoard=Member" \
    "$B/register-adult"
JR=$(awk -F, 'NR>1 && $4=="Lysander" {print $2}' "$ADULTS")
chk "their id carries no comma" "$JR" "ADULT:Whitmore~ Jr.:Lysander:2031"
accepted "and they can be seated" "$(seat 101 "$LATE7" "$FC1" "$M1" "$JR")"
chk "in room 101" "$(adult_room "$JR")" "101"
start "$LATE7" >/dev/null
accepted "and released" "$(complete "$LATE7" Approved)"
chk "nobody committed after section 14" "$(busy_adults)" "0"

# ------------------------------------------- 15. two operators at once
echo
echo "== 15. two laptops seat the same chair at the same moment =="

seat 101 "$LATE9"  "$FC1" "$M1" "$M2" > "$WORK/race1" &
r1=$!
seat 102 "$LATE10" "$FC1" "$M3" "$M4" > "$WORK/race2" &
r2=$!
wait "$r1" "$r2"
chk "exactly one of them was accepted" \
    "$(cat "$WORK/race1" "$WORK/race2" | grep -c '|OK')" "1"
chk "exactly one board convened"     "$(n_status Seated)" "1"
chk "the chair is committed once"    "$(busy_adults)" "3"
reset "$LATE9" >/dev/null; reset "$LATE10" >/dev/null
chk "nobody committed after section 15" "$(busy_adults)" "0"

# ----------------------------------------------- 16. the laptop restarts
echo
echo "== 16. the server restarts in the middle of the evening =="

# A laptop that sleeps, a Pi that loses power, a window closed by mistake:
# boards convening and running when it goes down must still be there -- and
# still finishable -- when it comes back.
seat 101 "$LATE9"  "$FC1" "$M1" "$M2" >/dev/null
start "$LATE9" >/dev/null
seat 102 "$LATE10" "$FC2" "$M3" "$M4" >/dev/null
scouts_before=$(awk 'NR>1' "$SCOUTS" | wc -l | tr -d ' ')
adults_before=$(awk 'NR>1' "$ADULTS" | wc -l | tr -d ' ')

kill "$SRV" 2>/dev/null
wait "$SRV" 2>/dev/null
for _ in 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 19 20; do
    curl -s -o /dev/null "$B/index.html" || break
    sleep 1
done
# shellcheck disable=SC2086
( cd "$WORK" && exec $LAUNCH "$TARGET" \
    -a AdultHistory.csv -c config.properties -port "$PORT" -d run -bind 127.0.0.1 ) >> "$WORK/server.log" 2>&1 &
SRV=$!
if curl -sf --retry 40 --retry-delay 1 --retry-connrefused --retry-all-errors \
     -o /dev/null "$B/index.html"; then
    ok "the server came back on the same data"
else
    bad "the server never came back on port $PORT"
fi

chk "no scout was lost or duplicated" "$(awk 'NR>1' "$SCOUTS" | wc -l | tr -d ' ')" "$scouts_before"
chk "no adult was lost or duplicated" "$(awk 'NR>1' "$ADULTS" | wc -l | tr -d ' ')" "$adults_before"
chk "the running review is still running"   "$(status_of "$LATE9")"  "InProgress"
chk "the convening board is still convening" "$(status_of "$LATE10")" "Seated"
chk "both boards' adults are still committed" "$(busy_adults)" "6"
refused "a committed chair still cannot be double-booked" \
    "$(seat 103 "$LATE11" "$FC1" "$M5" "$M6")"
accepted "the running review completes after the restart" "$(complete "$LATE9" Approved)"
accepted "the convening board starts after the restart"   "$(start "$LATE10")"
accepted "and completes"                                   "$(complete "$LATE10" Approved)"
accepted "the comma-named adult is still seatable after the restart" \
    "$(seat 101 "$LATE11" "$FC1" "$M1" "$JR")"
reset "$LATE11" >/dev/null

# ------------------------------------------- 17. a room changes board type
echo
echo "== 17. a room switched between Project and Final on the Admin page =="

# When the evening's mix turns out different from the plan, rooms get
# switched on the Admin page. A room's type is only where the UI suggests a
# board should go (process_seat.js asks before crossing it); the size and
# chair rules, and the room-card timers, all follow the SCOUT's board type.
# Switching a room must never loosen a rule or disturb a board already in it.
room_type() { awk -F, -v r="$1" 'NR>1 && $2==r {print $4}' "$ROOMS"; }
set_room_type() {
    post --data-urlencode "!nativeeditor_status=updated" --data-urlencode "gr_id=ROOM:$1" \
         --data-urlencode "BoardType=$2" "$B/room-update"
}

TYPE1=$(xscout Achterberg Maximilian 3201 Final)
TYPE2=$(xscout Brannigan Nikolai 3202 Project)
TYPE3=$(xscout Castellanos Octavian 3203 Final)

set_room_type 201A Final
chk "project room 201A is now a Final room" "$(room_type ROOM:201A)" "Final"
refused "a Final board of 2 is still refused there" "$(seat 201A "$TYPE1" "$FC1" "$M1")"
accepted "a Final board of 3 seats there" "$(seat 201A "$TYPE1" "$FC1" "$M1" "$M2")"

set_room_type 106 Project
chk "final room 106 is now a Project room" "$(room_type ROOM:106)" "Project"
refused "a Member still cannot chair a project review there" "$(seat 106 "$TYPE2" "$M3" "$M4")"
refused "nor can a Final-only chair" "$(seat 106 "$TYPE2" "$FC2" "$M3")"
accepted "a project review of 2 seats there" "$(seat 106 "$TYPE2" "$PC1" "$M3")"

# A room whose type does not match the scout is the UI's confirm, not a
# refusal: the operator may knowingly put a board in the "wrong" kind of room.
set_room_type 102 Project
accepted "a Final board may still go in a Project room" "$(seat 102 "$TYPE3" "$FC2" "$M5" "$M6")"

# Switch rooms back while the boards are sitting in them.
start "$TYPE1" >/dev/null
set_room_type 201A Project
set_room_type 106 Final
set_room_type 102 Final
chk "switching a room keeps the board in it" "$(room_scout ROOM:201A)" "Maximilian Achterberg"
chk "and its adults"                          "$(adult_room "$FC1")" "201A"
chk "and the review still under way"          "$(status_of "$TYPE1")" "InProgress"
chk "the scout's own board type is untouched" \
    "$(awk -F, -v i="$TYPE1" 'NR>1 && $2==i {print $12}' "$SCOUTS")" "Final"
refused "the switched room cannot be double-booked" "$(seat 201A "$LATE11" "$FC3" "$M7" "$M8")"

accepted "the Final board completes in what is now a Project room" "$(complete "$TYPE1" Approved)"
start "$TYPE2" >/dev/null
accepted "the project review completes in what is now a Final room" "$(complete "$TYPE2" Approved)"
start "$TYPE3" >/dev/null
accepted "and the third board completes too" "$(complete "$TYPE3" Approved)"
chk "every board kept the chair it was seated with" \
    "$(chair_of "$TYPE1") $(chair_of "$TYPE2") $(chair_of "$TYPE3")" "$FC1 $PC1 $FC2"
chk "the switched rooms are free again" \
    "$(room_scout ROOM:201A)$(room_scout ROOM:106)$(room_scout ROOM:102)" ""
chk "nobody committed after section 17" "$(busy_adults)" "0"

# ---------------------------------------- 18. correcting a recorded result
echo
echo "== 18. a result corrected on the Admin page =="

# Two mistakes the record has to survive: the wrong result clicked, and the
# right result recorded against the wrong scout. The Admin page's Boards tab
# is the path for both. admin_edit posts exactly what its grid posts when a
# cell is edited -- one field per request -- and checks the grid would call
# it saved.
admin_edit() { # <scout-id> <field> <value>
    _r=$(curl -s -X POST --data-urlencode "!nativeeditor_status=updated" \
        --data-urlencode "gr_id=$1" --data-urlencode "$2=$3" "$B/youth-update")
    case "$_r" in
        *'type="updated"'*) ;;
        *) bad "admin edit of $2 on $1 was not saved: $_r" ;;
    esac
}
result_of() { awk -F, -v i="$1" 'NR>1 && $2==i {print $19}' "$SCOUTS"; }

# The Admin window's choice lists must match what the app acts on. It is a
# WPF window with no endpoint to ask, so this reads how AdminWindow.xaml.cs
# builds each list and what the vocabulary it builds them from contains.
ADMIN_SRC="$ROOT/src/EagleBoards.App/AdminWindow.xaml.cs"
VOCAB_SRC="$ROOT/src/EagleBoards.Core/Records/Vocabulary.cs"
vocab_list() { # <class> -> the identifiers in that class's "All = [...]"
    awk -v c="class $1" '$0 ~ c {f=1} f && /All = \[/ {print; exit}' "$VOCAB_SRC" \
        | awk -F'[][]' '{print $2}' | tr -d ' ' | tr , ' '
}

# Every status offered must be one the app acts on. The Java version once
# offered "Waiting", which nothing recognised, so choosing it -- the obvious
# way to send a scout back to the queue -- left them where nothing could seat
# them.
case "$(grep 'StatusChoices =' "$ADMIN_SRC")" in
    *'BoardStatus.All'*) ok "the Admin window's statuses are the app's own statuses" ;;
    *) bad "the Admin window builds its Status list from something other than BoardStatus.All" ;;
esac
case " $(vocab_list BoardStatus) " in
    *" Registered "*) ok "and include Registered, to undo a result on the wrong scout" ;;
    *) bad "the Admin window cannot set a scout back to Registered" ;;
esac

# And every Result offered must be a board's decision -- one /complete-board
# would record. It once offered "Postponed", which is the Status of a scout
# sent away before any board met them, not something a board decides.
case "$(grep 'ResultChoices =' "$ADMIN_SRC")" in
    *'BoardResults.All'*) ok "the Admin window's results are the board's decisions" ;;
    *) bad "the Admin window builds its Result list from something other than BoardResults.All" ;;
esac
chk "which are exactly Approved, Adjourned and NotApproved" \
    "$(vocab_list BoardResults)" "Approved Adjourned NotApproved"

FIX1=$(xscout Dunleavy Peregrine 3301 Final)
SENT=$(xscout Fairweather Rupert 3304 Final)
RIGHT=$(xscout Esterhazy Quentin 3302 Final)
WRONG=$(xscout Esterbrook Quentin 3303 Final)

# The wrong result clicked.
seat 101 "$FIX1" "$FC1" "$M1" "$M2" >/dev/null
start "$FIX1" >/dev/null
complete "$FIX1" Approved >/dev/null
admin_edit "$FIX1" Result NotApproved
chk "a mis-clicked result can be corrected"   "$(result_of "$FIX1")" "NotApproved"
chk "without reopening the board"             "$(status_of "$FIX1")" "Completed"
chk "or losing who sat on it"                 "$(chair_of "$FIX1")" "$FC1"
chk "or committing its adults again"          "$(busy_adults)" "0"

# The right result on the wrong scout: two scouts with the same first name,
# and the operator picked the one still waiting outside.
seat 102 "$WRONG" "$FC2" "$M3" "$M4" >/dev/null
start "$WRONG" >/dev/null
complete "$WRONG" Approved >/dev/null
board_chair=$(awk -F, -v i="$WRONG" 'NR>1 && $2==i {print $20}' "$SCOUTS")
board_members=$(awk -F, -v i="$WRONG" 'NR>1 && $2==i {print $22}' "$SCOUTS")

admin_edit "$RIGHT" Status Completed
admin_edit "$RIGHT" Result Approved
admin_edit "$RIGHT" BoardChair "$board_chair"
admin_edit "$RIGHT" BoardMembers "$board_members"
admin_edit "$WRONG" Status Registered
admin_edit "$WRONG" Result ""
admin_edit "$WRONG" BoardChair ""
admin_edit "$WRONG" BoardMembers ""

chk "the scout who was reviewed now holds the result" \
    "$(status_of "$RIGHT") $(result_of "$RIGHT")" "Completed Approved"
chk "with the board that reviewed them" \
    "$(awk -F, -v i="$RIGHT" 'NR>1 && $2==i {print $20}' "$SCOUTS")" "$board_chair"
chk "the other is back in the queue with no result" \
    "$(status_of "$WRONG")|$(result_of "$WRONG")" "Registered|"
refused "the reviewed scout cannot be seated again" "$(seat 103 "$RIGHT" "$FC3" "$M5" "$M6")"
accepted "the other can be seated for their real board" \
    "$(seat 103 "$WRONG" "$FC3" "$M5" "$M6")"
start "$WRONG" >/dev/null
accepted "and it completes"                  "$(complete "$WRONG" NotApproved)"
chk "recording their own result"             "$(result_of "$WRONG")" "NotApproved"
chk "and their own chair"                    "$(chair_of "$WRONG")" "$FC3"
chk "while the first scout's result stands"  "$(result_of "$RIGHT")" "Approved"

# Completed by mistake, when the scout had in fact been sent away unprepared
# and never saw a board: they become Postponed, which carries no result.
seat 104 "$SENT" "$FC1" "$M1" "$M2" >/dev/null
start "$SENT" >/dev/null
complete "$SENT" NotApproved >/dev/null
admin_edit "$SENT" Status Postponed
admin_edit "$SENT" Result ""
chk "a scout sent away is recorded as Postponed, with no result" \
    "$(status_of "$SENT")|$(result_of "$SENT")" "Postponed|"
refused "and cannot be seated again that night" "$(seat 104 "$SENT" "$FC1" "$M1" "$M2")"
chk "nobody committed after section 18"      "$(busy_adults)" "0"

# ------------------------------------ 19. what an adult says at sign-in
echo
echo "== 19. Wood Badge, 'no thanks', and the scout an adult came to support =="

# adults.csv and the adult history both end with 17 WoodBadge, 18 Supporting.
adult_col() { awk -F, -v i="$1" -v c="$2" 'NR>1 && $2==i {print $c}' "$ADULTS"; }
history_col() { awk -F, -v i="$1" -v c="$2" 'NR>1 && $2==i {print $c}' "$WORK/AdultHistory.csv"; }

RSVP=$(xscout Galloway Tobias 3401 Final)
post --data "Last=Hargrove&First=Ines&Email=a41@example.org&UnitType=Troop&Unit=3401&ProjectReview=Member&FinalBoard=Member&WoodBadge=Y&Supporting=$RSVP|SCOUT:Nobody:Here:0" \
    "$B/register-adult"
LEADER="ADULT:Hargrove:Ines:3401"
chk "Wood Badge is recorded"           "$(adult_col "$LEADER" 17)" "Y"
chk "and the scouts they came to support" "$(adult_col "$LEADER" 18)" "$RSVP|SCOUT:Nobody:Here:0"
chk "neither is kept in the history for next month" \
    "$(history_col "$LEADER" 17)|$(history_col "$LEADER" 18)" "|"

post --data "Last=Hargrove&First=Ines&Email=a41@example.org&UnitType=Troop&Unit=3401&ProjectReview=Member&FinalBoard=Member&WoodBadge=yes&Supporting=" \
    "$B/register-adult"
chk "signing in again says what is true now" "$(adult_col "$LEADER" 18)" ""
chk "and Wood Badge is Y or nothing, never free text" "$(adult_col "$LEADER" 17)" ""

# "No thanks" is stored as Unavailable for that kind of board; the server
# refuses to seat them on one even when the request skips the scheduler.
post --data "Last=Ibarra&First=Juno&Email=a42@example.org&UnitType=Troop&Unit=3402&ProjectReview=Unavailable&FinalBoard=Member" \
    "$B/register-adult"
NOPROJ="ADULT:Ibarra:Juno:3402"
chk "'no thanks' to proposal reviews is recorded" "$(adult_col "$NOPROJ" 10)" "Unavailable"
PROJ=$(xscout Jaramillo Kai 3403 Project)
refused "they are not seated on a proposal review" "$(seat 200A "$PROJ" "$PC1" "$NOPROJ")"
chk "and nobody was committed" "$(busy_adults)" "0"
accepted "but they can sit on a Final board" "$(seat 101 "$RSVP" "$FC1" "$M1" "$NOPROJ")"
reset "$RSVP" >/dev/null

# The sign-in page lists RSVP'd scouts from this endpoint; keep it serving them.
post --data-urlencode "!nativeeditor_status=inserted" --data-urlencode "gr_id=SCOUT:Rsvp:Only:3999" \
     --data "Last=Rsvp&First=Only&UnitType=Troop&Unit=3999&BoardType=Final" "$B/youth-scheduled-update"
chk "an RSVP not yet signed in can be chosen at the door" \
    "$(curl -s "$B/youth-scheduled-cells?cols=First,Last,UnitName" | grep -c 'SCOUT:Rsvp:Only:3999')" "1"
chk "nobody committed after section 19" "$(busy_adults)" "0"


echo
echo "== the evening ends clean =="
chk "no board left convening"  "$(n_status Seated)" "0"
chk "no review left running"   "$(n_status InProgress)" "0"
chk "every adult released"     "$(busy_adults)" "0"

# ------------------------------------------------------------------- verdict
echo
if [ "$FAIL" -gt 0 ]; then
    echo "BOARD EVENING: FAIL — $FAIL of $((PASS + FAIL)) checks failed"
    echo "--- server log (tail) ---"
    tail -30 "$WORK/server.log"
    exit 1
fi

echo "BOARD EVENING: PASS — $PASS checks"
