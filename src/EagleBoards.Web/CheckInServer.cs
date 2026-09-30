using System.Net;
using System.Security;
using System.Text;
using System.Text.Json;
using EagleBoards.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace EagleBoards.Web;

public sealed class CheckInServerOptions
{
    public int Port { get; init; } = 8080;

    /// <summary>
    /// Serve on this address only (plus 127.0.0.1), so the socket never
    /// appears on Hyper-V, WSL or VPN adapters. Null listens everywhere.
    /// </summary>
    public IPAddress? BindAddress { get; init; }

    public bool Verbose { get; init; }

    public Action<string> Log { get; init; } = _ => { };
}

/// <summary>
/// The web server the check-in stations use. It serves the three sign-in
/// pages and the endpoints they call to anyone on the network.
///
/// It also answers every other endpoint the Java server had -- the grid
/// reads, record edits and board actions the old browser admin pages used --
/// with the same wire formats, but only to requests from this machine. Nothing
/// in the Windows app needs them (it calls <see cref="BoardService"/>
/// directly); they are kept so the HTTP test suite written against the Java
/// server can prove the port behaves the same, and so a sign-in station can
/// no longer seat a board or read a phone number it has no business with.
/// </summary>
public sealed class CheckInServer : IAsyncDisposable
{
    private readonly BoardService _service;
    private readonly CheckInServerOptions _options;
    private readonly Dictionary<string, Endpoint> _endpoints;
    private readonly Dictionary<string, byte[]> _assets;
    private WebApplication? _app;

    private delegate Task Handler(HttpContext context, IReadOnlyDictionary<string, string> p, bool isLocal);

    private sealed record Endpoint(Handler Handle, bool Public);

    /// <summary>Columns a check-in station may read from the youth and adult lists.</summary>
    private static readonly HashSet<string> PublicListColumns = ["RegTimeHM", "Last", "First", "UnitType", "Unit", "UnitName"];

    public CheckInServer(BoardService service, CheckInServerOptions options)
    {
        _service = service;
        _options = options;
        _assets = LoadAssets();
        _endpoints = new Dictionary<string, Endpoint>(StringComparer.Ordinal)
        {
            // Check-in stations.
            ["/register-youth"] = new(RegisterYouth, true),
            ["/register-adult"] = new(RegisterAdult, true),
            ["/youth-cells"] = new(Cells(DataTable.Scouts), true),
            ["/adult-cells"] = new(Cells(DataTable.Adults), true),
            // Whom an adult may say they will introduce (SPEC.md D-23): names and units only.
            ["/scout-choices"] = new(ScoutChoices, true),
            // The shared check-in pages' API (eagleboards-shared/checkin,
            // SPEC.md D-18): names and units for the lists, a lookup's own
            // fields for the forms, never a birthdate or a youth's phone
            // number (D-7, D-8).
            ["/api/checked-in"] = new(ApiCheckedIn, true),
            ["/api/scout-choices"] = new(ApiScoutChoices, true),
            ["/api/youth-lookup"] = new(ApiLookup(DataTable.ScoutsScheduled, YouthPrefill), true),
            ["/api/adult-lookup"] = new(ApiLookup(DataTable.AdultHistory, AdultPrefill), true),
            ["/adult-autofill"] = new(AutoFill(DataTable.AdultHistory, "Email"), true),
            ["/youth-autofill"] = new(AutoFill(DataTable.ScoutsScheduled, "Email"), true),
            ["/config-autofill"] = new(AutoFill(DataTable.Config, "Name"), true),

            // This machine only.
            ["/update-config"] = new(UpdateConfig, false),
            ["/youth-scheduled-cells"] = new(Cells(DataTable.ScoutsScheduled), false),
            ["/adult-history-cells"] = new(Cells(DataTable.AdultHistory), false),
            ["/room-cells"] = new(Cells(DataTable.Rooms), false),
            ["/approved-proposals-cells"] = new(ApprovedProposalsCells, false),
            ["/youth-update"] = new(Update(DataTable.Scouts), false),
            ["/youth-scheduled-update"] = new(Update(DataTable.ScoutsScheduled), false),
            ["/adult-update"] = new(Update(DataTable.Adults), false),
            ["/adult-history-update"] = new(Update(DataTable.AdultHistory), false),
            ["/room-update"] = new(Update(DataTable.Rooms), false),
            ["/room-change"] = new(BoardAction(p => _service.ChangeRoom(p.Get("RmID1"), p.Get("RmID2"))), false),
            ["/seat-board"] = new(BoardAction(p => _service.SeatBoard(p.Get("RoomID"), p.Get("ScoutID"), p.Get("ChairID"), p.Get("MemberIDs"))), false),
            ["/inprogress-board"] = new(BoardAction(p => _service.StartReview(p.Get("ScoutID"))), false),
            ["/complete-board"] = new(BoardAction(p => _service.CompleteBoard(p.Get("ScoutID"), p.Get("Result"), p.Get("Notes"),
                p.Get("Cost"), p.Get("BSAHours"), p.Get("OtherHours"))), false),
            ["/postpone-board"] = new(BoardAction(p => _service.PostponeBoard(p.Get("ScoutID"))), false),
            ["/reset-board"] = new(BoardAction(p => _service.ResetBoard(p.Get("ScoutID"))), false),
            // The Java version's Event page changes a seated board's members
            // over HTTP; same parameters as /seat-board, without the room.
            ["/change-board-members"] = new(BoardAction(p => _service.ChangeBoardMembers(p.Get("ScoutID"), p.Get("ChairID"), p.Get("MemberIDs"))), false),
            // And renames a room from its card; everyone in it follows.
            ["/rename-room"] = new(BoardAction(p => _service.RenameRoom(p.Get("RoomID"), p.Get("Room"))), false),
            // The Java version's Undo (SPEC.md O-2): the last reversible action, once.
            ["/restore-board"] = new(BoardAction(_ => _service.RestoreBoard()), false),
        };
    }

    public int Port => _options.Port;

    public async Task StartAsync(CancellationToken cancel = default)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = "EagleBoards",
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            var bind = _options.BindAddress;
            if (bind == null)
            {
                kestrel.ListenAnyIP(_options.Port);
            }
            else
            {
                kestrel.Listen(bind, _options.Port);

                // Pinning one address drops loopback; keep it so this machine
                // can still reach its own pages.
                if (!IPAddress.IsLoopback(bind))
                {
                    kestrel.Listen(IPAddress.Loopback, _options.Port);
                }
            }
        });

        var app = builder.Build();
        app.Run(DispatchAsync);
        await app.StartAsync(cancel).ConfigureAwait(false);
        _app = app;
    }

    public async Task StopAsync()
    {
        if (_app != null)
        {
            await _app.StopAsync().ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
            _app = null;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>Handle one request. Internal so tests can feed it a request "from" any address without a socket.</summary>
    internal async Task DispatchAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "/";
        var isLocal = IsLocal(context);
        if (_options.Verbose)
        {
            _options.Log($"{context.Request.Method} {path}{context.Request.QueryString} from {context.Connection.RemoteIpAddress}");
        }

        try
        {
            if (_endpoints.TryGetValue(path, out var endpoint))
            {
                if (!endpoint.Public && !isLocal)
                {
                    await SendAsync(context, 403, "text/plain", "This is only available on the admin computer.").ConfigureAwait(false);
                    return;
                }

                var parameters = await ReadParametersAsync(context).ConfigureAwait(false);
                await endpoint.Handle(context, parameters, isLocal).ConfigureAwait(false);
                return;
            }

            if (!TryServeAsset(context, path, out var task))
            {
                await SendAsync(context, 404, "text/plain", "Not found.").ConfigureAwait(false);
                return;
            }

            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // One bad request gets a clean 500; the server carries on.
            _options.Log($"ERROR handling request {path}: {ex}");
            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                await SendAsync(context, 500, "text/plain", "ERROR: " + ex.Message).ConfigureAwait(false);
            }
        }
    }

    private static bool IsLocal(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote == null)
        {
            return true; // in-process test server
        }

        if (remote.IsIPv4MappedToIPv6)
        {
            remote = remote.MapToIPv4();
        }

        return IPAddress.IsLoopback(remote);
    }

    /// <summary>Query string then form body, first value wins (servlet getParameter semantics).</summary>
    private static async Task<IReadOnlyDictionary<string, string>> ReadParametersAsync(HttpContext context)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, values) in context.Request.Query)
        {
            result.TryAdd(key, values.FirstOrDefault() ?? "");
        }

        if (context.Request.HasFormContentType)
        {
            var form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
            foreach (var (key, values) in form)
            {
                result.TryAdd(key, values.FirstOrDefault() ?? "");
            }
        }

        return result;
    }

    // ------------------------------------------------------------------
    // Handlers
    // ------------------------------------------------------------------

    private Task RegisterYouth(HttpContext context, IReadOnlyDictionary<string, string> p, bool isLocal)
    {
        _service.RegisterScout(p);
        context.Response.StatusCode = 200;
        return Task.CompletedTask;
    }

    private Task RegisterAdult(HttpContext context, IReadOnlyDictionary<string, string> p, bool isLocal)
    {
        _service.RegisterAdult(p);
        context.Response.StatusCode = 200;
        return Task.CompletedTask;
    }

    private Task UpdateConfig(HttpContext context, IReadOnlyDictionary<string, string> p, bool isLocal)
    {
        _service.UpdateConfig(p);
        context.Response.StatusCode = 200;
        return Task.CompletedTask;
    }

    /// <summary>
    /// A board action. "OK." with 200 on success. A refusal is its reason as
    /// plain text with 409 -- the Java server used 304, which may not carry a
    /// body here; the client contract (success is 200 and "OK") is unchanged.
    /// </summary>
    private static Handler BoardAction(Func<IReadOnlyDictionary<string, string>, ActionResult> act) => (context, p, _) =>
    {
        var result = act(p);
        return SendAsync(context, result.Ok ? 200 : 409, "text/plain", result.Message);
    };

    /// <summary>
    /// Grid rows. <c>cols=A,B</c> picks columns, <c>data=</c> adds userdata,
    /// <c>fmt=rows|data|csv</c>, <c>filter=Field~v1|v2</c> keeps rows whose
    /// Field is non-empty and appears in the value list (none, if Field is
    /// withheld), <c>filename=</c> makes it a download.
    /// </summary>
    private Handler Cells(DataTable table) => (context, p, isLocal) =>
    {
        var fmt = p.Get("fmt") ?? "rows";
        string? filterField = null, filterValues = null;
        if (p.Get("filter") is { } filter)
        {
            var parts = filter.Split(['~', '#'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
            {
                filterField = parts[0].Trim();
                filterValues = parts[1].Trim();
            }
        }

        var userData = Fields(p.Get("data"), null);
        var columns = _service.Read(table, f => Fields(p.Get("cols"), f.Columns.ToArray())!);

        // SPEC.md D-7 / O-5: a birthdate already on file stays there but is
        // never served, here or in an export, and nor is a youth's phone
        // number (D-8); an adult's still is. Values are read through these
        // copies, where a withheld column's name is one that holds nothing;
        // `columns` keeps the real names for the CSV header, so every column
        // still lines up.
        var valueColumns = Withhold(table, columns)!;
        var valueUserData = Withhold(table, userData);

        if (!isLocal)
        {
            // A station needs names and units for the "who's here" lists, not
            // phones, emails or dates of birth.
            var asked = columns.Concat(userData ?? []).Append(filterField ?? "Last");
            if (fmt != "rows" || !asked.All(PublicListColumns.Contains))
            {
                return SendAsync(context, 403, "text/plain", "This is only available on the admin computer.");
            }
        }

        var sb = new StringBuilder();
        string contentType;
        _service.Read(table, file =>
        {
            var rows = file.AllRecords.Where(r =>
            {
                if (filterField == null || filterValues == null)
                {
                    return true;
                }

                // A filter on a withheld column matches nothing: the rows it
                // kept would say whose birthdate or phone number it is
                // (SPEC.md D-7, D-8). An adult's phone number filters as before.
                if (IsWithheld(table, filterField))
                {
                    return false;
                }

                var v = r.Get(filterField);
                return !string.IsNullOrEmpty(v) && filterValues.Contains(v, StringComparison.Ordinal);
            });

            WriteRows(sb, fmt, rows, columns, valueColumns, valueUserData);
            return 0;
        });

        contentType = fmt == "csv" ? "text/csv" : "text/xml";
        if (p.Get("filename") is { Length: > 0 } filename)
        {
            var safe = new string(filename.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_').ToArray());
            context.Response.Headers.ContentDisposition = "attachment;filename=" + safe;
        }

        return SendAsync(context, 200, contentType, sb.ToString());
    };

    /// <summary>
    /// Records as a <c>-cells</c> endpoint answers: the grid's rows (the
    /// default), a data view, or CSV under <paramref name="columns"/>' names.
    /// Values are read through <paramref name="valueColumns"/>, where a
    /// withheld column is one that holds nothing.
    /// </summary>
    private static void WriteRows(StringBuilder sb, string fmt, IEnumerable<Core.Records.DataRecord> rows, string[] columns, string[] valueColumns, string[]? valueUserData,
        string rowsAttributes = "")
    {
        switch (fmt)
        {
            case "data":
                sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?><data>");
                foreach (var r in rows)
                {
                    r.ToDataView(sb, valueColumns, valueUserData);
                    sb.Append('\n');
                }

                sb.Append("</data>");
                break;
            case "csv":
                sb.AppendJoin(',', columns).Append('\n');
                foreach (var r in rows)
                {
                    r.ToCsv(sb, ',', valueColumns);
                    sb.Append('\n');
                }

                break;
            default:
                sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?><rows").Append(rowsAttributes).Append('>');
                foreach (var r in rows)
                {
                    r.ToCells(sb, valueColumns, valueUserData);
                    sb.Append('\n');
                }

                sb.Append("</rows>");
                break;
        }
    }

    /// <summary>
    /// SPEC.md D-22: the proposals approved at the earlier events in this
    /// data folder, answered as <c>/youth-cells</c> is, with only the
    /// columns the Approved proposals page shows (<see cref="ApprovalRecord"/>),
    /// whatever <c>cols</c> asks for: any other is served empty. As in the
    /// Java version, the <c>rows</c> element says which events were read
    /// (<c>read</c>, and <c>from</c> and <c>to</c> when any were) and names
    /// any that couldn't be (<c>unreadable</c>, "|"-separated "date: why"),
    /// for the line above the Java admin tab's list (event test section 28).
    /// </summary>
    private Task ApprovedProposalsCells(HttpContext context, IReadOnlyDictionary<string, string> p, bool isLocal)
    {
        var fmt = p.Get("fmt") ?? "rows";
        string[]? Shown(string[]? columns) => columns?.Select(c => ApprovalRecord.AllColumns.Contains(c) ? c : c + " (withheld)").ToArray();
        var columns = Fields(p.Get("cols"), [.. ApprovalRecord.AllColumns])!;
        var read = _service.ReadApprovedProposals();
        var attributes = new StringBuilder(" read=\"").Append(read.Events.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('"');
        if (read.Events.Count > 0)
        {
            attributes.Append(" from=\"").Append(read.Events[0]).Append("\" to=\"").Append(read.Events[read.Events.Count - 1]).Append('"');
        }

        attributes.Append(" unreadable=\"");
        Core.Records.DataRecord.AppendEscaped(attributes, string.Join("|", read.Unreadable.Select(u => u.Event + ": " + u.Problem)));
        attributes.Append('"');
        var sb = new StringBuilder();
        WriteRows(sb, fmt, read.Approvals, columns, Shown(columns)!, Shown(Fields(p.Get("data"), null)), attributes.ToString());
        return SendAsync(context, 200, fmt == "csv" ? "text/csv" : "text/xml", sb.ToString());
    }

    /// <summary>
    /// The adult sign-in page's "I'm here supporting" list, in the same
    /// rows format as the other lists: First, Last, UnitName for each RSVP
    /// and walk-in not yet finished (<see cref="BoardService.ScoutChoices"/>).
    /// Its own endpoint because the RSVP list is otherwise admin-only.
    /// </summary>
    private Task ScoutChoices(HttpContext context, IReadOnlyDictionary<string, string> p, bool isLocal)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><rows>");
        foreach (var (id, first, last, unitName) in _service.ScoutChoices())
        {
            sb.Append("<row id=\"").Append(SecurityElement.Escape(id)).Append("\">");
            foreach (var value in new[] { first, last, unitName })
            {
                sb.Append("<cell>").Append(SecurityElement.Escape(value)).Append("</cell>");
            }

            sb.Append("</row>\n");
        }

        sb.Append("</rows>");
        return SendAsync(context, 200, "text/xml", sb.ToString());
    }

    // ------------------------------------------------------------------
    // The shared check-in pages' API (SPEC.md D-18)
    // ------------------------------------------------------------------

    // No phone number for a youth (SPEC.md D-8); an adult's is still filled in.
    private static readonly string[] YouthPrefill = ["ID", "Last", "First", "UnitType", "Unit", "BoardType", "Leader"];
    private static readonly string[] AdultPrefill = ["ID", "Last", "First", "Phone", "UnitType", "Unit", "FinalBoard", "ProjectReview"];

    /// <summary>
    /// An email as the lookups compare it: trimmed, any case; NONE and blank
    /// match nobody. The Mac version's matchableEmail.
    /// </summary>
    private static string? Matchable(string? email)
    {
        var cleaned = (email ?? "").Trim().ToLowerInvariant();
        return cleaned.Length == 0 || cleaned == "none" ? null : cleaned;
    }

    /// <summary>Utf8JsonWriter rather than the serializer: no reflection, so it survives trimming.</summary>
    private static Task SendJsonAsync(HttpContext context, Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            write(json);
        }

        context.Response.Headers.CacheControl = "no-store";
        return SendAsync(context, 200, "application/json; charset=utf-8", Encoding.UTF8.GetString(buffer.ToArray()));
    }

    /// <summary>Who has signed in, in sign-in order: names and units, and the youth's sign-in time.</summary>
    private Task ApiCheckedIn(HttpContext context, IReadOnlyDictionary<string, string> p, bool isLocal)
    {
        var refresh = _service.Read(DataTable.Config, f => f.AllRecords.FirstOrDefault()?.GetValue("RefreshTimeSecs"));
        var youth = _service.Read(DataTable.Scouts, f => f.AllRecords
            .Select(r => (Time: r.GetValue("RegTimeHM"), Last: r.GetValue("Last"), First: r.GetValue("First"),
                UnitType: r.GetValue("UnitType"), Unit: r.GetValue("Unit")))
            .ToList());
        var adults = _service.Read(DataTable.Adults, f => f.AllRecords
            .Select(r => (Last: r.GetValue("Last"), First: r.GetValue("First"), UnitType: r.GetValue("UnitType"), Unit: r.GetValue("Unit")))
            .ToList());
        return SendJsonAsync(context, json =>
        {
            json.WriteStartObject();
            json.WriteNumber("refreshSeconds", int.TryParse(refresh, out var seconds) && seconds > 0 ? seconds : 30);
            json.WriteStartArray("youth");
            foreach (var y in youth)
            {
                json.WriteStartObject();
                json.WriteString("time", y.Time);
                json.WriteString("last", y.Last);
                json.WriteString("first", y.First);
                json.WriteString("unitType", y.UnitType);
                json.WriteString("unit", y.Unit);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteStartArray("adults");
            foreach (var a in adults)
            {
                json.WriteStartObject();
                json.WriteString("last", a.Last);
                json.WriteString("first", a.First);
                json.WriteString("unitType", a.UnitType);
                json.WriteString("unit", a.Unit);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        });
    }

    /// <summary>The youth an adult may say they will introduce (<see cref="BoardService.ScoutChoiceUnits"/>).</summary>
    private Task ApiScoutChoices(HttpContext context, IReadOnlyDictionary<string, string> p, bool isLocal)
    {
        var choices = _service.ScoutChoiceUnits();
        return SendJsonAsync(context, json =>
        {
            json.WriteStartArray();
            foreach (var (id, first, last, unitType, unit) in choices)
            {
                json.WriteStartObject();
                json.WriteString("id", id);
                json.WriteString("first", first);
                json.WriteString("last", last);
                json.WriteString("unitType", unitType);
                json.WriteString("unit", unit);
                json.WriteEndObject();
            }

            json.WriteEndArray();
        });
    }

    /// <summary>
    /// The record an email matches, as the fields its form fills in, or {}.
    /// A POST, so the address never lands in a URL on a shared tablet.
    /// </summary>
    private Handler ApiLookup(DataTable table, string[] columns) => (context, p, _) =>
    {
        var wanted = Matchable(p.Get("email"));
        var match = wanted == null
            ? null
            : _service.Read<List<(string Column, string Value)>?>(table, f =>
                f.AllRecords.FirstOrDefault(r => Matchable(r.GetValue("Email")) == wanted) is { } record
                    ? columns.Select(c => (Column: c, Value: record.GetValue(c))).ToList()
                    : null);
        return SendJsonAsync(context, json =>
        {
            json.WriteStartObject();
            if (match != null)
            {
                foreach (var (column, value) in match)
                {
                    json.WriteString(column, value);
                }
            }

            json.WriteEndObject();
        });
    };

    /// <summary>
    /// Insert, update or delete a record: <c>!nativeeditor_status</c>,
    /// <c>gr_id</c> and the column values. Answers with the dhtmlx
    /// data-processor XML the old grids expected; a refused edit's action
    /// holds the reason as its text, in words for the operator, as the Java
    /// version's does (SPEC.md P-6).
    /// </summary>
    private Handler Update(DataTable table) => (context, p, _) =>
    {
        var status = p.Get("!nativeeditor_status");
        var id = p.Get("gr_id");
        var action = _service.SaveRow(table, status, id, p, out var refusal, undoable: true);
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><data><action type=\"");
        Core.Records.DataRecord.AppendEscaped(sb, action);
        sb.Append("\" sid=\"");
        Core.Records.DataRecord.AppendEscaped(sb, id ?? "null");
        sb.Append("\" tid=\"");
        Core.Records.DataRecord.AppendEscaped(sb, id ?? "null");
        if (refusal == null)
        {
            sb.Append("\" /></data>");
        }
        else
        {
            sb.Append("\">");
            Core.Records.DataRecord.AppendEscaped(sb, refusal);
            sb.Append("</action></data>");
        }

        return SendAsync(context, 200, "text/xml", sb.ToString());
    };

    /// <summary>
    /// Autofill for the sign-in forms. With no lookup value (or <c>op=list</c>)
    /// the list of known values; with one, the first matching record as XML
    /// (or pseudo-JSON with <c>fmt=json</c>).
    /// </summary>
    private Handler AutoFill(DataTable table, string lookupField) => (context, p, _) =>
    {
        var value = p.Get(lookupField);
        var sb = new StringBuilder();
        var contentType = "text/json";
        _service.Read(table, file =>
        {
            if (p.Get("op") == "list" || value == null)
            {
                sb.Append("{ options: [\n");
                var first = true;
                foreach (var r in file.AllRecords)
                {
                    var v = r.GetValue(lookupField);
                    if (v.Length > 4)
                    {
                        if (!first)
                        {
                            sb.Append(",\n");
                        }

                        first = false;
                        sb.Append("   { value: \"").Append(v).Append("\", text:\"").Append(v).Append("\"}");
                    }
                }

                sb.Append("\n]\n}\n");
            }
            else if (p.Get("fmt") == "json")
            {
                file.FindWhere(lookupField, value).FirstOrDefault()?.ToLooseJson(sb, PrefillColumns(table, file.Columns));
            }
            else
            {
                contentType = "text/xml";
                sb.Append("<data>");
                var match = file.FindWhere(lookupField, value).FirstOrDefault();
                if (match != null)
                {
                    sb.AppendJoin('\n', PrefillColumns(table, file.Columns).Select(c =>
                    {
                        var cell = new StringBuilder();
                        Core.Records.DataRecord.AppendEscaped(cell, match.GetValue(c));
                        return $"<{c}>{cell}</{c}>";
                    }));
                }

                sb.Append("</data>");
            }

            return 0;
        });

        return SendAsync(context, 200, contentType, sb.ToString());
    };

    /// <summary>
    /// A column that <paramref name="table"/> never serves, though its file
    /// may hold one from an older event folder: the birthdate (SPEC.md D-7,
    /// O-5) and, for the youth tables, the phone number (D-8). An adult's
    /// phone number is served as before.
    /// </summary>
    private static bool IsWithheld(DataTable table, string column) =>
        column == Core.Records.ScoutRecord.DobField
        || (column == Core.Records.ScoutRecord.PhoneField && table is (DataTable.Scouts or DataTable.ScoutsScheduled));

    /// <summary>The columns to read values through: a withheld one is renamed ("Phone (withheld)"), a column that holds nothing.</summary>
    private static string[]? Withhold(DataTable table, string[]? columns) =>
        columns?.Select(c => IsWithheld(table, c) ? c + " (withheld)" : c).ToArray();

    /// <summary>What a pre-fill sends back: every column but the withheld ones.</summary>
    private static IEnumerable<string> PrefillColumns(DataTable table, IEnumerable<string> columns) =>
        columns.Where(c => !IsWithheld(table, c));

    // ------------------------------------------------------------------
    // Static pages
    // ------------------------------------------------------------------

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["html"] = "text/html; charset=utf-8",
        ["htm"] = "text/html; charset=utf-8",
        ["css"] = "text/css; charset=utf-8",
        ["js"] = "text/javascript; charset=utf-8",
        ["csv"] = "text/csv",
        ["xml"] = "text/xml",
        ["png"] = "image/png",
        ["gif"] = "image/gif",
        ["jpg"] = "image/jpeg",
        ["ico"] = "image/x-icon",
        ["json"] = "application/json",
    };

    private static Dictionary<string, byte[]> LoadAssets()
    {
        var assembly = typeof(CheckInServer).Assembly;
        var assets = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("wwwroot/", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            assets[name["wwwroot/".Length..]] = copy.ToArray();
        }

        return assets;
    }

    /// <summary>"/" is index.html; "/youth_register" finds youth_register.html.</summary>
    private bool TryServeAsset(HttpContext context, string path, out Task task)
    {
        var name = path.TrimStart('/');
        if (name.Length == 0)
        {
            name = "index.html";
        }

        if (!_assets.ContainsKey(name) && _assets.ContainsKey(name + ".html"))
        {
            name += ".html";
        }

        if (!_assets.TryGetValue(name, out var bytes))
        {
            task = Task.CompletedTask;
            return false;
        }

        var ext = Path.GetExtension(name).TrimStart('.');
        context.Response.StatusCode = 200;
        context.Response.ContentType = ContentTypes.GetValueOrDefault(ext, "text/plain");
        context.Response.ContentLength = bytes.Length;
        task = context.Response.Body.WriteAsync(bytes, context.RequestAborted).AsTask();
        return true;
    }

    // ------------------------------------------------------------------

    private static Task SendAsync(HttpContext context, int status, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.StatusCode = status;
        context.Response.ContentType = contentType;
        context.Response.ContentLength = bytes.Length;
        return context.Response.Body.WriteAsync(bytes, context.RequestAborted).AsTask();
    }

    /// <summary>A column list as the Java StringTokenizer read it: split on , + space [ ].</summary>
    private static string[]? Fields(string? list, string[]? fallback)
    {
        if (string.IsNullOrEmpty(list))
        {
            return fallback;
        }

        return list.Split([',', '+', ' ', '[', ']'], StringSplitOptions.RemoveEmptyEntries);
    }
}

internal static class ParameterExtensions
{
    public static string? Get(this IReadOnlyDictionary<string, string> p, string key) => p.TryGetValue(key, out var v) ? v : null;
}
