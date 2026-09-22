using System.Globalization;
using System.Text;

namespace EagleBoards.Core.Storage;

/// <summary>
/// Reader for the <c>java.util.Properties</c> text format, so a
/// config.properties from the Java version loads unchanged: <c>#</c>/<c>!</c>
/// comments, <c>=</c>, <c>:</c> or whitespace separators, backslash line
/// continuations and escapes including <c>\uXXXX</c>.
/// </summary>
public static class JavaProperties
{
    public static Dictionary<string, string> Parse(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var logical in LogicalLines(text))
        {
            var i = 0;
            var key = new StringBuilder();
            while (i < logical.Length)
            {
                var c = logical[i];
                if (c == '\\' && i + 1 < logical.Length)
                {
                    key.Append(logical, i, 2);
                    i += 2;
                    continue;
                }

                if (c is '=' or ':' or ' ' or '\t' or '\f')
                {
                    break;
                }

                key.Append(c);
                i++;
            }

            // Skip whitespace, at most one '=' or ':', then whitespace again.
            while (i < logical.Length && logical[i] is ' ' or '\t' or '\f')
            {
                i++;
            }

            if (i < logical.Length && logical[i] is '=' or ':')
            {
                i++;
            }

            while (i < logical.Length && logical[i] is ' ' or '\t' or '\f')
            {
                i++;
            }

            result[Unescape(key.ToString())] = Unescape(logical.Substring(i));
        }

        return result;
    }

    private static IEnumerable<string> LogicalLines(string text)
    {
        var pending = new StringBuilder();
        var continuing = false;
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } raw)
        {
            var line = raw.TrimStart(' ', '\t', '\f');
            if (!continuing && (line.Length == 0 || line[0] is '#' or '!'))
            {
                continue;
            }

            var trailing = 0;
            for (var j = line.Length - 1; j >= 0 && line[j] == '\\'; j--)
            {
                trailing++;
            }

            if (trailing % 2 == 1)
            {
                pending.Append(line, 0, line.Length - 1);
                continuing = true;
                continue;
            }

            pending.Append(line);
            continuing = false;
            yield return pending.ToString();
            pending.Clear();
        }

        if (pending.Length > 0)
        {
            yield return pending.ToString();
        }
    }

    private static string Unescape(string s)
    {
        if (!s.Contains('\\'))
        {
            return s;
        }

        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c != '\\' || i + 1 >= s.Length)
            {
                sb.Append(c);
                continue;
            }

            c = s[++i];
            switch (c)
            {
                case 't': sb.Append('\t'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 'f': sb.Append('\f'); break;
                case 'u' when i + 4 < s.Length
                    && int.TryParse(s.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code):
                    sb.Append((char)code);
                    i += 4;
                    break;
                default: sb.Append(c); break;
            }
        }

        return sb.ToString();
    }
}
