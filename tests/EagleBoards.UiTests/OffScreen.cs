using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using EagleBoards.App;
using EagleBoards.Core;
using EagleBoards.Core.Records;

// One UI thread for every test, one test at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace EagleBoards.UiTests;

/// <summary>
/// The one STA thread the windows live on, with a running dispatcher and a
/// plain Application carrying the app's theme. Never EagleBoards.App.App,
/// whose start-up (the start-up window, the command line) WPF would run.
/// </summary>
internal static class OffScreen
{
    private static readonly Lazy<Dispatcher> UiThread = new(StartUiThread);

    private static Dispatcher StartUiThread()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            ThemeSetup.Apply(app, ThemeMode.Light);
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        { IsBackground = true, Name = "UI tests" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return dispatcher!;
    }

    /// <summary>Run a test on the UI thread; its failures come back here.</summary>
    public static void Run(Action test) => UiThread.Value.Invoke(test);

    /// <summary>Let queued work run: bindings, layout, the window's debounced refresh.</summary>
    public static void Pump()
    {
        for (var i = 0; i < 6; i++)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            Thread.Sleep(60);
        }
    }

    public static object? Invoke(object target, string method, params object[] args) =>
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(target, args);

    public static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    public static T? Ancestor<T>(DependencyObject? from) where T : DependencyObject
    {
        for (var at = from; at != null; at = VisualTreeHelper.GetParent(at))
        {
            if (at is T found)
            {
                return found;
            }
        }

        return null;
    }
}

/// <summary>
/// A synthetic event in a temporary folder, served on the loopback address
/// only, and its main window drawn off-screen. Hidden when done, never
/// closed: closing asks "Close the scheduler?", which would appear on the
/// desktop of whoever runs the tests.
/// </summary>
internal sealed class TestEvent : IDisposable
{
    private readonly string _root;
    private readonly HttpClient _http = new();
    private EventSession? _session;
    private MainWindow? _window;

    public TestEvent()
    {
        _root = Path.Combine(Path.GetTempPath(), "eb-uitest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var history = Path.Combine(_root, "AdultHistory.csv");
        File.WriteAllText(history, string.Join(',', AdultRecord.AllColumns) + "\n");
        var config = Path.Combine(_root, "config.properties");
        File.WriteAllText(config, "Type=CONFIG\nID=DEFAULT\nName=DEFAULT\n");
        Plan = new LaunchPlan
        {
            DataDirectory = Path.Combine(_root, "2026-09-30"),
            AdultHistoryPath = history,
            ConfigPath = config,
            Port = 19000 + Random.Shared.Next(900),
            BindAddress = IPAddress.Loopback,
        };

        // Off the UI thread: the awaits inside mustn't wait on it.
        _session = Task.Run(() => EventSession.StartAsync(Plan)).GetAwaiter().GetResult();
    }

    public LaunchPlan Plan { get; }

    public BoardService Service => _session!.Service;

    public MainWindow Window => _window ??= Show(new MainWindow(_session!));

    public void Room(string name, string boardType) => Service.AddRoom(name, boardType);

    public string Youth(string last, string first, string unit, string boardType, string leader = "")
    {
        Service.RegisterScout(new Dictionary<string, string>
        {
            ["Last"] = last, ["First"] = first, ["UnitType"] = "Troop", ["Unit"] = unit,
            ["BoardType"] = boardType, ["Email"] = $"{last}@example.org", ["Leader"] = leader,
        });
        return $"SCOUT:{last}:{first}:{unit}";
    }

    /// <summary>An adult signs in before the window opens, straight through the service.</summary>
    public string Adult(string last, string first, string unit, string finalRole, string projectRole = BoardRoles.Member)
    {
        Service.RegisterAdult(AdultForm(last, first, unit, finalRole, projectRole));
        return $"ADULT:{last}:{first}:{unit}";
    }

    /// <summary>
    /// An adult signs in at the tablet while the window is open: over HTTP,
    /// so the change arrives from a server thread as it does at an event.
    /// </summary>
    public void AdultAtTheDoor(string last, string first, string unit, string finalRole, string projectRole = BoardRoles.Member)
    {
        var form = new FormUrlEncodedContent(AdultForm(last, first, unit, finalRole, projectRole));
        var response = Task.Run(() => _http.PostAsync($"http://127.0.0.1:{Plan.Port}/register-adult", form)).GetAwaiter().GetResult();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static Dictionary<string, string> AdultForm(string last, string first, string unit, string finalRole, string projectRole) => new()
    {
        ["Last"] = last, ["First"] = first, ["UnitType"] = "Troop", ["Unit"] = unit, ["Email"] = $"{last}@example.org",
        ["Phone"] = "555-0100", ["FinalBoard"] = finalRole, ["ProjectReview"] = projectRole,
    };

    private static MainWindow Show(MainWindow window)
    {
        window.WindowState = WindowState.Normal;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -32000;
        window.Top = 0;
        window.Width = 1440;
        window.Height = 900;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Show();
        OffScreen.Pump();
        return window;
    }

    public void Dispose()
    {
        _window?.Hide();
        _http.Dispose();
        Task.Run(async () => await _session!.DisposeAsync()).GetAwaiter().GetResult();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
