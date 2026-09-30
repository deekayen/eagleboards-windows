using System.Windows;
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
        var w = e.Window;
        OffScreen.Invoke(w, "ShowPage", "Adults");
        OffScreen.Pump();

        var page = OffScreen.Field<Dictionary<string, TablePage>>(w, "_tables")["Adults"];
        var opened = new List<ComboBox>();
        page.OpenList = opened.Add;   // a real list's popup would open on the desktop

        var row = page.Rows.Single();
        DataGridCell Cell(string header)
        {
            var column = page.Grid.Columns.First(c => (string)c.Header == header);
            page.Grid.ScrollIntoView(row, column);
            OffScreen.Pump();
            return OffScreen.Ancestor<DataGridCell>(column.GetCellContent(row))!;
        }

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

        var last = Cell("Last");
        Click(last);
        Assert.False(last.IsEditing);
        Assert.Equal(2, opened.Count);
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
