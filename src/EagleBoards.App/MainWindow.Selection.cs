using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using EagleBoards.Core.Records;
using Microsoft.Win32;

namespace EagleBoards.App;

public partial class MainWindow
{
    // ------------------------------------------------------------------
    // Selection
    // ------------------------------------------------------------------

    private void OnQueueSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_quiet && QueueList.SelectedItem is ScoutRow s)
        {
            Open(s);
        }
    }

    /// <summary>Clicking the youth already open opens them again, as a fresh click would.</summary>
    private void OnQueueClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(QueueList, (DependencyObject)e.OriginalSource) is ListBoxItem { Content: ScoutRow row }
            && ReferenceEquals(row, _current))
        {
            Dispatcher.BeginInvoke(() => Open(row), DispatcherPriority.Input);
        }
    }

    /// <summary>
    /// The youth list's shortcut menu: the open youth's steps, as the
    /// details pane's buttons have them. Shift+F10 on a youth the arrow keys
    /// reached without opening (Ctrl+arrow) opens them first. Nothing to do,
    /// no menu.
    /// </summary>
    private void OnQueueMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (Keyboard.FocusedElement is ListBoxItem { Content: ScoutRow row } && !ReferenceEquals(row, _current))
        {
            Open(row);
        }

        SyncQueueMenu();
        if (_current == null || QueueMenuItems().All(i => i.Item.Visibility != Visibility.Visible))
        {
            e.Handled = true;
        }
    }

    private (MenuItem Item, Button Button)[] QueueMenuItems() =>
    [
        (QueueSeat, SeatButton), (QueuePostpone, PostponeButton), (QueueStart, StartButton), (QueueComplete, CompleteButton), (QueueReset, ResetButton),
    ];

    /// <summary>The queue's shortcut menu as the step buttons stand: shown, enabled, and "Seat anyway" when Seat says so.</summary>
    private void SyncQueueMenu()
    {
        foreach (var (item, button) in QueueMenuItems())
        {
            item.Visibility = button.Visibility;
            item.IsEnabled = button.IsEnabled;
        }

        QueueSeat.Header = SeatText.Text;
    }
}
