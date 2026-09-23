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
    private static readonly string[] ScoutRegFields = ["First", "Last", "DOB", "Unit", "UnitType", "Email", "Phone", "Leader"];
    private static readonly string[] AdultRegFields = ["First", "Last", "Unit", "UnitType", "Email", "Phone", "ProjectReview", "FinalBoard"];

    private readonly Lock _lock = new();
    private readonly Action<string> _log;

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

    /// <summary>
    /// Open (creating where missing) tonight's files. Throws if an explicitly
    /// named adult history or config file does not exist.
    /// </summary>
    public static BoardService Open(EventOptions options, Action<string>? log = null)
    {
        log ??= _ => { };
        Directory.CreateDirectory(options.DataDirectory);

        string adultHistory;
        if (options.AdultHistoryPath != null)
        {
            if (!File.Exists(options.AdultHistoryPath))
            {
                throw new FileNotFoundException($"error: adult history file '{options.AdultHistoryPath}' does not exist.");
            }

            adultHistory = options.AdultHistoryPath;
        }
        else
        {
            adultHistory = Path.Combine(options.DataDirectory, "adult_history.csv");
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

            var incoming = new ScoutRecord(fields);
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
            var adult = Adults.Get(incoming.Id);
            if (adult != null)
            {
                adult.UpdateFrom(incoming, AdultRegFields);
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
        // back to Registered on the Admin window; they must be seatable.
        if (scout.Room != "" && scout.Room != AdultRoom.Disabled && scout.Room != room.Room)
        {
            return ActionResult.Error("ERROR: Scout Already Assigned Room: " + scout.Room);
        }

        if (string.IsNullOrWhiteSpace(memberIds))
        {
            return ActionResult.Error("ERROR: No board members selected");
        }

        var members = new List<AdultRecord>();
        var names = new StringBuilder();
        foreach (var raw in memberIds.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var id = raw.Trim();
            var member = Adults.Get(id);
            if (member == null)
            {
                return ActionResult.Error("ERROR: Invalid Member ID " + id);
            }

            // Counting one person twice would let "A,A,A" pass as a board of three.
            if (members.Contains(member))
            {
                return ActionResult.Error("ERROR: Member " + member.FullName + " is listed more than once");
            }

            if (member.Room == AdultRoom.Disabled)
            {
                return ActionResult.Error("ERROR: Member " + member.FullName + " has been disabled for tonight");
            }

            if (member.Room.Length > 0)
            {
                return ActionResult.Error("ERROR: Member " + member.FullName + " already assigned to a board in room " + member.Room);
            }

            // The browser checked this only for members listed before the
            // chair, and the Java server not at all, so an Unavailable adult
            // could be seated by ticking them after someone qualified.
            if (member.RoleFor(scout.BoardType) == BoardRoles.Unavailable)
            {
                return ActionResult.Error("ERROR: Member " + member.FullName + " is Unavailable for " + scout.BoardType + " boards");
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
            return ActionResult.Error($"ERROR: Only {members.Count} board member(s) selected; {min} required for {scout.BoardType} boards");
        }

        if (members.Count > BoardRules.BoardMaxMembers)
        {
            return ActionResult.Error($"ERROR: {members.Count} board members selected; no more than {BoardRules.BoardMaxMembers} permitted (Guide to Advancement 8.0.0.3)");
        }

        // The Chair designation is binding. Promoting a Member is a deliberate
        // edit on the Admin tables, never a side effect of seating a board
        // because the qualified chairs were all busy.
        var chair = Adults.Get(chairId);
        if (chair == null)
        {
            return ActionResult.Error("ERROR: Invalid Chair ID " + chairId);
        }

        if (!members.Contains(chair))
        {
            return ActionResult.Error("ERROR: Chair " + chair.FullName + " is not one of the board members");
        }

        var role = chair.RoleFor(scout.BoardType);
        if (role != BoardRoles.Chair)
        {
            return ActionResult.Error("ERROR: " + chair.FullName + " is not qualified to chair a " + scout.BoardType
                + " board (role: " + (role.Length == 0 ? "none" : role) + ")");
        }

        var leaders = names.ToString();
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
                scout.Status = BoardStatus.InProgress;
                scout.UpdateFields(true);
                Scouts.Store();
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
                scout.Status = BoardStatus.Postponed;
                scout.UpdateFields(true);
                Scouts.Store();
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
        // "" and "N/A" are never a board's room ("N/A" marks adults gone home).
        if (room.Length == 0 || room == AdultRoom.Disabled)
        {
            return;
        }

        foreach (var adult in Adults.Records)
        {
            if (adult.Room == room)
            {
                adult.Room = "";
            }
        }
    }

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
        (room1.Leaders, room2.Leaders) = (room2.Leaders, room1.Leaders);
        (room1.Scout, room2.Scout) = (room2.Scout, room1.Scout);
        scouts1.ForEach(s => s.Room = room2.Room);
        scouts2.ForEach(s => s.Room = room1.Room);
        adults1.ForEach(a => a.Room = room2.Room);
        adults2.ForEach(a => a.Room = room1.Room);
        Rooms.Store();
        Scouts.Store();
        Adults.Store();
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

    // ------------------------------------------------------------------
    // Generic record edits (the Admin tables) and settings
    // ------------------------------------------------------------------

    /// <summary>
    /// Insert, update or delete one record. <paramref name="status"/> is
    /// "inserted", "updated" or "deleted"; every field named in
    /// <paramref name="fields"/> that is one of the table's columns is written.
    /// Returns <paramref name="status"/> on success and "invalid" otherwise:
    /// no ID, updating a record that doesn't exist, inserting one that does.
    /// </summary>
    public string SaveRow(DataTable table, string? status, string? id, IReadOnlyDictionary<string, string> fields)
    {
        string action;
        lock (_lock)
        {
            action = SaveRowLocked(Table(table), status, id, fields);
        }

        if (action != "invalid")
        {
            OnChanged(table);
        }

        return action;
    }

    private string SaveRowLocked(IDataRecordFile file, string? status, string? id, IReadOnlyDictionary<string, string> fields)
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
