using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using EagleBoards.App;
using EagleBoards.Core;
using EagleBoards.Core.Records;

namespace EagleBoards.UiSnapshots;

/// <summary>
/// Renders the Windows walkthrough on eagleboards.page with the site's demo
/// cast of famous Eagle Scouts, the same scenes the Mac and Java pages show
/// (eagleboards-shared WEBSITE.md, "Re-shooting"):
///
///   event.png           dark, mid-event, Arthur Eldred's overrunning board open
///   seat-board.gif      dark, Bill Amend seated with Guion Bluford and Steve Fossett in 200B
///   complete-board.gif  light, Arthur Eldred's board completed and room 101 freed
///   checkin.png, youth.png, adult.png
///                       the shared check-in pages as a tablet shows them, in light,
///                       photographed with Edge (or Chrome) headless
///
/// The record clock is simulated, so every timer reads as the scene needs. The
/// names are public figures; nothing else about them is real, and nothing reads
/// a real data folder.
/// </summary>
internal static class Site
{
    private static DateTimeOffset _now = At(18, 50);

    // Last, first, unit, board type.
    private static readonly (string Last, string First, int Unit, string Type)[] Youth =
    [
        ("Eldred", "Arthur", 1001, BoardTypes.Final),
        ("Galifianakis", "Zach", 1002, BoardTypes.Final),
        ("Agre", "Peter", 1003, BoardTypes.Final),
        ("Corddry", "Rob", 1004, BoardTypes.Project),
        ("Amend", "Bill", 1005, BoardTypes.Project),
        ("Belle", "Albert", 1007, BoardTypes.Project),
        ("Cech", "Thomas", 1006, BoardTypes.Final),
    ];

    // Last, first, project review role, final board role, Wood Badge.
    private static readonly (string Last, string First, string Project, string Final, bool WoodBadge)[] Adults =
    [
        ("Armstrong", "Neil", "Member", "Chair", false),
        ("Lovell", "Jim", "Member", "Member", false),
        ("Duke", "Charles", "Member", "Member", false),
        ("Ford", "Gerald", "Member", "Chair", false),
        ("Gates", "Robert", "Member", "Member", false),
        ("Bloomberg", "Michael", "Member", "Member", false),
        ("Perot", "Ross", "Member", "Chair", false),
        ("Walton", "Sam", "Member", "Member", false),
        ("Bradley", "Bill", "Member", "Member", false),
        ("Spielberg", "Steven", "Chair", "Member", false),
        ("Rowe", "Mike", "Member", "Member", false),
        ("Bluford", "Guion", "Chair", "Member", true),
        ("Fossett", "Steve", "Member", "Member", true),
    ];

    public static int Run(string outDir)
    {
        Directory.CreateDirectory(outDir);
        DataRecord.Clock = () => _now;

        var root = Path.Combine(Path.GetTempPath(), "eb-site-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var history = Path.Combine(root, "AdultHistory.csv");
        File.WriteAllText(history, string.Join(',', AdultRecord.AllColumns) + "\n");
        var config = Path.Combine(root, "config.properties");
        File.WriteAllText(config, "Type=CONFIG\nID=DEFAULT\nName=DEFAULT\n");

        var app = Program.StartApplication(dark: true);
        var plan = new LaunchPlan
        {
            DataDirectory = Path.Combine(root, "2026-10-27"),
            AdultHistoryPath = history,
            ConfigPath = config,
            Port = 19400 + Random.Shared.Next(500),
            BindAddress = IPAddress.Loopback,
        };

        var session = Task.Run(() => EventSession.StartAsync(plan)).GetAwaiter().GetResult();
        try
        {
            SeedEvent(session.Service);
            session = Program.Restart(session, plan);
            _now = At(20, 0);

            // Dark: the event, then Bill Amend's board seated.
            var win = Open(session);
            Select(win, "Eldred");
            SavePng(Program.Render(win), Path.Combine(outDir, "event.png"));

            win.QueueList.SelectedItem = null;
            win.RoomList.SelectedItem = null;
            Program.Invoke(win, "ShowDetails");
            var seat = new List<(BitmapSource, int)> { (Shot(win), 2500) };
            Select(win, "Amend");
            seat.Add((Shot(win), 4000));
            Program.Invoke(win, "OnSeat", win, new RoutedEventArgs());
            seat.Add((Shot(win), 4000));
            GifWriter.Write(Path.Combine(outDir, "seat-board.gif"), seat);
            win.Hide();

            // Light: Arthur Eldred's board comes out and is recorded.
            ThemeSetup.Apply(app, ThemeMode.Light);
            _now = At(20, 2);
            win = Open(session);
            Select(win, "Eldred");
            var complete = new List<(BitmapSource, int)> { (Shot(win), 3000) };
            win.NotesBox.Text = "Well prepared. Led a strong service project for his community.";
            complete.Add((Shot(win), 3500));
            Program.Invoke(win, "OnComplete", win, new RoutedEventArgs());
            complete.Add((Shot(win), 4000));
            GifWriter.Write(Path.Combine(outDir, "complete-board.gif"), complete);
            win.Hide();

            // The check-in pages, as a tablet at the door shows them.
            var url = session.LocalUrl;
            Browser(url, Path.Combine(outDir, "checkin.png"));
            Browser(url + "youth_register", Path.Combine(outDir, "youth.png"));
            Browser(url + "adult_register", Path.Combine(outDir, "adult.png"));
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

        Console.WriteLine("site: " + outDir);
        return 0;
    }

    /// <summary>A time in the scene: 27 October 2026, or moved so 20:00 is now (<see cref="WriteEvent"/>).</summary>
    private static DateTimeOffset At(int hour, int minute) => new DateTimeOffset(2026, 10, 27, hour, minute, 0, TimeSpan.FromHours(-4)) + _shift;

    private static TimeSpan _shift = TimeSpan.Zero;

    /// <summary>
    /// Write the scene's event to <paramref name="dir"/> for another version to
    /// open (the Java jar, the Mac app): an empty AdultHistory.csv, a
    /// config.properties, and today's event folder, with every time moved so
    /// the scene's 20:00 is this minute and each timer reads as the pictures
    /// need while it's shot. Nothing is rendered.
    /// </summary>
    public static int WriteEvent(string dir)
    {
        var now = DateTimeOffset.Now;
        now = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Offset);
        _shift = now - At(20, 0);
        _now = At(18, 50);
        DataRecord.Clock = () => _now;

        Directory.CreateDirectory(dir);
        var history = Path.Combine(dir, "AdultHistory.csv");
        File.WriteAllText(history, string.Join(',', AdultRecord.AllColumns) + "\n");
        var config = Path.Combine(dir, "config.properties");
        File.WriteAllText(config, "Type=CONFIG\nID=DEFAULT\nName=DEFAULT\n");
        var eventDir = Path.Combine(dir, now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        SeedEvent(BoardService.Open(new EventOptions { DataDirectory = eventDir, AdultHistoryPath = history, ConfigPath = config }));

        Console.WriteLine("site event: " + eventDir);
        return 0;
    }

    private static string YouthId(string last) => Youth.Where(y => y.Last == last).Select(y => $"SCOUT:{y.Last}:{y.First}:{y.Unit}").Single();

    private static string AdultId(string last) =>
        Adults.Select((a, i) => (a, i)).Where(x => x.a.Last == last).Select(x => $"ADULT:{x.a.Last}:{x.a.First}:{2001 + x.i}").Single();

    private static string Board(params string[] lasts) => string.Join(",", lasts.Select(AdultId));

    /// <summary>
    /// An hour in, at 20:00: room 101 red at 49 minutes of review, 102 at 24,
    /// 103 convening for 12, 200A yellow at 28, 200B free; Bill Amend has
    /// waited 53 minutes, Albert Belle 45 and Thomas Cech 39.
    /// </summary>
    private static void SeedEvent(BoardService s)
    {
        foreach (var (room, type) in new[] { ("101", BoardTypes.Final), ("102", BoardTypes.Final), ("103", BoardTypes.Final), ("200A", BoardTypes.Project), ("200B", BoardTypes.Project) })
        {
            s.AddRoom(room, type);
        }

        for (var i = 0; i < Adults.Length; i++)
        {
            var a = Adults[i];
            s.RegisterAdult(new Dictionary<string, string>
            {
                ["Last"] = a.Last, ["First"] = a.First, ["UnitType"] = "Troop", ["Unit"] = (2001 + i).ToString(CultureInfo.InvariantCulture),
                ["Email"] = $"{a.First}.{a.Last}@example.org".ToLowerInvariant(), ["Phone"] = "555-0100",
                ["ProjectReview"] = a.Project, ["FinalBoard"] = a.Final, ["WoodBadge"] = a.WoodBadge ? "Y" : "",
            });
        }

        void SignIn(string last, int hour, int minute)
        {
            _now = At(hour, minute);
            var y = Youth.Single(x => x.Last == last);
            s.RegisterScout(new Dictionary<string, string>
            {
                ["Last"] = y.Last, ["First"] = y.First, ["UnitType"] = "Troop", ["Unit"] = y.Unit.ToString(CultureInfo.InvariantCulture),
                ["BoardType"] = y.Type, ["Email"] = $"{y.First}.{y.Last}@example.org".ToLowerInvariant(),
            });
        }

        SignIn("Eldred", 18, 55);
        SignIn("Galifianakis", 18, 57);
        SignIn("Agre", 19, 0);
        SignIn("Corddry", 19, 2);
        _now = At(19, 3);
        s.SeatBoard("ROOM:101", YouthId("Eldred"), AdultId("Armstrong"), Board("Armstrong", "Lovell", "Duke"));
        SignIn("Amend", 19, 7);
        _now = At(19, 11);
        s.StartReview(YouthId("Eldred"));
        SignIn("Belle", 19, 15);
        _now = At(19, 20);
        s.SeatBoard("ROOM:102", YouthId("Galifianakis"), AdultId("Ford"), Board("Ford", "Gates", "Bloomberg"));
        s.SeatBoard("ROOM:200A", YouthId("Corddry"), AdultId("Spielberg"), Board("Spielberg", "Rowe"));
        SignIn("Cech", 19, 21);
        _now = At(19, 32);
        s.StartReview(YouthId("Corddry"));
        _now = At(19, 36);
        s.StartReview(YouthId("Galifianakis"));
        _now = At(19, 48);
        s.SeatBoard("ROOM:103", YouthId("Agre"), AdultId("Perot"), Board("Perot", "Walton", "Bradley"));
    }

    private static MainWindow Open(EventSession session)
    {
        var win = new MainWindow(session)
        {
            WindowState = WindowState.Normal,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = 0,
            Width = 1440,
            Height = 900,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        win.Show();
        Settle(win);
        return win;
    }

    private static void Select(MainWindow win, string last)
    {
        win.QueueList.SelectedItem = win.QueueList.Items.Cast<ScoutRow>().First(s => s.Last == last);
        Settle(win);
    }

    private static BitmapSource Shot(MainWindow win)
    {
        Settle(win);
        return Program.Render(win);
    }

    /// <summary>Pump, then show the venue's check-in address and a usual data folder in place of this machine's.</summary>
    private static void Settle(MainWindow win)
    {
        Program.Pump();
        win.Notice.Close();
        win.UrlLabel.Text = "Check-in address: ";
        win.UrlLabel.ClearValue(TextBlock.ForegroundProperty);
        win.UrlList.ItemsSource = new[] { "http://192.168.1.23:8080/" };
        win.DataText.Text = @"Data: C:\eagleboards\2026-10-27";
        Program.Pump();
    }

    /// <summary>A landscape tablet (1180 x 820) at 2x, in light, photographed by a headless Edge or Chrome.</summary>
    private static void Browser(string url, string path)
    {
        string[] candidates =
        [
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
        ];
        var exe = candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("The check-in pictures need Microsoft Edge or Chrome.");
        var profile = Path.Combine(Path.GetTempPath(), "eb-site-browser-" + Guid.NewGuid().ToString("N"));
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[]
        {
            "--headless", "--disable-gpu", "--hide-scrollbars", "--force-device-scale-factor=2",
            "--blink-settings=preferredColorScheme=1", $"--user-data-dir={profile}",
            "--window-size=1180,820", $"--screenshot={path}", url,
        })
        {
            start.ArgumentList.Add(arg);
        }

        File.Delete(path);
        using (var browser = Process.Start(start)!)
        {
            if (!browser.WaitForExit(60_000))
            {
                browser.Kill(entireProcessTree: true);
            }
        }

        try
        {
            Directory.Delete(profile, recursive: true);
        }
        catch (IOException)
        {
        }

        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"{exe} drew nothing for {url}");
        }
    }

    private static void SavePng(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
