using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace EagleBoards.App;

/// <summary>
/// The status palette all three versions share (SPEC.md D-13 in
/// eagleboards-shared): the status pills' and room timers' colours, light and
/// dark, installed as brushes under fixed keys (<c>StatusSeatedBackgroundBrush</c>
/// and so on). The values are the spec's palette table, where
/// <c>scripts/check-palette.js</c> measures them for contrast and colour
/// blindness; change them there first. Its <c>check-drift.sh</c> finds any
/// that differ here. This is the one place the app uses hex colours: Fluent's
/// own caution and critical colours turn the same olive for deuteranopia. In a
/// contrast theme the palette gives way to the system's colours, with a border.
/// </summary>
internal static class StatusPalette
{
    private static readonly (string Key, string Light, string Dark)[] Colors =
    [
        ("StatusNeutralBackground", "#dddddd", "#565457"),
        ("StatusNeutralForeground", "#5b5a5b", "#d4d3d3"),
        ("StatusSeatedBackground", "#f3dfc4", "#685b3e"),
        ("StatusSeatedForeground", "#875107", "#ffdd78"),
        ("StatusReviewBackground", "#c9e3ea", "#425c62"),
        ("StatusReviewForeground", "#146377", "#9ae5ee"),
        ("StatusCompletedBackground", "#ddd7ef", "#504a65"),
        ("StatusCompletedForeground", "#5e4aa0", "#cbc2f7"),
        ("TimerLongBackground", "#f8d9ce", "#67493e"),
        ("TimerLongForeground", "#994122", "#fdbe9f"),
        ("TimerOverdueBackground", "#c23d65", "#ff6188"),
        ("TimerOverdueForeground", "#ffffff", "#221f22"),
    ];

    private static ThemeMode _mode = ThemeMode.System;
    private static bool _watching;

    /// <summary>
    /// Puts the brushes for <paramref name="mode"/> in the application's
    /// resources, and with <see cref="ThemeMode.System"/> keeps them in step
    /// when Windows switches between light, dark and a contrast theme.
    /// </summary>
    public static void Install(Application app, ThemeMode mode)
    {
        _mode = mode;
        Apply(app);
        if (mode == ThemeMode.System && !_watching)
        {
            _watching = true;
            SystemEvents.UserPreferenceChanged += (_, _) => app.Dispatcher.BeginInvoke(() => Apply(app));
        }
    }

    private static void Apply(Application app)
    {
        if (SystemParameters.HighContrast)
        {
            foreach (var (key, _, _) in Colors)
            {
                app.Resources[key + "Brush"] = key switch
                {
                    "TimerOverdueBackground" => SystemColors.HighlightBrush,
                    "TimerOverdueForeground" => SystemColors.HighlightTextBrush,
                    _ when key.EndsWith("Background", StringComparison.Ordinal) => SystemColors.WindowBrush,
                    _ => SystemColors.WindowTextBrush,
                };
            }

            app.Resources["StatusBorderBrush"] = SystemColors.WindowTextBrush;
            return;
        }

        var dark = IsDark();
        foreach (var (key, light, darkValue) in Colors)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? darkValue : light));
            brush.Freeze();
            app.Resources[key + "Brush"] = brush;
        }

        app.Resources["StatusBorderBrush"] = Brushes.Transparent;
    }

    /// <summary>What Fluent shows: the mode asked for, or the system's app setting.</summary>
    private static bool IsDark()
    {
        if (_mode == ThemeMode.Dark)
        {
            return true;
        }

        if (_mode == ThemeMode.Light)
        {
            return false;
        }

        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }
}
