using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace EagleBoards.App;

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
