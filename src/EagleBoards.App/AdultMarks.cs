using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace EagleBoards.App;

/// <summary>
/// The marks after an adult's name (SPEC.md D-20): the Wood Badge pentagon for
/// an adult counting today toward a Wood Badge ticket item, and a warning
/// triangle for one in the selected youth's own unit. Each is an icon with its
/// meaning in a tooltip and said to a screen reader. Put it straight after the
/// name, in the name's TextBlock (an InlineUIContainer), so it follows the last
/// word when the name wraps. Takes no room when there's nothing to mark.
/// </summary>
public sealed class AdultMarks : StackPanel
{
    public static readonly DependencyProperty WoodBadgeProperty = DependencyProperty.Register(
        nameof(WoodBadge), typeof(bool), typeof(AdultMarks), new PropertyMetadata(false, (d, _) => ((AdultMarks)d).Update()));

    public static readonly DependencyProperty SameUnitAsProperty = DependencyProperty.Register(
        nameof(SameUnitAs), typeof(string), typeof(AdultMarks), new PropertyMetadata(null, (d, _) => ((AdultMarks)d).Update()));

    private readonly Image _woodBadge = new()
    {
        Source = WoodBadgeMark.Image,
        Width = 16,
        Height = 16,
        Margin = new Thickness(6, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
        ToolTip = "Counting today toward a Wood Badge ticket item",
    };

    private readonly TextBlock _sameUnit = new()
    {
        Text = "",
        FontSize = 14,
        Margin = new Thickness(6, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    public AdultMarks()
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(_woodBadge, "Wood Badge");
        AutomationProperties.SetName(_sameUnit, "Same unit");
        _sameUnit.SetResourceReference(TextBlock.FontFamilyProperty, "SymbolThemeFontFamily");
        _sameUnit.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCautionBrush");
        Children.Add(_woodBadge);
        Children.Add(_sameUnit);
        Update();
    }

    /// <summary>Counting today toward a Wood Badge ticket item.</summary>
    public bool WoodBadge
    {
        get => (bool)GetValue(WoodBadgeProperty);
        set => SetValue(WoodBadgeProperty, value);
    }

    /// <summary>The youth's name when the adult is in the youth's own unit; null otherwise.</summary>
    public string? SameUnitAs
    {
        get => (string?)GetValue(SameUnitAsProperty);
        set => SetValue(SameUnitAsProperty, value);
    }

    private void Update()
    {
        _woodBadge.Visibility = WoodBadge ? Visibility.Visible : Visibility.Collapsed;
        _sameUnit.Visibility = SameUnitAs is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        _sameUnit.ToolTip = SameUnitAs is { Length: > 0 } youth ? "Same unit as " + youth : null;
    }
}

/// <summary>
/// The Wood Badge pentagon: eagleboards-shared's artwork/wood-badge.svg (the
/// Mac's drawing), banded clockwise from the top corner in navy, red, black,
/// green and yellow around a white face with a black center. Fixed colours in
/// both themes (SPEC.md D-16, D-20), like the status palette: the white face
/// keeps the navy and black visible on a dark background.
/// </summary>
internal static class WoodBadgeMark
{
    private static readonly (string Fill, string Path)[] Bands =
    [
        ("#ffffff", "M50,20.3L81.09,42.9L69.22,79.45L30.78,79.45L18.91,42.9Z"),
        ("#21306b", "M52.43,4.76L95.13,35.79L78.67,41.13L52.43,22.07Z"),
        ("#bf2136", "M96.63,40.4L80.32,90.6L70.14,76.6L80.17,45.75Z"),
        ("#000000", "M76.39,93.45L23.61,93.45L33.78,79.45L66.22,79.45Z"),
        ("#1f6633", "M19.68,90.6L3.37,40.4L19.83,45.75L29.86,76.6Z"),
        ("#f2b81a", "M4.87,35.79L47.57,4.76L47.57,22.07L21.33,41.13Z"),
    ];

    public static readonly DrawingImage Image = Create();

    private static DrawingImage Create()
    {
        var group = new DrawingGroup();

        // The SVG's 100 x 100 view box, so the pentagon sits in it as it does there.
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 100, 100))));
        foreach (var (fill, path) in Bands)
        {
            group.Children.Add(new GeometryDrawing(Fill(fill), null, Geometry.Parse(path)));
        }

        group.Children.Add(new GeometryDrawing(Fill("#000000"), null, new EllipseGeometry(new Point(50, 53), 12, 12)));
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    private static SolidColorBrush Fill(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
}
