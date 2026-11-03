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
    // Board lifecycle
    // ------------------------------------------------------------------

    /// <summary>
    /// Waiting → Seated. The builder has already listed every problem and
    /// only enables Seat when nothing blocks it; warnings (same unit, a larger
    /// board, the other type's room) make it "Seat anyway". The server
    /// re-checks the hard rules.
    /// </summary>
    private void OnSeat(object sender, RoutedEventArgs e)
    {
        if (_current is not { } scout || !BoardStatus.IsWaiting(scout.Status) || _builderRoomId is not { } roomId)
        {
            return;
        }

        var before = FocusedHere();
        var room = _rooms.FirstOrDefault(r => r.Id == roomId)?.Room;
        var picked = _adults.Where(IsPicked).ToList();
        var result = _svc.SeatBoard(roomId, scout.Id, _chairId, string.Join(",", picked.Select(a => a.Id)));
        if (!result.Ok)
        {
            ShowDetailNotice(Severity.Error, "Couldn't seat the board", Plain(result.Message));
        }

        _chairId = null;
        _builderRoomId = null;
        _roomChosenByOperator = false;
        _proposal = null;
        RefreshAll();
        Open(scout);
        if (result.Ok)
        {
            Say($"{scout.FullName}'s board is seated in room {room}. Start review is next.");
            FocusAfter(before, () => StartButton, () => QueueItemFor(scout));
        }
    }

    /// <summary>
    /// Seated → In review. On a board of review, first the reminder to fetch
    /// the adult who introduces the youth, saying where they are (SPEC.md
    /// D-23); a project review has no introduction and starts at once.
    /// Reset undoes either.
    /// </summary>
    private void OnStart(object sender, RoutedEventArgs e)
    {
        if (_current is not { Status: BoardStatus.Seated } scout)
        {
            return;
        }

        var before = FocusedHere();
        if (SchedulerLogic.IntroductionFor(scout.Info, _adults.Select(a => a.Info)) is { } introduction
            && !StartReviewReminder(this, scout, introduction).ShowDialog())
        {
            return;
        }

        if (Report(_svc.StartReview(scout.Id), "Couldn't start the review", $"{scout.FullName}'s review has started in room {scout.Room}. Complete is next."))
        {
            FocusAfter(before, () => _resultButtons.Values.FirstOrDefault(b => b.IsChecked == true), () => CompleteButton);
        }
    }

    /// <summary>
    /// The reminder Start review shows on a board of review (SPEC.md D-23).
    /// It's the owner's reminder, not a confirmation (CLAUDE.md), so it stays;
    /// its buttons answer the title with a verb, and Cancel (P-4).
    /// </summary>
    internal static AppDialog StartReviewReminder(Window owner, ScoutRow scout, Introduction introduction)
    {
        var dialog = new AppDialog(owner, "Start the review?", "Start review");
        dialog.AddMessage(IntroductionReminder(scout, introduction));
        dialog.AddMessage($"Then bring {scout.First} in to room {scout.Room}.");
        return dialog;
    }

    /// <summary>Whom to fetch for the introduction, and where they are; never a parent.</summary>
    internal static string IntroductionReminder(ScoutRow scout, Introduction introduction)
    {
        static string Name(AdultInfo a) => $"**{a.First} {a.Last}**";
        var youth = $"**{scout.FullName}**";
        return introduction switch
        {
            { Introducers: [var one] } => $"Fetch {Name(one)} to introduce {youth} to the board. They're {SchedulerLogic.Whereabouts(one)}.",
            { Introducers.Count: > 1 } => $"Fetch whoever introduces {youth} to the board:\n"
                + string.Join("\n", introduction.Introducers.Select(a => $"{Name(a)}, {SchedulerLogic.Whereabouts(a)}")),
            { Leaders: [var one] } => $"No one has said they'll introduce {youth}. Their leader, {Name(one)}, is {SchedulerLogic.Whereabouts(one)}.",
            { Leaders.Count: > 1 } => $"No one has said they'll introduce {youth}. Their leaders:\n"
                + string.Join("\n", introduction.Leaders.Select(a => $"{Name(a)}, {SchedulerLogic.Whereabouts(a)}")),
            _ => $"No one has said they'll introduce {youth}, and their leader hasn't signed in. Ask {scout.First} who will.",
        };
    }

    private void OnComplete(object sender, RoutedEventArgs e)
    {
        if (_current is not { Status: BoardStatus.InProgress } scout)
        {
            return;
        }

        var before = FocusedHere();
        var room = scout.Room;
        var outcome = _resultButtons.First(b => b.Value.IsChecked == true).Key;
        if (Report(_svc.CompleteBoard(scout.Id, outcome, NotesBox.Text.Trim()), "Couldn't complete the board",
                $"{scout.FullName}'s board is complete: {Display.Result(outcome)}. Room {room} is free."))
        {
            FocusAfter(before, () => QueueItemFor(scout), () => QueueList);
        }
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        if (_current is not { } scout)
        {
            return;
        }

        var before = FocusedHere();
        var room = scout.Room;
        if (AppDialog.Confirm(this, "Reset this board?",
                $"**{scout.FullName}** goes back to waiting. Room {room} and its members are freed, and the board would need seating again.",
                "Reset board")
            && Report(_svc.ResetBoard(scout.Id), "Couldn't reset the board", $"{scout.FullName} is waiting again. Room {room} is free."))
        {
            FocusAfter(before, () => QueueItemFor(scout), () => QueueList);
        }
    }

    private void OnPostpone(object sender, RoutedEventArgs e)
    {
        if (_current is not { } scout || !BoardStatus.IsWaiting(scout.Status))
        {
            return;
        }

        var before = FocusedHere();
        if (AppDialog.Confirm(this, "Postpone this board?",
                $"**{scout.FullName}** won't have a board at this event. This is usually because the paperwork isn't in order.", "Postpone")
            && Report(_svc.PostponeBoard(scout.Id), "Couldn't postpone the board", $"{scout.FullName}'s board is postponed."))
        {
            FocusAfter(before, () => QueueItemFor(scout), () => QueueList);
        }
    }
}
