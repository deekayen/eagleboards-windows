# CLAUDE.md — working notes for this repo

Guidance for anyone (human or AI) making changes here. Read this before editing.

## What this is

The Windows version of the Review Board Scheduler: a C#/.NET 10 port of the
Java project `deekayen/eagleboards-java` (which was reconstructed from an inherited
binary). Check-in stations still sign in through a website; the admin computer
runs a native WPF app instead of the Java app's browser pages.

- `EagleBoards.Core`: records, CSV/properties storage, `BoardService`,
  SignUpGenius and pre-registration import, `BoardRules`, `BoardCheck`,
  `SchedulerLogic`.
  **Every rule lives here.** The website and the app both call `BoardService`.
- `EagleBoards.Web`: `CheckInServer` (Kestrel) serves the embedded check-in
  pages in `wwwroot/` plus every endpoint the Java server had, same wire
  formats. Only the check-in endpoints answer the network; the rest answer
  loopback only.
- `EagleBoards.Server`: headless host taking the Java jar's options. Used by
  the tests.
- `EagleBoards.App`: the WPF app (`EagleBoards.exe`). Start-up window, the
  main window (`MainWindow`: a sidebar of pages, Event / Results / People /
  Settings, where Event is the youth queue, the rooms, and a details pane
  that builds and runs the selected youth's board), `AdminWindow`,
  `HelpWindow`. Also accepts the jar's options.

## Build, test, verify

- `dotnet build -c Release` (SDK 10; `global.json` pins the major).
- `dotnet test tests/EagleBoards.Tests -c Release`: rules (ported case for
  case from the Java `test-seat-conflicts.js`), storage format, lifecycle,
  auto-select, and each deliberate divergence from Java.
- `bash scripts/test-board-evening.sh`: the Java project's end-to-end HTTP
  evening, unchanged except for the launch lines and section 18's check of
  the Admin window's choice lists (Java reads admin.html; this reads
  `AdminWindow.xaml.cs`). New scenarios added in the Java repo are copied here
  and into the Mac version. `EB_JAR=<jar>` runs it against
  the Java server; it passes against both. `EB_KEEP=1` keeps the data files so
  two builds' output can be diffed.
- `EB_JAR=<jar> bash scripts/test-handoff.sh`: the two versions taking turns
  on one event folder (one "crashes", the other carries on, the first comes
  back), both directions, including a comma-named and an accented adult.
  Build the jar from the Java project's source (`./mvnw -DskipTests package`
  in a fresh clone), never from the old checkout that holds live data. Run it
  after touching storage, the record formats, or a board operation.
- `dotnet run --project tests/EagleBoards.UiSnapshots -c Release -- <dir> [--dark]`:
  renders every window off-screen to PNG over synthetic data, light or dark.
  This is how to look at a UI change without driving the desktop. Check both
  themes. `-- --demo docs/images [--dark]` re-renders the README's demo GIF,
  stills and social preview through the window's own handlers; do it after a
  UI change.
- **CI is the acceptance gate** (`.github/workflows/build.yml`, windows-latest):
  build, unit tests, the evening, snapshots (artifact), self-contained publish,
  and a smoke test of the published exe (`scripts/smoke-test-exe.sh`). Prefer
  pushing and reading the run over re-running the whole suite locally.
- **Releases** (`.github/workflows/release.yml`): Run workflow (today's date)
  or push a `vYYYY.MM.DD[.N]` tag. The tag is the only place the version lives;
  it's stamped into the exe with `-p:Version`, and local builds say `dev`. The
  workflow re-runs every test, refuses data files in the build, checks the exe
  reports the version, smoke-tests it, then attaches the exe and its SHA-256.
  Don't run `smoke-test-exe.sh` on the owner's desktop: it opens the window.
  CI runs on Windows Server, so before an event release someone should open
  the exe once on a Windows 10 PC: volunteers' donated machines may still run
  it, and the Fluent theme is only tested there by hand.

## The golden rules

1. **Never commit PII or secrets.** CSV/spreadsheets, dated `YYYY-MM-DD/`
   folders, the adult history, `.env` are gitignored and blocked by
   `scripts/hooks/pre-commit`. Install it per clone:
   `git config core.hooksPath scripts/hooks`.
2. **Never touch real data.** Tests, snapshots and practice runs use synthetic
   names in a temp folder. `C:\eagleboards` on the owner's machine is the live
   data folder, and the old Java checkout also holds live data: don't read,
   screenshot or test against either. Anything that defaults to "the usual data
   folder" (the start-up window does) must be pointed at a sandbox when
   exercised. The snapshot harness passes it an explicit `AppSettings` for
   exactly this reason.
3. **Keep the data format identical to the Java version.** Shared files, no
   quoting, `,`→`~` and newline→`+` in values, `yyyy-MM-dd_HH:mm±hhmm` times,
   column orders as in each record's `AllColumns`. A format change strands
   every existing event folder. `StorageTests` pins this.
4. **Rules go in Core, enforced by `BoardService`, explained by the window.**
   `BoardCheck.Review` lists a proposed board's problems (block or warn) and
   the window shows them as the board is built; warnings make Seat say "Seat
   anyway" (same-unit is override-only by design). Anything absolute must also
   be refused server-side, because the HTTP endpoints are another way in. Add a unit test for any rule you change
   and a case in `test-board-evening.sh` if it changes what the server does.
5. **Don't let a test or harness pop UI on the owner's desktop.** WPF runs
   `App.OnStartup` on the first message pump even without `Run()`, so harnesses
   use a plain `Application` plus `Theme.xaml`, never `EagleBoards.App.App`.
   Test servers use `-bind 127.0.0.1` (off the network, no firewall prompt).

## Conventions / gotchas

- **Auto-select** (`SchedulerLogic.AutoSelect`) weighs the whole waiting
  line: of every legal board it takes the one leaving the most other waiting
  youth able to get a full board at once, then the one using up the fewest
  chair qualifications, then the one keeping the most flexible adults, then
  the adults who have waited longest to volunteer since last free
  (`FreeSinceTimes`). The same algorithm and test cases are in the Java
  (`proposeBoard`) and Mac (`BoardSuggestion`) versions; change all three
  together.
- **Board lifecycle:** Registered → Seated → InProgress → Completed, or
  Registered → Postponed. Seat convenes the board (scout outside, members read
  the paperwork); Start Review brings the scout in. Room timers run on minutes
  since the last status change, so each phase is timed separately.
  `Verified` survives on legacy records only.
- **Composition:** board of review 3–6 (4–6 confirm), project review 2–6; the
  chair must be a qualified Chair for that board type and sit on the board; no
  adult on two boards, disabled (`Room = N/A`), or Unavailable for that type;
  same-unit adults warn with an override down to the national floor (one
  member from outside the unit).
- **Picks** (`Sel`) are the operator's work in progress, saved immediately.
  Clicking around never clears them; only Start over and seating do. Opening a
  seated board shows its members; it doesn't pick them. To choose someone
  yourself: remove, add, then Fill the rest (`SchedulerLogic.FillBoard`)
  completes the board around the operator's choices. Changing a board that's
  already seated or in review (someone has to leave) is
  `BoardService.ChangeBoardMembers`, from the details pane: the same member
  checks as seating (`CheckComposition`), leavers freed, timer not reset.
  Never ask the operator to hand-edit member lists in the admin tables.
- `BoardService.Changed` fires on the thread that made the change (often a
  Kestrel thread). The window marshals to the dispatcher and debounces.
- **The UI follows Microsoft's Windows (Fluent) guidance, not the Java app's
  look.** `ThemeSetup` loads WPF's Fluent theme (`ThemeMode`, still marked
  experimental: WPF0001 is suppressed in the csproj) and then `Theme.xaml`,
  whose styles are `BasedOn` Fluent's; a style that isn't falls back to the
  old look. Colours only from theme resources (`{DynamicResource ...}`),
  never hex, so light, dark, high contrast and the accent colour work.
  Spacing in multiples of 4; the Windows type ramp (Semibold, never Bold);
  sentence case; no abbreviations on screen (the files keep "InProgress",
  "N/A": show `Display.*` words). Messages go in an `InfoBar` where they're
  about, not pop-ups; success needs no message when the screen already shows
  it. Dialogs (`AppDialog`) only for the irreversible or an override, with
  verb buttons that answer the title. Status is text plus an icon, never
  colour alone.
- User-facing text says **event**, not "tonight" or "evening": boards happen
  in the daytime too.
- Values read back from the files carry the format's escapes (a comma saved as
  `~`); show names, notes and member lists through `Display.Text` /
  `Display.List`, never raw. The snapshot harness restarts from the saved
  files before rendering so this shows up.
- Nothing polls. Every change raises `BoardService.Changed`; the only thing
  that moves by itself is time, recounted by a timer on each minute (stamps
  are to the minute). `RefreshTimeSecs` stays in the config for the Java
  version and is ignored here.
- WPF gotchas met here: Fluent's accent button ignores `_` access keys unless
  the content is an `AccessText`; event handlers can't be wired inside a
  style setter (put context menus on the list, not the item style); a
  `VisualBrush` trims empty margins unless given an absolute viewbox (the
  snapshot harness); adorners don't follow their element's visibility.
- **Branding is district-neutral.** Never put a district or council name in the
  app or the pages.
- Endpoints are a contract with the check-in pages and the evening test:
  change the client before the server, and keep formats frozen.

## Workflow

- Branch `main`, remote `origin` = `deekayen/eagleboards-windows` (private).
- Fixes that also apply to the Java project are worth porting there (and vice
  versa); the two are separate repositories with no shared history.
