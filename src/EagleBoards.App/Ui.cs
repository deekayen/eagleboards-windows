using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace EagleBoards.App;

public enum ToastKind
{
    Info,
    Ok,
    Warn,
    Error,
}

/// <summary>A notice in the top-right corner. Click to dismiss; most expire on their own.</summary>
public sealed class Toast
{
    public Toast(string title, string body, ToastKind kind)
    {
        Title = title;
        Body = Rich(body);
        Background = kind switch
        {
            ToastKind.Error => Brushes2.FromHex("#F3B50C", Brushes.Orange),
            ToastKind.Ok => Brushes2.FromHex("#EEFFEE", Brushes.White),
            ToastKind.Warn => Brushes2.FromHex("#FFFF99", Brushes.White),
            _ => Brushes2.FromHex("#EEF4FF", Brushes.White),
        };
    }

    public string Title { get; }

    public TextBlock Body { get; }

    public Brush Background { get; }

    /// <summary>
    /// Plain text with **bold** spans and line breaks -- enough to make a
    /// name stand out in a notice without an HTML renderer.
    /// </summary>
    public static TextBlock Rich(string text)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var bold = false;
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
                    var run = new Run(lines[i]);
                    if (bold)
                    {
                        run.FontWeight = FontWeights.Bold;
                    }

                    block.Inlines.Add(run);
                }
            }

            bold = !bold;
        }

        return block;
    }
}

/// <summary>A small modal form: labelled fields, then OK and Cancel.</summary>
internal sealed class FormDialog : Window
{
    private readonly Grid _fields = new() { Margin = new Thickness(0, 0, 0, 12) };
    private readonly TextBlock _error = new() { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };

    public FormDialog(Window owner, string title, string okText = "OK")
    {
        Owner = owner;
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontSize = 13;
        MinWidth = 360;
        MaxWidth = 560;

        _fields.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 220 });

        var ok = new Button { Content = okText, IsDefault = true, MinWidth = 90, Padding = new Thickness(10, 3, 10, 3) };
        ok.Click += (_, _) =>
        {
            var problem = Validate?.Invoke();
            if (problem != null)
            {
                _error.Text = problem;
                _error.Visibility = Visibility.Visible;
                return;
            }

            DialogResult = true;
        };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(8, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        _error.Visibility = Visibility.Collapsed;
        var root = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
        root.Children.Add(_fields);
        root.Children.Add(_error);
        root.Children.Add(buttons);
        Content = root;
    }

    /// <summary>Return a message to keep the dialog open, or null to accept.</summary>
    public Func<string?>? Validate { get; set; }

    public void AddNote(string text)
    {
        var note = Toast.Rich(text);
        note.Margin = new Thickness(0, 0, 0, 10);
        note.MaxWidth = 500;
        AddRow(null, note);
    }

    public TextBox AddText(string label, string initial = "", bool readOnly = false)
    {
        var box = new TextBox { Text = initial, IsReadOnly = readOnly, Padding = new Thickness(2), VerticalContentAlignment = VerticalAlignment.Center };
        AddRow(label, box);
        return box;
    }

    public TextBox AddMultiline(string label, int lines = 4)
    {
        var box = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 18 * lines + 8,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(2),
        };
        AddRow(label, box);
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
        };
        combo.SelectedValue = selected ?? (items.Count > 0 ? items[0].Key : null);
        AddRow(label, combo);
        return combo;
    }

    public new bool ShowDialog()
    {
        Loaded += (_, _) =>
        {
            var first = _fields.Children.OfType<Control>().FirstOrDefault(c => c is TextBox { IsReadOnly: false } or ComboBox);
            first?.Focus();
        };
        return base.ShowDialog() == true;
    }

    private void AddRow(string? label, UIElement control)
    {
        var row = _fields.RowDefinitions.Count;
        _fields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        if (label != null)
        {
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 8) };
            Grid.SetRow(text, row);
            _fields.Children.Add(text);
        }

        if (control is FrameworkElement fe)
        {
            fe.Margin = new Thickness(fe.Margin.Left, fe.Margin.Top, fe.Margin.Right, Math.Max(fe.Margin.Bottom, 8));
        }

        Grid.SetRow(control, row);
        Grid.SetColumn(control, label == null ? 0 : 1);
        if (label == null)
        {
            Grid.SetColumnSpan(control, 2);
        }

        _fields.Children.Add(control);
    }
}

internal static class Ask
{
    public static bool Confirm(Window owner, string title, string message, MessageBoxImage icon = MessageBoxImage.Question) =>
        MessageBox.Show(owner, message, title, MessageBoxButton.OKCancel, icon, MessageBoxResult.Cancel) == MessageBoxResult.OK;
}
