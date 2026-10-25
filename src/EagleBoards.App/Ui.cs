using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

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
        StatusPalette.Install(app, mode);
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
/// Text a screen reader skips: an icon beside the words that carry its
/// meaning, or a hint drawn over a box that already has that name. WPF
/// otherwise exposes every TextBlock made in code or a data template as
/// text, an icon as a stray private-use character.
/// </summary>
public class SilentText : TextBlock
{
    protected override AutomationPeer OnCreateAutomationPeer() => new SilentPeer(this);

    private sealed class SilentPeer(TextBlock owner) : TextBlockAutomationPeer(owner)
    {
        protected override bool IsControlElementCore() => false;

        protected override bool IsContentElementCore() => false;
    }
}

/// <summary>
/// A Segoe Fluent Icons glyph in the Glyph style, silent to screen readers:
/// the words beside it, or its button's name, say what it means.
/// </summary>
public sealed class Glyph : SilentText
{
    public Glyph() => SetResourceReference(StyleProperty, "Glyph");
}

/// <summary>
/// Speech for screen readers with nothing put on screen, so D-14 still holds:
/// a step that worked, a room running long, a page shown.
/// </summary>
internal static class Announcer
{
    /// <summary>Say <paramref name="text"/>, raised from <paramref name="from"/> (an element with a peer: a window, a list, a text).</summary>
    public static void Say(UIElement from, string text, string activity, bool important = false)
    {
        if (text.Length > 0 && UIElementAutomationPeer.CreatePeerForElement(from) is { } peer)
        {
            peer.RaiseNotificationEvent(
                important ? AutomationNotificationKind.ActionCompleted : AutomationNotificationKind.Other,
                important ? AutomationNotificationProcessing.ImportantMostRecent : AutomationNotificationProcessing.MostRecent,
                text, activity);
        }
    }

    /// <summary>A live region's text changed (AutomationProperties.LiveSetting): have it read. WPF reads nothing without this.</summary>
    public static void LiveRegionChanged(UIElement element) =>
        UIElementAutomationPeer.CreatePeerForElement(element)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
}

/// <summary>
/// A choice in a list: what is stored (<see cref="Key"/>) and what is shown
/// (<see cref="Value"/>), as a KeyValuePair has them, for DisplayMemberPath
/// "Value" and SelectedValuePath "Key". A list item with no name of its own
/// is read aloud by its ToString, which for a KeyValuePair is
/// "[ROOM:102, 102 · Final board]"; this one reads what is shown.
/// </summary>
public sealed record Option(string Key, string Value)
{
    public override string ToString() => Value.Length > 0 ? Value.Replace(" · ", ", ", StringComparison.Ordinal) : "None";
}

/// <summary>A label over its value (the details pane's FactTemplate), read aloud as "Room: 101, in review for 12 minutes".</summary>
public sealed record Fact(string Key, string Value)
{
    public override string ToString() => $"{Key}: {Value.Replace(" · ", ", ", StringComparison.Ordinal)}";
}

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

/// <summary>Plain text with **semibold** spans and line breaks.</summary>
internal static class Rich
{
    /// <summary>The text as it is read aloud: no ** markers, line breaks as spaces.</summary>
    public static string Plain(string text) => text.Replace("**", "", StringComparison.Ordinal).Replace('\n', ' ').Trim();

    /// <summary>A title read as a sentence of its own, so it doesn't run into the message after it.</summary>
    public static string Sentence(string title)
    {
        var plain = Plain(title);
        return plain.Length == 0 || plain[^1] is '.' or '?' or '!' ? plain : plain + ".";
    }

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
    private readonly List<(Control Field, Func<string?> Problem)> _checks = [];

    /// <summary>The field a failed check pointed at, and the help text it had before.</summary>
    private (Control Field, string HelpText)? _flagged;

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

        // Never taller than its screen: the fields scroll, the buttons stay
        // (a long Add adult, or large display scaling on a small laptop).
        MaxHeight = SystemParameters.WorkArea.Height;
        ScreenFit.Keep(this);

        _primary = new Button { Content = primaryText, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Stretch };
        _primary.SetResourceReference(StyleProperty, "AccentButtonStyle");
        _primary.Click += (_, _) =>
        {
            // Closed first, so the same problem after another try is read out again.
            _error.Close();
            Unflag();
            foreach (var (field, check) in _checks)
            {
                if (check() is { } wrong)
                {
                    Flag(field, wrong);
                    return;
                }
            }

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

        var body = new StackPanel { Margin = new Thickness(24, 20, 24, 8) };
        body.Children.Add(_error);
        body.Children.Add(_fields);

        // The body first and the buttons after it, in rows: Tab and a screen
        // reader meet the question and its fields before the answers.
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        Grid.SetRow(commandArea, 1);
        root.Children.Add(scroll);
        root.Children.Add(commandArea);
        Content = root;
        AutomationProperties.SetIsDialog(this, true);
    }

    /// <summary>Return a message to keep the dialog open, or null to accept.</summary>
    public Func<string?>? Validate { get; set; }

    /// <summary>
    /// A check on one field, run before <see cref="Validate"/>: a message to
    /// keep the dialog open, or null. On a message the field takes the focus
    /// and is read out with the message, so the user lands on what to fix.
    /// </summary>
    public void Check(Control field, Func<string?> problem) => _checks.Add((field, problem));

    private void Flag(Control field, string message)
    {
        _error.Show(Severity.Error, "", message);
        _flagged = (field, AutomationProperties.GetHelpText(field));
        AutomationProperties.SetHelpText(field, message);
        field.Focus();
        (field as TextBox)?.SelectAll();
    }

    private void Unflag()
    {
        if (_flagged is var (field, help))
        {
            AutomationProperties.SetHelpText(field, help);
            _flagged = null;
        }
    }

    public void AddMessage(string text) => Add(Rich.Block(text));

    /// <summary>The dialog's messages as plain text, one to a line, as shown, for tests.</summary>
    internal string MessageText =>
        string.Join("\n", _fields.Children.OfType<TextBlock>().Select(b => new TextRange(b.ContentStart, b.ContentEnd).Text));

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

    public ComboBox AddChoice(string label, IEnumerable<Option> valueToText, string? selected)
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

    /// <summary>
    /// A choice of objects drawn by <paramref name="itemTemplate"/>, the first
    /// selected; SelectedValue is each one's <paramref name="valuePath"/>.
    /// Typing to jump and screen readers use the items' ToString.
    /// </summary>
    public ComboBox AddChoice(string label, System.Collections.IEnumerable items, string valuePath, DataTemplate itemTemplate)
    {
        var combo = new ComboBox
        {
            ItemsSource = items,
            ItemTemplate = itemTemplate,
            SelectedValuePath = valuePath,
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AddLabelled(label, combo);
        return combo;
    }

    /// <summary>Fields side by side, each with its label above it: a first and last name, say.</summary>
    public void AddRow(params (string Label, Control Control)[] fields)
    {
        var row = new Grid();
        for (var i = 0; i < fields.Length; i++)
        {
            if (i > 0)
            {
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            }

            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var (label, control) = fields[i];
            var group = new StackPanel();
            group.Children.Add(FieldLabel(label, control));
            group.Children.Add(control);
            Grid.SetColumn(group, i * 2);
            row.Children.Add(group);
        }

        Add(row);
    }

    /// <summary>
    /// A field's label above it. An "_" before a letter makes it the field's
    /// access key (Alt+letter goes to the field), as in "_Room number"; choose
    /// letters unique within the dialog. The field is named without the "_".
    /// </summary>
    internal static Label FieldLabel(string label, Control control)
    {
        AutomationProperties.SetName(control, AccessKeyText.Strip(label));
        return new Label { Content = label, Target = control, Padding = new Thickness(0), Margin = new Thickness(0, 0, 0, 8) };
    }

    /// <summary>Anything else the dialog needs, in order with its fields.</summary>
    public void AddContent(FrameworkElement element) => Add(element);

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
        var group = new StackPanel();
        group.Children.Add(FieldLabel(label, control));
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
/// Keeps a window that sizes to its content on its own monitor: never taller
/// than that monitor's work area, and moved up when it grows past the bottom
/// (an error shown, a list of matches), so its buttons stay in view.
/// SystemParameters.WorkArea is the primary monitor's alone, and a smaller
/// or more scaled second screen ran a dialog off its edge.
/// </summary>
internal static class ScreenFit
{
    public static void Keep(Window window)
    {
        window.SourceInitialized += (_, _) => Fit(window);
        window.DpiChanged += (_, _) => Fit(window);
        window.LocationChanged += (_, _) => Fit(window);
        window.SizeChanged += (_, _) => Fit(window);
    }

    private static void Fit(Window window)
    {
        if (WorkArea(window) is not { } work)
        {
            window.MaxHeight = SystemParameters.WorkArea.Height;
            return;
        }

        if (window.MaxHeight != work.Height)
        {
            window.MaxHeight = work.Height;
        }

        if (window.ActualHeight > 0 && window.Top + window.ActualHeight > work.Bottom)
        {
            window.Top = Math.Max(work.Top, work.Bottom - window.ActualHeight);
        }
    }

    /// <summary>The work area of the monitor the window is on, in its DIPs; null before it has a handle.</summary>
    private static Rect? WorkArea(Window window)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var info = new MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(handle, 2 /* MONITOR_DEFAULTTONEAREST */), ref info))
        {
            return null;
        }

        var dpi = VisualTreeHelper.GetDpi(window);
        var r = info.Work;
        return new Rect(r.Left / dpi.DpiScaleX, r.Top / dpi.DpiScaleY, (r.Right - r.Left) / dpi.DpiScaleX, (r.Bottom - r.Top) / dpi.DpiScaleY);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Bounds
    {
        public int Left, Top, Right, Bottom;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Bounds Monitor;
        public Bounds Work;
        public uint Flags;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}

/// <summary>
/// A file or network failure in plain words, with what to do: the
/// framework's own message ("The process cannot access the file ... because
/// it is being used by another process") names neither.
/// </summary>
internal static class ErrorText
{
    public static string For(Exception ex, string? path = null)
    {
        var name = path is { Length: > 0 } ? System.IO.Path.GetFileName(path) : "The file";
        return ex switch
        {
            System.IO.IOException io when (io.HResult & 0xFFFF) is 32 or 33 =>
                $"{name} is open in another program, probably Excel. Close it there and try again, or save under another name.",
            UnauthorizedAccessException => "Windows didn't allow saving there. Choose a folder in Documents, or another name.",
            System.IO.DirectoryNotFoundException => "That folder doesn't exist anymore. Choose another one.",
            System.IO.PathTooLongException => "That path is too long for Windows. Choose a shorter folder or name.",
            _ => ex.Message,
        };
    }
}

/// <summary>Labels with an access key: "_Seat board" shows S underlined and answers Alt+S.</summary>
internal static class AccessKeyText
{
    /// <summary>The label as read aloud: no access-key marker ("__" is a literal underscore).</summary>
    public static string Strip(string text) =>
        text.Replace("__", "\u0001", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal).Replace('\u0001', '_');

    /// <summary>
    /// Button content that shows the access key. Fluent's accent button
    /// ignores an "_" in plain string content; an AccessText it honours.
    /// </summary>
    public static AccessText Content(string text) => new() { Text = text };
}

/// <summary>
/// Hint text in an empty text box, like WinUI's PlaceholderText, which WPF's
/// TextBox lacks: <c>app:Placeholder.Text="Find a youth"</c>. Drawn on the
/// adorner layer so it never becomes part of the text, and silent there: the
/// box is named with it when it has no name of its own. A tooltip stays the
/// box's help text, so the keys it explains are read out too.
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

        // On Loaded: XAML and code set the name and tooltip after this.
        box.Loaded += (_, _) =>
        {
            if (string.IsNullOrEmpty(AutomationProperties.GetName(box)))
            {
                AutomationProperties.SetName(box, GetText(box) ?? "");
            }
        };
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

            // Silent: the box is named with it already. Fluent's placeholder
            // colour, as WinUI's TextBox uses: the tertiary text brush was
            // 3.3:1 in light, under the 4.5:1 text needs.
            _text = new SilentText { Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            _text.SetResourceReference(TextBlock.ForegroundProperty, "TextControlPlaceholderForeground");
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
