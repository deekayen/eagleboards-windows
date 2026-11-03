using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using EagleBoards.Core;
using EagleBoards.Core.Records;

namespace EagleBoards.App;

public partial class MainWindow
{
    // ------------------------------------------------------------------
    // Details pane
    // ------------------------------------------------------------------

    private void ShowDetails()
    {
        // The member lists are rebuilt on every change, a sign-in at the door
        // included: put the focus back where it was (C1).
        if (MarkFocus() is { } mark)
        {
            Dispatcher.BeginInvoke(() => RestoreFocus(mark), DispatcherPriority.Loaded);
        }

        var scout = _current;
        DetailsEmpty.Visibility = scout == null ? Visibility.Visible : Visibility.Collapsed;
        DetailsHead.Visibility = scout != null ? Visibility.Visible : Visibility.Collapsed;
        LocateSection.Visibility = DetailsHead.Visibility;
        BuilderSection.Visibility = scout != null && BoardStatus.IsWaiting(scout.Status) ? Visibility.Visible : Visibility.Collapsed;
        BoardSection.Visibility = scout != null && !BoardStatus.IsWaiting(scout.Status) ? Visibility.Visible : Visibility.Collapsed;
        ShowActions(scout);
        if (scout != null)
        {
            DetailName.Text = scout.FullName;
            DetailSub.Text = string.Join(" · ", new[] { Display.Unit(scout.UnitName), Display.BoardType(scout.BoardType), scout.RegNum }.Where(s => s.Length > 0));
            AutomationProperties.SetName(DetailSub, Display.Spoken(DetailSub.Text));
            DetailStatus.Content = null;
            DetailStatus.Content = scout;

            if (BoardStatus.IsWaiting(scout.Status))
            {
                ShowBuilder(scout);
            }
            else
            {
                ShowBoard(scout);
            }

            ShowLocate(scout);
        }

        SyncQueueMenu();
        SyncBoardMenu();
    }

    /// <summary>
    /// The buttons for the youth's next step, in the bar pinned to the foot
    /// of the pane: Seat board and Postpone while they wait, Start review or
    /// Complete once seated, and Reset. The bar goes when there's no step.
    /// </summary>
    private void ShowActions(ScoutRow? scout)
    {
        var status = scout?.Status;
        var waiting = status != null && BoardStatus.IsWaiting(status);
        var buttons = new (Button Button, bool Shown)[]
        {
            (SeatButton, waiting),
            (PostponeButton, waiting),
            (StartButton, status == BoardStatus.Seated),
            (CompleteButton, status == BoardStatus.InProgress),
            (ResetButton, status != null && !waiting && SchedulerLogic.ActionsFor(status).HasFlag(ScoutActions.Reset)),
        };
        foreach (var (button, shown) in buttons)
        {
            button.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        }

        ActionBar.Visibility = buttons.Any(b => b.Shown) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowBuilder(ScoutRow scout)
    {
        // Rooms: free ones, this board's type first.
        var free = _rooms.Where(r => r.IsFree).OrderBy(r => r.BoardType == scout.BoardType ? 0 : 1).ThenBy(r => r.Room, StringComparer.OrdinalIgnoreCase).ToList();
        var rooms = free.Select(r => new Option(r.Id, $"{r.Room} · {r.BoardTypeText}")).ToList();
        Quietly(() =>
        {
            if (BuilderRoom.ItemsSource is not List<Option> shown || !shown.SequenceEqual(rooms))
            {
                BuilderRoom.ItemsSource = rooms;
            }

            BuilderRoom.SelectedValue = _builderRoomId;
        });

        var picked = _adults.Where(IsPicked).Select(a => new PickRow(a, scout, OnChairChosen)).ToList();
        if (picked.Where(p => p.CanChair).All(p => p.Id != _chairId))
        {
            _chairId = picked.FirstOrDefault(p => p.CanChair)?.Id;
        }

        foreach (var p in picked)
        {
            p.SetChairQuietly(p.Id == _chairId);
        }

        SetRows(PickList, [.. picked.OrderByDescending(p => p.Id == _chairId)]);
        NoPicksText.Visibility = picked.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Found by the unit in full or as the file's short form ("T1001").
        var filter = AvailableFilter.Text.Trim();
        var available = _adults
            .Where(a => !a.Sel && a.CanPick && a.Info.RoleFor(scout.BoardType) != BoardRoles.Unavailable)
            .Where(a => Matches(filter, a.Last, a.First, a.FullName, a.UnitName, a.UnitLabel, Display.Unit(a.UnitName)))
            .Select(a => new PickRow(a, scout))
            .OrderBy(p => p.SameUnit)
            .ThenByDescending(p => p.CanChair && _chairId == null)
            .ThenBy(p => p.Adult.Last, StringComparer.OrdinalIgnoreCase)
            .ToList();
        SetRows(AvailableList, available);
        NoAvailableText.Visibility = available.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var room = _rooms.FirstOrDefault(r => r.Id == _builderRoomId);
        var issues = BoardCheck.Review(scout.Info, [.. picked.Select(p => p.Adult.Info)], _chairId, room?.Info,
            [.. _rooms.Where(r => r.IsFree).Select(r => r.Info)]);
        ShowIssues(scout, issues);

        var warn = issues.Any(i => i.Level == IssueLevel.Warn);
        SeatButton.IsEnabled = issues.All(i => i.Level != IssueLevel.Block);
        FillButton.IsEnabled = picked.Count < BoardRules.MinMembers(scout.BoardType) || picked.All(p => !p.CanChair);
        SeatText.Text = warn ? "_Seat anyway" : "_Seat board";
        AutomationProperties.SetName(SeatButton, warn ? "Seat anyway" : "Seat board");

        // A disabled button is skipped by Tab, so someone using the keyboard or
        // a screen reader never reaches the reason in the bars under Board
        // members; the button carries it as help text, which a screen reader reads
        // when it describes the button.
        var firstProblem = issues.FirstOrDefault(i => i.Level == IssueLevel.Block);
        AutomationProperties.SetHelpText(SeatButton, firstProblem is null ? "" : $"{firstProblem.Title}. {firstProblem.Message}".Replace("**", ""));
    }

    /// <summary>
    /// A message at the top of the details pane, brought into view. The buttons
    /// it answers (Replace, Remove, Add member) are lower in the pane, so the
    /// pane is usually scrolled past the bar: a refused change showed nothing
    /// on screen in the 2026-11-03 live test, until the pane was scrolled
    /// back up. Done once the layout has settled, since a step refreshes the
    /// pane after it reports. Focus stays where it is, and a screen reader
    /// hears the bar from its own live region.
    /// </summary>
    private void ShowDetailNotice(Severity severity, string title, string message)
    {
        DetailNotice.Show(severity, title, message);
        Dispatcher.BeginInvoke(() => DetailNotice.BringIntoView(), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// The seating review's problems, each an InfoBar, rebuilt only when they
    /// change: every sign-in refreshes the pane, and a new bar each time was
    /// a new message to read out. A problem that appears while the operator
    /// builds this youth's board is read out; those already there when a
    /// youth is opened are only shown.
    /// </summary>
    private void ShowIssues(ScoutRow scout, IReadOnlyList<BoardIssue> issues)
    {
        static string Key(BoardIssue i) => $"{i.Level}|{i.Title}|{i.Message}";
        var ordered = issues.OrderBy(i => i.Level).ToList();
        var key = string.Join("\n", ordered.Select(Key));
        if (key == _issueKey && scout.Id == _issueScoutId)
        {
            return;
        }

        var sameYouth = scout.Id == _issueScoutId;
        var before = _issueKey.Split('\n').ToHashSet(StringComparer.Ordinal);
        IssueList.Children.Clear();
        foreach (var issue in ordered)
        {
            var bar = new InfoBar { IsClosable = false, Margin = new Thickness(0, 0, 0, 8) };
            bar.Show(issue.Level == IssueLevel.Block ? Severity.Error : Severity.Warning, issue.Title, issue.Message,
                announce: sameYouth && !before.Contains(Key(issue)));
            IssueList.Children.Add(bar);
        }

        _issueKey = key;
        _issueScoutId = scout.Id;
    }

    /// <summary>Show these rows, unless the list already shows the same: a rebuild loses the focus and the place in the list.</summary>
    private static void SetRows(ItemsControl list, List<PickRow> rows)
    {
        if (list.ItemsSource is not List<PickRow> shown || !shown.Select(p => p.Signature).SequenceEqual(rows.Select(p => p.Signature)))
        {
            list.ItemsSource = rows;
        }
    }

    /// <summary>A seated, running, or finished board: its facts, and the members of one still running.</summary>
    private void ShowBoard(ScoutRow scout)
    {
        ShowFacts(scout);
        var active = BoardStatus.IsActive(scout.Status);
        MembersSection.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        if (active)
        {
            var members = _adults.Where(a => a.Room == scout.Room)
                .Select(a => new PickRow(a, scout)).ToList();
            foreach (var m in members)
            {
                m.SetChairQuietly(m.Id == scout.BoardChairId);
            }

            SetRows(BoardMemberList, [.. members.OrderByDescending(m => m.IsChair).ThenBy(m => m.Adult.Last, StringComparer.OrdinalIgnoreCase)]);
        }

        ResultSection.Visibility = scout.Status == BoardStatus.InProgress ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The board's facts, the room's minutes among them; nothing in them takes the focus, so the minute can replace them.</summary>
    private void ShowFacts(ScoutRow scout)
    {
        var facts = new List<Fact>();
        if (BoardStatus.IsActive(scout.Status))
        {
            var what = scout.Status == BoardStatus.Seated ? "convening" : "in review";
            facts.Add(new("Room", $"{scout.Room}, {what} for {Display.Minutes(scout.Mins ?? 0)}"));
        }

        // A finished board's members are history; a running board's can change.
        if (scout.BoardChair.Length > 0 && !BoardStatus.IsActive(scout.Status))
        {
            facts.Add(new("Chair", scout.BoardChair));
            facts.Add(new("Members", scout.BoardMembersText));
        }

        if (scout.Status == BoardStatus.Completed)
        {
            facts.Add(new("Result", scout.ResultText));
            if (scout.Notes.Length > 0)
            {
                facts.Add(new("Notes", scout.Notes));
            }
        }

        FactList.ItemsSource = facts;
    }

    /// <summary>Where the youth's leaders and parents are: for fetching them, and after the board.</summary>
    private void ShowLocate(ScoutRow scout)
    {
        var found = SchedulerLogic.Locate(scout.Info, _adults.Select(a => a.Info), includeParents: true);
        LocateList.ItemsSource = found.Select(f => new Fact(
            f.IsSupporting ? "Introduces them" : f.IsLeader ? "Leader" : "Parent",
            $"{f.Adult.First} {f.Adult.Last}, {(f.Adult.Room == AdultRoom.Disabled ? "gone home" : f.Adult.Room.Length == 0 ? "in the main room" : "in room " + f.Adult.Room)}")).ToList();
        LocateNone.Text = found.Count > 0 ? ""
            : scout.Leader.Length > 0 ? $"{scout.Leader} hasn't signed in, and no parent has."
            : "No leader was given at sign-in, and no parent has signed in.";
        LocateNone.Visibility = found.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        UnlinkButton.Visibility = _adults.Any(a => a.Info.Supports(scout.Id)) ? Visibility.Visible : Visibility.Collapsed;
    }
}
