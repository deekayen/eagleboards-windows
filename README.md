# Eagle Board Scheduler for Windows

Check-in and room scheduling for an evening of Eagle Scout boards of review.
Youth and adults sign in on a **website** from any tablet or laptop at the
venue; the person running the evening uses a **native Windows app** on the
admin computer to seat boards, start reviews, record results and manage rooms.

This is a Windows port of the Java [Eagle Board Scheduler](https://github.com/deekayen/eagleboards)
(itself reconstructed from an inherited binary). The server logic was ported
to C#, the check-in pages are the same, and the browser admin pages were
replaced by a WPF app. It reads and writes the **same data files** as the Java
version, so an existing data folder keeps working and either version can pick
up where the other left off (just not both at once: they would fight over the
port and the files).

```
  Check-in stations (any browser)             Admin computer
     /  /youth_register  /adult_register      EagleBoards.exe
              |                                 |  scheduler window, admin tables,
              |  HTTP, venue network            |  settings, reports
              +---------------> Kestrel <-------+  (in-process, no browser)
                                   |
                              BoardService  --  CSV files in the data folder
```

## Running an event night

1. Put `EagleBoards.exe` anywhere (e.g. in the data folder, `C:\eagleboards`)
   and double-click it. Nothing to install: it carries its own .NET runtime.
2. The start-up window asks for:
   - **Data folder**: holds `config.properties`, the adult history
     (`Master_AdultHistory.csv`), and one folder per event night named by date.
     A new folder gets a commented `config.properties`; a missing adult history
     can be started empty.
   - **Event date**: defaults to today; each night gets its own `YYYY-MM-DD`
     folder.
   - **Check-in network**: the venue Wi-Fi. Serving only that network keeps
     the site off Hyper-V, WSL and VPN adapters. "All networks" is there too.
   - **Port** (8080) and whether to **import tonight's SignUpGenius sign-ups**
     (needs `SUG_KEY=...` in a `.env` file in the data folder, or the `SUG_KEY`
     environment variable).
3. Press **Start**. The first time, Windows Firewall asks whether to allow the
   scheduler on the network: allow **private** networks, or the stations can't
   connect.
4. Point each check-in station's browser at the address in the scheduler's
   status bar (right-click it to copy), e.g. `http://192.168.1.23:8080`.
5. Add rooms (**+ Room**) and run the evening. **Help** in the app walks
   through seating, starting and completing boards.

Closing the scheduler stops the check-in site. Everything is saved as it
happens; there is no "save" step.

### Command line

The app accepts the Java jar's options, so an existing launcher keeps working
and skips the start-up window:

```bat
EagleBoards.exe -d 2026-09-22 -a Master_AdultHistory.csv -c config.properties -port 8080 -bind 192.168. -sugkey %SUG_KEY%
```

`-d` data folder for the night · `-a` adult history · `-c` config · `-p`
district pre-registration CSV · `-sugkey`/`-sugid` SignUpGenius · `-port` ·
`-bind` IPv4 prefix to serve on (`127.0.0.1` keeps it off the network) · `-v`
verbose log. `EagleBoards.Server` (for tests, or a machine with no desktop)
takes the same options and runs the check-in site with no window.

## Getting the exe

Download `EagleBoards.exe` from the repository's
[**Releases**](https://github.com/deekayen/eagleboards-windows/releases) page.
The version is in the window title. The exe isn't code-signed, so the first
time Windows may say it *protected your PC*: choose **More info**, then
**Run anyway**.

**Cutting a release.** On GitHub go to **Actions → release → Run workflow**.
That releases `main` as today's date (`v2026.09.22`); type a version to
override it, e.g. `2026.09.22.1` for a second release the same day. Or tag a
commit and push the tag (`git tag v2026.09.22 && git push origin v2026.09.22`).
Either way the workflow builds, runs every test, checks the exe carries no data
files, smoke-tests the exact exe, and only then publishes it with a SHA-256
checksum. The version is stamped in from the tag; there's no file to bump.

Every push to `main` also leaves a build of the latest code as the
**EagleBoards-win-x64** artifact on its Actions run (its title says
`YYYY.MM.DD-ci.N`); use a release for an event night. To build it yourself you
need the .NET 10 SDK:

```bat
dotnet publish src/EagleBoards.App -c Release -r win-x64 -o publish
```

## What's different from the Java version

Deliberate changes. The server-side ones each have a unit test in
`tests/EagleBoards.Tests`:

- **The admin side is a Windows app.** `/scheduler`, `/admin`, `/configure`
  and `/help` are gone from the website. A sign-in in the browser shows up in
  the scheduler immediately rather than at the next poll.
- **Stations can only check in.** The endpoints the old admin pages used
  (seat, complete, record edits, room changes, settings) still exist with the
  same wire formats, but answer only requests from the admin computer itself.
  From the network, the youth and adult lists return names and units only,
  never phones, emails or birthdates.
- **Seating is stricter where the old checks had gaps.** One adult listed
  twice no longer counts as two members, and an adult marked Unavailable for
  that board type is refused wherever they appear in the list (the browser
  only checked members listed before the chair, the server not at all).
- **Picks are used up by seating.** Seating a board clears its members'
  checkboxes; before, they stayed ticked and blocked auto-select for the next
  youth. Looking at a board that is already seated highlights its members
  instead of ticking them, for the same reason.
- **Sign-in numbering.** Submitting the sign-in form twice keeps your place
  in the queue instead of renumbering you to the back, and a youth with no
  email is no longer counted as pre-registered because some sign-up also had
  none.
- **SignUpGenius.** Imported adults get their real ID straight away (the Java
  import left them all as `ADULT:::` until a restart, so a sign-in that night
  created a duplicate history record). The API key is never written to the log.
- **Phone numbers** from pre-registration are normalized from their digits;
  the Java code formatted the raw text, so `(555) 123-4567` came out garbled.
- **Files.** Every save writes a temporary file and swaps it in, so a crash
  can't leave half a file. Old files in the Windows ANSI code page (accented
  names in an adult history from the 2019 build) and files saved by Excel with
  a byte-order mark are read correctly.
- **HTTP details.** A refused board action is `409` with the reason as text
  (the Java server used `304`, which may not carry a body). Success is still
  `200 OK.`

## Development

```
src/EagleBoards.Core     records, CSV storage, BoardService (every rule), SignUpGenius
src/EagleBoards.Web      Kestrel check-in server + the embedded check-in pages (wwwroot/)
src/EagleBoards.Server   headless console host (tests, no-desktop use)
src/EagleBoards.App      the WPF admin app (EagleBoards.exe)
tests/EagleBoards.Tests       xUnit: composition rules, storage format, lifecycle, auto-select
tests/EagleBoards.UiSnapshots renders every window off-screen to PNG over synthetic data
scripts/test-board-evening.sh the Java project's HTTP end-to-end evening, run against this server
```

```bat
dotnet build -c Release
dotnet test tests/EagleBoards.Tests -c Release
bash scripts/test-board-evening.sh
dotnet run --project tests/EagleBoards.UiSnapshots -c Release -- snapshots
```

`EB_JAR=/path/to/eagleboardscheduler-*.jar bash scripts/test-board-evening.sh`
runs the same evening against the Java server, for comparison.

See [CLAUDE.md](CLAUDE.md) for the working rules (data privacy above all).

## Privacy

The data folder holds personal information about minors. Keep it on the admin
computer, out of shared and cloud-synced folders, and out of git: `.gitignore`
and `scripts/hooks/pre-commit` refuse CSV and spreadsheet files, dated event
folders, the adult history and `.env`. Install the hook once per clone with
`git config core.hooksPath scripts/hooks`.

## License

Apache License 2.0. See [LICENSE](LICENSE) and [NOTICE](NOTICE).
