using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using EagleBoards.Core;
using EagleBoards.Core.Records;

namespace EagleBoards.App;

/// <summary>
/// Room timers and the refresh interval (the CONFIG record in
/// config.properties), and About. The status colour keys the Java version
/// reads stay in the file untouched; this app draws status from the theme.
/// </summary>
public partial class SettingsPage : UserControl
{
    private static readonly (string Key, string Title, string Description, string Unit, int Min)[] Timers =
    [
        ("ConveneRedMins", "Convening board is overdue after",
            "Members read the application, references and workbook before the youth comes in. The Guide to Advancement "
            + "asks for at least 30 minutes; past it, the youth is being kept waiting.", "minutes", 0),
        ("FinalYellowMins", "Final board is running long after",
            "Counted from Start review. Boards \"generally last 30 minutes or somewhat longer\" (Guide to Advancement 8.0.3.0).", "minutes", 0),
        ("FinalRedMins", "Final board is overdue after",
            "\"Rarely should one last longer than 45 minutes\" (Guide to Advancement 8.0.3.0).", "minutes", 0),
        ("ProjectYellowMins", "Project review is running long after",
            "District practice: the Guide sets no length for a project proposal review.", "minutes", 0),
        ("ProjectRedMins", "Project review is overdue after", "", "minutes", 0),
    ];

    /// <summary>The project's funding links (.github/FUNDING.yml).</summary>
    private static readonly KeyValuePair<string, string>[] Support =
    [
        new("GitHub Sponsors", "https://github.com/sponsors/deekayen"),
        new("Ko-fi", "https://ko-fi.com/deekayen"),
        new("Liberapay", "https://liberapay.com/deekayen"),
        new("PayPal", "https://paypal.me/deekayen"),
        new("Venmo", "https://venmo.com/drdnorman"),
        new("Buy Me a Coffee", "https://buymeacoff.ee/deekayen"),
    ];

    /// <summary>Venmo's Pay screen with the note filled in, for the Support card's QR code (SPEC.md D-17).</summary>
    private const string VenmoPay = "https://venmo.com/u/drdnorman?txn=pay&note=Eagle%20Boards";

    private readonly BoardService _svc;
    private readonly Action _saved;
    private readonly Dictionary<string, TextBox> _fields = [];

    public SettingsPage(BoardService svc, Action saved)
    {
        _svc = svc;
        _saved = saved;
        InitializeComponent();
        foreach (var t in Timers)
        {
            TimerCards.Children.Add(Card(t.Key, t.Title, t.Description, t.Unit));
        }

        AppIcon.Source = LargestIconFrame();
        VersionText.Text = "Version " + AppVersion.Text;
        SupportLinks.ItemsSource = Support;
        VenmoQr.Source = QrWindow.Render(VenmoPay);
        Load();
    }

    /// <summary>Show what's saved now. Called whenever the page is opened.</summary>
    public void Load()
    {
        Fill(_svc.GetConfig().Fields);
        SaveNotice.Close();
    }

    /// <summary>Scroll the Support card into view, once the page has been laid out.</summary>
    public void ShowSupport() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => SupportCard.BringIntoView());

    private UIElement Card(string key, string title, string description, string unit)
    {
        var box = new TextBox { Width = 80, HorizontalContentAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(box, $"{title} ({unit})");
        _fields[key] = box;

        var value = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        value.Children.Add(box);
        var unitText = new TextBlock { Text = unit, Width = 64, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        unitText.SetResourceReference(StyleProperty, "Secondary");
        value.Children.Add(unitText);
        DockPanel.SetDock(value, Dock.Right);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        if (description.Length > 0)
        {
            var hint = new TextBlock { Text = description };
            hint.SetResourceReference(StyleProperty, "Hint");
            text.Children.Add(hint);
        }

        var row = new DockPanel();
        row.Children.Add(value);
        row.Children.Add(text);
        var card = new Border { Child = row, Margin = new Thickness(0, 0, 0, 4), Padding = new Thickness(16, 12, 16, 12) };
        card.SetResourceReference(StyleProperty, "Card");
        return card;
    }

    private void Fill(IReadOnlyDictionary<string, string> values)
    {
        foreach (var (key, box) in _fields)
        {
            box.Text = values.TryGetValue(key, out var v) ? v : "";
        }
    }

    private void OnDefaults(object sender, RoutedEventArgs e)
    {
        Fill(ConfigRecord.Defaults);
        SaveNotice.Show(Severity.Informational, "Defaults restored", "Save to keep them.");
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        foreach (var (key, title, _, unit, min) in Timers)
        {
            if (!int.TryParse(_fields[key].Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < min)
            {
                SaveNotice.Show(Severity.Error, "Not saved", $"\"{title}\" needs a whole number of {unit}{(min > 0 ? $", at least {min}" : "")}.");
                _fields[key].Focus();
                return;
            }
        }

        // Start from what's saved so the keys this page doesn't show (the
        // Java version's status colours and refresh interval) are written
        // back unchanged.
        var fields = new Dictionary<string, string>(_svc.GetConfig().Fields, StringComparer.Ordinal);
        foreach (var (key, box) in _fields)
        {
            fields[key] = box.Text.Trim();
        }

        fields["Type"] = "CONFIG";
        fields["ID"] = ConfigRecord.DefaultId;
        fields["Name"] = ConfigRecord.DefaultId;
        _svc.UpdateConfig(fields);
        SaveNotice.Show(Severity.Success, "Saved", "The new times apply now.");
        _saved();
    }

    /// <summary>The icon file's biggest image: WPF would otherwise take the first (smallest) and blur it up.</summary>
    private static System.Windows.Media.ImageSource LargestIconFrame()
    {
        var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
            new Uri("pack://application:,,,/EagleBoards;component/Assets/EagleBoards.ico"),
            System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        return decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
    }

    private void OnSupportLink(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string url })
        {
            MainWindow.OpenUrl(url);
        }
    }
}
