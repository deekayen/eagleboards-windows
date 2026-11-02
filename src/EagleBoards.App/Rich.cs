using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace EagleBoards.App;

/// <summary>Plain text with **semibold** spans and line breaks.</summary>
internal static class Rich
{
    /// <summary>The text as it is read aloud: no ** markers, line breaks as spaces.</summary>
    public static string Plain(string text) => text.Replace("**", "", StringComparison.Ordinal).Replace('\n', ' ').Trim();

    /// <summary>A title read as a sentence of its own, so it doesn't run into the message after it.</summary>
    public static string Sentence(string title)
    {
        var plain = Plain(title);
        return plain.Length == 0 || plain[^1] is '.' or '?' or '!' ? plain : plain + ".";
    }

    public static TextBlock Block(string text)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AppendTo(block, text);
        return block;
    }

    public static void AppendTo(TextBlock block, string text)
    {
        var strong = false;
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
                    block.Inlines.Add(new Run(lines[i]) { FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal });
                }
            }

            strong = !strong;
        }
    }
}
