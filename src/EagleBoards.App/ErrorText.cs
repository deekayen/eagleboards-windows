using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace EagleBoards.App;

/// <summary>
/// A file or network failure in plain words, with what to do: the
/// framework's own message ("The process cannot access the file ... because
/// it is being used by another process") names neither.
/// </summary>
internal static class ErrorText
{
    public static string For(Exception ex, string? path = null)
    {
        var name = path is { Length: > 0 } ? System.IO.Path.GetFileName(path) : "The file";
        return ex switch
        {
            System.IO.IOException io when (io.HResult & 0xFFFF) is 32 or 33 =>
                $"{name} is open in another program, probably Excel. Close it there and try again, or save under another name.",
            UnauthorizedAccessException => "Windows didn't allow saving there. Choose a folder in Documents, or another name.",
            System.IO.DirectoryNotFoundException => "That folder doesn't exist anymore. Choose another one.",
            System.IO.PathTooLongException => "That path is too long for Windows. Choose a shorter folder or name.",
            _ => ex.Message,
        };
    }
}
