using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EagleBoards.Core;
using EagleBoards.Core.Records;

namespace EagleBoards.App;

/// <summary>Timers and status colors: the CONFIG record in config.properties.</summary>
public partial class SettingsWindow : Window
{
    private static readonly (string Key, string Label, int Min)[] Timers =
    [
        ("RefreshTimeSecs", "Refresh time (seconds)", 1),
        ("ConveneRedMins", "Convene red (minutes)", 0),
        ("ProjectYellowMins", "Project yellow (minutes)", 0),
        ("ProjectRedMins", "Project red (minutes)", 0),
        ("FinalYellowMins", "Final yellow (minutes)", 0),
        ("FinalRedMins", "Final red (minutes)", 0),
    ];

    private static readonly string[] Statuses = [BoardStatus.Registered, BoardStatus.Verified, BoardStatus.Seated, BoardStatus.InProgress, BoardStatus.Completed, BoardStatus.Postponed];

    private readonly BoardService _svc;
    private readonly Dictionary<string, TextBox> _fields = [];

    public SettingsWindow(BoardService svc)
    {
        _svc = svc;
        InitializeComponent();

        foreach (var (key, label, _) in Timers)
        {
            var row = TimerFields.RowDefinitions.Count;
            TimerFields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var text = new TextBlock { Text = label + ":", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6) };
            var box = new TextBox { Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(2), HorizontalContentAlignment = HorizontalAlignment.Right };
            Grid.SetRow(text, row);
            Grid.SetRow(box, row);
            Grid.SetColumn(box, 1);
            TimerFields.Children.Add(text);
            TimerFields.Children.Add(box);
            _fields[key] = box;
        }

        foreach (var status in Statuses)
        {
            NormalColors.Children.Add(ColorRow(status + "Color", status));
            HighlightColors.Children.Add(ColorRow(status + "HiColor", status));
        }

        Fill(_svc.GetConfig().Fields);
    }

    private UIElement ColorRow(string key, string label)
    {
        var swatch = new Border { Width = 28, Height = 20, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 6, 0) };
        var box = new TextBox { Width = 76, Padding = new Thickness(2), VerticalContentAlignment = VerticalAlignment.Center };
        box.TextChanged += (_, _) => swatch.Background = Brushes2.FromHex(box.Text.Trim(), Brushes.Transparent);
        var pick = new Button { Content = "...", Width = 28, Margin = new Thickness(4, 0, 0, 0), ToolTip = "Choose a color" };
        pick.Click += (_, _) => PickColor(box);

        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(swatch);
        right.Children.Add(box);
        right.Children.Add(pick);
        DockPanel.SetDock(right, Dock.Right);
        row.Children.Add(right);
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        _fields[key] = box;
        return row;
    }

    /// <summary>The standard Windows color picker.</summary>
    private void PickColor(TextBox box)
    {
        using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true, AnyColor = true };
        if (Brushes2.FromHex(box.Text.Trim(), Brushes.White) is SolidColorBrush current)
        {
            dialog.Color = System.Drawing.Color.FromArgb(current.Color.R, current.Color.G, current.Color.B);
        }

        var owner = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (dialog.ShowDialog(new Win32Owner(owner)) == System.Windows.Forms.DialogResult.OK)
        {
            box.Text = $"#{dialog.Color.R:x2}{dialog.Color.G:x2}{dialog.Color.B:x2}";
        }
    }

    private void Fill(IReadOnlyDictionary<string, string> values)
    {
        foreach (var (key, box) in _fields)
        {
            box.Text = values.TryGetValue(key, out var v) ? v : "";
        }
    }

    private void OnDefaults(object sender, RoutedEventArgs e) => Fill(ConfigRecord.Defaults);

    private void OnSave(object sender, RoutedEventArgs e)
    {
        foreach (var (key, label, min) in Timers)
        {
            if (!int.TryParse(_fields[key].Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < min)
            {
                ErrorText.Text = $"{label} must be a whole number{(min > 0 ? $" of at least {min}" : "")}.";
                _fields[key].Focus();
                return;
            }
        }

        foreach (var (key, box) in _fields.Where(f => f.Key.EndsWith("Color", StringComparison.Ordinal)))
        {
            if (!Regex.IsMatch(box.Text.Trim(), "^#[0-9a-fA-F]{6}$"))
            {
                ErrorText.Text = $"{key} must be a color like #ffcc00.";
                box.Focus();
                return;
            }
        }

        var fields = _fields.ToDictionary(f => f.Key, f => f.Value.Text.Trim().ToLowerInvariant());
        fields["Type"] = "CONFIG";
        fields["ID"] = ConfigRecord.DefaultId;
        fields["Name"] = ConfigRecord.DefaultId;
        _svc.UpdateConfig(fields);
        DialogResult = true;
    }

    private sealed class Win32Owner(IntPtr handle) : System.Windows.Forms.IWin32Window
    {
        public IntPtr Handle { get; } = handle;
    }
}
