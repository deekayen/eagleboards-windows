using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Documents;

namespace EagleBoards.App;

/// <summary>
/// The guide. It opens with the document focused (FocusedElement in the
/// XAML), so Page down and the arrow keys scroll it at once rather than
/// after a Tab.
/// </summary>
public partial class HelpWindow : Window
{
    public HelpWindow()
    {
        InitializeComponent();
        Title = $"Help - Eagle Board Scheduler {AppVersion.Text}";
    }
}

/// <summary>
/// A heading in Help's document, which a screen reader can list and jump
/// between (heading levels, not only larger text). A plain Paragraph has no
/// automation peer at all, so AutomationProperties.HeadingLevel set on one
/// reaches nobody; this one has a peer, which reads the level from it
/// (ContentElementAutomationPeer's own GetHeadingLevelCore) and is named by
/// its words.
/// </summary>
public sealed class Heading : Paragraph
{
    protected override AutomationPeer OnCreateAutomationPeer() => new HeadingPeer(this);

    private sealed class HeadingPeer(Heading owner) : TextElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

        protected override string GetClassNameCore() => nameof(Heading);

        protected override string GetNameCore() => new TextRange(owner.ContentStart, owner.ContentEnd).Text;

        protected override bool IsControlElementCore() => true;

        protected override bool IsContentElementCore() => true;
    }
}
