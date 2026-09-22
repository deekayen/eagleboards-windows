# CLAUDE.md — working notes for this repo

Guidance for anyone (human or AI) making changes here. Read this before editing.

## What this is

The Windows version of the Review Board Scheduler: a C#/.NET 10 port of the
Java project `deekayen/eagleboards` (which was reconstructed from an inherited
binary). Check-in stations still sign in through a website; the admin computer
runs a native WPF app instead of the Java app's browser pages.

- `EagleBoards.Core`: records, CSV/properties storage, `BoardService`,
  SignUpGenius and pre-registration import, `BoardRules`, `SchedulerLogic`.
  **Every rule lives here.** The website and the app both call `BoardService`.
- `EagleBoards.Web`: `CheckInServer` (Kestrel) serves the embedded check-in
  pages in `wwwroot/` plus every endpoint the Java server had, same wire
  formats. Only the check-in endpoints answer the network; the rest answer
  loopback only.
- `EagleBoards.Server`: headless host taking the Java jar's options. Used by
  the tests.
- `EagleBoards.App`: the WPF app (`EagleBoards.exe`). Start-up window, the
  scheduler (`MainWindow`), `AdminWindow`, `SettingsWindow`, `HelpWindow`.
  Also accepts the jar's options.

## Build, test, verify

- `dotnet build -c Release` (SDK 10; `global.json` pins the major).
- `dotnet test tests/EagleBoards.Tests -c Release`: rules (ported case for
  case from the Java `test-seat-conflicts.js`), storage format, lifecycle,
  auto-select, and each deliberate divergence from Java.
- `bash scripts/test-board-evening.sh`: the Java project's end-to-end HTTP
  evening, unchanged except for the launch line. `EB_JAR=<jar>` runs it against
  the Java server; it passes against both. `EB_KEEP=1` keeps the data files so
  two builds' output can be diffed.
- `dotnet run --project tests/EagleBoards.UiSnapshots -c Release -- <dir>`:
  renders every window off-screen to PNG over synthetic data. This is how to
  look at a UI change without driving the desktop.
- **CI is the acceptance gate** (`.github/workflows/build.yml`, windows-latest):
  build, unit tests, the evening, snapshots (artifact), self-contained publish,
  and a smoke test of the published exe. Prefer pushing and reading the run
  over re-running the whole suite locally.

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
   The window may warn and offer overrides (same-unit is override-only by
   design); anything absolute must also be refused server-side, because the
   HTTP endpoints are another way in. Add a unit test for any rule you change
   and a case in `test-board-evening.sh` if it changes what the server does.
5. **Don't let a test or harness pop UI on the owner's desktop.** WPF runs
   `App.OnStartup` on the first message pump even without `Run()`, so harnesses
   use a plain `Application` plus `Theme.xaml`, never `EagleBoards.App.App`.
   Test servers use `-bind 127.0.0.1` (off the network, no firewall prompt).

## Conventions / gotchas

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
  Clicking around never clears them; only Clear picks and seating do. Looking
  at a seated board *highlights* its members; it doesn't tick them.
- `BoardService.Changed` fires on the thread that made the change (often a
  Kestrel thread). The window marshals to the dispatcher and debounces.
- WPF + WinForms in one project: WinForms implicit usings are removed; the only
  WinForms type (ColorDialog) is fully qualified.
- **Branding is district-neutral.** Never put a district or council name in the
  app or the pages.
- Endpoints are a contract with the check-in pages and the evening test:
  change the client before the server, and keep formats frozen.

## Workflow

- Branch `main`, remote `origin` = `deekayen/eagleboards-windows` (private).
- Fixes that also apply to the Java project are worth porting there (and vice
  versa); the two are separate repositories with no shared history.
