using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace EagleBoards.App;

/// <summary>
/// Loads WPF's Fluent theme (Windows 11 controls; light or dark with the
/// system; the accent colour; Mica on Windows 11, a solid background on
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
    }
}

public enum Severity
{
    Informational,
    Success,
    Warning,
    Error,
}

/// <summary>
/// An inline message, after WinUI's InfoBar: a tinted strip with an icon, a
/// bold title and the message, sitting in the layout it is about rather
/// than floating over it. Stays until closed or replaced.
/// </summary>
public sealed class InfoBar : Border
{
    private readonly TextBlock _icon = new() { Margin = new Thickness(0, 1, 12, 0), VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _action = new() { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly Button _close = new()
    {
        Content = "",
        Width = 32,
        Height = 32,
        Padding = new Thickness(0),
        Margin = new Thickness(8, -4, -4, -4),
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        VerticalAlignment = VerticalAlignment.Top,
        ToolTip = "Close",
    };

    private Action? _onAction;

    public InfoBar()
    {
        BorderThickness = new Thickness(1);
        Padding = new Thickness(16, 12, 16, 12);
        SetResourceReference(BorderBrushProperty, "CardStrokeColorDefaultBrush");
        SetResourceReference(CornerRadiusProperty, "ControlCornerRadius");
        _icon.SetResourceReference(StyleProperty, "Glyph");
        _close.SetResourceReference(Control.FontFamilyProperty, "SymbolThemeFontFamily");
        _close.FontSize = 12;
        AutomationProperties.SetName(_close, "Close");
        _close.Click += (_, _) => Close();
        _action.Click += (_, _) => _onAction?.Invoke();

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_text, 1);
        Grid.SetColumn(_action, 2);
        Grid.SetColumn(_close, 3);
        grid.Children.Add(_icon);
        grid.Children.Add(_text);
        grid.Children.Add(_action);
        grid.Children.Add(_close);
        Child = grid;
        Visibility = Visibility.Collapsed;
    }

    public bool IsClosable
    {
        get => _close.Visibility == Visibility.Visible;
        set => _close.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    public Severity Severity { get; private set; }

    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>The whole message as plain text, for tests and screen readers.</summary>
    public string Text => new TextRange(_text.ContentStart, _text.ContentEnd).Text;

    /// <summary>Show a message, replacing any already shown. <paramref name="message"/> may use **bold**.</summary>
    public void Show(Severity severity, string title, string message, string? actionText = null, Action? action = null)
    {
        Severity = severity;
        var (background, foreground, glyph) = severity switch
        {
            Severity.Success => ("SystemFillColorSuccessBackgroundBrush", "SystemFillColorSuccessBrush", ""),
            Severity.Warning => ("SystemFillColorCautionBackgroundBrush", "SystemFillColorCautionBrush", ""),
            Severity.Error => ("SystemFillColorCriticalBackgroundBrush", "SystemFillColorCriticalBrush", ""),
            _ => ("SystemFillColorAttentionBackgroundBrush", "SystemFillColorAttentionBrush", ""),
        };
        SetResourceReference(BackgroundProperty, background);
        _icon.SetResourceReference(TextBlock.ForegroundProperty, foreground);
        _icon.Text = glyph;

        _text.Inlines.Clear();
        if (title.Length > 0)
        {
            _text.Inlines.Add(new Run(title) { FontWeight = FontWeights.SemiBold });
            _text.Inlines.Add(new Run("   "));
        }

        Rich.AppendTo(_text, message);
        _onAction = action;
        _action.Content = actionText;
        _action.Visibility = actionText != null ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(this, $"{severity}: {title}. {Text}");
        Visibility = Visibility.Visible;
    }

    public void Close() => Visibility = Visibility.Collapsed;
}

/// <summary>Plain text with **semibold** spans and line breaks.</summary>
internal static class Rich
{
    public static TextBlock Block(string text)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AppendTo(block, text);
        return block;
    }

    public static void AppendTo(TextBlock block, string text)
    {
        var strong = false;
        foreach (var part in text.Split("**"))
        {
            var lines = part.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0)
                {
                    block.Inlines.Add(new LineBreak());
                }

                if (lines[i].Length > 0)
                {
                    block.Inlines.Add(new Run(lines[i]) { FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal });
                }
            }

            strong = !strong;
        }
    }
}

/// <summary>
/// A modal dialog after WinUI's ContentDialog: the title asks the question,
/// the body explains, and the buttons answer it with verbs ("Seat board",
/// "Cancel"), primary on the left in the accent colour. Fields have their
/// label above them. Replaces MessageBox, whose OK/Cancel don't answer
/// anything.
/// </summary>
internal sealed class AppDialog : Window
{
    private readonly StackPanel _fields = new();
    private readonly InfoBar _error = new() { IsClosable = false, Margin = new Thickness(0, 0, 0, 16) };
    private readonly Button _primary;

    public AppDialog(Window? owner, string title, string primaryText, string? closeText = "Cancel")
    {
        Owner = owner;
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        ShowInTaskbar = owner == null;
        Icon = owner?.Icon;
        MinWidth = 420;
        MaxWidth = 560;

        _primary = new Button { Content = primaryText, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Stretch };
        _primary.SetResourceReference(StyleProperty, "AccentButtonStyle");
        _primary.Click += (_, _) =>
        {
            var problem = Validate?.Invoke();
            if (problem != null)
            {
                _error.Show(Severity.Error, "", problem);
                return;
            }

            DialogResult = true;
        };

        var buttons = new Grid();
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.Children.Add(_primary);
        if (closeText != null)
        {
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var close = new Button { Content = closeText, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Stretch };
            Grid.SetColumn(close, 2);
            buttons.Children.Add(close);
        }
        else
        {
            _primary.IsCancel = true;
        }

        // The command area: a band across the bottom, as in ContentDialog.
        var commandArea = new Border { Padding = new Thickness(24), BorderThickness = new Thickness(0, 1, 0, 0), Child = buttons };
        commandArea.SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        commandArea.SetResourceReference(BorderBrushProperty, "CardStrokeColorDefaultBrush");
        DockPanel.SetDock(commandArea, Dock.Bottom);

        var body = new StackPanel { Margin = new Thickness(24, 20, 24, 8) };
        body.Children.Add(_error);
        body.Children.Add(_fields);

        var root = new DockPanel();
        root.Children.Add(commandArea);
        root.Children.Add(body);
        Content = root;
    }

    /// <summary>Return a message to keep the dialog open, or null to accept.</summary>
    public Func<string?>? Validate { get; set; }

    public void AddMessage(string text) => Add(Rich.Block(text));

    /// <summary>A warning or note inside the dialog, above the buttons it qualifies.</summary>
    public void AddInfo(Severity severity, string title, string message)
    {
        var bar = new InfoBar { IsClosable = false };
        bar.Show(severity, title, message);
        Add(bar);
    }

    public TextBox AddText(string label, string initial = "")
    {
        var box = new TextBox { Text = initial };
        AddLabelled(label, box);
        return box;
    }

    public TextBox AddMultiline(string label, int lines = 4)
    {
        var box = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = (20 * lines) + 12,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        AddLabelled(label, box);
        return box;
    }

    public ComboBox AddChoice(string label, IEnumerable<KeyValuePair<string, string>> valueToText, string? selected)
    {
        var items = valueToText.ToList();
        var combo = new ComboBox
        {
            ItemsSource = items,
            DisplayMemberPath = "Value",
            SelectedValuePath = "Key",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        combo.SelectedValue = selected ?? (items.Count > 0 ? items[0].Key : null);
        AddLabelled(label, combo);
        return combo;
    }

    public new bool ShowDialog()
    {
        Loaded += (_, _) =>
        {
            var first = _fields.Children.OfType<StackPanel>().SelectMany(p => p.Children.OfType<Control>())
                .FirstOrDefault(c => c is TextBox or ComboBox);
            (first ?? (Control)_primary).Focus();
        };
        return base.ShowDialog() == true;
    }

    /// <summary>Ask before something that can't be undone or overrides a rule. True if the primary button was pressed.</summary>
    public static bool Confirm(Window? owner, string title, string message, string primaryText, Severity? severity = null, string? detail = null)
    {
        var dialog = new AppDialog(owner, title, primaryText);
        if (severity is { } s)
        {
            dialog.AddInfo(s, "", message);
        }
        else
        {
            dialog.AddMessage(message);
        }

        if (detail != null)
        {
            dialog.AddMessage(detail);
        }

        return dialog.ShowDialog();
    }

    /// <summary>Tell the user something they must read before carrying on (start-up failures and the like).</summary>
    public static void Alert(Window? owner, string title, string message, Severity severity = Severity.Error)
    {
        var dialog = new AppDialog(owner, title, "OK", closeText: null);
        dialog.AddInfo(severity, "", message);
        dialog.ShowDialog();
    }

    private void AddLabelled(string label, Control control)
    {
        var text = new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 8) };
        AutomationProperties.SetName(control, label);
        var group = new StackPanel();
        group.Children.Add(text);
        group.Children.Add(control);
        Add(group);
    }

    private void Add(FrameworkElement element)
    {
        element.Margin = new Thickness(element.Margin.Left, element.Margin.Top, element.Margin.Right, 16);
        _fields.Children.Add(element);
    }
}

/// <summary>
/// Hint text in an empty text box, like WinUI's PlaceholderText, which WPF's
/// TextBox lacks: <c>app:Placeholder.Text="Find a youth"</c>. Drawn on the
/// adorner layer so it never becomes part of the text, and used as the
/// box's accessible help text too.
/// </summary>
public static class Placeholder
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached("Text", typeof(string), typeof(Placeholder), new PropertyMetadata(null, OnTextChanged));

    public static string? GetText(DependencyObject o) => (string?)o.GetValue(TextProperty);

    public static void SetText(DependencyObject o, string? value) => o.SetValue(TextProperty, value);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box)
        {
            return;
        }

        AutomationProperties.SetHelpText(box, (string?)e.NewValue ?? "");
        box.Loaded += (_, _) => Attach(box);
        box.TextChanged += (_, _) => Attach(box);

        // Adorners don't follow their element's visibility: a box on a hidden
        // page would leave its hint floating over the page shown instead.
        box.IsVisibleChanged += (_, _) => box.Dispatcher.BeginInvoke(() => Attach(box), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static void Attach(TextBox box)
    {
        if (System.Windows.Documents.AdornerLayer.GetAdornerLayer(box) is not { } layer)
        {
            return;
        }

        var existing = layer.GetAdorners(box)?.OfType<HintAdorner>().FirstOrDefault();
        var show = box.Text.Length == 0 && box.IsVisible;
        if (show && existing == null)
        {
            layer.Add(new HintAdorner(box, GetText(box) ?? ""));
        }
        else if (!show && existing != null)
        {
            layer.Remove(existing);
        }
    }

    private sealed class HintAdorner : System.Windows.Documents.Adorner
    {
        private readonly TextBlock _text;

        public HintAdorner(TextBox box, string text)
            : base(box)
        {
            IsHitTestVisible = false;
            _text = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            _text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
            AddVisualChild(_text);
        }

        protected override int VisualChildrenCount => 1;

        protected override Visual GetVisualChild(int index) => _text;

        protected override Size MeasureOverride(Size constraint)
        {
            _text.Measure(AdornedElement.RenderSize);
            return AdornedElement.RenderSize;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var box = (TextBox)AdornedElement;
            var left = box.Padding.Left + box.BorderThickness.Left + 2;
            var top = Math.Max(0, (finalSize.Height - _text.DesiredSize.Height) / 2);
            _text.Arrange(new Rect(left, top, Math.Max(0, finalSize.Width - left - 8), _text.DesiredSize.Height));
            return finalSize;
        }
    }
}
