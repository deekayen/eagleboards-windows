using System.IO;
using System.Net;
using System.Net.Http;
using EagleBoards.Core;
using EagleBoards.Web;

namespace EagleBoards.App;

/// <summary>Everything needed to start an event night.</summary>
public sealed class LaunchPlan
{
    public required string DataDirectory { get; init; }

    public string? AdultHistoryPath { get; init; }

    public string? ConfigPath { get; init; }

    public string? PreRegistrationPath { get; init; }

    public string? SignUpGeniusKey { get; init; }

    public string? SignUpGeniusId { get; init; }

    public int Port { get; init; } = 8080;

    public IPAddress? BindAddress { get; init; }

    public bool Verbose { get; init; }
}

/// <summary>
/// A running event night: the data, the check-in website serving it, and a
/// log file in the night's folder.
/// </summary>
public sealed class EventSession : IAsyncDisposable
{
    private readonly StreamWriter? _log;
    private readonly Lock _logLock = new();

    private EventSession(LaunchPlan plan, StreamWriter? log)
    {
        Plan = plan;
        _log = log;
    }

    public LaunchPlan Plan { get; }

    public BoardService Service { get; private set; } = null!;

    public CheckInServer Server { get; private set; } = null!;

    /// <summary>What the SignUpGenius import did, for the status bar, or null if it didn't run.</summary>
    public string? ImportSummary { get; private set; }

    /// <summary>Set when the import was attempted and failed; the night still runs without it.</summary>
    public string? ImportError { get; private set; }

    /// <summary>The addresses check-in stations can use, most likely first.</summary>
    public IReadOnlyList<string> CheckInUrls { get; private set; } = [];

    /// <summary>This machine's own view of the check-in site.</summary>
    public string LocalUrl => $"http://127.0.0.1:{Plan.Port}/";

    public static async Task<EventSession> StartAsync(LaunchPlan plan, IProgress<string>? progress = null)
    {
        Directory.CreateDirectory(plan.DataDirectory);
        StreamWriter? log = null;
        try
        {
            log = new StreamWriter(Path.Combine(plan.DataDirectory, "eagleboards.log"), append: true) { AutoFlush = true };
        }
        catch (IOException)
        {
            // Another copy may hold the log; carry on without one.
        }

        var session = new EventSession(plan, log);
        try
        {
            await session.StartCoreAsync(progress).ConfigureAwait(true);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(true);
            throw;
        }
    }

    private async Task StartCoreAsync(IProgress<string>? progress)
    {
        Log($"--- starting: data {Plan.DataDirectory}, port {Plan.Port}, bind {Plan.BindAddress?.ToString() ?? "all"}");
        progress?.Report("Opening tonight's data...");
        Service = BoardService.Open(new EventOptions
        {
            DataDirectory = Plan.DataDirectory,
            AdultHistoryPath = Plan.AdultHistoryPath,
            ConfigPath = Plan.ConfigPath,
            PreRegistrationPath = Plan.PreRegistrationPath,
        }, Log);
        Service.Verbose = Plan.Verbose;

        if (Plan.PreRegistrationPath == null && Plan.SignUpGeniusKey != null)
        {
            progress?.Report("Importing sign-ups from SignUpGenius...");
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                var s = await Service.ImportSignUpGeniusAsync(Plan.SignUpGeniusKey, Plan.SignUpGeniusId, http).ConfigureAwait(true);
                ImportSummary = $"SignUpGenius: {s.ScoutsAdded} youth, {s.AdultsAdded} new adults, {s.AdultsUpdated} adults updated";
                Log(ImportSummary);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException
                or System.Text.Json.JsonException or KeyNotFoundException)
            {
                ImportError = ex.Message;
                Log("SignUpGenius error: " + ex.Message);
            }
        }

        progress?.Report("Starting the check-in website...");
        Server = new CheckInServer(Service, new CheckInServerOptions
        {
            Port = Plan.Port,
            BindAddress = Plan.BindAddress,
            Verbose = Plan.Verbose,
            Log = Log,
        });
        await Server.StartAsync().ConfigureAwait(true);

        CheckInUrls = Plan.BindAddress != null
            ? [$"http://{Plan.BindAddress}:{Plan.Port}/"]
            : LanAddresses.Find().Select(a => $"http://{a.Address}:{Plan.Port}/").ToList();
        Log("check-in: " + string.Join("  ", CheckInUrls));
    }

    public void Log(string message)
    {
        lock (_logLock)
        {
            try
            {
                _log?.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}");
            }
            catch (IOException)
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Server != null)
        {
            await Server.DisposeAsync().ConfigureAwait(false);
        }

        Log("--- stopped");
        lock (_logLock)
        {
            _log?.Dispose();
        }
    }
}
