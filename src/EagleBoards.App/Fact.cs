using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace EagleBoards.App;

/// <summary>A label over its value (the details pane's FactTemplate), read aloud as "Room: 101, in review for 12 minutes".</summary>
public sealed record Fact(string Key, string Value)
{
    public override string ToString() => $"{Key}: {Value.Replace(" · ", ", ", StringComparison.Ordinal)}";
}
