using System.Globalization;
using System.IO;
using System.Net;
using System.Windows;
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
    private EventSession? _session;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        CommandLine cmd;
        try
        {
            cmd = new CommandLine(e.Args);
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show("Invalid command line: " + ex.Message + "\n" + CommandLine.Usage, "Eagle Board Scheduler",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        if (cmd.Help)
        {
            MessageBox.Show(CommandLine.Usage, "Eagle Board Scheduler");
            Shutdown(0);
            return;
        }

        if (cmd.DataDirectory != null || cmd.AdultHistory != null || cmd.Config != null)
        {
            _session = await StartFromCommandLineAsync(cmd);
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
                Port = cmd.Port,
                BindAddress = bind,
                Verbose = cmd.Verbose,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException
            or InvalidOperationException)
        {
            MessageBox.Show("The scheduler could not start:\n\n" + ex.Message, "Eagle Board Scheduler",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
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
        var message = e.Exception is IOException
            ? "A data file could not be saved. If it is open in Excel, close it and try again.\n\n" + e.Exception.Message
            : "Something went wrong:\n\n" + e.Exception.Message + "\n\nThe check-in website is still running.";
        MessageBox.Show(message, "Eagle Board Scheduler", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
