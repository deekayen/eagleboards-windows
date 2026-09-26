using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EagleBoards.UiSnapshots;

/// <summary>
/// Writes a looping animated GIF from same-sized frames. WPF's GifBitmapEncoder
/// can't set frame delays or looping, so this does the file format itself: one
/// palette for the whole animation (flat UI colours survive, text anti-aliasing
/// snaps to the nearest entry, no dithering), and each frame after the first
/// stores only the rectangle that changed.
/// </summary>
internal static class GifWriter
{
    public static void Write(string path, IReadOnlyList<(BitmapSource Image, int DelayMs)> frames)
    {
        var width = frames[0].Image.PixelWidth;
        var height = frames[0].Image.PixelHeight;
        var pixels = frames.Select(f => ToBgra(f.Image, width, height)).ToList();
        var palette = BuildPalette(frames.Select(f => f.Image).ToList());
        var indexed = pixels.Select(p => Map(p, palette)).ToList();

        using var file = File.Create(path);
        using var w = new BinaryWriter(file);
        w.Write("GIF89a"u8);
        w.Write((ushort)width);
        w.Write((ushort)height);
        w.Write((byte)0xF7); // global colour table, 8 bits, 256 entries
        w.Write((byte)0);
        w.Write((byte)0);
        foreach (var c in palette)
        {
            w.Write(c.R);
            w.Write(c.G);
            w.Write(c.B);
        }

        // NETSCAPE2.0: loop forever.
        w.Write([0x21, 0xFF, 0x0B]);
        w.Write("NETSCAPE2.0"u8);
        w.Write([0x03, 0x01, 0x00, 0x00, 0x00]);

        for (var f = 0; f < indexed.Count; f++)
        {
            var (x0, y0, x1, y1) = f == 0 ? (0, 0, width - 1, height - 1) : Changed(indexed[f - 1], indexed[f], width, height);
            if (x1 < x0)
            {
                // Identical to the previous frame: a 1x1 patch carries the delay.
                (x0, y0, x1, y1) = (0, 0, 0, 0);
            }

            // Graphic control extension: do not dispose, delay in 1/100 s.
            w.Write([0x21, 0xF9, 0x04, 0x04]);
            w.Write((ushort)(frames[f].DelayMs / 10));
            w.Write([0x00, 0x00]);

            var fw = x1 - x0 + 1;
            var fh = y1 - y0 + 1;
            w.Write((byte)0x2C);
            w.Write((ushort)x0);
            w.Write((ushort)y0);
            w.Write((ushort)fw);
            w.Write((ushort)fh);
            w.Write((byte)0);

            var patch = new byte[fw * fh];
            for (var y = 0; y < fh; y++)
            {
                Array.Copy(indexed[f], (y0 + y) * width + x0, patch, y * fw, fw);
            }

            w.Write((byte)8);
            var data = Lzw(patch);
            for (var i = 0; i < data.Length; i += 255)
            {
                var n = Math.Min(255, data.Length - i);
                w.Write((byte)n);
                w.Write(data, i, n);
            }

            w.Write((byte)0);
        }

        w.Write((byte)0x3B);
    }

    private static int[] ToBgra(BitmapSource image, int width, int height)
    {
        var src = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var pixels = new int[width * height];
        src.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    /// <summary>WPF's optimal palette over every frame, stacked, so all frames share it.</summary>
    private static Color[] BuildPalette(IReadOnlyList<BitmapSource> images)
    {
        var width = images[0].PixelWidth;
        var height = images[0].PixelHeight;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            for (var i = 0; i < images.Count; i++)
            {
                dc.DrawImage(images[i], new System.Windows.Rect(0, i * height, width, height));
            }
        }

        var stacked = new RenderTargetBitmap(width, height * images.Count, 96, 96, PixelFormats.Pbgra32);
        stacked.Render(visual);
        var colors = new BitmapPalette(stacked, 256).Colors.ToList();
        while (colors.Count < 256)
        {
            colors.Add(Colors.Black);
        }

        return [.. colors];
    }

    private static byte[] Map(int[] pixels, Color[] palette)
    {
        var cache = new Dictionary<int, byte>();
        var result = new byte[pixels.Length];
        for (var i = 0; i < pixels.Length; i++)
        {
            var p = pixels[i] | unchecked((int)0xFF000000);
            if (!cache.TryGetValue(p, out var index))
            {
                int b = p & 0xFF, g = (p >> 8) & 0xFF, r = (p >> 16) & 0xFF;
                var best = int.MaxValue;
                for (var k = 0; k < palette.Length; k++)
                {
                    int dr = palette[k].R - r, dg = palette[k].G - g, db = palette[k].B - b;
                    var d = (dr * dr * 3) + (dg * dg * 4) + (db * db * 2);
                    if (d < best)
                    {
                        best = d;
                        index = (byte)k;
                    }
                }

                cache[p] = index;
            }

            result[i] = index;
        }

        return result;
    }

    private static (int X0, int Y0, int X1, int Y1) Changed(byte[] a, byte[] b, int width, int height)
    {
        int x0 = width, y0 = height, x1 = -1, y1 = -1;
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                if (a[row + x] != b[row + x])
                {
                    x0 = Math.Min(x0, x);
                    x1 = Math.Max(x1, x);
                    y0 = Math.Min(y0, y);
                    y1 = Math.Max(y1, y);
                }
            }
        }

        return (x0, y0, x1, y1);
    }

    /// <summary>GIF-flavoured LZW with 8-bit pixels, codes packed LSB first.</summary>
    private static byte[] Lzw(byte[] input)
    {
        const int clear = 256, end = 257, maxCode = 4095;
        var output = new MemoryStream();
        int bitBuffer = 0, bitCount = 0, codeSize = 9, next = 258;
        var table = new Dictionary<int, int>();

        void Emit(int code)
        {
            bitBuffer |= code << bitCount;
            bitCount += codeSize;
            while (bitCount >= 8)
            {
                output.WriteByte((byte)bitBuffer);
                bitBuffer >>= 8;
                bitCount -= 8;
            }
        }

        Emit(clear);
        var prefix = (int)input[0];
        for (var i = 1; i < input.Length; i++)
        {
            var c = input[i];
            var key = (prefix << 8) | c;
            if (table.TryGetValue(key, out var code))
            {
                prefix = code;
                continue;
            }

            Emit(prefix);
            if (next <= maxCode)
            {
                table[key] = next++;
                if (next > (1 << codeSize) && codeSize < 12)
                {
                    codeSize++;
                }
            }
            else
            {
                Emit(clear);
                table.Clear();
                next = 258;
                codeSize = 9;
            }

            prefix = c;
        }

        Emit(prefix);

        // The decoder adds its last entry on reading that code, one behind
        // the encoder, and may widen the code size before the end code.
        if (next == 1 << codeSize && codeSize < 12)
        {
            codeSize++;
        }

        Emit(end);
        if (bitCount > 0)
        {
            output.WriteByte((byte)bitBuffer);
        }

        return output.ToArray();
    }
}
