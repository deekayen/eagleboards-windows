using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace EagleBoards.App;

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
