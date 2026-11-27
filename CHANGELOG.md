# What changed

What an operator or contributor would notice, newest first. Every release is
on the [Releases page](https://github.com/deekayen/eagleboards-windows/releases)
with its exe; this says why to update. Decisions behind the changes are in
[`docs/decisions`](docs/decisions/README.md) and, for behavior all three
versions share, `SPEC.md` in `deekayen/eagleboards-shared`.

## 2026.11.27

### Fixed

- **A search in Find an adult no longer hides everyone who signs in
  later** (found reproducing a report from the 2026-11-17 event). If you
  found someone in the details pane's *Find an adult* and added them, the
  search stayed. Every adult who signed in afterwards was left out of *Add
  members* until another youth was opened, under the words "No other adults
  are free for this board." Now adding the person you found empties the
  box. While a search is in it, the line under the list says how many free
  adults it's hiding, instead of saying no one is free, and a screen reader
  reads that line as you type.
- **Someone who signs in shows up at once, even when a data file is open
  elsewhere.** If Excel, OneDrive, or antivirus had a data file open as a
  station sent a sign-in, the save failed and the scheduler wasn't told.
  The person was signed in but appeared nowhere until the next change,
  which in a quiet spell was minutes later. Now the queue, the board picker,
  and the Adults page show them right away. The station still reports the
  failed save, and the next save writes them to the file. A save also waits
  up to a second for a program that holds the file only for a moment.
- **A newcomer is listed before the board is proposed again.** At a large
  event, proposing a board for the open youth took a second or more, and
  the lists weren't drawn until it was done. Now the new name is drawn
  first, and the proposal follows. The proposal itself is four to eight
  times faster, with the same answers.

### Changed

- **The event's log says more, and never who.** `eagleboards.log`, in the
  event's folder, now records each station request and how it was answered,
  what each sign-in did, when the window was slow to show a change, the
  computer's network addresses as they change, and sleep and waking. These
  lines hold counts, times, rooms, and addresses, never a name, email, or
  phone, so the log can be shared when reporting a problem. An error's own
  text, and everything `-v` adds, can still hold more (decision 0004).

### For contributors

- QRCoder is 1.9.0 (was 1.8.1).

## 2026.11.12

### Fixed

- **A board you changed stays with its youth.** Change one waiting youth's
  proposed board, open another youth, and the second youth's pane showed the
  first youth's members and chair. Now the second youth gets their own
  proposal, and returning to the first shows the board as you left it (SPEC.md
  D-12, amended 2026-11-08). Start over, Seat board, and a youth being seated
  from another computer end the kept board. The kept boards are held by the
  open window, so closing it forgets them; the members you picked for the open
  youth are still saved as before.
- **Pre-registered phone numbers with a country code or only seven digits.**
  `+1 (555) 222-0007` was stored as `155-522-20007` and `222-0005` as
  `222-000-5`. They now come out `555-222-0007` and `222-0005`, and a number
  that fits neither shape (one with an extension, or too few digits) is kept as
  typed. The Mac and Java versions write them the same way.
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
  Remove, or Add member, the bar that says why sat at the top of the details
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
- **Start review lists each adult as Name (where they are)**, as the Mac and
  Java versions do. Before, one adult read "They're on the board in room 104"
  and a list read "Name, on the board in room 104". Whom it names has not
  changed: the adults who said at sign-in they will introduce the youth, or
  else the youth's leaders, never a parent.
- Help and the README explain how a board you changed is kept for its youth,
  and what ends it.

### For contributors

- Tests run on xunit v3 and the Microsoft Testing Platform: `dotnet test`
  takes `--project` (decision 0002).
- Code style is checked in CI (`.editorconfig`: naming and whitespace). The
  main window is split into a file per part (decision 0001), and the room
  rule and the find box's wording are classes of their own with tests.
- The release workflow makes a draft and wants a passing build of the same
  commit. See [`docs/release-checklist.md`](docs/release-checklist.md).
- xunit.v3 is 4.0.3 (was 4.0.2).
