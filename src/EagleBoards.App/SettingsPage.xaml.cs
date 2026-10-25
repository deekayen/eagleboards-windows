using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using EagleBoards.Core;
using EagleBoards.Core.Records;

namespace EagleBoards.App;

/// <summary>
/// Room timers and the refresh interval (the CONFIG record in
/// config.properties), and About. Status colours aren't settings: they come
/// from the shared palette (<see cref="StatusPalette"/>, SPEC.md D-13, D-19).
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

    /// <summary>
    /// The project's funding links (.github/FUNDING.yml): the address kept,
    /// the service shown, and read out as the list item's name rather than
    /// "[Ko-fi, https://ko-fi.com/deekayen]" (A4).
    /// </summary>
    private static readonly Option[] Support =
    [
        new("https://github.com/sponsors/deekayen", "GitHub Sponsors"),
        new("https://ko-fi.com/deekayen", "Ko-fi"),
        new("https://liberapay.com/deekayen", "Liberapay"),
        new("https://paypal.me/deekayen", "PayPal"),
        new("https://venmo.com/drdnorman", "Venmo"),
        new("https://buymeacoff.ee/deekayen", "Buy Me a Coffee"),
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

        AppIcon.ImageSource = LargestIconFrame();
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

    /// <summary>
    /// Scroll the Support card into view, once the page has been laid out,
    /// and focus its first button (C3): otherwise the next Tab went back to
    /// the first timer at the top. Focus first: the scroll asked for last is
    /// the one kept, and the button's own would stop short of the card's foot.
    /// </summary>
    public void ShowSupport() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (SupportLinks.ItemContainerGenerator.ContainerFromIndex(0) is DependencyObject first && FirstButton(first) is { } button)
            {
                button.Focus();
            }

            SupportCard.BringIntoView();
        });

    /// <summary>Focus the first timer, as the window opens: otherwise nothing has the focus (C3).</summary>
    public void FocusFirst() => _fields[Timers[0].Key].Focus();

    private static Button? FirstButton(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if ((child as Button ?? FirstButton(child)) is { } button)
            {
                return button;
            }
        }

        return null;
    }

    private UIElement Card(string key, string title, string description, string unit)
    {
        // The guidance printed under the title is read with the box (A12).
        var box = new TextBox { Width = 80, HorizontalContentAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(box, $"{title} ({unit})");
        if (description.Length > 0)
        {
            AutomationProperties.SetHelpText(box, description);
        }

        _fields[key] = box;

        var value = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        value.Children.Add(box);
        var unitText = new TextBlock { Text = unit, Width = 64, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        unitText.SetResourceReference(StyleProperty, "Secondary");
        value.Children.Add(unitText);
        Grid.SetColumn(value, 1);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        if (description.Length > 0)
        {
            var hint = new TextBlock { Text = description };
            hint.SetResourceReference(StyleProperty, "Hint");
            text.Children.Add(hint);
        }

        // Columns in reading order: the setting's name, then its value (a
        // screen reader's scan read the box before what it is for).
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(text);
        row.Children.Add(value);
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
        foreach (var (key, title, description, unit, min) in Timers)
        {
            if (!int.TryParse(_fields[key].Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < min)
            {
                // Focus the box with the message as its help text, so it's
                // heard there too; its guidance comes back once it changes.
                var message = $"\"{title}\" needs a whole number of {unit}{(min > 0 ? $", at least {min}" : "")}.";
                SaveNotice.Close();
                SaveNotice.Show(Severity.Error, "Not saved", message);
                var box = _fields[key];
                AutomationProperties.SetHelpText(box, message);
                void Changed(object sender, TextChangedEventArgs e)
                {
                    box.TextChanged -= Changed;
                    AutomationProperties.SetHelpText(box, description);
                }

                box.TextChanged += Changed;
                box.Focus();
                box.SelectAll();
                return;
            }
        }

        // Start from what's saved so the key this page doesn't show (the
        // refresh interval, kept for the Java version) is written back
        // unchanged.
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
        if (sender is FrameworkElement { Tag: string url } && !MainWindow.OpenUrl(url))
        {
            SupportNotice.Show(Severity.Warning, "Couldn't open the link", $"Windows has no browser to open {url} with. Type the address into a browser instead.");
        }
    }
}
