using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EagleBoards.App;
using EagleBoards.Core;
using EagleBoards.Core.Records;

namespace EagleBoards.UiTests;

/// <summary>
/// The Board menu (SPEC.md P-1, amended 2026-10-24), Remove room's question
/// (O-2, amended) and a table that won't delete someone a board holds (P-6,
/// amended). A menu item is run and asked as it runs and asks its command,
/// and a question is read through <see cref="MainWindow.Ask"/>: no menu or
/// dialog is opened.
/// </summary>
public class BoardMenuTests
{
    private static void Open(MainWindow window, string last)
    {
        window.QueueList.SelectedItem = window.QueueList.Items.Cast<ScoutRow>().First(s => s.Last == last);
        OffScreen.Pump();
    }

    private static string Status(TestEvent e, string id) => e.Service.Snapshot(DataTable.Scouts).Single(r => r["ID"] == id)["Status"];

    /// <summary>Room 101, three adults (one a chair), and a youth waiting: a board ready to seat.</summary>
    private static (string Youth, string Chair, string[] Members) ReadyToSeat(TestEvent e)
    {
        e.Room("101", BoardTypes.Final);
        var chair = e.Adult("Abernathy", "Anneliese", "2001", BoardRoles.Chair);
        var b = e.Adult("Blackwood", "Bartholomew", "2002", BoardRoles.Member);
        var c = e.Adult("Castellano", "Clementine", "2003", BoardRoles.Member);
        return (e.Youth("Aldridge", "Alexander", "1001", BoardTypes.Final), chair, [chair, b, c]);
    }

    private static MenuItem[] TopMenus(MainWindow window) => OffScreen.Descendant<Menu>(window)!.Items.OfType<MenuItem>().ToArray();

    private static MenuItem[] BoardItems(MainWindow window) => TopMenus(window).Single(m => (string)m.Header == "_Board").Items.OfType<MenuItem>().ToArray();

    private static MenuItem Item(MainWindow window, string header) => BoardItems(window).Single(i => (string)i.Header == header);

    /// <summary>What selecting the item does: its command, from the item, as the menu would run it.</summary>
    private static void Choose(MenuItem item)
    {
        ((RoutedCommand)item.Command).Execute(item.CommandParameter, item);
        OffScreen.Pump();
    }

    /// <summary>
    /// Whether the item is enabled when its menu opens. A closed menu's items
    /// don't ask their commands (WPF asks as the menu opens, and says enabled
    /// till then), so this asks as the open menu would: the command, from the item.
    /// </summary>
    private static bool Enabled(MenuItem item) => ((RoutedCommand)item.Command).CanExecute(item.CommandParameter, item);

    [Fact]
    public void TheBoardMenuHasEveryStepAndItsShortcut() => OffScreen.Run(() =>
    {
        using var e = new TestEvent();
        ReadyToSeat(e);
        var w = e.Window;

        // Between View and Help, its letter clear of theirs.
        Assert.Equal(["_File", "_Edit", "_View", "_Board", "_Help"], TopMenus(w).Select(m => (string)m.Header));
        Assert.Equal(
            ["_Seat board", "_Postpone...", "Rese_t...", "_Link an adult...", "_Unlink...",
             "_Add room...", "_Move or swap board...", "Re_name room...", "S_witch room type", "_Remove room..."],
            BoardItems(w).Select(i => (string)i.Header));

        // The shortcuts come from the commands, shown by the items themselves.
        Assert.Equal("Ctrl+Enter", w.BoardStep.InputGestureText);
        Assert.Equal("F2", Item(w, "Re_name room...").InputGestureText);
        Assert.All(BoardItems(w).Where(i => i != w.BoardStep && (string)i.Header != "Re_name room..."), i => Assert.Equal("", i.InputGestureText));
    });

    [Fact]
    public void TheBoardMenusStepIsNamedForTheStepAndEnabledAsItsButtonIs() => OffScreen.Run(() =>
    {
        using var e = new TestEvent();
        var board = ReadyToSeat(e);
        var w = e.Window;

        // No youth open: nothing to take a step on.
        Assert.False(Enabled(w.BoardStep));
        Assert.False(Enabled(Item(w, "_Postpone...")));
        Assert.False(Enabled(Item(w, "_Link an adult...")));

        Open(w, "Aldridge");
        Assert.Equal("_Seat board", w.BoardStep.Header);
        Assert.Equal(w.SeatButton.IsEnabled, Enabled(w.BoardStep));
        Assert.True(Enabled(w.BoardStep));
        Assert.True(Enabled(Item(w, "_Postpone...")));
        Assert.False(Enabled(Item(w, "Rese_t...")));
        Assert.True(Enabled(Item(w, "_Link an adult...")));
        Assert.False(Enabled(Item(w, "_Unlink...")));

        // Seat board is disabled while a problem blocks it, and so is the item.
        var chair = w.PickList.Items.Cast<PickRow>().First(p => p.Id == board.Chair);
        chair.Adult.Sel = false;
        Invoke(w, "ShowDetails");
        OffScreen.Pump();
        Assert.False(w.SeatButton.IsEnabled);
        Assert.False(Enabled(w.BoardStep));
        Invoke(w, "OnFillBoard", w.FillButton, new System.Windows.RoutedEventArgs());
        OffScreen.Pump();
        Assert.True(Enabled(w.BoardStep));

        // Board > Seat board seats it, and the item is Start review next.
        Choose(w.BoardStep);
        Assert.Equal(BoardStatus.Seated, Status(e, board.Youth));
        Assert.Equal("_Start review", w.BoardStep.Header);
        Assert.True(Enabled(w.BoardStep));
        Assert.False(Enabled(Item(w, "_Postpone...")));
        Assert.True(Enabled(Item(w, "Rese_t...")));

        Assert.True(e.Service.StartReview(board.Youth).Ok);
        OffScreen.Pump();
        Assert.Equal("_Complete", w.BoardStep.Header);
        Assert.True(Enabled(w.BoardStep));

        // On a table page the Board menu's buttons aren't there, nor its items.
        Invoke(w, "ShowPage", "Adults");
        OffScreen.Pump();
        Assert.All(BoardItems(w), i => Assert.False(Enabled(i), (string)i.Header));
    });

    [Fact]
    public void TheBoardMenusStepDoesNotKeepSeatAnywayOnceThereIsNoStep() => OffScreen.Run(() =>
    {
        using var e = new TestEvent();
        ReadyToSeat(e);
        var fourth = e.Adult("Delacroix", "Desmond", "2004", BoardRoles.Member);
        var postponed = e.Youth("Bertrand", "Beatrix", "1002", BoardTypes.Final);
        Assert.True(e.Service.PostponeBoard(postponed).Ok);
        var w = e.Window;

        // A fourth member makes a larger board than usual, which warns.
        Open(w, "Aldridge");
        w.AvailableList.Items.Cast<PickRow>().First(p => p.Id == fourth).Adult.Sel = true;
        Invoke(w, "ShowDetails");
        OffScreen.Pump();
        Assert.Equal("_Seat anyway", w.BoardStep.Header);

        // A postponed youth has no step: nothing to seat anyway.
        Open(w, "Bertrand");
        Assert.Equal(System.Windows.Visibility.Collapsed, w.SeatButton.Visibility);
        Assert.Equal("_Seat board", w.BoardStep.Header);
        Assert.False(Enabled(w.BoardStep));
    });

    [Fact]
    public void TheRoomItemsAndButtonsAreEnabledOnceARoomIsSelected() => OffScreen.Run(() =>
    {
        using var e = new TestEvent();
        e.Room("101", BoardTypes.Final);
        var w = e.Window;

        Assert.True(Enabled(Item(w, "_Add room...")));
        Assert.False(Enabled(Item(w, "_Remove room...")));
        Assert.False(w.RemoveRoomButton.IsEnabled);
        Assert.False(w.MoveRoomButton.IsEnabled);
        Assert.False(BoardCommands.RenameRoom.CanExecute(null, w));

        // An icon-only button looks disabled too: its glyph kept the enabled colour.
        Color Of(object brush) => ((SolidColorBrush)brush).Color;
        var glyph = OffScreen.Descendant<Glyph>(w.RemoveRoomButton)!;
        Assert.Equal(Of(w.FindResource("TextFillColorDisabledBrush")), Of(glyph.Foreground));

        w.RoomList.SelectedItem = w.RoomList.Items.Cast<RoomCard>().Single();
        OffScreen.Pump();
        Assert.All(BoardItems(w).Skip(5), i => Assert.True(Enabled(i), (string)i.Header));
        Assert.True(w.RemoveRoomButton.IsEnabled);
        Assert.True(w.MoveRoomButton.IsEnabled);
        Assert.True(BoardCommands.RenameRoom.CanExecute(null, w));
        Assert.NotEqual(Of(w.FindResource("TextFillColorDisabledBrush")), Of(glyph.Foreground));
    });

    [Fact]
    public void RemoveRoomAsksFirstNamingTheRoom() => OffScreen.Run(() =>
    {
        // SPEC.md O-2, amended 2026-10-24: in the Java version's words.
        using var e = new TestEvent();
        e.Room("101", BoardTypes.Final);
        e.Room("102", BoardTypes.Project);
        var w = e.Window;
        var asked = new List<AppDialog>();
        var answer = false;
        w.Ask = dialog =>
        {
            asked.Add(dialog);
            return answer;
        };
        string[] Rooms() => e.Service.Snapshot(DataTable.Rooms).Select(r => r["Room"]).Order().ToArray();

        w.RoomList.SelectedItem = w.RoomList.Items.Cast<RoomCard>().First(r => r.Room == "101");
        OffScreen.Pump();
        Choose(Item(w, "_Remove room..."));
        var question = Assert.Single(asked);
        Assert.Equal("Remove room 101?", question.Title);
        Assert.Equal("It comes off the list for the rest of this event. You can add it again.", question.MessageText);
        Assert.Equal("Remove room", OffScreen.Field<Button>(question, "_primary").Content);
        Assert.Equal(["101", "102"], Rooms());

        answer = true;
        Choose(Item(w, "_Remove room..."));
        Assert.Equal(2, asked.Count);
        Assert.Equal(["102"], Rooms());

        // The Rooms page asks the same.
        var page = OffScreen.Field<Dictionary<string, TablePage>>(w, "_tables")["Rooms"];
        Invoke(w, "ShowPage", "Rooms");
        OffScreen.Pump();
        page.Grid.SelectedItem = page.Rows.Single();
        answer = false;
        Invoke(page, "RemoveRoom");
        Assert.Equal("Remove room 102?", asked[^1].Title);
        Assert.Equal(["102"], Rooms());
    });

    [Fact]
    public void ARoomWithABoardIsRefusedBeforeAnythingIsAsked() => OffScreen.Run(() =>
    {
        using var e = new TestEvent();
        var board = ReadyToSeat(e);
        Assert.True(e.Service.SeatBoard("ROOM:101", board.Youth, board.Chair, string.Join(",", board.Members)).Ok);
        var w = e.Window;
        var asked = 0;
        w.Ask = _ => ++asked > 0;

        w.RoomList.SelectedItem = w.RoomList.Items.Cast<RoomCard>().Single();
        OffScreen.Pump();
        Choose(Item(w, "_Remove room..."));

        Assert.Equal(0, asked);
        Assert.Equal(Severity.Warning, w.Notice.Severity);
        Assert.Contains("Room 101 is in use", w.Notice.Text, StringComparison.Ordinal);
        Assert.Single(e.Service.Snapshot(DataTable.Rooms));
    });

    [Fact]
    public void ATableSaysWhyItWontDeleteSomeoneABoardHolds() => OffScreen.Run(() =>
    {
        // SPEC.md P-6, amended 2026-10-24: said before "Delete this record?",
        // so no dialog is opened here.
        using var e = new TestEvent();
        var board = ReadyToSeat(e);
        Assert.True(e.Service.SeatBoard("ROOM:101", board.Youth, board.Chair, string.Join(",", board.Members)).Ok);
        var w = e.Window;
        var tables = OffScreen.Field<Dictionary<string, TablePage>>(w, "_tables");

        foreach (var (name, id, said) in new[]
        {
            ("Youth", board.Youth, "Alexander Aldridge's board is in room 101. Reset it or complete it on the Event page first."),
            ("Adults", board.Chair, "Anneliese Abernathy is on the board in room 101. Reset or complete that board on the Event page first."),
        })
        {
            var page = tables[name];
            Invoke(w, "ShowPage", name);
            OffScreen.Pump();
            page.Grid.SelectedItem = page.Rows.Single(r => r.Id == id);
            Invoke(page, "Delete");

            Assert.True(page.Notice.IsOpen);
            Assert.Equal(Severity.Warning, page.Notice.Severity);
            Assert.StartsWith("That can't be deleted yet", page.Notice.Text, StringComparison.Ordinal);
            Assert.Contains(said, page.Notice.Text, StringComparison.Ordinal);
            Assert.Contains(page.Rows, r => r.Id == id);
        }

        Assert.Equal(BoardStatus.Seated, Status(e, board.Youth));
    });

    private static void Invoke(object target, string method, params object[] args) => OffScreen.Invoke(target, method, args);
}
