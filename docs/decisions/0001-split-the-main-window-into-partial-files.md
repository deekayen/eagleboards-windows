# 0001. Split the main window into partial files, one per part

Status: accepted (2026-11-02)

## Context

`MainWindow.xaml.cs` had grown to about 2,400 lines: the Event page's queue,
the rooms, the board builder, every board step, the dialogs, the keyboard
handling and the menu commands in one file. A newcomer (or the owner, months
later) could not find where a thing lived, and had to read a screen of fields
and methods to learn which part of the window a method belonged to.

The file already had fifteen section banners (Refresh, Navigation, Keys,
Rooms, Picks and so on), so the parts were known. What made a deeper split
hard:

- Most parts share the builder's state (`_current`, `_chairId`,
  `_builderRoomId`, `_proposal`), so moving them into classes of their own
  means designing how that state is passed, not just cutting.
- The window tests reach members of `MainWindow` by name through reflection
  (`OffScreen.Invoke`, `OffScreen.Field`), so renaming or moving members to
  other types breaks them.

Published advice does not settle it. Microsoft's
[WPF and MVVM guidance](https://learn.microsoft.com/en-us/windows/apps/develop/data-binding/data-binding-and-mvvm)
names code-behind that collects unrelated code as the problem and says to
change incrementally. Its page on
[partial classes](https://learn.microsoft.com/en-us/dotnet/csharp/programming-guide/classes-and-structs/partial-classes-and-methods)
describes them as a way to split a type across files, not as a design
remedy. Fowler's [Code Smell](https://martinfowler.com/bliki/CodeSmell.html)
says size is a hint, not a verdict.

## Decision

Split `MainWindow` into one partial-class file per existing section banner
(`MainWindow.Rooms.cs`, `MainWindow.Picks.cs`, ...), by pure moves: no member
renamed, no line changed besides the class wrapper. The fields, constructor
and the class summary stay in `MainWindow.xaml.cs`.

Pull out into classes of their own only what reads no window state, and only
when a part is next changed (Fowler's
[opportunistic refactoring](https://martinfowler.com/bliki/OpportunisticRefactoring.html)).
Done so far: `BoardCommands`, `RelayCommand` and `FocusSearch`.

## Consequences

- A part of the window is found by file name, and a change to it is a change
  to one small file.
- It is still one type with one set of shared state. The split makes the code
  easier to find, not less coupled; that needs the extractions above, one at
  a time, each with a test first where the behaviour isn't covered.
- The window tests keep working unchanged, since every member is still on
  `MainWindow`.
- A contributor adding to the window picks the part it belongs to, and adds a
  new `MainWindow.<Part>.cs` only for a new kind of work, not a new method.
