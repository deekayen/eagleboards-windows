using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace EagleBoards.App;

/// <summary>
/// An inline message, after WinUI's InfoBar: a tinted strip with an icon, a
/// bold title and the message, sitting in the layout it is about rather
/// than floating over it. Stays until closed or replaced. A screen reader
/// meets it as one status message, severity first, and hears it when it
/// appears or changes (a live region: assertive for an error).
/// </summary>
public sealed class InfoBar : Border
{
    private readonly Glyph _icon = new() { Margin = new Thickness(0, 1, 12, 0), VerticalAlignment = VerticalAlignment.Top };
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

    /// <summary>What was last read out, so a bar shown again unchanged isn't read again.</summary>
    private string? _spoken;

    public InfoBar()
    {
        BorderThickness = new Thickness(1);
        Padding = new Thickness(16, 12, 16, 12);
        SetResourceReference(BorderBrushProperty, "CardStrokeColorDefaultBrush");
        SetResourceReference(CornerRadiusProperty, "ControlCornerRadius");
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

    /// <summary>The whole message as plain text, as shown, for tests.</summary>
    public string Text => new TextRange(_text.ContentStart, _text.ContentEnd).Text;

    /// <summary>What a screen reader says: the severity in words, then the title and message as sentences.</summary>
    public string SpokenText { get; private set; } = "";

    /// <summary>
    /// Show a message, replacing any already shown. <paramref name="message"/>
    /// may use **bold**. A bar shown again with the same words isn't read out
    /// again; <paramref name="announce"/> false shows it without reading it out
    /// at all (a list of problems rebuilt on every change, say).
    /// </summary>
    public void Show(Severity severity, string title, string message, string? actionText = null, Action? action = null, bool announce = true)
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

        var word = severity switch
        {
            Severity.Success => "Success",
            Severity.Warning => "Warning",
            Severity.Error => "Error",
            _ => "Information",
        };
        SpokenText = word + ": " + string.Join(" ", new[] { Rich.Sentence(title), Rich.Plain(message) }.Where(s => s.Length > 0));
        AutomationProperties.SetLiveSetting(this, severity == Severity.Error ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
        Visibility = Visibility.Visible;
        if (announce && SpokenText != _spoken)
        {
            _spoken = SpokenText;
            Dispatcher.BeginInvoke(() => Announcer.LiveRegionChanged(this), DispatcherPriority.Loaded);
        }
        else if (!announce)
        {
            _spoken = SpokenText;
        }
    }

    public void Close()
    {
        Visibility = Visibility.Collapsed;
        _spoken = null;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new InfoBarPeer(this);

    /// <summary>
    /// One status message, as WinUI's InfoBar is to UI Automation: named with
    /// <see cref="SpokenText"/>, with only its buttons inside. A Border has no
    /// peer, so without this the name went nowhere and the icon was read as a
    /// stray character.
    /// </summary>
    private sealed class InfoBarPeer(InfoBar owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.StatusBar;

        protected override string GetClassNameCore() => nameof(InfoBar);

        protected override string GetNameCore() => ((InfoBar)Owner).SpokenText;

        protected override List<AutomationPeer> GetChildrenCore()
        {
            var bar = (InfoBar)Owner;
            return new[] { bar._action, bar._close }.Where(b => b.IsVisible)
                .Select(UIElementAutomationPeer.CreatePeerForElement).OfType<AutomationPeer>().ToList();
        }
    }
}
