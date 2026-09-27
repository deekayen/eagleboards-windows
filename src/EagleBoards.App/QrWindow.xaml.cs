using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using QRCoder;

namespace EagleBoards.App;

/// <summary>One check-in address, rendered as a QR code image for <see cref="QrWindow"/>.</summary>
internal sealed record QrCode(string Url, BitmapImage Image);

/// <summary>
/// A QR code per check-in address, for a tablet's camera instead of typing
/// the address in by hand. Mirrors the Mac app's sign-in QR window (SPEC.md,
/// parity gap "sign-in QR code window").
/// </summary>
public partial class QrWindow : Window
{
    public QrWindow(IEnumerable<string> urls)
    {
        InitializeComponent();
        CodeList.ItemsSource = urls.Select(url => new QrCode(url, Render(url))).ToList();
    }

    /// <summary>The address as a QR code bitmap, high enough contrast for a phone camera in a hallway.</summary>
    internal static BitmapImage Render(string url)
    {
        var generator = new QRCodeGenerator();
        var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(data).GetGraphic(10);

        var image = new BitmapImage();
        using (var stream = new MemoryStream(png))
        {
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
        }

        image.Freeze();
        return image;
    }
}
