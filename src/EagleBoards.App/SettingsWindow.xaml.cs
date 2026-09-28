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
    }

    public SettingsPage Page { get; }
}
