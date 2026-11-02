using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace EagleBoards.App;

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
