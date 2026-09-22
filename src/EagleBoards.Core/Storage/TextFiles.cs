using System.Text;

namespace EagleBoards.Core.Storage;

/// <summary>Reading and writing the data files.</summary>
public static class TextFiles
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    static TextFiles()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>
    /// Read a data file as text. UTF-8 is what gets written, but the adult
    /// history is cumulative and older copies were written by a Java 8 build
    /// on Windows in the ANSI code page; decoding those as UTF-8 would turn
    /// every accented name into U+FFFD and the next save would make that
    /// permanent. So: strict UTF-8 first, Windows-1252 if that fails. A
    /// leading byte-order mark (Excel's "CSV UTF-8") is dropped rather than
    /// read as part of the first column name.
    /// </summary>
    public static string Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var span = bytes.AsSpan();
        if (span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            span = span[3..];
        }

        try
        {
            return StrictUtf8.GetString(span);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(1252).GetString(span);
        }
    }

    /// <summary>
    /// Replace the file's contents via a temporary file in the same folder,
    /// so a crash or power cut mid-write leaves the old file rather than half
    /// of a new one. The adult history accumulates across every event night;
    /// a truncated copy of it is not recoverable from anywhere.
    /// </summary>
    public static void WriteAtomically(string path, string contents)
    {
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(contents);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        try
        {
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
