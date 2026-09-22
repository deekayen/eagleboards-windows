using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EagleBoards.App;
using EagleBoards.Core;
using EagleBoards.Core.Records;

namespace EagleBoards.UiSnapshots;

/// <summary>
/// Opens each window off-screen over a synthetic evening and saves a PNG of
/// it. Every name here is made up; nothing reads a real data folder.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var outDir = Path.GetFullPath(args.Length > 0 ? args[0] : "snapshots");
        Directory.CreateDirectory(outDir);
        var root = Path.Combine(Path.GetTempPath(), "eb-snap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var history = Path.Combine(root, "AdultHistory.csv");
        File.WriteAllText(history, string.Join(',', AdultRecord.AllColumns) + "\n");
        var config = Path.Combine(root, "config.properties");
        File.WriteAllText(config, "Type=CONFIG\nID=DEFAULT\nName=DEFAULT\n");

        // A plain Application with the app's theme -- NOT EagleBoards.App.App,
        // whose startup (command line, start-up window, message boxes) WPF
        // runs on the first message pump even without Run().
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/EagleBoards;component/Theme.xaml"),
        });

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
            Seed(session.Service);
            Snap(new MainWindow(session), outDir, "1-scheduler.png", win =>
            {
                var grid = (DataGrid)win.FindName("ScoutGrid");
                return
                [
                    ("2-autoselect.png", () => grid.SelectedItem = grid.Items.Cast<ScoutRow>().First(s => s.Status == BoardStatus.Registered)),
                    ("3-seated-board.png", () =>
                    {
                        // Clear the auto-select's picks first, or they stay the operator's board.
                        foreach (var a in session.Service.Snapshot(DataTable.Adults).Where(a => a["Sel"] == "1"))
                        {
                            session.Service.SaveRow(DataTable.Adults, "updated", a["ID"], new Dictionary<string, string> { ["Sel"] = "0" });
                        }

                        Pump();
                        grid.SelectedItem = grid.Items.Cast<ScoutRow>().First(s => s.Status == BoardStatus.InProgress);
                    }),
                ];
            });
            Snap(new AdminWindow(session.Service), outDir, "4-admin.png");
            Snap(new SettingsWindow(session.Service), outDir, "5-settings.png");
            // Pointed at the sandbox: the default would suggest and read the
            // machine's real data folder.
            Snap(new StartupWindow(new AppSettings { DataFolder = root, AdultHistoryFile = "AdultHistory.csv", Port = plan.Port }),
                outDir, "6-startup.png");
            Snap(new HelpWindow(), outDir, "7-help.png");

            // The dialogs the Seat and Complete buttons open, built the same way.
            var owner = new Window { Left = -32000, Width = 200, Height = 200, ShowInTaskbar = false, ShowActivated = false };
            owner.Show();
            var seat = new FormDialog(owner, "Seat Board: Dorian Dunmore", "Seat");
            seat.AddNote("Room **103**, Final board.\nMembers: Anneliese Abernathy, Genevieve Grimaldi, Horatio Hollingsworth");
            seat.AddChoice("Chair:", [new("A", "Anneliese Abernathy")], "A");
            Snap(seat, outDir, "8-seat-dialog.png");
            var complete = new FormDialog(owner, "Complete Board: Beauregard Bram", "Complete");
            complete.AddChoice("Result:", BoardResults.All.Select(r => new KeyValuePair<string, string>(r, r)), BoardResults.Approved);
            complete.AddMultiline("Notes:");
            Snap(complete, outDir, "9-complete-dialog.png");
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
        s.CompleteBoard(S(2), "Approved", "Well prepared.");
        s.PostponeBoard(S(4));
    }

    private static void Snap(Window win, string dir, string name, Func<Window, (string Name, Action Act)[]>? steps = null)
    {
        win.WindowState = WindowState.Normal;
        win.WindowStartupLocation = WindowStartupLocation.Manual;
        win.Left = -32000;
        win.Top = 0;
        if (win.SizeToContent == SizeToContent.Manual)
        {
            win.Width = Math.Max(win.Width is double.NaN ? 1500 : win.Width, 1500);
            win.Height = Math.Max(win.Height is double.NaN ? 900 : win.Height, 880);
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

    private static void Pump()
    {
        for (var i = 0; i < 5; i++)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            Thread.Sleep(60);
        }
    }

    private static void Save(Window win, string path)
    {
        var content = (FrameworkElement)win.Content;
        var width = (int)Math.Ceiling(content.ActualWidth);
        var height = (int)Math.Ceiling(content.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(win.Background ?? Brushes.White, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, width, height));
        }

        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
