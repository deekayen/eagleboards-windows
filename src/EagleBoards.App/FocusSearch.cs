using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace EagleBoards.App;

/// <summary>
/// The two questions the main window asks before it moves the keyboard focus
/// (<c>MainWindow.Focus.cs</c>): has the focus been lost, and where can it go.
/// Neither reads any state of the window.
/// </summary>
internal static class FocusSearch
{
    /// <summary>Focus has gone nowhere useful: to the window, a scroll area, or an element no longer shown.</summary>
    public static bool HasLostFocus(IInputElement? now, IInputElement? was, Window window) =>
        now == null || ReferenceEquals(now, window) || ReferenceEquals(now, was) || now is ScrollViewer || now is UIElement { IsVisible: false } or UIElement { IsEnabled: false };

    /// <summary>The first control under <paramref name="from"/> that can take the focus and passes <paramref name="test"/>.</summary>
    public static UIElement? FirstFocusable(DependencyObject from, Func<UIElement, bool> test)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(from); i++)
        {
            var child = VisualTreeHelper.GetChild(from, i);
            if (child is UIElement { Focusable: true, IsVisible: true, IsEnabled: true } element && test(element))
            {
                return element;
            }

            if (FirstFocusable(child, test) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
