using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
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
    // Changing a board already seated or in review
    // ------------------------------------------------------------------

    /// <summary>The seated board's members as they stand, and its chair.</summary>
    private (List<AdultRow> Members, string ChairId)? RunningBoard() =>
        _current is { } s && BoardStatus.IsActive(s.Status)
            ? (_adults.Where(a => a.Room == s.Room).ToList(), s.BoardChairId)
            : null;

    /// <summary>Free adults who could join this youth's board, as dropdown choices; same-unit ones last and marked.</summary>
    private List<PickRow> JoinChoices(ScoutRow scout) =>
        [.. _adults.Where(a => a.CanPick && a.Info.RoleFor(scout.BoardType) != BoardRoles.Unavailable)
            .Select(a => new PickRow(a, scout))
            .OrderBy(p => p.SameUnit).ThenBy(p => p.Adult.Last, StringComparer.OrdinalIgnoreCase)];

    /// <summary>A dropdown of adults, each shown with the marks after their name (SPEC.md D-20); its value is the adult's ID.</summary>
    private ComboBox AddAdultChoice(AppDialog dialog, string label, IReadOnlyList<PickRow> adults) =>
        dialog.AddChoice(label, adults, nameof(PickRow.Id), (DataTemplate)FindResource("AdultChoice"));

    private void ChangeMembers(ScoutRow scout, IEnumerable<string> memberIds, string chairId, string success)
    {
        var ids = memberIds.Distinct().ToList();
        Report(_svc.ChangeBoardMembers(scout.Id, chairId, string.Join(",", ids)), "Couldn't change the board", success);
        var conflicts = BoardRules.FindUnitConflicts(scout.UnitName,
            _adults.Where(a => ids.Contains(a.Id)).Select(a => new BoardCandidate(a.Id, a.Last, a.First, a.UnitName)));
        if (conflicts.Count > 0 && !DetailNotice.IsOpen)
        {
            DetailNotice.Show(Severity.Warning, "Same unit",
                $"{string.Join(", ", conflicts.Select(c => $"{c.First} {c.Last}"))} {(conflicts.Count == 1 ? "is" : "are")} in the youth's own unit.");
        }
    }

    /// <summary>The chair after a change: the same one if they stay, else the first qualified member left.</summary>
    private string ChairAfter(ScoutRow scout, IReadOnlyCollection<string> ids, string currentChair) =>
        ids.Contains(currentChair) ? currentChair
        : _adults.FirstOrDefault(a => ids.Contains(a.Id) && a.Info.RoleFor(scout.BoardType) == BoardRoles.Chair)?.Id ?? currentChair;

    private void OnReplaceMember(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: PickRow leaving } || _current is not { } scout || RunningBoard() is not { } board)
        {
            return;
        }

        var choices = JoinChoices(scout);
        if (choices.Count == 0)
        {
            DetailNotice.Show(Severity.Informational, "No one is free", "Everyone else is on a board, gone home, or unavailable for this board type.");
            return;
        }

        var dialog = new AppDialog(this, $"Replace {leaving.Name}?", "Replace");
        dialog.AddMessage($"**{leaving.Name}** leaves room {scout.Room} and is free for another board. The board carries on; its time isn't reset.");
        var pick = AddAdultChoice(dialog, "Replace _with", choices);
        if (leaving.IsChair)
        {
            dialog.AddInfo(Severity.Informational, "", "They're the chair. The new member takes the chair if they're qualified; otherwise another qualified member on the board does.");
        }

        if (!dialog.ShowDialog() || pick.SelectedValue is not string joining)
        {
            return;
        }

        var ids = board.Members.Select(m => m.Id == leaving.Id ? joining : m.Id).ToList();
        var chair = leaving.IsChair && _adults.FirstOrDefault(a => a.Id == joining)?.Info.RoleFor(scout.BoardType) == BoardRoles.Chair
            ? joining
            : ChairAfter(scout, ids, board.ChairId);
        var name = _adults.FirstOrDefault(a => a.Id == joining)?.FullName;
        ChangeMembers(scout, ids, chair, $"{name} replaces {leaving.Name} on the board.");
    }

    /// <summary>
    /// No confirmation: adding them back is as easy. Except the only member
    /// who can chair: the board would have no chair, which the service
    /// refuses in file words; say what to do instead (D2).
    /// </summary>
    private void OnRemoveMember(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: PickRow leaving } || _current is not { } scout || RunningBoard() is not { } board)
        {
            return;
        }

        if (leaving.Id == board.ChairId
            && !board.Members.Any(m => m.Id != leaving.Id && m.Info.RoleFor(scout.BoardType) == BoardRoles.Chair))
        {
            DetailNotice.Show(Severity.Warning, $"{leaving.Name} is the only chair",
                "No one else on this board can chair it. Use Replace to swap in a qualified chair, or add one with Add member first.");
            return;
        }

        var ids = board.Members.Where(m => m.Id != leaving.Id).Select(m => m.Id).ToList();
        ChangeMembers(scout, ids, ChairAfter(scout, ids, board.ChairId), $"{leaving.Name} is off the board.");
    }

    private void OnAddMember(object sender, RoutedEventArgs e)
    {
        if (_current is not { } scout || RunningBoard() is not { } board)
        {
            return;
        }

        var choices = JoinChoices(scout);
        if (choices.Count == 0)
        {
            DetailNotice.Show(Severity.Informational, "No one is free", "Everyone else is on a board, gone home, or unavailable for this board type.");
            return;
        }

        var dialog = new AppDialog(this, "Add a member?", "Add");
        var pick = AddAdultChoice(dialog, "_Add to the board in room " + scout.Room.Replace("_", "__", StringComparison.Ordinal), choices);
        if (dialog.ShowDialog() && pick.SelectedValue is string joining)
        {
            var name = _adults.FirstOrDefault(a => a.Id == joining)?.FullName;
            ChangeMembers(scout, board.Members.Select(m => m.Id).Append(joining), board.ChairId, $"{name} is on the board.");
        }
    }

    private void OnChangeChair(object sender, RoutedEventArgs e)
    {
        if (_current is not { } scout || RunningBoard() is not { } board)
        {
            return;
        }

        var qualified = board.Members.Where(m => m.Info.RoleFor(scout.BoardType) == BoardRoles.Chair && m.Id != board.ChairId)
            .Select(m => new PickRow(m, scout)).ToList();
        if (qualified.Count == 0)
        {
            DetailNotice.Show(Severity.Informational, "No one else can chair",
                "No other member of this board is qualified to chair. Replace someone with a qualified chair, or make a member a chair on the Adults page.");
            return;
        }

        var dialog = new AppDialog(this, "Change the chair?", "Change chair");
        var pick = AddAdultChoice(dialog, "_New chair", qualified);
        if (dialog.ShowDialog() && pick.SelectedValue is string chair)
        {
            var name = _adults.FirstOrDefault(a => a.Id == chair)?.FullName;
            ChangeMembers(scout, board.Members.Select(m => m.Id), chair, $"{name} is now the chair.");
        }
    }

    /// <summary>
    /// A step's result: a refusal in the details pane, and a step that
    /// worked said aloud, speech only, since the screen shows it (SPEC.md
    /// D-14) but the button used has often gone (K2). True if it worked.
    /// </summary>
    private bool Report(ActionResult result, string failure, string? success = null)
    {
        if (!result.Ok)
        {
            DetailNotice.Show(Severity.Error, failure, Plain(result.Message));
        }

        RefreshAll();
        if (result.Ok && success != null)
        {
            Say(success);
        }

        return result.Ok;
    }

    /// <summary>A step that worked, said to a screen reader and put nowhere on screen.</summary>
    private void Say(string text) => Announcer.Say(this, text, "EagleBoards.Step", important: true);

    /// <summary>
    /// The server's refusals start "ERROR: " for the check-in pages; the window
    /// says it once, in the title. Names in them are as stored, so a comma
    /// saved as "~" is put back.
    /// </summary>
    private static string Plain(string message) => Display.Text(message.StartsWith("ERROR: ", StringComparison.Ordinal) ? message[7..] : message);
}
