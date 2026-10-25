using System.Globalization;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using EagleBoards.Web;

namespace EagleBoards.App;

/// <summary>
/// The admin computer's app. Started with no arguments it asks where the data
/// lives and which network to serve (<see cref="StartupWindow"/>). Started
/// with the Java jar's options (-d, -a, -c, -port, -bind, -sugkey...) it goes
/// straight to the scheduler, so an existing launcher keeps working.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// The command-line options (the Java jar's), one to a row with what
    /// each does in a sentence. <see cref="CommandLine.Usage"/> is a console
    /// block, aligned with spaces and broken mid-sentence, that came apart in
    /// a window and was read out as fragments (F11); it stays the console's.
    /// </summary>
    private static readonly (string Option, string Value, string Description)[] Options =
    [
        ("-d", "folder", "The event's data folder. By default, a folder named with today's date, such as 2026-10-24. Also written -dir."),
        ("-a", "file", "The adult history file. Also written -adult or -adults."),
        ("-c", "file", "The scheduler's settings file. Also written -config."),
        ("-p", "file", "A pre-registration file (CSV) from the district website. Also written -prereg."),
        ("-sugkey", "key", "The SignUpGenius API key, to import the event's sign-ups."),
        ("-sugid", "ID", "The ID of the SignUpGenius sign-up to import. By default, the Eagle board sign-up that covers today."),
        ("-port", "number", "The port the check-in website uses. By default, 8080."),
        ("-bind", "prefix", "Serve the check-in website only on the network whose address starts with this, such as 192.168. This computer can still use 127.0.0.1. By default, every network."),
        ("-v", "", "Write more detail to the event's log. Also written -verbose."),
        ("-h", "", "Show these options. Also written -help."),
    ];

    private EventSession? _session;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ThemeSetup.Apply(this, ThemeMode.System);
        DispatcherUnhandledException += OnUnhandledException;

        CommandLine cmd;
        try
        {
            cmd = new CommandLine(e.Args);
        }
        catch (ArgumentException ex)
        {
            ShowOptions("That command line didn't work", $"The scheduler didn't start: {ex.Message}.", Severity.Error);
            Shutdown(1);
            return;
        }

        if (cmd.Help)
        {
            ShowOptions("Command-line options", null, Severity.Informational);
            Shutdown(0);
            return;
        }

        if (cmd.DataDirectory != null || cmd.AdultHistory != null || cmd.Config != null)
        {
            _session = await StartFromCommandLineAsync(cmd);
            if (_session == null)
            {
                // SPEC.md D-9: a command-line start that failed says so to
                // whatever started it. Cancelling the startup window below
                // is not a failure, and stays 0.
                Shutdown(1);
                return;
            }
        }
        else
        {
            var startup = new StartupWindow();
            if (startup.ShowDialog() == true)
            {
                _session = startup.Session;
            }
        }

        if (_session == null)
        {
            Shutdown(0);
            return;
        }

        var main = new MainWindow(_session);
        MainWindow = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        main.Show();
    }

    private static async Task<EventSession?> StartFromCommandLineAsync(CommandLine cmd)
    {
        int port;
        try
        {
            port = cmd.Port;
        }
        catch (ArgumentException ex)
        {
            ShowOptions("That command line didn't work", $"The scheduler didn't start: {ex.Message}.", Severity.Error);
            return null;
        }

        try
        {
            IPAddress? bind = null;
            if (cmd.BindPrefix != null)
            {
                bind = LanAddresses.MatchPrefix(cmd.BindPrefix)?.Address;
            }

            var key = cmd.SignUpGeniusKey;
            return await EventSession.StartAsync(new LaunchPlan
            {
                DataDirectory = cmd.DataDirectory ?? DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                AdultHistoryPath = cmd.AdultHistory,
                ConfigPath = cmd.Config,
                PreRegistrationPath = cmd.PreRegistration,
                SignUpGeniusKey = key is { Length: > 10 } ? key : null,
                SignUpGeniusId = cmd.SignUpGeniusId,
                Port = port,
                BindAddress = bind,
                Verbose = cmd.Verbose,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException
            or InvalidOperationException)
        {
            AppDialog.Alert(null, "The scheduler couldn't start", EventSession.StartProblem(ex, port));
            return null;
        }
    }

    private static void ShowOptions(string title, string? problem, Severity severity) => OptionsDialog(title, problem, severity).ShowDialog();

    /// <summary>
    /// The options, after what went wrong if something did, with OK to close.
    /// Not yet shown, so the snapshot tool can draw it.
    /// </summary>
    internal static AppDialog OptionsDialog(string title, string? problem, Severity severity)
    {
        var dialog = new AppDialog(null, title, "OK", closeText: null);
        if (problem != null)
        {
            dialog.AddInfo(severity, "", problem);
        }

        dialog.AddMessage("**EagleBoards.exe** takes the same options as the Java version. With -d, -a or -c, it starts the event without the start-up window.");
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var (option, value, description) in Options)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var row = grid.RowDefinitions.Count - 1;

            // The option in semibold and the value it takes in italic, as
            // command syntax is written; then what it does, wrapping freely.
            var name = new TextBlock { Margin = new Thickness(0, 0, 0, 8) };
            name.Inlines.Add(new Run(option) { FontWeight = FontWeights.SemiBold });
            if (value.Length > 0)
            {
                name.Inlines.Add(new Run(" " + value) { FontStyle = FontStyles.Italic });
            }

            var text = new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
            Grid.SetRow(name, row);
            Grid.SetRow(text, row);
            Grid.SetColumn(text, 2);
            grid.Children.Add(name);
            grid.Children.Add(text);
        }

        dialog.AddContent(grid);
        return dialog;
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_session != null)
        {
            await _session.DisposeAsync();
        }

        base.OnExit(e);
    }

    /// <summary>
    /// Last resort. Something went wrong in the window, not in the data:
    /// every change is already on disk by the time it is shown. Say so and
    /// keep running, because closing would also take the check-in site down.
    /// </summary>
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _session?.Log("UNHANDLED: " + e.Exception);
        var (title, message) = e.Exception is IOException
            ? ("A data file couldn't be saved", "If it's open in Excel, close it and try again. " + e.Exception.Message)
            : ("Something went wrong", e.Exception.Message + "\n\nNothing was lost: every change is saved as it happens, and the check-in website is still running.");
        AppDialog.Alert(MainWindow, title, message, Severity.Warning);
        e.Handled = true;
    }
}
