using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace EagleBoards.App;

public partial class MainWindow
{
    // ------------------------------------------------------------------
    // Focus
    // ------------------------------------------------------------------

    /// <summary>The focus on a row's button in one of the details pane's lists: which list, whose row, which button, and where.</summary>
    private sealed record FocusMark(ItemsControl List, string RowId, string Role, int Index, UIElement Element);

    /// <summary>The focused element, or, while a dialog of this window has just closed, the one the window will give it back to.</summary>
    private IInputElement? FocusedHere() =>
        Keyboard.FocusedElement is DependencyObject d && GetWindow(d) == this ? Keyboard.FocusedElement : FocusManager.GetFocusedElement(this);

    private FocusMark? MarkFocus()
    {
        if (FocusedHere() is not FrameworkElement { DataContext: PickRow row } focused)
        {
            return null;
        }

        foreach (var list in new ItemsControl[] { PickList, AvailableList, BoardMemberList })
        {
            if (list.IsAncestorOf(focused))
            {
                return new FocusMark(list, row.Id, AutomationProperties.GetAutomationId(focused), list.Items.IndexOf(row), focused);
            }
        }

        return null;
    }

    /// <summary>
    /// Back on the same button of the same adult's row once the list is
    /// rebuilt; if they left it (added, removed), on the row now in their
    /// place, or the last; if the list is empty, on Find an adult or Add
    /// member. Nothing moves if the list wasn't rebuilt, or the operator has
    /// gone somewhere else meanwhile.
    /// </summary>
    private void RestoreFocus(FocusMark mark)
    {
        var now = Keyboard.FocusedElement;
        if (mark.Element.IsVisible || !FocusSearch.HasLostFocus(now, mark.Element, this) || !mark.List.IsVisible)
        {
            return;
        }

        var rows = mark.List.Items.OfType<PickRow>().ToList();
        if (rows.Count == 0)
        {
            (ReferenceEquals(mark.List, BoardMemberList) ? AddMemberButton : (UIElement)AvailableFilter).Focus();
            return;
        }

        var row = rows.FirstOrDefault(p => p.Id == mark.RowId) ?? rows[Math.Clamp(mark.Index, 0, rows.Count - 1)];
        if (mark.List.ItemContainerGenerator.ContainerFromItem(row) is DependencyObject container
            && (FocusSearch.FirstFocusable(container, e => AutomationProperties.GetAutomationId(e) == mark.Role) ?? FocusSearch.FirstFocusable(container, _ => true)) is { } target)
        {
            target.Focus();
        }
    }

    /// <summary>
    /// After a step, when the control that had the focus has gone (a step's
    /// button collapses once its step is done; Gone home is disabled once
    /// pressed), the focus goes to the first of these that can take it, the
    /// next step's button usually, not to the window, where the next
    /// Tab starts again at the top (C2). A step run with Ctrl+Enter from a
    /// control still shown leaves the focus where it was.
    /// </summary>
    private void FocusAfter(IInputElement? before, params Func<IInputElement?>[] candidates)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var now = Keyboard.FocusedElement is DependencyObject d && GetWindow(d) == this ? Keyboard.FocusedElement : null;
            var gone = before is UIElement { IsVisible: false } or UIElement { IsEnabled: false };
            if (!gone && !FocusSearch.HasLostFocus(now, null, this))
            {
                return;
            }

            foreach (var candidate in candidates)
            {
                if (candidate() is UIElement { IsVisible: true, IsEnabled: true } target && target.Focus())
                {
                    return;
                }
            }
        }, DispatcherPriority.Loaded);
    }

    /// <summary>A youth's item in the queue.</summary>
    private ListBoxItem? QueueItemFor(ScoutRow? scout) =>
        scout == null ? null : QueueList.ItemContainerGenerator.ContainerFromItem(scout) as ListBoxItem;
}
