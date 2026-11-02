using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;

namespace EagleBoards.App;

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
