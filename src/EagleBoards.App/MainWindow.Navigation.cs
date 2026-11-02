using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using EagleBoards.Core.Records;
using Microsoft.Win32;

namespace EagleBoards.App;

public partial class MainWindow
{
    // ------------------------------------------------------------------
    // Navigation
    // ------------------------------------------------------------------

    private void OnViewPage(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string page })
        {
            ShowPage(page);
        }
    }

    /// <summary>
    /// The Event page or a table page, with the View menu's check on the page
    /// shown and its name in the title (what a screen reader's "read title"
    /// and Alt+Tab say). The focus goes into the page shown: left on the page
    /// just hidden, it fell to the window and the next Tab started at the top.
    /// </summary>
    private void ShowPage(string page)
    {
        // A cell still being edited on the page being left is saved first, so
        // the page shown next has it (the menu doesn't end the grid's edit).
        foreach (var table in _tables.Values.Where(t => t.Visibility == Visibility.Visible))
        {
            table.CommitEdit();
        }

        foreach (var (item, element) in _pages)
        {
            var shown = (string)item.Tag == page;
            item.IsChecked = shown;
            element.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            if (shown && element is TablePage table)
            {
                table.Reload(quiet: false);
            }
        }

        _page = page;
        var name = _tables.TryGetValue(page, out var shownTable) ? shownTable.Spec.Title : "Event";
        Title = $"{name} - Eagle Board Scheduler {AppVersion.Text}";
        UpdatePageButtons();

        // The Board menu acts on the Event page alone.
        CommandManager.InvalidateRequerySuggested();
        if (IsLoaded)
        {
            Dispatcher.BeginInvoke(FocusPage, DispatcherPriority.Loaded);
        }
    }

    /// <summary>Ctrl+Tab and Ctrl+Shift+Tab: the next or previous page, in the View menu's order.</summary>
    private void ShowNextPage(int step)
    {
        var at = _pages.FindIndex(p => (string)p.Item.Tag == _page);
        ShowPage((string)_pages[(at + step + _pages.Count) % _pages.Count].Item.Tag);
    }

    /// <summary>The focus into the page shown: the open youth on the Event page, a table's selected row.</summary>
    private void FocusPage()
    {
        if (EventPage.IsVisible)
        {
            FocusQueue();
        }
        else
        {
            _tables.Values.FirstOrDefault(t => t.IsVisible)?.FocusContent();
        }
    }

    /// <summary>The open youth's item in the queue, else the first, else the list.</summary>
    private void FocusQueue()
    {
        if (!FocusItem(QueueList))
        {
            QueueList.Focus();
        }
    }

    /// <summary>A list's selected item, or its first, takes the focus.</summary>
    private static bool FocusItem(ListBox list)
    {
        var item = list.SelectedItem ?? (list.Items.Count > 0 ? list.Items[0] : null);
        if (item == null)
        {
            return false;
        }

        list.ScrollIntoView(item);
        return list.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container && container.Focus();
    }

    /// <summary>A list given the focus itself (Alt+Y, Alt+O, F6) passes it to its selected item, where the arrow keys start.</summary>
    private void OnListFocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is ListBox list && ReferenceEquals(e.NewFocus, list))
        {
            Dispatcher.BeginInvoke(() => FocusItem(list), DispatcherPriority.Loaded);
        }
    }

    /// <summary>
    /// F6 and Shift+F6: the next or previous pane. On the Event page the
    /// Youth list, the Rooms card (Find a person first), the details pane and
    /// the check-in address; on a table page Find and its commands, then the
    /// grid. Windows apps move between panes this way; Tab alone took a dozen
    /// stops from the queue to the board.
    /// </summary>
    private void CyclePane(bool back)
    {
        var focused = Keyboard.FocusedElement as DependencyObject;
        (DependencyObject Area, Action Focus)[] panes;
        if (EventPage.IsVisible)
        {
            panes =
            [
                (QueuePane, FocusQueue),
                (RoomsPane, () => PersonFind.Focus()),
                (DetailsPane, FocusDetails),
                (Footer, () => Footer.MoveFocus(new TraversalRequest(FocusNavigationDirection.First))),
            ];
        }
        else if (_tables.Values.FirstOrDefault(t => t.IsVisible) is { } page)
        {
            // A cell being edited is saved as it's left, as ShowPage does.
            page.CommitEdit();
            panes = [(page, () => page.Find.Focus()), (page.Grid, page.FocusContent)];
        }
        else
        {
            return;
        }

        // The innermost pane holding the focus: a table page holds its grid.
        var at = -1;
        for (var i = 0; i < panes.Length; i++)
        {
            if (Within(panes[i].Area, focused))
            {
                at = i;
            }
        }

        var next = at < 0 ? (back ? panes.Length - 1 : 0) : (at + (back ? -1 : 1) + panes.Length) % panes.Length;
        panes[next].Focus();
    }

    /// <summary>The details pane's first control, else its step buttons, else the pane itself to scroll.</summary>
    private void FocusDetails()
    {
        if (!Details.MoveFocus(new TraversalRequest(FocusNavigationDirection.First))
            && !(ActionBar.IsVisible && ActionBar.MoveFocus(new TraversalRequest(FocusNavigationDirection.First))))
        {
            DetailsScroll.Focus();
        }
    }

    /// <summary>Is <paramref name="element"/> <paramref name="area"/> or inside it? A link in text counts by the text holding it.</summary>
    private static bool Within(DependencyObject area, DependencyObject? element)
    {
        while (element is not null and not Visual)
        {
            element = LogicalTreeHelper.GetParent(element);
        }

        return element != null && (ReferenceEquals(area, element) || (area is Visual a && a.IsAncestorOf(element)));
    }

    /// <summary>The board selected on Results, on the Event page.</summary>
    private void OpenBoard()
    {
        if (_tables[TableSpecs.Results.Name].Selected is { } row && _scouts.FirstOrDefault(s => s.Id == row.Id) is { } scout)
        {
            ShowPage("Event");
            Open(scout);
        }
    }

    /// <summary>Change a selection without running the cascade that a user's click runs.</summary>
    private void Quietly(Action action)
    {
        var was = _quiet;
        _quiet = true;
        try
        {
            action();
        }
        finally
        {
            _quiet = was;
        }
    }
}
