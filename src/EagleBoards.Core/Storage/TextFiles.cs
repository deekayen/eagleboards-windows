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
    /// leading byte-order mark (Excel's "CSV UTF-8") is dropped, not
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
    /// so a crash or power cut mid-write leaves the old file, not half
    /// of a new one. The adult history accumulates across every event;
    /// a truncated copy of it is not recoverable from anywhere.
    /// </summary>
    public static void WriteAtomically(string path, string contents)
    {
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(contents);
        var temp = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);

                // The rename below is atomic, but without this the new name
                // can reach the disk before the new contents do, and a power
                // cut then leaves an empty file under the real name.
                stream.Flush(flushToDisk: true);
            }

            MoveOver(temp, path);
        }
        catch
        {
            // Any failure, including the temporary file itself: a stale .tmp
            // beside the data would be taken for a half-finished save.
            TryDelete(temp);
            throw;
        }
    }

    private const int MoveAttempts = 10;

    /// <summary>How long a save waits between tries while another program holds the file.</summary>
    private static readonly TimeSpan MoveRetryPause = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Put the new file in the old one's place, trying again for about a
    /// second while another program holds the old one. Antivirus scanning a
    /// file that was just written, OneDrive uploading it, and the Windows
    /// search indexer each hold a file for a moment, and a door sign-in that
    /// met one failed. Excel holds a file until it is closed, which no wait
    /// outlasts: that still fails, and says so.
    /// </summary>
    private static void MoveOver(string temp, string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < MoveAttempts)
            {
                Thread.Sleep(MoveRetryPause);
            }
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
