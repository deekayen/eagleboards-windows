using System.Globalization;
using System.Text;
using EagleBoards.Core.Import;
using EagleBoards.Core.Records;
using EagleBoards.Core.Storage;

namespace EagleBoards.Core;

public enum DataTable
{
    Scouts,
    ScoutsScheduled,
    Adults,
    AdultHistory,
    Rooms,
    Config,
}

/// <summary>Outcome of a board action. Success carries "OK.", the wire's success text.</summary>
public sealed record ActionResult(bool Ok, string Message)
{
    public static ActionResult Success { get; } = new(true, "OK.");

    public static ActionResult Error(string message) => new(false, message);
}

public sealed class DataChangedEventArgs(IReadOnlyCollection<DataTable> tables) : EventArgs
{
    public IReadOnlyCollection<DataTable> Tables { get; } = tables;
}

/// <summary>Where tonight's data lives and what to pre-load.</summary>
public sealed class EventOptions
{
    /// <summary>Tonight's folder, conventionally named YYYY-MM-DD. Created if missing.</summary>
    public required string DataDirectory { get; init; }

    /// <summary>
    /// The cumulative adult history (Master_AdultHistory.csv). When given it
    /// must exist; when null a per-night adult_history.csv is used.
    /// </summary>
    public string? AdultHistoryPath { get; init; }

    /// <summary>
    /// config.properties. When given it must exist; when null the data folder's
    /// copy is used if present, else config.properties in the working folder.
    /// </summary>
    public string? ConfigPath { get; init; }

    /// <summary>Optional district-website pre-registration CSV.</summary>
    public string? PreRegistrationPath { get; init; }
}

/// <summary>
/// Everything an event night does to its data: check-in, the board
/// lifecycle, rooms, settings and the generic record edits behind the Admin
/// tables. The check-in website and the Windows admin app both go through
/// here, so a rule enforced here holds however the request arrives.
///
/// Ported from the handler classes of the Java <c>EagleBoardScheduler</c>.
/// Every operation runs under one lock and stores what it changed before
/// returning, then raises <see cref="Changed"/> (outside the lock).
/// </summary>
public sealed class BoardService
{
    // What a youth's repeat sign-in refreshes. Not DOB or Phone: neither is
    // taken any more (SPEC.md D-7, D-8), and one already on file stays (O-5).
    private static readonly string[] ScoutRegFields = ["First", "Last", "Unit", "UnitType", "Email", "Leader"];
    private static readonly string[] AdultRegFields = ["First", "Last", "Unit", "UnitType", "Email", "Phone", "ProjectReview", "FinalBoard"];

    /// <summary>How to reverse one change: every record it touched, and what it set on each.</summary>
    private sealed record UndoEntry(string Description, IReadOnlyList<Touched> Records);

    private const int MaxUndoEntries = 50;

    /// <summary>
    /// The operator's in-progress pick. Clicking around never clears it, so
    /// it neither stops an undo nor is put back over a newer pick.
    /// </summary>
    private const string SelField = "Sel";

    private readonly Lock _lock = new();
    private readonly Action<string> _log;
    private readonly List<UndoEntry> _undo = [];

    /// <summary>Whether the last thing on the stack to change was an undo, for <see cref="RestoreBoard"/>.</summary>
    private bool _lastWasUndo;

    private BoardService(EventOptions options, Action<string> log, string adultHistoryPath, string configPath)
    {
        Options = options;
        _log = log;
        var dir = options.DataDirectory;
        Config = new DataRecordFile<ConfigRecord>(configPath, ConfigRecord.Factory);
        Scouts = new DataRecordFile<ScoutRecord>(Path.Combine(dir, "scouts.csv"), ScoutRecord.Factory);
        Adults = new DataRecordFile<AdultRecord>(Path.Combine(dir, "adults.csv"), AdultRecord.Factory);
        Rooms = new DataRecordFile<RoomRecord>(Path.Combine(dir, "rooms.csv"), RoomRecord.Factory);
        AdultHistory = new DataRecordFile<AdultRecord>(adultHistoryPath, AdultRecord.Factory);
        ScoutsScheduled = new DataRecordFile<ScoutRecord>(Path.Combine(dir, "scouts_scheduled.csv"), ScoutRecord.Factory);
    }

    public EventOptions Options { get; }

    internal DataRecordFile<ConfigRecord> Config { get; }

    internal DataRecordFile<ScoutRecord> Scouts { get; }

    internal DataRecordFile<AdultRecord> Adults { get; }

    internal DataRecordFile<RoomRecord> Rooms { get; }

    internal DataRecordFile<AdultRecord> AdultHistory { get; }

    internal DataRecordFile<ScoutRecord> ScoutsScheduled { get; }

    /// <summary>Raised after any change is stored, on the thread that made it.</summary>
    public event EventHandler<DataChangedEventArgs>? Changed;

    public bool Verbose { get; set; }

    // ------------------------------------------------------------------
    // Undo
    // ------------------------------------------------------------------

    /// <summary>Whether <see cref="Undo"/> has anything to reverse.</summary>
    public bool CanUndo
    {
        get { lock (_lock) return _undo.Count > 0; }
    }

    /// <summary>What <see cref="Undo"/> would reverse, for the button's label. Null when there's nothing.</summary>
    public string? UndoDescription
    {
        get { lock (_lock) return _undo.Count > 0 ? _undo[^1].Description : null; }
    }

    /// <summary>
    /// Reverse the most recent board step, room change, Disable/Enable or
    /// Link/Unlink. Each of those operations records every field it set and
    /// what it was before; undoing puts those fields back and re-stores the
    /// affected files, leaving everything else on the records as it is now.
    /// Picks (<c>Sel</c>) and Admin-window edits are not on this stack: the
    /// operator's picks survive clicking around by design.
    ///
    /// Refused, as the Java version's restore is, when anything else (a
    /// correction on a table page, a door sign-in) has changed one of
    /// those fields since: putting the old value back would silently undo
    /// that change too. The whole stack is then let go, since everything
    /// older sits behind the change that can't be taken back.
    /// </summary>
    public ActionResult Undo()
    {
        ActionResult result;
        DataTable[] tables = [];
        lock (_lock)
        {
            result = UndoLocked(out tables);
        }

        if (result.Ok)
        {
            OnChanged(tables);
        }

        return result;
    }

    /// <summary>
    /// <c>/restore-board</c>: the Java version's Undo, which holds a single
    /// action (SPEC.md O-2). It takes back the most recent reversible action,
    /// once; asked again, there is nothing to undo until something else is
    /// done. The window's <see cref="Undo"/> goes further back.
    /// </summary>
    public ActionResult RestoreBoard()
    {
        ActionResult result;
        DataTable[] tables = [];
        lock (_lock)
        {
            result = _lastWasUndo ? ActionResult.Error("Nothing to undo.") : UndoLocked(out tables);
        }

        if (result.Ok)
        {
            OnChanged(tables);
        }

        return result;
    }

    private ActionResult UndoLocked(out DataTable[] tables)
    {
        tables = [];
        if (_undo.Count == 0)
        {
            return ActionResult.Error("Nothing to undo.");
        }

        var entry = _undo[^1];
        foreach (var touched in entry.Records)
        {
            if (!ReferenceEquals(Table(touched.Table).Find(touched.Record.Id), touched.Record))
            {
                _undo.Clear();
                return ActionResult.Error($"{touched.Name} is no longer on the list, so this can't be undone automatically.");
            }

            if (touched.Set.Any(f => f.Key != SelField && touched.Record.Fields.GetValueOrDefault(f.Key, "") != f.Value))
            {
                _undo.Clear();
                return ActionResult.Error($"{touched.Name} has been changed since, so this can't be undone automatically.");
            }
        }

        _undo.RemoveAt(_undo.Count - 1);
        foreach (var touched in entry.Records)
        {
            foreach (var (field, value) in touched.Set)
            {
                if (touched.Record.Fields.GetValueOrDefault(field, "") == value)
                {
                    touched.Record.Put(field, touched.Before.GetValueOrDefault(field, ""));
                }
            }
        }

        tables = entry.Records.Select(t => t.Table).Distinct().ToArray();
        foreach (var table in tables)
        {
            Table(table).Store();
        }

        _lastWasUndo = true;
        return ActionResult.Success;
    }

    /// <summary>A record as an undoable change found it. <see cref="PushUndo"/> adds what the change set.</summary>
    private sealed class Touched(DataRecord record)
    {
        public DataRecord Record { get; } = record;

        /// <summary>Only an event's youth, adults and rooms are changed by anything Undo covers.</summary>
        public DataTable Table { get; } = record switch
        {
            ScoutRecord => DataTable.Scouts,
            AdultRecord => DataTable.Adults,
            RoomRecord => DataTable.Rooms,
            _ => throw new ArgumentException("Undo covers youth, adults and rooms only.", nameof(record)),
        };

        public Dictionary<string, string> Before { get; } = new(record.Fields, StringComparer.Ordinal);

        /// <summary>Each field the change set, and the value it set.</summary>
        public Dictionary<string, string> Set { get; } = new(StringComparer.Ordinal);

        public string Name => Record switch
        {
            PersonRecord person => person.FullName,
            RoomRecord room => "Room " + room.Room,
            _ => Record.Id,
        };
    }

    /// <summary>A record as it is just before an undoable change.</summary>
    private static Touched Snap(DataRecord record) => new(record);

    /// <summary>
    /// Remember how to reverse a change. Call under <see cref="_lock"/>,
    /// after the change has succeeded, with each record it touched as
    /// <see cref="Snap"/> found it beforehand (null for a room the board had
    /// lost). Nothing is remembered for a change that set nothing but picks.
    /// </summary>
    private void PushUndo(string description, IEnumerable<Touched?> records)
    {
        var changed = new List<Touched>();
        foreach (var touched in records.OfType<Touched>())
        {
            foreach (var (field, value) in touched.Record.Fields)
            {
                if (touched.Before.GetValueOrDefault(field, "") != value)
                {
                    touched.Set[field] = value;
                }
            }

            if (touched.Set.Count > 0)
            {
                changed.Add(touched);
            }
        }

        if (!changed.Any(t => t.Set.Keys.Any(f => f != SelField)))
        {
            return;
        }

        _undo.Add(new UndoEntry(description, changed));
        _lastWasUndo = false;
        if (_undo.Count > MaxUndoEntries)
        {
            _undo.RemoveAt(0);
        }
    }

    /// <summary>
    /// Open (creating where missing) tonight's files. A missing adult history
    /// is started empty (SPEC.md D-9); throws if an explicitly named config
    /// file does not exist.
    /// </summary>
    public static BoardService Open(EventOptions options, Action<string>? log = null)
    {
        log ??= _ => { };
        Directory.CreateDirectory(options.DataDirectory);

        var adultHistory = options.AdultHistoryPath ?? Path.Combine(options.DataDirectory, "adult_history.csv");

        // SPEC.md D-9: a new install has no adult history, and no release ships
        // one (it would hold participant data), so a missing one is started
        // with just the header row, as the startup window does, rather than
        // refused. The full path is logged, so a mistyped -a shows up as a new
        // history in an unexpected place.
        if (!File.Exists(adultHistory))
        {
            var fullPath = Path.GetFullPath(adultHistory);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, string.Join(',', AdultRecord.AllColumns) + "\n");
            log($"\n   Started a new, empty adult history: {fullPath}\n");
        }

        string config;
        if (options.ConfigPath != null)
        {
            if (!File.Exists(options.ConfigPath))
            {
                throw new FileNotFoundException($"error: config file '{options.ConfigPath}' does not exist.");
            }

            config = options.ConfigPath;
        }
        else
        {
            config = Path.Combine(options.DataDirectory, "config.properties");
            if (!File.Exists(config))
            {
                config = "config.properties";
            }
        }

        var service = new BoardService(options, log, adultHistory, config);
        if (service.Config.Get(ConfigRecord.DefaultId) == null)
        {
            var record = service.Config.AddNew(ConfigRecord.DefaultId, false);
            record.SetValue("Name", ConfigRecord.DefaultId);
            service.Config.Store();
        }

        if (options.PreRegistrationPath != null)
        {
            service.ImportPreRegistrations(options.PreRegistrationPath);
        }

        return service;
    }

    // ------------------------------------------------------------------
    // Reading
    // ------------------------------------------------------------------

    public IDataRecordFile Table(DataTable table) => table switch
    {
        DataTable.Scouts => Scouts,
        DataTable.ScoutsScheduled => ScoutsScheduled,
        DataTable.Adults => Adults,
        DataTable.AdultHistory => AdultHistory,
        DataTable.Rooms => Rooms,
        DataTable.Config => Config,
        _ => throw new ArgumentOutOfRangeException(nameof(table)),
    };

    /// <summary>Run <paramref name="read"/> against a table while holding the lock.</summary>
    public TResult Read<TResult>(DataTable table, Func<IDataRecordFile, TResult> read)
    {
        lock (_lock)
        {
            return read(Table(table));
        }
    }

    /// <summary>Copies of every record in a table, computed fields resolved.</summary>
    public List<Dictionary<string, string>> Snapshot(DataTable table)
    {
        lock (_lock)
        {
            return Table(table).AllRecords.Select(r => r.Snapshot()).ToList();
        }
    }

    /// <summary>
    /// A table as CSV with the given columns, written the way the data files
    /// are (commas in values become ~). Used for the Report and Admin exports.
    /// </summary>
    public string ExportCsv(DataTable table, IReadOnlyList<string> columns, Func<DataRecord, bool>? include = null)
    {
        lock (_lock)
        {
            var sb = new StringBuilder();
            sb.AppendJoin(',', columns).Append('\n');
            foreach (var record in Table(table).AllRecords)
            {
                if (include == null || include(record))
                {
                    record.ToCsv(sb, ',', columns);
                    sb.Append('\n');
                }
            }

            return sb.ToString();
        }
    }

    /// <summary>A detached copy of the settings.</summary>
    public ConfigRecord GetConfig()
    {
        lock (_lock)
        {
            var record = Config.Get(ConfigRecord.DefaultId) ?? Config.Records.FirstOrDefault();
            return new ConfigRecord(record?.Fields);
        }
    }

    // ------------------------------------------------------------------
    // Check-in (the website's sign-in pages)
    // ------------------------------------------------------------------

    /// <summary>
    /// A youth signs in. Matched to a pre-registration (by ID, or by email)
    /// they get a "P" number, otherwise they are a walk-in and get "W"; each
    /// counter runs in sign-in order within its group.
    /// </summary>
    public void RegisterScout(IEnumerable<KeyValuePair<string, string>> fields)
    {
        lock (_lock)
        {
            int pre = 0, walkIn = 0;
            foreach (var s in Scouts.Records)
            {
                if (s.RegNum.StartsWith('P'))
                {
                    pre++;
                }
                else if (s.RegNum.StartsWith('W'))
                {
                    walkIn++;
                }
            }

            // A sign-in page cached from before SPEC.md D-7 still sends a
            // birthdate, and one from before D-8 a phone number. Nothing uses
            // either, so they are dropped here, never stored; one already on
            // the record is left as it is (O-5, D-8).
            var incoming = new ScoutRecord(fields.Where(f => f.Key is not (ScoutRecord.DobField or ScoutRecord.PhoneField)));
            var scout = Scouts.Get(incoming.Id);
            if (scout != null)
            {
                Trace("UPDATING EXISTING SCOUT RECORD: " + scout);
                scout.UpdateFrom(incoming, ScoutRegFields);
                scout.UpdateFields(false);
                if (scout.Status.Length == 0)
                {
                    scout.Status = BoardStatus.Registered;
                }
            }
            else
            {
                Trace("NEW SCOUT RECORD: " + incoming);
                scout = incoming;
                Scouts.Add(scout, false);
                scout.Status = BoardStatus.Registered;
            }

            // An empty email matches nothing: a walk-in who left it blank is
            // not "pre-registered" just because some sign-up also has none.
            var preRegistered = ScoutsScheduled.Get(scout.Id) != null
                || (scout.Email.Length > 0 && ScoutsScheduled.Where("Email", scout.Email).Count > 0);
            var prefix = preRegistered ? 'P' : 'W';

            // Someone who submits the form twice keeps their place in the
            // queue rather than being renumbered to the back of it.
            if (!scout.RegNum.StartsWith(prefix))
            {
                scout.RegNum = prefix + ((preRegistered ? pre : walkIn) + 1).ToString(CultureInfo.InvariantCulture);
            }

            Scouts.Store();
        }

        OnChanged(DataTable.Scouts);
    }

    /// <summary>
    /// An adult signs in. They are added to tonight's list and merged into
    /// the cumulative history, whose board-history column gains tonight's
    /// date. Flags record whether they were already known ("P") or new ("W").
    /// </summary>
    public void RegisterAdult(IEnumerable<KeyValuePair<string, string>> fields)
    {
        lock (_lock)
        {
            var incoming = new AdultRecord(fields) { Room = "" };
            incoming.WoodBadge = incoming.WoodBadge == "Y" ? "Y" : "";
            incoming.Supporting = incoming.Supporting.Trim();
            var adult = Adults.Get(incoming.Id);
            if (adult != null)
            {
                adult.UpdateFrom(incoming, AdultRegFields);
                // Tonight-only answers: the latest sign-in says what is true now.
                adult.WoodBadge = incoming.WoodBadge;
                adult.Supporting = incoming.Supporting;
                adult.UpdateFields(false);
            }
            else
            {
                Trace("NEW ADULT RECORD: " + incoming);
                adult = incoming;
                Adults.Add(adult, false);
            }

            var history = AdultHistory.Get(adult.Id);
            if (history == null)
            {
                history = adult.Clone();

                // The history pre-fills next month's form; whom someone came to
                // support, and whether it counted toward Wood Badge, are for
                // tonight only.
                history.WoodBadge = "";
                history.Supporting = "";
                Trace("Adding new Adult History Record: " + history);
                AdultHistory.Add(history, false);
                adult.Flags = "W";
            }
            else
            {
                history.UpdateFrom(adult, AdultRegFields);
                history.UpdateFields(false);
                adult.Flags = "P";
            }

            history.BoardHistory += "(" + DataRecord.Clock().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ")";
            Adults.Store();
            AdultHistory.Store();
        }

        OnChanged(DataTable.Adults, DataTable.AdultHistory);
    }

    /// <summary>
    /// Link an adult to a scout as someone who came to support them, or
    /// unlink them: the scheduler's Link button, for the adult who did not
    /// check the scout at sign-in. Writes the same Supporting column.
    /// </summary>
    public ActionResult SetSupporting(string adultId, string scoutId, bool linked)
    {
        ActionResult result;
        lock (_lock)
        {
            var adult = Adults.Get(adultId);
            if (adult == null)
            {
                result = ActionResult.Error("There is no adult " + adultId);
            }
            else if (linked && Scouts.Get(scoutId) == null && ScoutsScheduled.Get(scoutId) == null)
            {
                result = ActionResult.Error("There is no youth " + scoutId);
            }
            else
            {
                var adultSnap = Snap(adult);
                adult.Supporting = SchedulerLogic.WithSupportLink(adult.Supporting, scoutId, linked);
                Adults.Store();
                PushUndo(linked ? $"linking {adult.FullName}" : $"unlinking {adult.FullName}", [adultSnap]);
                result = ActionResult.Success;
            }
        }

        if (result.Ok)
        {
            OnChanged(DataTable.Adults);
        }

        return result;
    }

    /// <summary>An adult has gone home: freed from any pick, unavailable for the rest of the event.</summary>
    public ActionResult DisableAdult(string? adultId)
    {
        ActionResult result;
        lock (_lock)
        {
            var adult = Adults.Get(adultId);
            if (adult == null)
            {
                result = ActionResult.Error("ERROR: Invalid Adult ID" + adultId);
            }
            else if (adult.Room.Length > 0)
            {
                result = ActionResult.Error("ERROR: " + adult.FullName + " is on a board or already gone home");
            }
            else
            {
                var adultSnap = Snap(adult);
                adult.Room = AdultRoom.Disabled;
                adult.Sel = "0";
                Adults.Store();
                PushUndo($"marking {adult.FullName} gone home", [adultSnap]);
                result = ActionResult.Success;
            }
        }

        if (result.Ok)
        {
            OnChanged(DataTable.Adults);
        }

        return result;
    }

    /// <summary>An adult who had gone home is back and available again.</summary>
    public ActionResult EnableAdult(string? adultId)
    {
        ActionResult result;
        lock (_lock)
        {
            var adult = Adults.Get(adultId);
            if (adult == null)
            {
                result = ActionResult.Error("ERROR: Invalid Adult ID" + adultId);
            }
            else if (adult.Room != AdultRoom.Disabled)
            {
                result = ActionResult.Error("ERROR: " + adult.FullName + " isn't marked gone home");
            }
            else
            {
                var adultSnap = Snap(adult);
                adult.Room = "";
                Adults.Store();
                PushUndo($"marking {adult.FullName} back", [adultSnap]);
                result = ActionResult.Success;
            }
        }

        if (result.Ok)
        {
            OnChanged(DataTable.Adults);
        }

        return result;
    }

    /// <summary>
    /// The scouts an adult may say at sign-in they came to support: everyone
    /// who RSVP'd, plus tonight's walk-ins, leaving out anyone whose evening
    /// is over (Completed or Postponed). Names and units only -- this is read
    /// by the check-in stations, which see nothing more.
    /// </summary>
    public List<(string Id, string First, string Last, string UnitName)> ScoutChoices()
    {
        lock (_lock)
        {
            return ScoutChoiceRecords().Select(s => (s.Id, s.First, s.Last, s.UnitName)).ToList();
        }
    }

    /// <summary>
    /// The same youth with the unit type and number apart, for the shared
    /// check-in pages' <c>/api/scout-choices</c> (SPEC.md D-18).
    /// </summary>
    public List<(string Id, string First, string Last, string UnitType, string Unit)> ScoutChoiceUnits()
    {
        lock (_lock)
        {
            return ScoutChoiceRecords().Select(s => (s.Id, s.First, s.Last, s.UnitType, s.Unit)).ToList();
        }
    }

    /// <summary>Only called under _lock.</summary>
    private List<ScoutRecord> ScoutChoiceRecords()
    {
        var done = Scouts.Records
            .Where(s => s.Status is BoardStatus.Completed or BoardStatus.Postponed)
            .Select(s => s.Id)
            .ToHashSet();
        return ScoutsScheduled.Records.Concat(Scouts.Records)
            .Where(s => s.Last.Length > 0 && !done.Contains(s.Id))
            .GroupBy(s => s.Id)
            .Select(g => g.Last())
            .OrderBy(s => s.Last, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(s => s.First, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    // ------------------------------------------------------------------
    // Board lifecycle
    // ------------------------------------------------------------------

    /// <summary>
    /// Registered → Seated. The members get the room and the paperwork; the
    /// scout stays outside until <see cref="StartReview"/> (GTA 8.0.3.0 #8).
    /// Enforces the composition rules the scheduler window also warns about,
    /// because the window is not the only way in.
    /// </summary>
    public ActionResult SeatBoard(string? roomId, string? scoutId, string? chairId, string? memberIds)
    {
        ActionResult result;
        lock (_lock)
        {
            result = SeatBoardLocked(roomId, scoutId, chairId, memberIds);
        }

        if (result.Ok)
        {
            OnChanged(DataTable.Scouts, DataTable.Rooms, DataTable.Adults);
        }

        return result;
    }

    private ActionResult SeatBoardLocked(string? roomId, string? scoutId, string? chairId, string? memberIds)
    {
        Trace($"SeatBoard RoomID={roomId} ScoutID={scoutId} ChairID={chairId} MemberIDs={memberIds}");
        var room = Rooms.Get(roomId);
        if (room == null)
        {
            return ActionResult.Error("ERROR: Invalid Room ID" + roomId);
        }

        if (room.Scout.Length > 0)
        {
            return ActionResult.Error("ERROR: Room " + room.Room + " is already assigned to " + room.Scout);
        }

        var scout = Scouts.Get(scoutId);
        if (scout == null)
        {
            return ActionResult.Error("ERROR: Invalid Scout ID" + scoutId);
        }

        if (!BoardStatus.IsWaiting(scout.Status))
        {
            return ActionResult.Error("ERROR: Invalid Status '" + scout.Status + "', expected 'Registered'");
        }

        // "N/A" is what Complete leaves in a scout's Room. A Registered scout
        // holding it had a result recorded against them by mistake and was set
        // back to Registered on the Youth or Results page; they must be seatable.
        if (scout.Room != "" && scout.Room != AdultRoom.Disabled && scout.Room != room.Room)
        {
            return ActionResult.Error("ERROR: Scout Already Assigned Room: " + scout.Room);
        }

        if (string.IsNullOrWhiteSpace(memberIds))
        {
            return ActionResult.Error("ERROR: No board members selected");
        }

        if (CheckComposition(scout, memberIds, chairId, boardRoom: null) is not { } board)
        {
            return _lastRefusal!;
        }

        var (members, chair, names) = board;
        var leaders = names;
        var roomSnap = Snap(room);
        var scoutSnap = Snap(scout);
        var memberSnaps = members.Select(Snap).ToList();

        room.Scout = scout.FullName;
        room.Leaders = leaders;
        scout.Room = room.Room;
        scout.Status = BoardStatus.Seated;
        scout.BoardMembers = leaders;
        scout.BoardMemberIds = memberIds;
        scout.BoardChair = chair.FullName;
        scout.BoardChairId = chair.Id;
        foreach (var member in members)
        {
            member.Room = room.Room;

            // Their pick is used up. Left at "1", the scheduler would keep
            // reading them as the operator's chosen board for every scout
            // selected afterwards.
            member.Sel = "0";
        }

        scout.UpdateFields(true);
        Scouts.Store();
        Rooms.Store();
        Adults.Store();
        PushUndo($"seating {scout.FullName}'s board", [roomSnap, scoutSnap, .. memberSnaps]);
        return ActionResult.Success;
    }

    /// <summary>Seated → InProgress: the scout is brought in and the interview starts.</summary>
    public ActionResult StartReview(string? scoutId)
    {
        ActionResult result;
        lock (_lock)
        {
            var scout = Scouts.Get(scoutId);
            if (scout == null)
            {
                result = ActionResult.Error("ERROR: Invalid Scout ID" + scoutId);
            }
            else if (scout.Status != BoardStatus.Seated)
            {
                result = ActionResult.Error("ERROR: Invalid Status '" + scout.Status + "', expected 'Seated'");
            }
            else
            {
                var scoutSnap = Snap(scout);
                scout.Status = BoardStatus.InProgress;
                scout.UpdateFields(true);
                Scouts.Store();
                PushUndo($"starting {scout.FullName}'s review", [scoutSnap]);
                result = ActionResult.Success;
            }
        }

        if (result.Ok)
        {
            OnChanged(DataTable.Scouts);
        }

        return result;
    }

    /// <summary>
    /// InProgress → Completed, recording the result. Only InProgress: a
    /// Seated scout is still outside, and completing there would record a
    /// result for a review that never happened. Frees the room and members.
    /// </summary>
    public ActionResult CompleteBoard(string? scoutId, string? result, string? notes, string? cost = null, string? bsaHours = null, string? otherHours = null)
    {
        ActionResult outcome;
        lock (_lock)
        {
            outcome = CompleteBoardLocked(scoutId, result, notes, cost, bsaHours, otherHours);
        }

        if (outcome.Ok)
        {
            OnChanged(DataTable.Scouts, DataTable.Rooms, DataTable.Adults);
        }

        return outcome;
    }

    private ActionResult CompleteBoardLocked(string? scoutId, string? result, string? notes, string? cost, string? bsaHours, string? otherHours)
    {
        // Exactly the board's three decisions. This used to accept any string of
        // five or more characters, so "Maybe" or a typo went into the record.
        if (result == null || !BoardResults.All.Contains(result))
        {
            return ActionResult.Error("Invalid Result '" + result + "', expected Approved, Adjourned or NotApproved");
        }

        notes ??= "";
        if (cost != null)
        {
            notes += "(Cost: " + cost + ")";
        }

        if (bsaHours != null)
        {
            notes += "(BSA: " + bsaHours + " hrs)";
        }

        if (otherHours != null)
        {
            notes += "(Other: " + otherHours + " hrs)";
        }

        var scout = Scouts.Get(scoutId);
        if (scout == null)
        {
            return ActionResult.Error("Invalid Scout ID" + scoutId);
        }

        if (scout.Status != BoardStatus.InProgress)
        {
            return ActionResult.Error("Invalid Scout Status '" + scout.Status + "' expected 'InProgress'");
        }

        // The review happened even if its room was renamed or deleted on the
        // Admin window meanwhile, so the result is recorded and the adults are
        // released by the room name the scout holds. Refusing here used to lose
        // the result and leave every member committed to a vanished room.
        var room = FindBoardRoom(scout);
        var releasedAdults = ReleasableAdults(scout.Room);
        var scoutSnap = Snap(scout);
        var roomSnap = room != null ? Snap(room) : null;
        var adultSnaps = releasedAdults.Select(Snap).ToList();

        ReleaseAdults(scout.Room);
        scout.Status = BoardStatus.Completed;
        scout.Room = AdultRoom.Disabled;
        scout.Notes = notes;
        scout.Result = result;
        if (room != null)
        {
            room.Scout = "";
            room.Leaders = "";
        }
        scout.UpdateFields(true);
        Scouts.Store();
        Rooms.Store();
        Adults.Store();
        PushUndo($"completing {scout.FullName}'s board", [scoutSnap, roomSnap, .. adultSnaps]);
        return ActionResult.Success;
    }

    /// <summary>Registered → Postponed, usually a paperwork problem.</summary>
    public ActionResult PostponeBoard(string? scoutId)
    {
        ActionResult result;
        lock (_lock)
        {
            var scout = Scouts.Get(scoutId);
            if (scout == null)
            {
                result = ActionResult.Error("ERROR: Invalid Scout ID" + scoutId);
            }
            else if (!BoardStatus.IsWaiting(scout.Status))
            {
                result = ActionResult.Error("ERROR: Invalid Status '" + scout.Status + "', expected 'Registered | Verified'");
            }
            else
            {
                var scoutSnap = Snap(scout);
                scout.Status = BoardStatus.Postponed;
                scout.UpdateFields(true);
                Scouts.Store();
                PushUndo($"postponing {scout.FullName}'s board", [scoutSnap]);
                result = ActionResult.Success;
            }
        }

        if (result.Ok)
        {
            OnChanged(DataTable.Scouts);
        }

        return result;
    }

    /// <summary>Seated/InProgress → Registered, handing the room and members back.</summary>
    public ActionResult ResetBoard(string? scoutId)
    {
        ActionResult result;
        lock (_lock)
        {
            var scout = Scouts.Get(scoutId);
            if (scout == null)
            {
                result = ActionResult.Error("Invalid Scout ID" + scoutId);
            }
            else if (scout.Status is not (BoardStatus.InProgress or BoardStatus.Seated or BoardStatus.Verified))
            {
                result = ActionResult.Error("Invalid Scout Status '" + scout.Status + "' expected 'Verified', 'InProgress' or 'Seated'");
            }
            else
            {
                // Released by the scout's room name, as in Complete: a room renamed
                // or deleted under the board must not strand its members.
                var room = FindBoardRoom(scout);
                var releasedAdults = ReleasableAdults(scout.Room);
                var scoutSnap = Snap(scout);
                var roomSnap = room != null ? Snap(room) : null;
                var adultSnaps = releasedAdults.Select(Snap).ToList();

                ReleaseAdults(scout.Room);
                if (room != null)
                {
                    room.Scout = "";
                    room.Leaders = "";
                }

                scout.Status = BoardStatus.Registered;
                scout.Room = "";
                scout.BoardChair = "";
                scout.BoardChairId = "";
                scout.BoardMemberIds = "";
                scout.BoardMembers = "";
                scout.UpdateFields(true);
                Scouts.Store();
                Rooms.Store();
                Adults.Store();
                PushUndo($"resetting {scout.FullName}'s board", [scoutSnap, roomSnap, .. adultSnaps]);
                result = ActionResult.Success;
            }
        }

        if (result.Ok)
        {
            OnChanged(DataTable.Scouts, DataTable.Rooms, DataTable.Adults);
        }

        return result;
    }

    /// <summary>Why <see cref="CheckComposition"/> last said no. Only touched under _lock.</summary>
    private ActionResult? _lastRefusal;

    /// <summary>
    /// Who may sit on a scout's board, for seating it or changing it: each
    /// member exists, is listed once, is here, is free (or already on this
    /// board, <paramref name="boardRoom"/>), and isn't Unavailable for the
    /// board type; three to six of them (two for a project review); and a
    /// chair who is one of them and qualified to chair. Null, with the refusal
    /// in <see cref="_lastRefusal"/>, when any of that fails.
    /// </summary>
    private (List<AdultRecord> Members, AdultRecord Chair, string Names)? CheckComposition(
        ScoutRecord scout, string? memberIds, string? chairId, string? boardRoom)
    {
        (List<AdultRecord>, AdultRecord, string)? Refuse(string message)
        {
            _lastRefusal = ActionResult.Error(message);
            return null;
        }

        if (string.IsNullOrWhiteSpace(memberIds))
        {
            return Refuse("ERROR: No board members selected");
        }

        var members = new List<AdultRecord>();
        var names = new StringBuilder();
        foreach (var raw in memberIds.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var id = raw.Trim();
            var member = Adults.Get(id);
            if (member == null)
            {
                return Refuse("ERROR: Invalid Member ID " + id);
            }

            // Counting one person twice would let "A,A,A" pass as a board of three.
            if (members.Contains(member))
            {
                return Refuse("ERROR: Member " + member.FullName + " is listed more than once");
            }

            if (member.Room == AdultRoom.Disabled)
            {
                return Refuse("ERROR: Member " + member.FullName + " has been disabled for tonight");
            }

            if (member.Room.Length > 0 && member.Room != boardRoom)
            {
                return Refuse("ERROR: Member " + member.FullName + " already assigned to a board in room " + member.Room);
            }

            // The browser checked this only for members listed before the
            // chair, and the Java server not at all, so an Unavailable adult
            // could be seated by ticking them after someone qualified.
            if (member.RoleFor(scout.BoardType) == BoardRoles.Unavailable)
            {
                return Refuse("ERROR: Member " + member.FullName + " is Unavailable for " + scout.BoardType + " boards");
            }

            if (names.Length > 0)
            {
                names.Append(',');
            }

            // Full names on the room card: it is how someone finds which room
            // an adult is in, and "F. Last" made that lookup by first name
            // impossible.
            names.Append(member.FullName);
            members.Add(member);
        }

        var isProject = scout.BoardType == BoardTypes.Project;
        var min = isProject ? BoardRules.ProjectMinMembers : BoardRules.BoardMinMembers;
        if (members.Count < min)
        {
            return Refuse($"ERROR: Only {members.Count} board member(s) selected; {min} required for {scout.BoardType} boards");
        }

        if (members.Count > BoardRules.BoardMaxMembers)
        {
            return Refuse($"ERROR: {members.Count} board members selected; no more than {BoardRules.BoardMaxMembers} permitted (Guide to Advancement 8.0.0.3)");
        }

        // The Chair designation is binding. Promoting a Member is a deliberate
        // edit on the table pages, never a side effect of seating a board
        // because the qualified chairs were all busy.
        var chair = Adults.Get(chairId);
        if (chair == null)
        {
            return Refuse("ERROR: Invalid Chair ID " + chairId);
        }

        if (!members.Contains(chair))
        {
            return Refuse("ERROR: Chair " + chair.FullName + " is not one of the board members");
        }

        var role = chair.RoleFor(scout.BoardType);
        if (role != BoardRoles.Chair)
        {
            return Refuse("ERROR: " + chair.FullName + " is not qualified to chair a " + scout.BoardType
                + " board (role: " + (role.Length == 0 ? "none" : role) + ")");
        }

        return (members, chair, names.ToString());
    }

    /// <summary>
    /// Change who sits on a board already seated or in review: swap a member
    /// who had to leave, add one, or change the chair. The same rules as
    /// seating apply. Members who leave are freed; those who join are marked
    /// in the room. The board's timer keeps running: it's the same board.
    /// </summary>
    public ActionResult ChangeBoardMembers(string? scoutId, string? chairId, string? memberIds)
    {
        ActionResult result;
        lock (_lock)
        {
            Trace($"ChangeBoardMembers ScoutID={scoutId} ChairID={chairId} MemberIDs={memberIds}");
            var scout = Scouts.Get(scoutId);
            var room = scout == null ? null : FindBoardRoom(scout);
            if (scout == null)
            {
                result = ActionResult.Error("ERROR: Invalid Scout ID" + scoutId);
            }
            else if (!BoardStatus.IsActive(scout.Status) || room == null)
            {
                result = ActionResult.Error("ERROR: " + scout.FullName + " has no board seated or in review to change");
            }
            else if (CheckComposition(scout, memberIds, chairId, room.Room) is not { } board)
            {
                result = _lastRefusal!;
            }
            else
            {
                var (members, chair, names) = board;

                // Everyone whose Room or Sel is about to change: those leaving the
                // room and those joining it (already there or not).
                var touched = Adults.Records.Where(a => a.Room == room.Room).Concat(members).Distinct().ToList();
                var adultSnaps = touched.Select(Snap).ToList();
                var scoutSnap = Snap(scout);
                var roomSnap = Snap(room);

                foreach (var adult in Adults.Records.Where(a => a.Room == room.Room && !members.Contains(a)))
                {
                    adult.Room = "";
                }

                foreach (var member in members)
                {
                    member.Room = room.Room;
                    member.Sel = "0";
                }

                room.Leaders = names;
                scout.BoardMembers = names;
                scout.BoardMemberIds = string.Join(",", members.Select(m => m.Id));
                scout.BoardChair = chair.FullName;
                scout.BoardChairId = chair.Id;
                Scouts.Store();
                Rooms.Store();
                Adults.Store();
                PushUndo($"changing {scout.FullName}'s board members", [scoutSnap, roomSnap, .. adultSnaps]);
                result = ActionResult.Success;
            }
        }

        if (result.Ok)
        {
            OnChanged(DataTable.Scouts, DataTable.Rooms, DataTable.Adults);
        }

        return result;
    }

    /// <summary>
    /// The room a board sits in: by name, or, for a room renamed under the
    /// board, the one whose card still names the scout.
    /// </summary>
    private RoomRecord? FindBoardRoom(ScoutRecord scout) =>
        Rooms.Records.FirstOrDefault(r => r.Room == scout.Room)
        ?? Rooms.Records.FirstOrDefault(r => r.Scout.Length > 0 && r.Scout == scout.FullName);

    private void ReleaseAdults(string room)
    {
        foreach (var adult in ReleasableAdults(room))
        {
            adult.Room = "";
        }
    }

    /// <summary>The adults <see cref="ReleaseAdults"/> would free, snapshotted before it runs.</summary>
    private List<AdultRecord> ReleasableAdults(string room) =>
        // "" and "N/A" are never a board's room ("N/A" marks adults gone home).
        room.Length == 0 || room == AdultRoom.Disabled
            ? []
            : Adults.Records.Where(a => a.Room == room).ToList();

    // ------------------------------------------------------------------
    // Rooms
    // ------------------------------------------------------------------

    /// <summary>
    /// Swap two rooms: each one's scout, members and card move to the other.
    /// Either room may be empty, which makes it a move.
    /// </summary>
    public ActionResult ChangeRoom(string? roomId1, string? roomId2)
    {
        ActionResult result;
        lock (_lock)
        {
            result = ChangeRoomLocked(roomId1, roomId2);
        }

        if (result.Ok)
        {
            OnChanged(DataTable.Scouts, DataTable.Rooms, DataTable.Adults);
        }

        return result;
    }

    private ActionResult ChangeRoomLocked(string? roomId1, string? roomId2)
    {
        if (string.IsNullOrEmpty(roomId1))
        {
            return ActionResult.Error("ERROR: Invalid Room ID" + roomId1);
        }

        if (string.IsNullOrEmpty(roomId2))
        {
            return ActionResult.Error("ERROR: Invalid Room ID" + roomId2);
        }

        var room1 = Rooms.Get(roomId1);
        var room2 = Rooms.Get(roomId2);
        if (room1 == null)
        {
            return ActionResult.Error("ERROR: No Such Room" + roomId1);
        }

        if (room2 == null)
        {
            return ActionResult.Error("ERROR: No Such Room" + roomId2);
        }

        var scouts1 = Scouts.Where("Room", room1.Room);
        var scouts2 = Scouts.Where("Room", room2.Room);
        if (scouts1.Count > 1)
        {
            return ActionResult.Error("ERROR: Room assigned to multiple scouts: " + room1.Room);
        }

        if (scouts2.Count > 1)
        {
            return ActionResult.Error("ERROR: Room assigned to multiple scouts: " + room2.Room);
        }

        var adults1 = Adults.Where("Room", room1.Room);
        var adults2 = Adults.Where("Room", room2.Room);

        var room1Snap = Snap(room1);
        var room2Snap = Snap(room2);
        var scoutSnaps = scouts1.Concat(scouts2).Select(Snap).ToList();
        var adultSnaps = adults1.Concat(adults2).Select(Snap).ToList();

        (room1.Leaders, room2.Leaders) = (room2.Leaders, room1.Leaders);
        (room1.Scout, room2.Scout) = (room2.Scout, room1.Scout);
        scouts1.ForEach(s => s.Room = room2.Room);
        scouts2.ForEach(s => s.Room = room1.Room);
        adults1.ForEach(a => a.Room = room2.Room);
        adults2.ForEach(a => a.Room = room1.Room);
        Rooms.Store();
        Scouts.Store();
        Adults.Store();
        PushUndo($"moving room {room1.Room}'s board", [room1Snap, room2Snap, .. scoutSnaps, .. adultSnaps]);
        return ActionResult.Success;
    }

    public ActionResult AddRoom(string room, string boardType)
    {
        room = room.Trim();
        if (room.Length == 0)
        {
            return ActionResult.Error("Room # is required.");
        }

        if (boardType is not (BoardTypes.Final or BoardTypes.Project))
        {
            return ActionResult.Error("Board type must be Final or Project.");
        }

        var id = RoomRecord.IdFor(room);
        var action = SaveRow(DataTable.Rooms, "inserted", id, new Dictionary<string, string>
        {
            ["Room"] = room,
            ["BoardType"] = boardType,
            ["Scout"] = "",
            ["Leaders"] = "",
        });
        return action == "inserted" ? ActionResult.Success : ActionResult.Error("Room '" + room + "' Already Exists.");
    }

    public ActionResult RemoveRoom(string roomId)
    {
        lock (_lock)
        {
            var room = Rooms.Get(roomId);
            if (room == null)
            {
                return ActionResult.Error("No Such Room: " + roomId);
            }

            if (room.Scout.Length > 0)
            {
                return ActionResult.Error("Room '" + room.Room + "' is currently in use, cannot remove.");
            }
        }

        return SaveRow(DataTable.Rooms, "deleted", roomId, new Dictionary<string, string>()) == "deleted"
            ? ActionResult.Success
            : ActionResult.Error("Remove Room '" + roomId + "' Failed.");
    }

    /// <summary>
    /// Rename a room, on the room card itself rather than only the Admin
    /// tables. The room keeps its ID; every scout and adult currently in it
    /// moves to the new name with it, so a board in progress isn't stranded
    /// looking for a room that no longer matches.
    /// </summary>
    public ActionResult RenameRoom(string? roomId, string? newName)
    {
        ActionResult result;
        lock (_lock)
        {
            result = RenameRoomLocked(roomId, newName);
        }

        if (result.Ok)
        {
            OnChanged(DataTable.Rooms, DataTable.Scouts, DataTable.Adults);
        }

        return result;
    }

    private ActionResult RenameRoomLocked(string? roomId, string? newName)
    {
        var room = Rooms.Get(roomId);
        if (room == null)
        {
            return ActionResult.Error("ERROR: No Such Room" + roomId);
        }

        newName = (newName ?? "").Trim();
        if (newName.Length == 0)
        {
            return ActionResult.Error("ERROR: Room # is required.");
        }

        // "N/A" marks adults who have gone home, so a room by that name would
        // make everyone in it look gone; a comma is written to the CSV as "~"
        // and read back as a different name. The Mac and Java versions refuse
        // both too.
        if (string.Equals(newName, AdultRoom.Disabled, StringComparison.OrdinalIgnoreCase))
        {
            return ActionResult.Error("ERROR: N/A marks adults who have gone home; choose another name");
        }

        if (newName.Contains(','))
        {
            return ActionResult.Error("ERROR: A room name cannot contain a comma");
        }

        var oldName = room.Room;
        if (newName == oldName)
        {
            return ActionResult.Success;
        }

        if (Rooms.Records.Any(r => r != room && r.Room == newName))
        {
            return ActionResult.Error("ERROR: Room '" + newName + "' already exists.");
        }

        var scouts = Scouts.Where("Room", oldName);
        var adults = Adults.Where("Room", oldName);
        var roomSnap = Snap(room);
        var scoutSnaps = scouts.Select(Snap).ToList();
        var adultSnaps = adults.Select(Snap).ToList();

        room.Room = newName;
        scouts.ForEach(s => s.Room = newName);
        adults.ForEach(a => a.Room = newName);
        Rooms.Store();
        Scouts.Store();
        Adults.Store();
        PushUndo($"renaming room {oldName} to {newName}", [roomSnap, .. scoutSnaps, .. adultSnaps]);
        return ActionResult.Success;
    }

    /// <summary>
    /// Switch a room between final boards and project reviews, on the room
    /// card itself rather than only the Rooms page. A board already seated
    /// there isn't disturbed (GTA 8.0.5.3): a final board may sit in a
    /// project-review room and back, as section 17 of the event test
    /// covers.
    /// </summary>
    public ActionResult SetRoomType(string? roomId, string? boardType)
    {
        ActionResult result;
        lock (_lock)
        {
            var room = Rooms.Get(roomId);
            if (room == null)
            {
                result = ActionResult.Error("ERROR: No Such Room" + roomId);
            }
            else if (boardType is not (BoardTypes.Final or BoardTypes.Project))
            {
                result = ActionResult.Error("ERROR: Board type must be Final or Project.");
            }
            else if (boardType == room.BoardType)
            {
                result = ActionResult.Success;
            }
            else
            {
                var roomSnap = Snap(room);
                var label = boardType == BoardTypes.Project ? "project reviews" : "final boards";
                room.BoardType = boardType;
                Rooms.Store();
                PushUndo($"switching room {room.Room} to {label}", [roomSnap]);
                result = ActionResult.Success;
            }
        }

        if (result.Ok)
        {
            OnChanged(DataTable.Rooms);
        }

        return result;
    }

    // ------------------------------------------------------------------
    // Generic record edits (the table pages) and settings
    // ------------------------------------------------------------------

    /// <summary>
    /// Insert, update or delete one record. <paramref name="status"/> is
    /// "inserted", "updated" or "deleted"; every field named in
    /// <paramref name="fields"/> that is one of the table's columns is written.
    /// Returns <paramref name="status"/> on success and "invalid" otherwise:
    /// no ID, updating a record that doesn't exist, inserting one that does.
    ///
    /// <paramref name="undoable"/> puts an edit to an adult or a room on the
    /// Undo stack. The Java version's Event page marks adults gone home and
    /// links them through <c>/adult-update</c>, and renames rooms through
    /// <c>/room-update</c>, so those endpoints keep its Undo (SPEC.md O-2);
    /// this app has its own operations for those, and the table pages'
    /// hand edits stay off the stack.
    /// </summary>
    public string SaveRow(DataTable table, string? status, string? id, IReadOnlyDictionary<string, string> fields, bool undoable = false)
    {
        string action;
        lock (_lock)
        {
            action = SaveRowLocked(Table(table), status, id, fields,
                undoable && table is (DataTable.Adults or DataTable.Rooms) && status is not ("inserted" or "deleted"));
        }

        if (action != "invalid")
        {
            OnChanged(table);
        }

        return action;
    }

    private string SaveRowLocked(IDataRecordFile file, string? status, string? id, IReadOnlyDictionary<string, string> fields, bool undoable)
    {
        if (id == null)
        {
            return "invalid";
        }

        var record = file.Find(id);
        if (record == null)
        {
            if (status != "inserted")
            {
                Trace("no such record: " + id);
                return "invalid";
            }

            record = file.AddNewRecord(id);
        }
        else if (status == "inserted")
        {
            Trace("record already exists: " + id);
            return "invalid";
        }

        var snap = undoable ? Snap(record) : null;
        if (status == "deleted")
        {
            file.Remove(id);
        }
        else
        {
            foreach (var column in file.Columns)
            {
                if (fields.TryGetValue(column, out var value))
                {
                    Trace("UPDATING: " + column + " => " + value);
                    record.Put(column, value);
                }
            }
        }

        record.UpdateFields(false);
        file.Store();
        if (snap != null)
        {
            PushUndo(record is RoomRecord room ? $"editing room {room.Room}" : $"editing {snap.Name}", [snap]);
        }

        return status ?? "null";
    }

    /// <summary>
    /// Merge settings into the stored config. Fields not given fall back to
    /// their defaults, so pass the whole set (as the Settings window does).
    /// </summary>
    public void UpdateConfig(IEnumerable<KeyValuePair<string, string>> fields)
    {
        lock (_lock)
        {
            var incoming = new ConfigRecord(fields);
            var existing = Config.Get(incoming.Id);
            if (existing != null)
            {
                existing.UpdateFrom(incoming, ConfigRecord.AllColumns);
            }
            else
            {
                Config.Add(incoming, false);
            }

            Config.Store();
        }

        OnChanged(DataTable.Config);
    }

    // ------------------------------------------------------------------
    // Pre-registration
    // ------------------------------------------------------------------

    /// <summary>Load a district-website pre-registration CSV into tonight's schedule and the adult history.</summary>
    public void ImportPreRegistrations(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"error: no preregistration file '{path}' found.");
        }

        lock (_lock)
        {
            _log($"loading SCOUT pre-registrations: {path}");
            ScoutsScheduled.ClearAll();
            new PreRegScoutConverter(ScoutsScheduled, Trace).Convert(path);
            ScoutsScheduled.Store();
            _log($"loading ADULT pre-registrations: {path}");
            new PreRegAdultConverter(AdultHistory, Trace).Convert(path);
            AdultHistory.Store();
        }

        OnChanged(DataTable.ScoutsScheduled, DataTable.AdultHistory);
    }

    /// <summary>
    /// Pull tonight's sign-ups from SignUpGenius into the schedule (youth) and
    /// the adult history (adults). Throws on network or API errors.
    /// </summary>
    public async Task<SignUpGeniusSummary> ImportSignUpGeniusAsync(string key, string? signupId, HttpClient http, CancellationToken cancel = default)
    {
        var sug = new SignUpGenius(http, key, _log, Trace);
        var id = signupId ?? await sug.FindSignupIdAsync(cancel).ConfigureAwait(false);
        var entries = await sug.FetchFilledSlotsAsync(id, cancel).ConfigureAwait(false);

        SignUpGeniusSummary summary;
        lock (_lock)
        {
            summary = sug.Merge(entries, AdultHistory, ScoutsScheduled);
            AdultHistory.Store();
            ScoutsScheduled.Store();
        }

        OnChanged(DataTable.ScoutsScheduled, DataTable.AdultHistory);
        return summary with { SignupId = id };
    }

    // ------------------------------------------------------------------

    private void Trace(string message)
    {
        if (Verbose)
        {
            _log(message);
        }
    }

    private void OnChanged(params DataTable[] tables) => Changed?.Invoke(this, new DataChangedEventArgs(tables));
}
