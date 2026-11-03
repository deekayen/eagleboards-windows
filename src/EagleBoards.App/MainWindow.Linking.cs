using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace EagleBoards.App;

public partial class MainWindow
{
    // ------------------------------------------------------------------
    // Linking the adult who introduces a youth
    // ------------------------------------------------------------------

    /// <summary>
    /// Link an adult to this youth as the one who introduces them to their
    /// board (SPEC.md D-23), for the adult who didn't say so at sign-in.
    /// They're listed under Leaders and parents from then on, with where to
    /// find them, and Start review reminds the operator to fetch them. Works
    /// for an adult on a board too: a Scoutmaster often is by then. With no
    /// one to link, it says so instead of doing nothing (K10).
    /// </summary>
    private void OnLinkAdult(object sender, RoutedEventArgs e)
    {
        if (_current is not { } scout)
        {
            return;
        }

        var choices = _adults.Where(a => !a.Info.Supports(scout.Id))
            .OrderBy(a => a.Last, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.First, StringComparer.OrdinalIgnoreCase)
            .Select(a => new Option(a.Id, string.Join(" · ", new[] { a.FullName, Display.Unit(a.UnitName) }.Where(s => s.Length > 0)))).ToList();
        if (choices.Count == 0)
        {
            ShowDetailNotice(Severity.Informational, "No adult to link",
                _adults.Count == 0 ? "No adult has signed in yet." : $"Every adult who has signed in is already linked to {scout.FullName}.");
            return;
        }

        var dialog = new AppDialog(this, $"Link an adult to {scout.FullName}?", "Link");
        dialog.AddMessage($"Choose the adult who will introduce **{scout.FullName}** to their board. "
            + "This is usually their Scoutmaster or a leader standing in. Start review will remind you to fetch them.");
        var pick = dialog.AddChoice("_Adult", choices, null);
        if (dialog.ShowDialog() && pick.SelectedValue is string adultId)
        {
            var name = _adults.FirstOrDefault(a => a.Id == adultId)?.FullName;
            Report(_svc.SetSupporting(adultId, scout.Id, linked: true), "Couldn't link them", $"{name} is linked to {scout.FullName}.");
        }
    }

    private void OnUnlinkAdult(object sender, RoutedEventArgs e)
    {
        if (_current is not { } scout)
        {
            return;
        }

        var linked = _adults.Where(a => a.Info.Supports(scout.Id))
            .Select(a => new Option(a.Id, a.FullName)).ToList();
        if (linked.Count == 0)
        {
            return;
        }

        var before = FocusedHere();
        var dialog = new AppDialog(this, $"Unlink an adult from {scout.FullName}?", "Unlink");
        var pick = dialog.AddChoice("_Adult", linked, null);
        if (dialog.ShowDialog() && pick.SelectedValue is string adultId)
        {
            var name = _adults.FirstOrDefault(a => a.Id == adultId)?.FullName;
            Report(_svc.SetSupporting(adultId, scout.Id, linked: false), "Couldn't unlink them", $"{name} is no longer linked to {scout.FullName}.");

            // Unlink goes with the last link.
            FocusAfter(before, () => LinkButton);
        }
    }

    private void OnChairChosen(PickRow row)
    {
        _chairId = row.Id;
        Dispatcher.BeginInvoke(ShowDetails, DispatcherPriority.Input);
    }

    private void OnBuilderRoomChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_quiet)
        {
            _builderRoomId = BuilderRoom.SelectedValue as string;

            // A room chosen from the list is the operator's own, so it stays
            // even when it is set up for the other board type (SPEC.md D-24).
            _roomChosenByOperator = true;
            ShowDetails();
        }
    }
}
