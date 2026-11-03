using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace EagleBoards.App;

/// <summary>
/// Loads WPF's Fluent theme (Windows 11 controls; light or dark with the
/// system; the accent color; Mica on Windows 11, a solid background on
/// Windows 10), then the app's own styles, which build on it. The app and the
/// snapshot harness both start here so they look the same.
/// </summary>
internal static class ThemeSetup
{
    public static void Apply(Application app, ThemeMode mode)
    {
        app.ThemeMode = mode;

        // After the theme: Theme.xaml's styles are BasedOn Fluent's.
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/EagleBoards;component/Theme.xaml"),
        });
        StatusPalette.Install(app, mode);
    }
}
