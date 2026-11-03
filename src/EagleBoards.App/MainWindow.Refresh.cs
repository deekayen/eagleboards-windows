using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using EagleBoards.Core;
using EagleBoards.Core.Records;

namespace EagleBoards.App;

public partial class MainWindow
{
    // ------------------------------------------------------------------
    // Refresh
    // ------------------------------------------------------------------

    private void RefreshAll()
    {
        _config = _svc.GetConfig();
        RowSync.Sync(_scouts, _svc.Snapshot(DataTable.Scouts), r => new ScoutRow(r), (row, r) => row.Update(r));
        RowSync.Sync(_adults, _svc.Snapshot(DataTable.Adults), r => new AdultRow(r) { OnSelChanged = OnPickChanged }, (row, r) => row.Update(r));
        RowSync.Sync(_rooms, _svc.Snapshot(DataTable.Rooms), r => new RoomCard(r), (row, r) => row.Update(r));
        if (_current != null && !_scouts.Contains(_current))
        {
            _current = null;
        }

        // A board proposed for the open youth, and not changed since, follows
        // the event: an adult who signs in or frees up is weighed at once,
        // without Start over (SPEC.md D-12). One the operator has changed keeps
        // its picks, but its room still follows the event: a room the app
        // proposed that is taken or switched to the other board type is
        // withdrawn (D-24), not left for the operator to find in the warnings.
        // A board the operator emptied is theirs and stays empty, so a
        // proposal alone, with no picks left, is not enough to propose again.
        if (_current is { } open && BoardStatus.IsWaiting(open.Status)
            && ((_proposal?.ScoutId == open.Id && ProposalUntouched()) || _adults.Any(IsPicked)))
        {
            ProposeBoard(open);
        }

        UpdateRoomTimers();
        ShowDetails();
        foreach (var table in _tables.Values.Where(t => t.Visibility == Visibility.Visible))
        {
            table.Reload(quiet: true);
        }

        UpdatePageButtons();
        if (PersonFind.Text.Trim().Length > 0)
        {
            UpdatePersonFind();
        }

        // Edit > Undo (and Ctrl+Z) is the one way to undo, and names the
        // step, as the Mac's does. A name's own underscore mustn't become an
        // access key.
        UndoMenu.IsEnabled = _svc.CanUndo;
        UndoMenu.Header = _svc.UndoDescription is { } step ? "_Undo " + step.Replace("_", "__", StringComparison.Ordinal) : "_Undo";
    }

    /// <summary>Note everyone signed in now, so only those who come later are said to have arrived.</summary>
    private void Remember()
    {
        _known.UnionWith(_scouts.Select(s => s.Id));
        _known.UnionWith(_adults.Select(a => a.Id));
    }

    /// <summary>
    /// Who signed in at the door since the last refresh, said once and
    /// politely (K2): one sentence per debounce however many came, never one
    /// per person during the rush. Speech only: the queue shows them.
    /// </summary>
    private void SayArrivals()
    {
        var youth = _scouts.Where(s => !_known.Contains(s.Id)).Select(s => s.FullName).ToList();
        var adults = _adults.Where(a => !_known.Contains(a.Id)).Select(a => a.FullName).ToList();
        Remember();
        var text = (youth.Count, adults.Count) switch
        {
            (0, 0) => "",
            (1, 0) => $"{youth[0]} signed in.",
            (0, 1) => $"{adults[0]} signed in.",
            (_, 0) => $"{Display.Count(youth.Count, "youth", "youth")} signed in.",
            (0, _) => $"{Display.Count(adults.Count, "adult", "adults")} signed in.",
            _ => $"{Display.Count(youth.Count, "youth", "youth")} and {Display.Count(adults.Count, "adult", "adults")} signed in.",
        };
        Announcer.Say(QueueList, text, "EagleBoards.SignIn");
    }

    /// <summary>Times are stamped to the minute, so counts change on the clock's minute: tick just after it.</summary>
    private void ScheduleMinute()
    {
        var now = DateTime.Now;
        _minute.Interval = TimeSpan.FromSeconds(60 - now.Second) - TimeSpan.FromMilliseconds(now.Millisecond) + TimeSpan.FromMilliseconds(250);
        _minute.Start();
    }

    /// <summary>
    /// Only what the minute changes: the counts, the room timers and the
    /// details pane's Room fact. The member lists are left as they are, so a
    /// keyboard user on a Replace or Remove button keeps their place (F2).
    /// </summary>
    private void OnMinute()
    {
        _minute.Stop();
        foreach (var s in _scouts)
        {
            s.Tick();
        }

        UpdateRoomTimers();
        if (_current is { } scout && !BoardStatus.IsWaiting(scout.Status))
        {
            ShowFacts(scout);
        }

        ScheduleMinute();
    }

    /// <summary>
    /// Minutes on each room holding a board, caution then overdue at the
    /// board-type thresholds. Runs on the youth's minutes since the last
    /// change, so the clock restarts by itself at Seat and at Start review.
    /// A room turning running long or overdue is said, once, as the tint is
    /// seen in passing (F4); speech only, nothing more on screen.
    /// </summary>
    private void UpdateRoomTimers()
    {
        var turned = new List<string>();
        foreach (var card in _rooms)
        {
            var scout = _scouts.FirstOrDefault(s => s.Room == card.Room && BoardStatus.IsActive(s.Status));
            var mins = scout?.Mins;
            var state = mins is { } m ? SchedulerLogic.TimerFor(scout!.Status, scout.BoardType, m, _config) : TimerState.None;
            var was = card.TimerState;
            card.Mins = mins;
            card.TimerText = mins is { } n ? $"{n} min" : "";
            card.TimerState = state;
            if (_timedRooms.Contains(card.Id) && state != was && state is TimerState.Warning or TimerState.Overdue)
            {
                turned.Add($"Room {card.Room} is {(state == TimerState.Overdue ? "overdue" : "running long")}: {scout!.FullName}, {Display.Minutes(mins!.Value)}.");
            }

            _timedRooms.Add(card.Id);
        }

        Announcer.Say(RoomList, string.Join(" ", turned), "EagleBoards.RoomTimer");
    }
}
