using System.Globalization;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EagleBoards.App;
using EagleBoards.Core;
using EagleBoards.Core.Records;

namespace EagleBoards.UiSnapshots;

/// <summary>
/// Renders the README's demo: one board's trip from sign-in to result,
/// driven through the scheduler's own handlers over a made-up evening. The
/// record clock is simulated, so room timers read like a real night. Every
/// name is invented; nothing reads a real data folder.
/// </summary>
internal static class Demo
{
    private const int Width = 1366;
    private const int Height = 800;
    private const int CaptionHeight = 52;

    // The fourth Tuesday of October 2026.
    private static DateTimeOffset _now = At(18, 30);

    private static readonly string[] AdultLast =
        ["Abernathy", "Blackwood", "Castellano", "Duxbury", "Ellsworth", "Fairbanks", "Grimaldi", "Hollingsworth", "Ivanovic", "Jankowski", "Kaminski", "Lindqvist", "Montgomery", "Nakashima"];

    private static readonly string[] AdultFirst =
        ["Anneliese", "Bartholomew", "Clementine", "Desmond", "Evangeline", "Fitzgerald", "Genevieve", "Horatio", "Isadora", "Jebediah", "Katarina", "Leopold", "Marguerite", "Nathaniel"];

    // Project role / Final role.
    private static readonly string[] AdultRoles =
        ["Chair/Chair", "Member/Chair", "Chair/Member", "Member/Member", "Member/Member", "Chair/Chair", "Member/Member", "Member/Member", "Unavailable/Member", "Member/Chair", "Member/Member", "Member/Member", "Chair/Member", "Member/Member"];

    private static readonly string[] ScoutLast = ["Aldridge", "Bram", "Carrington", "Dunmore", "Everly", "Fenwick", "Gallagher", "Holloway", "Ingram"];

    private static readonly string[] ScoutFirst = ["Alexander", "Beauregard", "Cormac", "Dorian", "Emmett", "Finnegan", "Gideon", "Harriet", "Isabel"];

    /// <summary>The walk-in the demo follows, and the leader from their unit who signed in too.</summary>
    private const int Walkin = 8;

    private const int Leader = 12;

    public static int Run(string outDir, bool dark)
    {
        Directory.CreateDirectory(outDir);
        DataRecord.Clock = () => _now;

        var root = Path.Combine(Path.GetTempPath(), "eb-demo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var history = Path.Combine(root, "AdultHistory.csv");
        File.WriteAllText(history, string.Join(',', AdultRecord.AllColumns) + "\n");
        var config = Path.Combine(root, "config.properties");
        File.WriteAllText(config, "Type=CONFIG\nID=DEFAULT\nName=DEFAULT\n");

        Program.StartApplication(dark);
        var plan = new LaunchPlan
        {
            DataDirectory = Path.Combine(root, "2026-10-27"),
            AdultHistoryPath = history,
            ConfigPath = config,
            Port = 18900 + Random.Shared.Next(500),
            BindAddress = IPAddress.Loopback,
        };

        var session = Task.Run(() => EventSession.StartAsync(plan)).GetAwaiter().GetResult();
        try
        {
            SeedEvening(session.Service);
            session = Program.Restart(session, plan);
            var svc = session.Service;
            _now = At(19, 31);

            var win = new MainWindow(session);
            Place(win);
            var frames = new List<(BitmapSource, int)>();
            var stills = new List<BitmapSource>();
            void Frame(string caption, int ms)
            {
                Settle(win);
                stills.Add(Program.Render(win));
                frames.Add((Compose(stills[^1], caption), ms));
            }

            Frame("A board of review event: youth waiting, boards in session, every room timed", 3500);

            _now = At(19, 33);
            svc.RegisterScout(Scout(Walkin, "Final", unit: (2001 + Leader).ToString(CultureInfo.InvariantCulture),
                leader: $"{AdultFirst[Leader]} {AdultLast[Leader]}"));
            Frame("1   A youth signs in on a check-in tablet and joins the queue", 3000);

            var youth = win.QueueList.Items.Cast<ScoutRow>().First(s => s.Last == ScoutLast[Walkin]);
            win.QueueList.SelectedItem = youth;
            Frame("2   Select them: a free room, a qualified chair and members from other units are proposed", 4000);

            // Add their own leader to the board, to show a rule being flagged.
            var leader = FindAdult(win, Leader);
            leader.Sel = true;
            Program.Invoke(win, "ShowDetails");
            Frame("3   Rules are checked as you build: someone from the youth's own unit is flagged", 4000);

            leader.Sel = false;
            Program.Invoke(win, "ShowDetails");
            _now = At(19, 34);
            Program.Invoke(win, "OnSeat", win, new RoutedEventArgs());
            Frame("4   Seat the board: the members convene and read the paperwork while the youth waits", 3500);

            _now = At(19, 43);
            Program.Invoke(win, "RefreshAll");
            Program.Invoke(win, "OnStart", win, new RoutedEventArgs());
            Frame("5   Start review brings the youth in, and the room timer starts again", 3000);

            _now = At(20, 12);
            Program.Invoke(win, "RefreshAll");
            win.NotesBox.Text = "Well prepared. Strong project leadership.";
            Frame("6   Record the result", 3000);

            Program.Invoke(win, "OnComplete", win, new RoutedEventArgs());
            Frame("    The room is free again, and you can see where the youth's leader is", 4500);

            GifWriter.Write(Path.Combine(outDir, dark ? "scheduler-demo-dark.gif" : "scheduler-demo.gif"), frames);
            SavePng(stills[2], Path.Combine(outDir, dark ? "scheduler-dark.png" : "scheduler.png"));
            if (!dark)
            {
                SavePng(SocialPreview(stills[2]), Path.Combine(outDir, "social-preview.png"));
            }
            win.Hide();
        }
        finally
        {
            Task.Run(async () => await session.DisposeAsync()).GetAwaiter().GetResult();
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        Console.WriteLine("demo: " + outDir);
        return 0;
    }

    private static DateTimeOffset At(int hour, int minute) => new(2026, 10, 27, hour, minute, 0, TimeSpan.FromHours(-4));

    private static string A(int i) => $"ADULT:{AdultLast[i]}:{AdultFirst[i]}:{2001 + i}";

    private static string S(int i) => $"SCOUT:{ScoutLast[i]}:{ScoutFirst[i]}:{1001 + i}";

    private static AdultRow FindAdult(MainWindow win, int i) =>
        win.AdultGrid.Items.Cast<AdultRow>().First(a => a.Last == AdultLast[i]);

    private static Dictionary<string, string> Scout(int i, string boardType, string? unit = null, string leader = "") => new()
    {
        ["Last"] = ScoutLast[i], ["First"] = ScoutFirst[i], ["UnitType"] = "Troop",
        ["Unit"] = unit ?? (1001 + i).ToString(CultureInfo.InvariantCulture),
        ["BoardType"] = boardType, ["Email"] = $"s{i}@example.org", ["Leader"] = leader,
    };

    /// <summary>An hour into the evening: two boards under way, one done, one postponed, a few waiting.</summary>
    private static void SeedEvening(BoardService s)
    {
        foreach (var (room, type) in new[] { ("101", "Final"), ("102", "Final"), ("103", "Final"), ("104", "Final"), ("201", "Project"), ("202", "Project") })
        {
            s.AddRoom(room, type);
        }

        for (var i = 0; i < AdultLast.Length; i++)
        {
            var r = AdultRoles[i].Split('/');
            s.RegisterAdult(new Dictionary<string, string>
            {
                ["Last"] = AdultLast[i], ["First"] = AdultFirst[i], ["UnitType"] = "Troop", ["Unit"] = (2001 + i).ToString(CultureInfo.InvariantCulture),
                ["Email"] = $"a{i}@example.org", ["ProjectReview"] = r[0], ["FinalBoard"] = r[1],
            });
        }

        _now = At(18, 34);
        s.RegisterScout(Scout(0, "Final"));
        s.RegisterScout(Scout(1, "Final"));
        s.RegisterScout(Scout(5, "Project"));
        _now = At(18, 41);
        s.RegisterScout(Scout(2, "Final"));
        s.RegisterScout(Scout(4, "Final"));
        s.RegisterScout(Scout(6, "Project"));

        _now = At(18, 45);
        s.SeatBoard("ROOM:101", S(0), A(0), $"{A(0)},{A(3)},{A(4)}");
        _now = At(18, 52);
        s.StartReview(S(0));
        _now = At(19, 2);
        s.SeatBoard("ROOM:102", S(1), A(1), $"{A(1)},{A(6)},{A(7)}");
        _now = At(19, 12);
        s.StartReview(S(1));
        _now = At(19, 20);
        s.CompleteBoard(S(0), "Approved", "Well prepared.");
        _now = At(19, 22);
        s.SeatBoard("ROOM:201", S(5), A(2), $"{A(2)},{A(10)}");
        s.PostponeBoard(S(4));
        _now = At(19, 24);
        s.SeatBoard("ROOM:101", S(2), A(9), $"{A(9)},{A(0)},{A(3)}");
        _now = At(19, 29);
        s.RegisterScout(Scout(3, "Final"));
        s.RegisterScout(Scout(7, "Project"));
    }

    private static void Place(Window win)
    {
        win.WindowState = WindowState.Normal;
        win.WindowStartupLocation = WindowStartupLocation.Manual;
        win.Left = -32000;
        win.Top = 0;
        win.Width = Width;
        win.Height = Height;
        win.ShowActivated = false;
        win.ShowInTaskbar = false;
        win.Show();
    }

    /// <summary>Pump, then put back what the demo shows in place of this machine's details.</summary>
    private static void Settle(MainWindow win)
    {
        Program.Pump();
        // As a normal start on the venue Wi-Fi shows it: the demo serves on
        // this computer only, which the window would otherwise (rightly) flag.
        win.Notice.Close();
        win.UrlLabel.Text = "Check-in address: ";
        win.UrlLabel.ClearValue(TextBlock.ForegroundProperty);
        win.UrlList.ItemsSource = new[] { "http://192.168.1.23:8080/" };
        win.DataText.Text = @"Data: C:\eagleboards\2026-10-27";
        Program.Pump();
    }

    /// <summary>The window with a caption strip under it.</summary>
    private static BitmapSource Compose(BitmapSource main, string caption)
    {
        var width = main.PixelWidth;
        var height = main.PixelHeight + CaptionHeight;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(main, new Rect(0, 0, main.PixelWidth, main.PixelHeight));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1B, 0x2A, 0x41)), null, new Rect(0, main.PixelHeight, width, CaptionHeight));
            var text = new FormattedText(caption, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                19, Brushes.White, 1.0);
            dc.DrawText(text, new Point(20, main.PixelHeight + ((CaptionHeight - text.Height) / 2)));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    /// <summary>GitHub's social preview size, 1280 x 640: name, what it does, and the window.</summary>
    private static BitmapSource SocialPreview(BitmapSource window)
    {
        const int w = 1280, h = 640;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1B, 0x2A, 0x41)), null, new Rect(0, 0, w, h));
            FormattedText Text(string t, double size, FontWeight weight, Brush brush) => new(t, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal), size, brush, 1.0);
            dc.DrawText(Text("Eagle Board Scheduler", 52, FontWeights.SemiBold, Brushes.White), new Point(64, 56));
            dc.DrawText(Text("Check-in and room scheduling for Eagle Scout board of review events", 24, FontWeights.Normal,
                new SolidColorBrush(Color.FromRgb(0xC9, 0xD3, 0xE0))), new Point(66, 128));

            // The window across the width, cropped at the bottom edge.
            var scale = (w - 128.0) / window.PixelWidth;
            var shot = new Rect(64, 196, window.PixelWidth * scale, window.PixelHeight * scale);
            dc.PushClip(new RectangleGeometry(new Rect(64, 196, w - 128, h - 196), 8, 8));
            dc.DrawImage(window, shot);
            dc.Pop();
        }

        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    private static void SavePng(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
