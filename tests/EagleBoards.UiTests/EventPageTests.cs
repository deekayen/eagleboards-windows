using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using EagleBoards.App;
using EagleBoards.Core;
using EagleBoards.Core.Records;

namespace EagleBoards.UiTests;

/// <summary>
/// The Event page from the keyboard and to a screen reader: where the focus
/// goes after a step, a sign-in or a minute, F6 between panes, a letter
/// typed without Alt, and what the window says and names. Keys and text go
/// through the input system as typing does (<see cref="OffScreen.Press"/>,
/// <see cref="OffScreen.Type"/>); no dialog is opened.
/// </summary>
public class EventPageTests
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

    private static void Seat(TestEvent e, (string Youth, string Chair, string[] Members) board) =>
        Assert.True(e.Service.SeatBoard("ROOM:101", board.Youth, board.Chair, string.Join(",", board.Members)).Ok);

    /// <summary>The button on a row of one of the details pane's lists, by the adult's last name and the button's AutomationId.</summary>
    private static ButtonBase RowButton(ItemsControl list, string last, string role)
    {
        var row = list.Items.Cast<PickRow>().First(p => p.Adult.Last == last);
        var container = (DependencyObject)list.ItemContainerGenerator.ContainerFromItem(row);
        return OffScreen.Descendant<ButtonBase>(container, b => AutomationProperties.GetAutomationId(b) == role)!;
    }

    [Fact]
    public void ALetterTypedWithoutAltRunsNoStep() => OffScreen.Run(() =>
    {
        // K1: WPF runs an access key from a bare letter when the focused
        // control takes no text, so a "c" typed on a Result choice completed
        // the board with no dialog.
        using var e = new TestEvent();
        var board = ReadyToSeat(e);
        Seat(e, board);
        e.Service.StartReview(board.Youth);
        var w = e.Window;
        Open(w, "Aldridge");

        var approved = w.ResultChoices.Children.OfType<RadioButton>().First();
        Assert.True(approved.Focus());
        OffScreen.Type(approved, "c");
        Assert.Equal(BoardStatus.InProgress, Status(e, board.Youth));

        // Alt+C, which arrives as system text, still completes it.
        OffScreen.TypeWithAlt(approved, "c");
        Assert.Equal(BoardStatus.Completed, Status(e, board.Youth));
    });

    [Fact]
    public void SeatingABoardPutsTheFocusOnStartReview() => OffScreen.Run(() =>
    {
        // C2: Seat board collapses once its step is done, and the focus fell
        // to the window, the next Tab starting at the top.
        using var e = new TestEvent();
        var board = ReadyToSeat(e);
        var w = e.Window;
        Open(w, "Aldridge");

        Assert.True(w.SeatButton.Focus());
        w.SeatButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, w.SeatButton));
        OffScreen.Pump();

        Assert.Equal(BoardStatus.Seated, Status(e, board.Youth));
        Assert.True(w.StartButton.IsKeyboardFocused);
    });

    [Fact]
    public void TheFocusStaysOnAnAdultsAddButtonWhenSomeoneSignsIn() => OffScreen.Run(() =>
    {
        // C1: every sign-in rebuilt the lists, and a focused Add button with them.
        using var e = new TestEvent();
        ReadyToSeat(e);
        e.Adult("Duxbury", "Desmond", "2004", BoardRoles.Member);
        e.Adult("Ellsworth", "Evangeline", "2005", BoardRoles.Member);
        var w = e.Window;
        Open(w, "Aldridge");

        // The operator's own board, so the proposal stays as it is.
        RowButton(w.PickList, "Castellano", "Remove").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        OffScreen.Pump();
        var add = RowButton(w.AvailableList, "Castellano", "Add");
        Assert.True(add.Focus());

        e.AdultAtTheDoor("Fairbanks", "Fitzgerald", "3001", BoardRoles.Member);
        OffScreen.Pump();

        Assert.Contains(w.AvailableList.Items.Cast<PickRow>(), p => p.Adult.Last == "Fairbanks");
        var focused = Assert.IsAssignableFrom<FrameworkElement>(Keyboard.FocusedElement);
        Assert.Equal("Add", AutomationProperties.GetAutomationId(focused));
        Assert.Equal("Castellano", Assert.IsType<PickRow>(focused.DataContext).Adult.Last);
    });

    [Fact]
    public void AddingAnAdultPutsTheFocusOnTheNextOnesAddButton() => OffScreen.Run(() =>
    {
        using var e = new TestEvent();
        ReadyToSeat(e);
        e.Adult("Duxbury", "Desmond", "2004", BoardRoles.Member);
        e.Adult("Ellsworth", "Evangeline", "2005", BoardRoles.Member);
        var w = e.Window;
        Open(w, "Aldridge");

        var first = w.AvailableList.Items.Cast<PickRow>().First().Adult.Last;
        var add = RowButton(w.AvailableList, first, "Add");
        Assert.True(add.Focus());
        add.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        OffScreen.Pump();

        Assert.Contains(w.PickList.Items.Cast<PickRow>(), p => p.Adult.Last == first);
        var focused = Assert.IsAssignableFrom<FrameworkElement>(Keyboard.FocusedElement);
        Assert.Equal("Add", AutomationProperties.GetAutomationId(focused));
        Assert.Same(w.AvailableList.Items.Cast<PickRow>().First(), focused.DataContext);
    });

    [Fact]
    public void TheMinuteLeavesASeatedBoardsMembersAlone() => OffScreen.Run(() =>
    {
        // F2: the minute replaced the member list, and a focused Replace with it.
        using var e = new TestEvent();
        var board = ReadyToSeat(e);
        Seat(e, board);
        var w = e.Window;
        Open(w, "Aldridge");

        var replace = RowButton(w.BoardMemberList, "Blackwood", "Replace");
        Assert.True(replace.Focus());
        OffScreen.Invoke(w, "OnMinute");
        OffScreen.Pump();

        Assert.True(replace.IsKeyboardFocused);
    });

    [Fact]
    public void F6MovesBetweenTheEventPagesPanes() => OffScreen.Run(() =>
    {
        // C11: the Youth list, Rooms (Find a person first), the details pane, the footer.
        using var e = new TestEvent();
        ReadyToSeat(e);
        var w = e.Window;
        Open(w, "Aldridge");
        Assert.True(((ListBoxItem)w.QueueList.ItemContainerGenerator.ContainerFromIndex(0)).Focus());

        OffScreen.Press(w, Key.F6);
        Assert.True(w.PersonFind.IsKeyboardFocused);
        OffScreen.Press(w, Key.F6);
        Assert.True(w.DetailsPane.IsKeyboardFocusWithin);
        OffScreen.Press(w, Key.F6);
        Assert.True(w.Footer.IsKeyboardFocusWithin);
        OffScreen.Press(w, Key.F6);
        Assert.True(w.QueueList.IsKeyboardFocusWithin);
    });

    [Fact]
    public void APageShownTakesTheFocusAndNamesTheWindow() => OffScreen.Run(() =>
    {
        // C3, K9: the focus stayed on the page just hidden, and the title
        // never said which page was shown.
        using var e = new TestEvent();
        ReadyToSeat(e);
        var w = e.Window;
        Assert.StartsWith("Event - Eagle Board Scheduler", w.Title, StringComparison.Ordinal);

        OffScreen.Invoke(w, "ShowPage", "Adults");
        OffScreen.Pump();
        var adults = OffScreen.Field<Dictionary<string, TablePage>>(w, "_tables")["Adults"];
        Assert.StartsWith("Adults - Eagle Board Scheduler", w.Title, StringComparison.Ordinal);
        Assert.True(adults.IsKeyboardFocusWithin);

        // Ctrl+Tab: the next page in the View menu's order.
        OffScreen.Invoke(w, "ShowNextPage", 1);
        OffScreen.Pump();
        Assert.StartsWith("Youth - Eagle Board Scheduler", w.Title, StringComparison.Ordinal);
    });

    [Fact]
    public void ARoomCardsNameFollowsARename() => OffScreen.Run(() =>
    {
        // A8: a rename keeps the room's ID, so its card is updated in place,
        // and its name kept saying the old room.
        using var e = new TestEvent();
        e.Room("103", BoardTypes.Final);
        var w = e.Window;
        var card = w.RoomList.Items.Cast<RoomCard>().Single();

        Assert.True(e.Service.RenameRoom(card.Id, "104").Ok);
        OffScreen.Pump();

        var item = (ListBoxItem)w.RoomList.ItemContainerGenerator.ContainerFromItem(card);
        Assert.Equal("Room 104, Final board, free", AutomationProperties.GetName(item));
    });

    [Fact]
    public void FindAPersonSaysTheRoomTheyreIn() => OffScreen.Run(() =>
    {
        // F3: the note shows only those in no room (SPEC.md D-21), so a
        // screen reader heard nothing when the match was on a board.
        using var e = new TestEvent();
        Seat(e, ReadyToSeat(e));
        var w = e.Window;

        w.PersonFind.Text = "Blackwood";
        OffScreen.Pump();

        Assert.Equal("", w.PersonFindNote.Text);
        Assert.Equal("Bartholomew Blackwood is in room 101.", AutomationProperties.GetName(w.PersonFindNote));
    });

    [Fact]
    public void OpenOnEventPageWaitsForABoardToBeSelected() => OffScreen.Run(() =>
    {
        // K10: with no row selected it did nothing, and said nothing.
        using var e = new TestEvent();
        Seat(e, ReadyToSeat(e));
        var w = e.Window;
        OffScreen.Invoke(w, "ShowPage", "Results");
        OffScreen.Pump();
        var results = OffScreen.Field<Dictionary<string, TablePage>>(w, "_tables")["Results"];
        var open = OffScreen.Field<Button>(w, "_openButton");
        results.Grid.SelectedItem = null;
        OffScreen.Pump();
        Assert.False(open.IsEnabled);

        results.Grid.SelectedItem = results.Rows.Single();
        OffScreen.Pump();
        Assert.True(open.IsEnabled);
    });

    [Fact]
    public void LinkAnAdultWithNoOneToLinkSaysSo() => OffScreen.Run(() =>
    {
        // K10: it returned without a word when no adult had signed in.
        using var e = new TestEvent();
        e.Youth("Aldridge", "Alexander", "1001", BoardTypes.Final);
        var w = e.Window;
        Open(w, "Aldridge");

        OffScreen.Invoke(w, "OnLinkAdult", w.LinkButton, new RoutedEventArgs());

        Assert.True(w.DetailNotice.IsOpen);
        Assert.Contains("No adult has signed in yet.", w.DetailNotice.Text, StringComparison.Ordinal);
    });

    [Fact]
    public void RemovingTheOnlyChairSaysWhatToDoInstead() => OffScreen.Run(() =>
    {
        // D2: the service's refusal named no next step.
        using var e = new TestEvent();
        var board = ReadyToSeat(e);
        var d = e.Adult("Duxbury", "Desmond", "2004", BoardRoles.Member);
        Seat(e, board with { Members = [.. board.Members, d] });
        var w = e.Window;
        Open(w, "Aldridge");

        OffScreen.Invoke(w, "OnRemoveMember", RowButton(w.BoardMemberList, "Abernathy", "Remove"), new RoutedEventArgs());

        Assert.Equal(Severity.Warning, w.DetailNotice.Severity);
        Assert.Contains("Use Replace", w.DetailNotice.Text, StringComparison.Ordinal);
        Assert.Equal(4, w.BoardMemberList.Items.Count);
    });

    [Fact]
    public void UndoOnATablePageSaysThereWhatItUndid() => OffScreen.Run(() =>
    {
        // C12: Ctrl+Z there undoes a board step, and nothing on the page said so.
        using var e = new TestEvent();
        var board = ReadyToSeat(e);
        Seat(e, board);
        var w = e.Window;
        OffScreen.Invoke(w, "ShowPage", "Results");
        OffScreen.Pump();

        OffScreen.Invoke(w, "OnUndo", w, new RoutedEventArgs());
        OffScreen.Pump();

        var results = OffScreen.Field<Dictionary<string, TablePage>>(w, "_tables")["Results"];
        Assert.Equal(BoardStatus.Registered, Status(e, board.Youth));
        Assert.True(results.Notice.IsOpen);
        Assert.Contains("Undid seating Alexander Aldridge's board.", results.Notice.Text, StringComparison.Ordinal);
    });

    [Fact]
    public void ADisabledButtonsLabelLooksDisabled() => OffScreen.Run(() =>
    {
        // A label with an access key (an AccessText) took the button's own
        // colour, not the template's, so Fill the rest looked enabled when full.
        using var e = new TestEvent();
        ReadyToSeat(e);
        var w = e.Window;
        Open(w, "Aldridge");
        Assert.False(w.FillButton.IsEnabled);

        Color Of(object brush) => ((SolidColorBrush)brush).Color;
        var label = OffScreen.Descendant<AccessText>(w.FillButton)!;
        Assert.Equal(Of(w.FindResource("TextFillColorDisabledBrush")), Of(label.Foreground));
        Assert.Equal(Of(w.FindResource("TextFillColorPrimaryBrush")), Of(OffScreen.Descendant<AccessText>(w.PostponeButton)!.Foreground));
    });
}
