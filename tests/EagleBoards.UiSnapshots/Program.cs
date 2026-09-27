using System.IO;
using System.Net;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EagleBoards.App;
using EagleBoards.Core;
using EagleBoards.Core.Records;

namespace EagleBoards.UiSnapshots;

/// <summary>
/// Opens each window off-screen over a synthetic evening and saves a PNG of
/// it, in the light theme, or the dark one with --dark. Every name here is
/// made up; nothing reads a real data folder.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var dark = args.Contains("--dark");
        var rest = args.Where(a => a != "--dark").ToList();
        if (rest.Count > 0 && rest[0] == "--demo")
        {
            return Demo.Run(Path.GetFullPath(rest.Count > 1 ? rest[1] : "demo"), dark);
        }

        var outDir = Path.GetFullPath(rest.Count > 0 ? rest[0] : "snapshots");
        Directory.CreateDirectory(outDir);
        var root = Path.Combine(Path.GetTempPath(), "eb-snap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var history = Path.Combine(root, "AdultHistory.csv");
        File.WriteAllText(history, string.Join(',', AdultRecord.AllColumns) + "\n");
        var config = Path.Combine(root, "config.properties");
        File.WriteAllText(config, "Type=CONFIG\nID=DEFAULT\nName=DEFAULT\n");

        StartApplication(dark);

        var plan = new LaunchPlan
        {
            DataDirectory = Path.Combine(root, "2026-09-22"),
            AdultHistoryPath = history,
            ConfigPath = config,
            Port = 18400 + Random.Shared.Next(500),
            BindAddress = IPAddress.Loopback,
        };

        // Off the UI thread, so the awaits inside don't wait on a dispatcher
        // that isn't pumping yet.
        var session = Task.Run(() => EventSession.StartAsync(plan)).GetAwaiter().GetResult();
        try
        {
            // Seed, then start again from the saved files, as the app does
            // between runs: the window must show what the files hold (their
            // escapes included), not just what was typed.
            Seed(session.Service);
            session = Restart(session, plan);
            Snap(new MainWindow(session), outDir, "1-event.png", win =>
            {
                var w = (MainWindow)win;
                var queue = w.QueueList;
                return
                [
                    ("2-builder.png", () => queue.SelectedItem = queue.Items.Cast<ScoutRow>().First(s => s.Status == BoardStatus.Registered)),
                    ("3-board.png", () => queue.SelectedItem = queue.Items.Cast<ScoutRow>().First(s => s.Status == BoardStatus.InProgress)),
                    ("4-results.png", () => w.MainNav.SelectedIndex = 1),
                    ("5-people.png", () => w.MainNav.SelectedIndex = 2),
                    ("6-settings.png", () => w.FooterNav.SelectedIndex = 0),
                    // What the sidebar's Donate link opens: the Support card
                    // and its Venmo code (SPEC.md D-17), below the fold above.
                    ("6-support.png", () => Invoke(w, "OnDonate", w, new RoutedEventArgs())),
                ];
            });

            Snap(new AdminWindow(session.Service), outDir, "7-admin.png");

            // Pointed at the sandbox: the default would suggest and read the
            // machine's real data folder.
            Snap(new StartupWindow(new AppSettings { DataFolder = root, AdultHistoryFile = "AdultHistory.csv", Port = plan.Port }),
                outDir, "8-startup.png");
            Snap(new HelpWindow(), outDir, "9-help.png");
            Snap(new QrWindow(["http://192.168.1.23:8080/", "http://10.0.0.5:8080/"]), outDir, "12-qr.png");

            // The dialogs, built the way the window builds them.
            var owner = new Window { Left = -32000, Width = 200, Height = 200, ShowInTaskbar = false, ShowActivated = false };
            owner.Show();
            var reset = new AppDialog(owner, "Reset this board?", "Reset board");
            reset.AddMessage("**Beauregard Bram** goes back to waiting. Room 101 and its members are freed, and the board would need seating again.");
            Snap(reset, outDir, "10-confirm-dialog.png");
            var add = new AppDialog(owner, "Add a room", "Add room");
            add.AddText("Room number");
            add.AddChoice("Used for", [new(BoardTypes.Final, "Final board"), new(BoardTypes.Project, "Project review")], BoardTypes.Final);
            add.AddMessage("For project reviews sharing one room, add one entry per table, like 200A and 200B.");
            Snap(add, outDir, "11-form-dialog.png");
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

        Console.WriteLine("snapshots: " + outDir);
        return 0;
    }

    /// <summary>
    /// A plain Application with the app's theme -- NOT EagleBoards.App.App,
    /// whose startup (command line, start-up window, dialogs) WPF runs on the
    /// first message pump even without Run().
    /// </summary>
    internal static Application StartApplication(bool dark)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        ThemeSetup.Apply(app, dark ? ThemeMode.Dark : ThemeMode.Light);
        return app;
    }

    private static void Seed(BoardService s)
    {
        foreach (var (room, type) in new[] { ("101", "Final"), ("102", "Final"), ("103", "Final"), ("200A", "Project"), ("200B", "Project") })
        {
            s.AddRoom(room, type);
        }

        string[] last = ["Abernathy", "Blackwood", "Castellano", "Duxbury", "Ellsworth", "Fairbanks", "Grimaldi", "Hollingsworth", "Ivanovic", "Jankowski", "Kaminski"];
        string[] first = ["Anneliese", "Bartholomew", "Clementine", "Desmond", "Evangeline", "Fitzgerald", "Genevieve", "Horatio", "Isadora", "Jebediah", "Katarina"];
        string[] roles = ["Chair/Chair", "Member/Chair", "Chair/Member", "Member/Member", "Member/Member", "Member/Member", "Member/Member", "Member/Member", "Unavailable/Member", "Member/Member", "Member/Member"];
        for (var i = 0; i < last.Length; i++)
        {
            var r = roles[i].Split('/');
            s.RegisterAdult(new Dictionary<string, string>
            {
                ["Last"] = last[i], ["First"] = first[i], ["UnitType"] = "Troop", ["Unit"] = (2001 + i).ToString(),
                ["Email"] = $"a{i}@example.org", ["ProjectReview"] = r[0], ["FinalBoard"] = r[1],
            });
        }

        string[] slast = ["Aldridge", "Bram", "Carrington", "Dunmore", "Everly", "Fenwick", "Gallagher"];
        string[] sfirst = ["Alexander", "Beauregard", "Cormac", "Dorian", "Emmett", "Finnegan", "Gideon"];
        for (var i = 0; i < slast.Length; i++)
        {
            s.RegisterScout(new Dictionary<string, string>
            {
                ["Last"] = slast[i], ["First"] = sfirst[i], ["UnitType"] = "Troop", ["Unit"] = (1001 + i).ToString(),
                ["BoardType"] = i >= 5 ? "Project" : "Final", ["Email"] = $"s{i}@example.org", ["Leader"] = i == 0 ? "Anneliese Abernathy" : "",
            });
        }

        string A(int i) => $"ADULT:{last[i]}:{first[i]}:{2001 + i}";
        string S(int i) => $"SCOUT:{slast[i]}:{sfirst[i]}:{1001 + i}";
        s.SeatBoard("ROOM:101", S(1), A(1), $"{A(1)},{A(3)},{A(4)}");
        s.StartReview(S(1));
        s.SeatBoard("ROOM:200A", S(5), A(2), $"{A(2)},{A(5)}");
        s.SeatBoard("ROOM:102", S(2), A(0), $"{A(0)},{A(6)},{A(7)}");
        s.StartReview(S(2));
        s.CompleteBoard(S(2), "Approved", "Well prepared, and a strong project.");
        s.PostponeBoard(S(4));
    }

    /// <summary>Stop the session and start a new one on the same files.</summary>
    internal static EventSession Restart(EventSession session, LaunchPlan plan)
    {
        Task.Run(async () => await session.DisposeAsync()).GetAwaiter().GetResult();
        var again = new LaunchPlan
        {
            DataDirectory = plan.DataDirectory,
            AdultHistoryPath = plan.AdultHistoryPath,
            ConfigPath = plan.ConfigPath,
            Port = plan.Port + 1,
            BindAddress = plan.BindAddress,
        };
        return Task.Run(() => EventSession.StartAsync(again)).GetAwaiter().GetResult();
    }

    private static void Snap(Window win, string dir, string name, Func<Window, (string Name, Action Act)[]>? steps = null)
    {
        win.WindowState = WindowState.Normal;
        win.WindowStartupLocation = WindowStartupLocation.Manual;
        win.Left = -32000;
        win.Top = 0;
        if (win.SizeToContent == SizeToContent.Manual)
        {
            win.Width = 1440;
            win.Height = 900;
        }

        win.ShowActivated = false;
        win.ShowInTaskbar = false;
        win.Show();
        Pump();
        Save(win, Path.Combine(dir, name));
        foreach (var (stepName, act) in steps?.Invoke(win) ?? [])
        {
            act();
            Pump();
            Save(win, Path.Combine(dir, stepName));
        }

        win.Hide();
    }

    internal static void Pump()
    {
        for (var i = 0; i < 5; i++)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            Thread.Sleep(60);
        }
    }

    internal static object? Invoke(object target, string method, params object[] args) =>
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(target, args);

    private static void Save(Window win, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Render(win)));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    /// <summary>
    /// The window's content over the theme's base colour. On screen that base
    /// is Mica (Windows 11), which an off-screen render can't draw.
    /// </summary>
    internal static BitmapSource Render(Window win)
    {
        // The window template's root, not its Content: it includes the
        // adorner layer (placeholder text, focus visuals).
        var content = (FrameworkElement)VisualTreeHelper.GetChild(win, 0);
        var width = (int)Math.Ceiling(content.ActualWidth);
        var height = (int)Math.Ceiling(content.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var backdrop = Application.Current.TryFindResource("SolidBackgroundFillColorBaseBrush") as Brush ?? Brushes.White;
            dc.DrawRectangle(backdrop, null, new Rect(0, 0, width, height));
            // Absolute viewbox: by default a VisualBrush trims the content's
            // empty margins and stretches what's left to fill.
            var brush = new VisualBrush(content)
            {
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, width, height),
            };
            dc.DrawRectangle(brush, null, new Rect(0, 0, width, height));
        }

        bitmap.Render(visual);
        return bitmap;
    }
}
