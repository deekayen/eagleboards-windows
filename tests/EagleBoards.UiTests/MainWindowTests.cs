using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using EagleBoards.App;
using EagleBoards.Core;
using EagleBoards.Core.Records;

namespace EagleBoards.UiTests;

/// <summary>
/// The Event page and the table pages, as the operator uses them. Each one
/// was reported at the 2026-09-30 event.
/// </summary>
public class MainWindowTests
{
    private static void Open(MainWindow window, string last)
    {
        window.QueueList.SelectedItem = window.QueueList.Items.Cast<ScoutRow>().First(s => s.Last == last);
        OffScreen.Pump();
    }

    private static string[] Picks(MainWindow window) =>
        window.PickList.Items.Cast<PickRow>().Select(p => p.Adult.Last).Order().ToArray();

    private static string[] Available(MainWindow window) =>
        window.AvailableList.Items.Cast<PickRow>().Select(p => p.Adult.Last).Order().ToArray();

    /// <summary>A table page, shown, with its list-opening seam recording instead of opening a popup.</summary>
    private static TablePage Page(MainWindow window, string name, List<ComboBox>? opened = null)
    {
        OffScreen.Invoke(window, "ShowPage", name);
        OffScreen.Pump();
        var page = OffScreen.Field<Dictionary<string, TablePage>>(window, "_tables")[name];
        if (opened != null)
        {
            page.OpenList = opened.Add;   // a real list's popup would open on the desktop
        }

        return page;
    }

    private static DataGridCell Cell(TablePage page, RecordRow row, string header)
    {
        var column = page.Grid.Columns.First(c => (string)c.Header == header);
        page.Grid.ScrollIntoView(row, column);
        OffScreen.Pump();
        return OffScreen.Ancestor<DataGridCell>(column.GetCellContent(row))!;
    }

    /// <summary>A key, as real input arrives: tunnelling down to the element, then bubbling up unless that was handled.</summary>
    private static void Press(UIElement target, Key key)
    {
        var source = PresentationSource.FromVisual(target)!;
        var preview = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(preview);
        if (!preview.Handled)
        {
            target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.KeyDownEvent });
        }

        OffScreen.Pump();
    }

    private static void InCulture(string name, Action test)
    {
        var was = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
        try
        {
            test();
        }
        finally
        {
            CultureInfo.CurrentCulture = was;
        }
    }

    [Fact]
    public void AdultsWhoSignInAfterAYouthIsOpenedCompleteTheBoardProposedForThem() => OffScreen.Run(() =>
    {
        // SPEC.md D-12 (amended), eagleboards-shared#20. A board proposed
        // while one adult was there stayed a board of one, with Seat blocked,
        // until Start over.
        using var e = new TestEvent();
        e.Room("101", BoardTypes.Final);
        e.Adult("Abernathy", "Anneliese", "2001", BoardRoles.Chair);
        e.Youth("Aldridge", "Alexander", "1001", BoardTypes.Final);
        var w = e.Window;

        Open(w, "Aldridge");
        Assert.Equal(["Abernathy"], Picks(w));
        Assert.False(w.SeatButton.IsEnabled);

        e.AdultAtTheDoor("Blackwood", "Bartholomew", "3001", BoardRoles.Member);
        e.AdultAtTheDoor("Castellano", "Clementine", "3002", BoardRoles.Member);
        OffScreen.Pump();

        Assert.Equal(["Abernathy", "Blackwood", "Castellano"], Picks(w));
        Assert.True(w.SeatButton.IsEnabled);
    });

    [Fact]
    public void ABoardTheOperatorChangedIsKeptAndNewAdultsAreListedToAdd() => OffScreen.Run(() =>
    {
        using var e = new TestEvent();
        e.Room("101", BoardTypes.Final);
        e.Adult("Abernathy", "Anneliese", "2001", BoardRoles.Chair);
        e.Adult("Blackwood", "Bartholomew", "2002", BoardRoles.Member);
        e.Adult("Castellano", "Clementine", "2003", BoardRoles.Member);
        e.Youth("Aldridge", "Alexander", "1001", BoardTypes.Final);
        var w = e.Window;

        Open(w, "Aldridge");
        Assert.Equal(["Abernathy", "Blackwood", "Castellano"], Picks(w));

        // The operator takes Castellano off, as the Remove button does.
        w.PickList.Items.Cast<PickRow>().First(p => p.Adult.Last == "Castellano").Adult.Sel = false;
        OffScreen.Invoke(w, "ShowDetails");
        OffScreen.Pump();

        e.AdultAtTheDoor("Duxbury", "Desmond", "3001", BoardRoles.Member);
        OffScreen.Pump();

        // Their board stays theirs; the newcomer is there to add at once.
        Assert.Equal(["Abernathy", "Blackwood"], Picks(w));
        Assert.Equal(["Castellano", "Duxbury"], Available(w));
    });

    [Fact]
    public void OpeningAnotherYouthProposesTheirOwnBoard() => OffScreen.Run(() =>
    {
        // A board proposed for one youth used to be kept for the next one
        // opened, whose unit it might share.
        using var e = new TestEvent();
        e.Room("101", BoardTypes.Final);
        e.Room("102", BoardTypes.Final);
        e.Adult("Abernathy", "Anneliese", "2001", BoardRoles.Chair);
        e.Adult("Blackwood", "Bartholomew", "1002", BoardRoles.Member);
        e.Adult("Castellano", "Clementine", "2003", BoardRoles.Member);
        e.Adult("Duxbury", "Desmond", "2004", BoardRoles.Member);
        e.Youth("Aldridge", "Alexander", "1001", BoardTypes.Final);
        e.Youth("Bram", "Beauregard", "1002", BoardTypes.Final);
        var w = e.Window;

        Open(w, "Aldridge");
        Assert.Contains("Blackwood", Picks(w));

        Open(w, "Bram");
        Assert.DoesNotContain("Blackwood", Picks(w));
        Assert.True(w.SeatButton.IsEnabled);
    });

    [Fact]
    public void OneClickOnARoleOpensItsListAndAPickIsSaved() => OffScreen.Run(() =>
    {
        // eagleboards-windows#17: Member to Chair took a click to select the
        // cell, another to edit it and another to open the list.
        using var e = new TestEvent();
        var id = e.Adult("Abernathy", "Anneliese", "2001", BoardRoles.Member);
        var opened = new List<ComboBox>();
        var page = Page(e.Window, "Adults", opened);
        var row = page.Rows.Single();
        DataGridCell Cell(string header) => MainWindowTests.Cell(page, row, header);

        void Click(DataGridCell cell)
        {
            cell.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                // As real input arrives: tunnelling down to the cell, raised as
                // PreviewMouseLeftButtonDown on each element it passes.
                RoutedEvent = Mouse.PreviewMouseDownEvent,
            });
            OffScreen.Pump();
        }

        var role = Cell("Final board role");
        Click(role);
        Assert.True(role.IsEditing);
        var list = Assert.Single(opened);
        list.SelectedValue = BoardRoles.Chair;
        OffScreen.Pump();

        Assert.False(role.IsEditing);
        Assert.Equal(BoardRoles.Chair, e.Service.Snapshot(DataTable.Adults).Single(r => r["ID"] == id)["FinalBoard"]);

        // Wood Badge is a choice while it's changed too; a name is still typed.
        var woodBadge = Cell("Wood Badge");
        Click(woodBadge);
        Assert.True(woodBadge.IsEditing);
        Assert.Equal(2, opened.Count);
        page.CommitEdit();

        var last = Cell("Last name");
        Click(last);
        Assert.False(last.IsEditing);
        Assert.Equal(2, opened.Count);
    });

    /// <summary>Text typed at an element, as real input arrives: the preview tunnels, then TextInput bubbles unless that was handled.</summary>
    private static void Type(UIElement target, string text)
    {
        var composition = new TextComposition(InputManager.Current, target, text);
        var preview = new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) { RoutedEvent = TextCompositionManager.PreviewTextInputEvent };
        target.RaiseEvent(preview);
        if (!preview.Handled)
        {
            target.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) { RoutedEvent = TextCompositionManager.TextInputEvent });
        }

        OffScreen.Pump();
    }

    [Fact]
    public void TypingNeverOverwritesACellAndFindWorksWithAnEditOpen() => OffScreen.Run(() =>
    {
        // The focus is put in the table now (a page shown, F6, Find's Down
        // Arrow), where a letter typed to search started an edit that
        // replaced the name; a closed list's Right Arrow picked and saved the
        // next choice; and typing in Find with an edit open threw.
        using var e = new TestEvent();
        var id = e.Adult("Abernathy", "Anneliese", "2001", BoardRoles.Member);
        e.Adult("Blackwood", "Bartholomew", "2002", BoardRoles.Member);
        var opened = new List<ComboBox>();
        var page = Page(e.Window, "Adults", opened);
        var row = page.Rows.Single(r => r.Id == id);
        string Saved(string field) => e.Service.Snapshot(DataTable.Adults).Single(r => r["ID"] == id)[field];

        var last = Cell(page, row, "Last name");
        page.Grid.CurrentCell = new DataGridCellInfo(last);
        last.Focus();
        Type(last, "Z");
        Assert.False(last.IsEditing);
        Assert.Equal("Abernathy", Saved("Last"));

        var role = Cell(page, row, "Final board role");
        Press(role, Key.F2);
        var list = Assert.Single(opened);
        Press(list, Key.Right);
        Assert.Equal(BoardRoles.Member, Saved("FinalBoard"));
        page.CommitEdit();

        page.Grid.CurrentCell = new DataGridCellInfo(last);
        Press(last, Key.F2);
        Assert.True(last.IsEditing);
        page.Find.Text = "Black";
        OffScreen.Pump();
        Assert.False(last.IsEditing);
        Assert.Equal("Blackwood", Assert.Single(page.Grid.Items.Cast<RecordRow>())["Last"]);
    });

    [Fact]
    public void TheKeyboardOpensAChoiceAndAnArrowOnAClosedListSavesNothing() => OffScreen.Run(() =>
    {
        // Up or Down Arrow on a closed list took the next value and saved
        // it, and the adult history with it, then ended the edit, so the
        // next press moved to the next row. Wood Badge's list never even
        // took the focus.
        using var e = new TestEvent();
        var id = e.Adult("Abernathy", "Anneliese", "2001", BoardRoles.Member);
        var opened = new List<ComboBox>();
        var page = Page(e.Window, "Adults", opened);
        var row = page.Rows.Single();
        string Saved() => e.Service.Snapshot(DataTable.Adults).Single(r => r["ID"] == id)["FinalBoard"];

        var role = Cell(page, row, "Final board role");
        Press(role, Key.F2);
        Assert.True(role.IsEditing);
        var list = Assert.Single(opened);
        Assert.Equal("Final board role", AutomationProperties.GetName(list));

        // The test opens no popup, so the list is still closed: Down opens it
        // rather than moving to Chair.
        Press(list, Key.Down);
        Assert.Equal(2, opened.Count);
        Assert.Equal(BoardRoles.Member, list.SelectedValue);
        Assert.Equal(BoardRoles.Member, Saved());
        Assert.True(role.IsEditing);

        // A pick from the open list is saved at once, and ends the edit.
        list.SelectedValue = BoardRoles.Chair;
        OffScreen.Pump();
        Assert.False(role.IsEditing);
        Assert.Equal(BoardRoles.Chair, Saved());

        // F4 opens a choice that isn't being edited yet; Wood Badge's is
        // named after its column too.
        var woodBadge = Cell(page, row, "Wood Badge");
        Press(woodBadge, Key.F4);
        Assert.True(woodBadge.IsEditing);
        Assert.Equal(3, opened.Count);
        Assert.Equal("Wood Badge", AutomationProperties.GetName(opened[2]));
        page.CommitEdit();

        // A text cell is still typed into: F2 edits it (the grid's own key,
        // on its current cell), with no list.
        var last = Cell(page, row, "Last name");
        page.Grid.CurrentCell = new DataGridCellInfo(last);
        Press(last, Key.F2);
        Assert.True(last.IsEditing);
        Assert.Equal(3, opened.Count);
        page.CommitEdit();
    });

    [Fact]
    public void RowsAndCellsAreReadAsWhatTheyShow() => OffScreen.Run(() => InCulture("en-US", () =>
    {
        // A row was read as "EagleBoards.App.RecordRow", and a cell with no
        // text of its own (a status, the Wood Badge mark, a blank) as "Item:
        // EagleBoards.App.RecordRow, Column Display Index: 6".
        using var e = new TestEvent();
        e.Adult("Abernathy", "Anneliese", "2001", BoardRoles.Member, BoardRoles.Unavailable);
        e.Youth("Aldridge", "Alexander", "1001", BoardTypes.Final);
        var adults = Page(e.Window, "Adults");
        var adult = adults.Rows.Single();
        string Name(TablePage page, RecordRow row, string header) => AutomationProperties.GetName(Cell(page, row, header));

        Assert.Equal("Anneliese Abernathy", adult.ToString());
        Assert.Equal("Abernathy", Name(adults, adult, "Last name"));
        Assert.Equal("Member", Name(adults, adult, "Final board role"));
        Assert.Equal("No thanks", Name(adults, adult, "Project review role"));
        Assert.Equal("No", Name(adults, adult, "Wood Badge"));
        Assert.Equal("Blank", Name(adults, adult, "Introduces"));

        // A time as the clock shows one, not the file's 24-hour stamp.
        Assert.Matches(@"^\d{1,2}:\d{2}\s?(AM|PM)$", Name(adults, adult, "Time signed in"));

        // What the table holds is read with it.
        Assert.StartsWith(TableSpecs.Adults.Description, AutomationProperties.GetHelpText(adults.Grid));

        var youth = Page(e.Window, "Youth");
        var scout = youth.Rows.Single();
        Assert.Equal("Waiting", Name(youth, scout, "Status"));
        Assert.Equal("Final board", Name(youth, scout, "Board type"));
        Assert.Equal("Blank", Name(youth, scout, "Result"));

        // The history's roles are in words too, its last event a date in words.
        var history = Page(e.Window, "AdultHistory");
        var kept = history.Rows.Single();
        Assert.Equal("No thanks", Name(history, kept, "Project review role"));
        Assert.Equal("Today", Name(history, kept, "Last event"));
        Assert.Equal("August 25, 2026", RecordRow.DateText("2026-08-25"));
        InCulture("en-GB", () => Assert.Equal("25 August 2026", RecordRow.DateText("2026-08-25")));
    }));

    [Fact]
    public void TheGridsShortcutMenuSortsAColumnAndHasThePagesCommands() => OffScreen.Run(() =>
    {
        // A column heading takes no keyboard focus: sorting and widening a
        // column were the mouse's alone.
        using var e = new TestEvent();
        e.Adult("Abernathy", "Anneliese", "2001", BoardRoles.Member);
        e.Adult("Blackwood", "Bartholomew", "2002", BoardRoles.Member);
        var page = Page(e.Window, "Adults");
        var column = page.Grid.Columns.First(c => (string)c.Header == "Last name");
        Assert.Equal(System.ComponentModel.ListSortDirection.Ascending, column.SortDirection);

        var items = page.MenuFor(column).Items.OfType<MenuItem>().ToList();
        Assert.Equal(["Sor_t by Last name", "Sort by Last name, d_escending", "_Fit Last name to its contents"], items.Take(3).Select(i => (string)i.Header));

        // Then every command on the page, enabled as its button is.
        var buttons = OffScreen.Field<StackPanel>(page, "_commands").Children.OfType<Button>().ToList();
        Assert.Equal(buttons.Count, items.Count - 3);
        Assert.Equal(buttons.Select(b => b.IsEnabled), items.Skip(3).Select(i => i.IsEnabled));
        Assert.Contains(items, i => (string)i.Header == "E_xport...");
        Assert.Contains(items, i => (string)i.Header == "_Delete...");

        items[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        OffScreen.Pump();
        Assert.Equal(["Blackwood", "Abernathy"], page.Grid.Items.Cast<RecordRow>().Select(r => r["Last"]));
        Assert.Equal(System.ComponentModel.ListSortDirection.Descending, column.SortDirection);

        items[2].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.True(column.Width.IsAuto);
    });

    [Fact]
    public void FindNarrowsTheTableAndGoesToTheFirstMatch() => OffScreen.Run(() =>
    {
        using var e = new TestEvent();
        e.Adult("Abernathy", "Anneliese", "2001", BoardRoles.Member);
        e.Adult("Blackwood", "Bartholomew", "2002", BoardRoles.Member);
        e.Adult("Castellano", "Clementine", "2003", BoardRoles.Member);
        var page = Page(e.Window, "Adults");
        var count = OffScreen.Field<TextBlock>(page, "_count");
        Assert.Equal("3 records", count.Text);

        // The count is a polite live region, read out once typing pauses.
        Assert.Equal(System.Windows.Automation.AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(count));
        page.Find.Text = "black";
        OffScreen.Pump();
        Assert.Equal("1 of 3 records", count.Text);
        Assert.Single(page.Grid.Items);

        Press(page.Find, Key.Down);
        Assert.Equal("Blackwood", page.Selected?["Last"]);
        Assert.Equal("black", page.Find.Text);

        Press(page.Find, Key.Escape);
        Assert.Equal("", page.Find.Text);
        Assert.Equal(3, page.Grid.Items.Count);
    });

    [Fact]
    public void TheStartReviewReminderSaysWhomToFetchAndWhere() => OffScreen.Run(() =>
    {
        // SPEC.md D-23. The words only: the reminder itself is a dialog.
        var youth = new ScoutRow(new Dictionary<string, string>
        {
            ["ID"] = "S1", ["Last"] = "Aldridge", ["First"] = "Alexander", ["BoardType"] = BoardTypes.Final,
            ["Room"] = "101", ["Status"] = BoardStatus.Seated,
        });
        AdultInfo Adult(string first, string last, string room) => new("A" + last, last, first, "Troop1001", room, BoardRoles.Member, BoardRoles.Member);

        Assert.Equal("Fetch **Jo Jones** to introduce **Alexander Aldridge** to the board. They're on the board in room 104.",
            MainWindow.IntroductionReminder(youth, new Introduction([Adult("Jo", "Jones", "104")], [])));
        Assert.Equal("No one has said they'll introduce **Alexander Aldridge**. Their leader, **Sam Smith**, is in the main room.",
            MainWindow.IntroductionReminder(youth, new Introduction([], [Adult("Sam", "Smith", "")])));
        Assert.Equal("No one has said they'll introduce **Alexander Aldridge**, and their leader hasn't signed in. Ask Alexander who will.",
            MainWindow.IntroductionReminder(youth, new Introduction([], [])));
    });
}
