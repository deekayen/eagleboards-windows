using System.Globalization;

namespace EagleBoards.Web;

/// <summary>
/// The Java jar's command line, accepted by both the server and the Windows
/// app so an existing launcher keeps working. Options take one value; a
/// single or double leading dash both work.
/// </summary>
public sealed class CommandLine
{
    private static readonly Dictionary<string, string> OptionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["d"] = "dir", ["dir"] = "dir",
        ["a"] = "adults", ["adults"] = "adults", ["adult"] = "adults",
        ["p"] = "prereg", ["prereg"] = "prereg",
        ["c"] = "config", ["config"] = "config",
        ["port"] = "port",
        ["sugkey"] = "sugkey",
        ["sugid"] = "sugid",
        ["bind"] = "bind",
    };

    private static readonly Dictionary<string, string> FlagNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["v"] = "verbose", ["verbose"] = "verbose",
        ["?"] = "help", ["h"] = "help", ["help"] = "help",
        ["debug"] = "debug",
        ["w"] = "windows", ["windows"] = "windows",
    };

    private readonly Dictionary<string, string> _options = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);

    public CommandLine(IReadOnlyList<string> args)
    {
        Count = args.Count;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith('-') && !arg.StartsWith('/'))
            {
                throw new ArgumentException($"unexpected argument '{arg}'");
            }

            var name = arg.TrimStart('-', '/');
            if (FlagNames.TryGetValue(name, out var flag))
            {
                _flags.Add(flag);
            }
            else if (OptionNames.TryGetValue(name, out var option))
            {
                if (i + 1 >= args.Count)
                {
                    throw new ArgumentException($"option '{arg}' needs a value");
                }

                _options[option] = args[++i];
            }
            else
            {
                throw new ArgumentException($"unknown option '{arg}'");
            }
        }
    }

    public int Count { get; }

    public bool Help => _flags.Contains("help");

    public bool Verbose => _flags.Contains("verbose") || _flags.Contains("debug");

    public string? DataDirectory => Get("dir");

    public string? AdultHistory => Get("adults");

    public string? PreRegistration => Get("prereg");

    public string? Config => Get("config");

    public string? SignUpGeniusKey => Get("sugkey");

    public string? SignUpGeniusId => Get("sugid");

    public string? BindPrefix => Get("bind");

    public int Port
    {
        get
        {
            var value = Get("port");
            if (value == null)
            {
                return 8080;
            }

            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
            {
                throw new ArgumentException($"'{value}' is not a port number");
            }

            return port;
        }
    }

    public string? Get(string name) => _options.TryGetValue(name, out var v) ? v : null;

    public const string Usage = """

           USAGE

                  EagleBoards.Server <options>

              OPTIONS

                 -v[erbose]                     : print verbose messages
                 -h[elp]                        : print this message

                 -d[ir]    <data-directory>     : directory where data files live
                                                  (default: today's date, yyyy-MM-dd)
                 -a[dult]  <adult-history-file> : file containing adult auto-fill data
                 -c[onfig] <config-file>        : scheduler config file
                 -p[rereg] <prereg-file>        : preregistration data file (csv) from district website
                 -sugkey <signup-genius-key>    : SignUpGenius API key
                 -sugid  <signup-id>            : SignUpGenius sign-up ID (optional)
                 -port   <port>                 : port to serve on (default 8080)

                 -bind   <ip-prefix>            : only listen on / advertise the interface whose
                                                  IPv4 address starts with this (e.g. 192.168.);
                                                  127.0.0.1 stays reachable either way;
                                                  default is every interface

        """;
}
