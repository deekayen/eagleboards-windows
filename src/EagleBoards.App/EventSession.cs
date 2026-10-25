using System.IO;
using System.Net;
using System.Net.Http;
using EagleBoards.Core;
using EagleBoards.Web;

namespace EagleBoards.App;

/// <summary>Everything needed to start an event.</summary>
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
/// A running event: the data, the check-in website serving it, and a log
/// file in the event's folder.
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

    /// <summary>Set when the import was attempted and failed; the event still runs without it.</summary>
    public string? ImportError { get; private set; }

    /// <summary>The addresses check-in stations can use, most likely first.</summary>
    public IReadOnlyList<string> CheckInUrls { get; private set; } = [];

    /// <summary>
    /// Served on this computer only (-bind 127.0.0.1): the check-in stations,
    /// being other computers, can't reach it.
    /// </summary>
    public bool IsLocalOnly => Plan.BindAddress is { } a && IPAddress.IsLoopback(a);

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
        catch (Exception ex)
        {
            // The window says it in plain words (StartProblem); the log keeps
            // the framework's own, with the file it was about.
            session.Log("couldn't start: " + ex.Message);
            await session.DisposeAsync().ConfigureAwait(true);
            throw;
        }
    }

    private async Task StartCoreAsync(IProgress<string>? progress)
    {
        Log($"--- starting: data {Plan.DataDirectory}, port {Plan.Port}, bind {Plan.BindAddress?.ToString() ?? "all"}");
        progress?.Report("Opening the event's data...");
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
                ImportSummary = "SignUpGenius: " + string.Join(", ", Display.Count(s.ScoutsAdded, "youth", "youth"),
                    Display.Count(s.AdultsAdded, "new adult", "new adults"), Display.Count(s.AdultsUpdated, "adult updated", "adults updated"));
                Log(ImportSummary);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException
                or System.Text.Json.JsonException or KeyNotFoundException)
            {
                // The log keeps what SignUpGenius said; the window, what it
                // means and what to do (K3).
                ImportError = ImportProblem(ex);
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

    /// <summary>
    /// Why the import failed, in plain words: the exception's own text is a
    /// status code and the API's address ("SignUpGenius answered HTTP 401
    /// for https://..."), read out URL and all.
    /// </summary>
    internal static string ImportProblem(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } =>
            "SignUpGenius didn't accept the API key. Check the key, and then start the scheduler again.",

        // No answer at all: no internet, the name not found, or nothing
        // back within the 30 seconds.
        HttpRequestException { StatusCode: null, HttpRequestError: not HttpRequestError.Unknown }
            or HttpRequestException { StatusCode: null, InnerException: not null } or TaskCanceledException =>
            "Couldn't reach SignUpGenius. Check the internet connection, and then start the scheduler again.",
        HttpRequestException =>
            "SignUpGenius couldn't send the sign-ups just now. Start the scheduler again later to try again.",
        System.Text.Json.JsonException or KeyNotFoundException =>
            "SignUpGenius sent the sign-ups in a form the scheduler doesn't recognize.",

        // Core's own words already: no sign-up for the event covers today.
        _ => ex.Message,
    };

    /// <summary>
    /// Why the event couldn't start, in plain words with what to do, for the
    /// start-up window and a command-line start (K3). The exception's own text
    /// goes to the log.
    /// </summary>
    internal static string StartProblem(Exception ex, int port)
    {
        if (ex is IOException && ex.Message.Contains("bind", StringComparison.OrdinalIgnoreCase))
        {
            return $"Port {port} is already in use. The scheduler or the Java version may already be running. Close it, or choose another port.";
        }

        // A file open in Excel: the event's own, the adult history or the
        // settings, which needn't be in the event's folder. Named by its file
        // name; the framework's message has it in a path read out in full.
        return LockedFile(ex) is { } locked
            ? $"{Path.GetFileName(locked)} is open in another program, probably Excel. Close it there, and then start again."
            : ex is IOException io && (io.HResult & 0xFFFF) is 32 or 33
                ? "A file the event uses is open in another program, probably Excel. Close it there, and then start again."
                : ErrorText.For(ex);
    }

    /// <summary>The file another program has open, from a sharing violation's message (it quotes the path), or null.</summary>
    internal static string? LockedFile(Exception ex)
    {
        if (ex is not IOException io || (io.HResult & 0xFFFF) is not (32 or 33))
        {
            return null;
        }

        // Greedy, so a path with an apostrophe in it stays whole.
        var quoted = System.Text.RegularExpressions.Regex.Match(io.Message, "'(.+)'");
        return quoted.Success ? quoted.Groups[1].Value : null;
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
