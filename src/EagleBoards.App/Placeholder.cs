using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace EagleBoards.App;

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
