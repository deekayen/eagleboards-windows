using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EagleBoards.Core.Records;
using Microsoft.Win32;

namespace EagleBoards.App;

public partial class MainWindow
{
    // ------------------------------------------------------------------
    // Keys
    // ------------------------------------------------------------------

    /// <summary>
    /// Ctrl+Enter runs the youth's next step (SPEC.md P-3), the Board menu's
    /// first item: Seat board, Start review or Complete, whichever the pane
    /// shows, from anywhere on the Event page, the Notes box included (hence a
    /// preview: the box would otherwise take the key for a new line). Ctrl+Tab
    /// and F6 here too, so a grid or a list that uses Tab itself can't keep them.
    /// </summary>
    private void OnPreviewKey(object sender, KeyEventArgs e)
    {
        // An open menu keeps its keys.
        if (InMenu(Keyboard.FocusedElement as DependencyObject))
        {
            return;
        }

        var modifiers = Keyboard.Modifiers;
        if (e.Key == Key.Tab && modifiers.HasFlag(ModifierKeys.Control))
        {
            ShowNextPage(modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
            e.Handled = true;
        }
        else if (e.Key == Key.F6 && (modifiers & ~ModifierKeys.Shift) == ModifierKeys.None)
        {
            CyclePane(back: modifiers == ModifierKeys.Shift);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && modifiers == ModifierKeys.Control && BoardCommands.NextStep.CanExecute(null, this))
        {
            BoardCommands.NextStep.Execute(null, this);
            e.Handled = true;
        }
    }

    /// <summary>
    /// A letter or digit typed with no Alt that no control took: kept from
    /// WPF's access keys, which would run the button it underlines (K1).
    /// Alt plus the letter comes as system text, and still does.
    /// </summary>
    private void OnBareLetter(object sender, TextCompositionEventArgs e)
    {
        if (e.Text.Length == 1 && char.IsLetterOrDigit(e.Text[0]) && !InMenu(e.OriginalSource as DependencyObject))
        {
            e.Handled = true;
        }
    }

    /// <summary>In the menu bar, a submenu or a shortcut menu, whose letters choose their items.</summary>
    private static bool InMenu(DependencyObject? element)
    {
        for (var at = element; at != null; at = at is Visual ? VisualTreeHelper.GetParent(at) ?? LogicalTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at))
        {
            if (at is MenuItem or System.Windows.Controls.Primitives.MenuBase)
            {
                return true;
            }
        }

        return false;
    }
}
