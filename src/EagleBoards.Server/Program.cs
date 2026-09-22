using System.Globalization;
using EagleBoards.Core;
using EagleBoards.Web;

CommandLine cmd;
try
{
    cmd = new CommandLine(args);
}
catch (ArgumentException ex)
{
    Console.WriteLine($"\n\n   ERROR:  invalid command line argument(s): {ex.Message}\n\n");
    return 1;
}

if (cmd.Help)
{
    Console.WriteLine(CommandLine.Usage);
    return 1;
}

void Log(string message) => Console.WriteLine(message);

int port;
try
{
    port = cmd.Port;
}
catch (ArgumentException ex)
{
    Console.WriteLine($"\n\n   ERROR:  {ex.Message}\n\n");
    return 1;
}

var dataDir = cmd.DataDirectory ?? DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

Console.WriteLine("\nAvailable Network Interfaces");
foreach (var lan in LanAddresses.Find())
{
    if (cmd.BindPrefix == null || lan.Address.ToString().StartsWith(cmd.BindPrefix, StringComparison.Ordinal))
    {
        Console.WriteLine($"                 http://{lan.Address}:{port}   ({lan.InterfaceName})");
    }
}

// Only -bind narrows the listener; without it, every interface is served.
var bound = cmd.BindPrefix == null ? null : LanAddresses.MatchPrefix(cmd.BindPrefix);

if (cmd.BindPrefix != null)
{
    if (bound == null)
    {
        // Don't strand the event if the wifi is down at startup: warn loudly
        // and fall back to listening everywhere.
        Console.WriteLine($"\n   WARNING: no active interface matches -bind {cmd.BindPrefix}; listening on all interfaces.\n");
    }
    else
    {
        Console.WriteLine($"\n   binding to {bound.Address} and 127.0.0.1 (-bind {cmd.BindPrefix})\n");
    }
}

BoardService service;
try
{
    service = BoardService.Open(new EventOptions
    {
        DataDirectory = dataDir,
        AdultHistoryPath = cmd.AdultHistory,
        ConfigPath = cmd.Config,
        PreRegistrationPath = cmd.PreRegistration,
    }, Log);
    service.Verbose = cmd.Verbose;

    if (cmd.PreRegistration == null && cmd.SignUpGeniusKey is { Length: > 10 } key)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var summary = await service.ImportSignUpGeniusAsync(key, cmd.SignUpGeniusId, http);
            Log($"\n   SignUpGenius: sign-up {summary.SignupId}: {summary.ScoutsAdded} youth, "
                + $"{summary.AdultsAdded} new adults, {summary.AdultsUpdated} adults updated\n");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException
            or System.Text.Json.JsonException or KeyNotFoundException)
        {
            Log($"\n\n   SignupGenius Error: {ex.Message}\n\n");
        }
    }
    else if (cmd.PreRegistration == null)
    {
        Log("\n   no prereg-file or SignupGenius DB loaded \n");
    }
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
{
    Console.Error.WriteLine($"\n\n  ERROR: {ex.Message}\n\n");
    return 1;
}

await using var server = new CheckInServer(service, new CheckInServerOptions
{
    Port = port,
    BindAddress = bound?.Address,
    Verbose = cmd.Verbose,
    Log = Log,
});

try
{
    await server.StartAsync();
}
catch (IOException ex)
{
    Console.Error.WriteLine($"\n\n  ERROR: cannot serve on port {port}: {ex.Message}\n\n");
    return 1;
}

Console.WriteLine($"\n URL: http://<ip-address>:{port}\n");

var stop = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.TrySetResult();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.TrySetResult();
await stop.Task;
return 0;
