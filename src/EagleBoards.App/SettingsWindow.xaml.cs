using System.Windows;

namespace EagleBoards.App;

/// <summary>The Settings page in a window of its own, opened from the File and Help menus.</summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsPage page)
    {
        InitializeComponent();
        Page = page;
        Host.Content = page;

        // Something has the focus once it opens (C3); Help > Donate then
        // moves it on to the Support card.
        Loaded += (_, _) => page.FocusFirst();
    }

    public SettingsPage Page { get; }
}
