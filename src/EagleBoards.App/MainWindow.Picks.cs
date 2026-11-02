using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
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
    // Picks
    // ------------------------------------------------------------------

    /// <summary>
    /// Ticked for the board being built. Only a free adult counts: the Java
    /// version leaves a seated board's members ticked in the file, so after a
    /// hand-off from it those stale ticks must not reappear on the next board.
    /// </summary>
    private static bool IsPicked(AdultRow a) => a.Sel && a.CanPick;

    /// <summary>A pick is saved at once, so nothing can undo it behind the operator's back.</summary>
    private void OnPickChanged(AdultRow adult, bool picked) =>
        _svc.SaveRow(DataTable.Adults, "updated", adult.Id, new Dictionary<string, string> { ["Sel"] = picked ? "1" : "0" });

    private void OnAddPick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PickRow row })
        {
            row.Adult.Sel = true;
            ShowDetails();
        }
    }

    private void OnRemovePick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PickRow row })
        {
            row.Adult.Sel = false;
            ShowDetails();
        }
    }

    /// <summary>
    /// The override: keep the adults the operator chose and let auto-select
    /// complete the board around them (a chair if none of them can chair,
    /// then members up to the minimum).
    /// </summary>
    private void OnFillBoard(object sender, RoutedEventArgs e)
    {
        if (_current is not { } scout || !BoardStatus.IsWaiting(scout.Status))
        {
            return;
        }

        var before = FocusedHere();
        var picked = _adults.Where(IsPicked).Select(a => a.Id).ToList();
        var (adults, waiting) = SelectionContext(scout);
        var fill = SchedulerLogic.FillBoard(scout.Info, adults, picked, waiting);
        foreach (var id in fill.AllAdultIds)
        {
            if (_adults.FirstOrDefault(a => a.Id == id) is { } adult)
            {
                adult.Sel = true;
            }
        }

        _chairId ??= fill.ChairIds.FirstOrDefault();
        ShowDetails();

        // Fill the rest is disabled once the board is full: Seat is next.
        FocusAfter(before, () => SeatButton);
    }

    /// <summary>Untick everyone; for a waiting youth, propose a fresh board.</summary>
    private void OnClearPicks(object sender, RoutedEventArgs e)
    {
        foreach (var a in _adults.Where(a => a.Sel).ToList())
        {
            a.Sel = false;
        }

        _chairId = null;
        if (_current is { } s && BoardStatus.IsWaiting(s.Status))
        {
            ProposeBoard(s);
        }

        ShowDetails();
    }
}
