using System.Reflection;

namespace EagleBoards.App;

/// <summary>The release version stamped in at build time (e.g. 2026.09.22), for window titles.</summary>
internal static class AppVersion
{
    public static string Text { get; } = Read();

    private static string Read()
    {
        var info = typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";

        // Drop the "+<commit>" the SDK appends.
        var plus = info.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            info = info[..plus];
        }

        return info.Length == 0 || info.StartsWith("0.0.0", StringComparison.Ordinal) ? "dev" : info;
    }
}
