# What changed

What an operator or contributor would notice, newest first. Every release is
on the [Releases page](https://github.com/deekayen/eagleboards-windows/releases)
with its exe; this says why to update. Decisions behind the changes are in
[`docs/decisions`](docs/decisions/README.md) and, for behavior all three
versions share, `SPEC.md` in `deekayen/eagleboards-shared`.

## Not yet released

### Fixed

- **A project review is no longer proposed a final board's room**, nor a
  board of review a project room (found at the 2026-10-27 event; SPEC.md
  D-24). A board takes only a free room set up for its own type. When there
  is none, the bar under *Board members* says why and what to do: select a
  free room in Rooms and switch its type (**Board** > **Switch room type**),
  or choose a room of the other type yourself.
- **A proposed room follows the event even after you change the board.** A
  room the scheduler proposed that is taken, or switched to the other type,
  is withdrawn instead of staying selected.
- **Sign-in numbers no longer repeat.** Deleting a youth who is waiting used
  to give the next walk-in a number already in use.
- **A board of the youth's own unit is refused at every door**, not only on
  screen: seating a board, or changing its members, needs at least one
  member from outside the unit (Guide to Advancement 8.0.3.0).
- **The sign-in pages store only the sign-in fields**, each cut to 200
  characters (the list of youth an adult will introduce has far more room).
  A room, a status, or a result sent with a sign-in is ignored; the sign-in
  itself is still recorded.
- **A data file open in Excel is reported honestly.** The message now says
  the last change may not be saved and what to do. Files are flushed to disk
  when written, and a failed write leaves no stray `.tmp` file.
- **A refused step's error is brought into view.** After a refused Replace,
  Remove or Add member, the bar that says why sat at the top of the details
  pane, out of sight when the pane was scrolled down to the buttons.
- **If the main window can't be built, the scheduler now stops** instead of
  leaving the check-in website running with no window to close it.

### Changed

- The app's check-in website answers only the check-in endpoints, and its
  lists give this computer no more than a station gets. The older admin
  endpoints are served by the headless test server only (decision 0003).
- *Rename room* and *Switch room type* have the same names in the Board
  menu, the room's shortcut menu, and Help. Help explains the room rule.
- *Seat board*, when unavailable, gives the reason as help text for a
  screen reader.

### For contributors

- Tests run on xunit v3 and the Microsoft Testing Platform: `dotnet test`
  takes `--project` (decision 0002).
- Code style is checked in CI (`.editorconfig`: naming and whitespace). The
  main window is split into a file per part (decision 0001), and the room
  rule and the find box's wording are classes of their own with tests.
- The release workflow makes a draft and wants a passing build of the same
  commit. See [`docs/release-checklist.md`](docs/release-checklist.md).
