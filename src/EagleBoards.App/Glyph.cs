namespace EagleBoards.App;

/// <summary>
/// A Segoe Fluent Icons glyph in the Glyph style, silent to screen readers:
/// the words beside it, or its button's name, say what it means.
/// </summary>
public sealed class Glyph : SilentText
{
    public Glyph() => SetResourceReference(StyleProperty, "Glyph");
}
