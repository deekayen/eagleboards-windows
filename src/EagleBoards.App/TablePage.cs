using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using EagleBoards.Core;
using EagleBoards.Core.Records;
using Microsoft.Win32;

namespace EagleBoards.App;

/// <summary>A record in a table page, with an indexer the grid binds to ("[Last]").</summary>
public sealed class RecordRow : Row
{
    private Dictionary<string, string> _values;
    private string _supportingNames = "";

    public RecordRow(IReadOnlyDictionary<string, string> values)
    {
        _values = new Dictionary<string, string>(values, StringComparer.Ordinal);
        Id = V(values, "ID");
    }

    /// <summary>Called when the operator edits a cell: row, field, new value.</summary>
    public Action<RecordRow, string, string>? Edited { get; set; }

    public string this[string field]
    {
        get => _values.TryGetValue(field, out var v) ? Display.Text(v) : "";
        set
        {
            value ??= "";
            if (value == this[field])
            {
                return;
            }

            _values[field] = value;
            Raise(string.Empty);
            Edited?.Invoke(this, field, value);
        }
    }

    /// <summary>A field as the file holds it (IDs, with the format's escapes).</summary>
    public string Raw(string field) => _values.TryGetValue(field, out var v) ? v : "";

    /// <summary>The stored status, for the status pill (<c>StatusCell</c>).</summary>
    public string Status => Raw("Status");

    public string StatusText => Display.Status(Status);

    /// <summary>An adult counting the event toward Wood Badge: the mark (SPEC.md D-20).</summary>
    public bool IsWoodBadge => Raw("WoodBadge") == "Y";

    /// <summary>An adult who has gone home (the file's Room "N/A").</summary>
    public bool IsDisabled => Raw("Room") == AdultRoom.Disabled;

    /// <summary>An adult on a board now.</summary>
    public bool IsBusy => Raw("Room") is not ("" or "-") && !IsDisabled;

    /// <summary>An adult's room, or "Gone home".</summary>
    public string RoomText => IsDisabled ? "Gone home" : Raw("Room");

    /// <summary>A board's room, blank once it's over (the file says "N/A").</summary>
    public string BoardRoomText => Raw("Room") == AdultRoom.Disabled ? "" : Raw("Room");

    /// <summary>A board's members as "A, B, C" (the file joins them with "," or "~").</summary>
    public string MembersText => Display.List(Raw("BoardMembers"));

    /// <summary>A board's members but its chair, who has a column of their own.</summary>
    public string OtherMembersText => string.Join(", ", MembersText.Split(", ", StringSplitOptions.RemoveEmptyEntries).Where(m => m != this["BoardChair"]));

    /// <summary>A room's members, the same way.</summary>
    public string LeadersText => Display.List(Raw("Leaders"));

    /// <summary>An adult's roles in words, as Add adult and the tablet's form say them: "No thanks" where the file keeps Unavailable.</summary>
    public string FinalBoardText => Display.Role(Raw("FinalBoard"));

    public string ProjectReviewText => Display.Role(Raw("ProjectReview"));

    /// <summary>
    /// When someone signed in, as this computer's clock shows a time ("4:48
    /// PM"): the file's 24-hour stamp has no AM or PM. The stamp sorts the
    /// column, since "10:05 AM" would sort before "4:48 PM" as text.
    /// </summary>
    public string SignedInText => DataRecord.ParseTime(Raw(DataRecord.RegTimeField)) is { } at
        ? at.DateTime.ToString("t", CultureInfo.CurrentCulture)
        : this[DataRecord.RegTimeHmField];

    /// <summary>The earlier event that approved a proposal, in words; its folder's date sorts the column.</summary>
    public string ApprovedOnText => DateText(Raw("Event"));

    /// <summary>
    /// The last event an adult signed in at, from the history's
    /// "(2026-08-25)(2026-09-27)", as the file writes it: what the Last event
    /// column sorts by.
    /// </summary>
    public string LastEventSort => Regex.Matches(Raw("BoardHistory"), @"\d{4}-\d{2}-\d{2}") is { Count: > 0 } dates ? dates[^1].Value : "";

    /// <summary>That event's date in words; "Today" once they have signed in today.</summary>
    public string LastEvent => LastEventSort is { Length: > 0 } last
        ? last == DataRecord.Clock().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ? "Today" : DateText(last)
        : "";

    /// <summary>Whom an adult introduces, by name; set by the window, which knows the youth.</summary>
    public string SupportingNames { get => _supportingNames; set => Set(ref _supportingNames, value); }

    public string Describe() => this["Last"].Length > 0 ? $"{this["First"]} {this["Last"]}" : this["Room"].Length > 0 ? "Room " + this["Room"] : Id;

    /// <summary>
    /// What a screen reader calls the row, someone's name or the room:
    /// a grid row is named by its item, which read "EagleBoards.App.RecordRow".
    /// </summary>
    public override string ToString() => Describe();

    /// <summary>Any value as shown, the status and roles in words, or whom an adult introduces.</summary>
    public bool Contains(string text) =>
        _values.Keys.Select(k => this[k]).Append(StatusText).Append(FinalBoardText).Append(ProjectReviewText).Append(SupportingNames)
            .Any(v => v.Contains(text, StringComparison.OrdinalIgnoreCase));

    /// <summary>Only these fields, as shown.</summary>
    public bool Contains(string text, IEnumerable<string> fields) =>
        fields.Any(f => this[f].Contains(text, StringComparison.OrdinalIgnoreCase));

    public void Update(IReadOnlyDictionary<string, string> values)
    {
        _values = new Dictionary<string, string>(values, StringComparer.Ordinal);
        Raise(string.Empty);
    }

    /// <summary>
    /// A file's date ("2026-08-25") as this computer's region writes a date
    /// in words, without the weekday: "August 25, 2026" in the US. A
    /// screen reader reads the file's form a group of digits at a time.
    /// </summary>
    internal static string DateText(string iso) =>
        DateOnly.TryParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString(DatePattern(CultureInfo.CurrentCulture), CultureInfo.CurrentCulture)
            : iso;

    /// <summary>The region's long date ("dddd, MMMM d, yyyy") without its weekday, else its short date.</summary>
    private static string DatePattern(CultureInfo culture)
    {
        var pattern = Regex.Replace(culture.DateTimeFormat.LongDatePattern, @"\s*dddd[,.]?\s*", " ").Trim(' ', ',');
        return pattern.Contains('d') && pattern.Contains('M') && pattern.Contains('y') ? pattern : "d";
    }
}

public enum ColumnKind
{
    Text,
    ReadOnly,
    Choice,

    /// <summary>
    /// The status pill, read-only: a youth's status changes only through the
    /// Event page's steps, which free the room and its members as they go.
    /// </summary>
    Status,

    /// <summary>The Wood Badge mark (SPEC.md D-20), and Yes or No while editing.</summary>
    WoodBadge,
}

/// <summary>
/// A column of a table page. <paramref name="Path"/> shows something other
/// than the field itself: a computed field ("[RegTimeHm]") or a
/// <see cref="RecordRow"/> property ("SupportingNames"), read-only.
/// <paramref name="SortPath"/> sorts by something other than what's shown
/// (a time in words by its stamp). <paramref name="Wrap"/> wraps a long
/// value (a board's members, notes) over more lines, where any other value
/// too long for its column ends in an ellipsis, in full in its tooltip.
/// </summary>
public sealed record TableColumn(string Field, string Header, double Width, ColumnKind Kind = ColumnKind.Text,
    Option[]? Choices = null, string? Path = null, string? SortPath = null, bool Wrap = false);

/// <summary>
/// One table as a page of the main window. <paramref name="ExportName"/> null
/// means no Export button; <paramref name="Filter"/> narrows which records
/// the page is about. <paramref name="ReadOnly"/> shows a table as it is kept,
/// with Find and Export only. <paramref name="Source"/> reads the rows from
/// somewhere other than <paramref name="Table"/>, each time the page is
/// shown; <paramref name="FindIn"/> narrows what Find looks through.
/// </summary>
public sealed record TableSpec(string Name, string Title, string Description, string FindHint, DataTable Table, TableColumn[] Columns,
    string? ExportName, bool CanDelete = true, bool IsRooms = false, int Frozen = 2, string? SortField = null,
    Func<RecordRow, bool>? Filter = null, string? RowStyle = null, bool ReadOnly = false,
    Func<BoardService, PageRows>? Source = null, string[]? FindIn = null);

/// <summary>
/// A page's rows from a <see cref="TableSpec.Source"/>: the rows, a line
/// about where they came from, and anything that couldn't be read.
/// </summary>
public sealed record PageRows(List<Dictionary<string, string>> Rows, string? About = null, string? Problem = null);

/// <summary>
/// Every table the operator can see and correct, each a page on the View
/// menu (SPEC.md P-6): the Java app's admin.html tabs, editable in place.
/// Results is the Boards table, and Adults the event's adults, with what the
/// operator does there (Save report, Mark gone home, Add adult) beside them.
/// Headings say what a column holds in full ("Time signed in" beside
/// "Sign-in number", "Last name"), since a screen reader names each cell by
/// its column's heading.
/// </summary>
public static class TableSpecs
{
    private static Option[] Same(params string[] values) => values.Select(v => new Option(v, v)).ToArray();

    public static readonly Option[] BoardTypeChoices =
        [new("", ""), new(BoardTypes.Final, Display.BoardType(BoardTypes.Final)), new(BoardTypes.Project, Display.BoardType(BoardTypes.Project))];
    private static readonly Option[] UnitTypeChoices = Same(["", .. UnitTypes.All]);
    // A board's three decisions only. Not "Postponed": that is the Status of a
    // scout sent away unprepared before any board met them, who has no Result.
    private static readonly Option[] ResultChoices = [new("", ""), .. BoardResults.All.Select(v => new Option(v, Display.Result(v)))];
    private static readonly Option[] WoodBadgeChoices = [new("", "No"), new("Y", "Yes")];
    private static readonly Option[] RoleChoices = [new("", ""), .. BoardRoles.All.Select(v => new Option(v, Display.Role(v)))];

    private const string SignedIn = "[" + DataRecord.RegTimeField + "]";

    // The youth tables, and so their exports, have no Phone or DOB column: a
    // youth's number and birthdate are never shown or exported, though one may
    // be on file from an older event (SPEC.md D-7, D-8). Adults' numbers are.
    // The descriptions give the mouse's way and then the keyboard's (the
    // style guide's order when both are named).
    public static readonly TableSpec Results = new("Results", "Results",
        "Every board at this event and its result. To correct a cell, double-click it, or select it and use F2. A choice, such as Result, opens its list with one click, or with F4. A status changes on the Event page.",
        "Find a board", DataTable.Scouts,
        [
            new("RegNum", "Sign-in number", 128, ColumnKind.ReadOnly), new("Last", "Last name", 110), new("First", "First name", 100),
            new("UnitType", "Unit type", 85, ColumnKind.Choice, UnitTypeChoices), new("Unit", "Unit", 60),
            new("BoardType", "Board type", 120, ColumnKind.Choice, BoardTypeChoices),
            new("Status", "Status", 128, ColumnKind.Status), new("Room", "Room", 68, ColumnKind.ReadOnly, Path: nameof(RecordRow.BoardRoomText)),
            new("Result", "Result", 110, ColumnKind.Choice, ResultChoices),
            // Who sat is changed on the Event page while the board runs, under the
            // rules for seating; never typed into a list here.
            new("BoardChair", "Chair", 160, ColumnKind.ReadOnly),
            new("BoardMembers", "Members", 300, ColumnKind.ReadOnly, Path: nameof(RecordRow.MembersText), Wrap: true),
            new("Leader", "Leader", 140), new("Notes", "Notes", 320, Wrap: true),
        ], ExportName: null, CanDelete: false, Frozen: 3, Filter: r => !BoardStatus.IsWaiting(r.Status));

    // The adults page lists only adults, so it is Adults, not People (SPEC.md
    // P-6). A change to someone's name, unit, contact or roles is made in the
    // adult history too (BoardService.SaveRow).
    public static readonly TableSpec Adults = new("Adults", "Adults",
        "The adults signed in at this event. To change a cell, double-click it, or select it and use F2. A choice, such as a role or Wood Badge, opens its list with one click, or with F4. A change to someone's name, unit, contact or roles is made in the adult history too.",
        "Find an adult", DataTable.Adults,
        [
            new("RegTime", "Time signed in", 120, ColumnKind.ReadOnly, Path: nameof(RecordRow.SignedInText), SortPath: SignedIn),
            new("Last", "Last name", 120), new("First", "First name", 110), new("UnitType", "Unit type", 90, ColumnKind.Choice, UnitTypeChoices),
            new("Unit", "Unit", 64), new("Email", "Email", 180), new("Phone", "Phone", 110),
            new("Room", "Room", 90, ColumnKind.ReadOnly, Path: nameof(RecordRow.RoomText)),
            new("FinalBoard", "Final board role", 130, ColumnKind.Choice, RoleChoices),
            new("ProjectReview", "Project review role", 150, ColumnKind.Choice, RoleChoices),
            new("WoodBadge", "Wood Badge", 104, ColumnKind.WoodBadge, WoodBadgeChoices),
            new("Supporting", "Introduces", 200, ColumnKind.ReadOnly, Path: nameof(RecordRow.SupportingNames), Wrap: true),
        ], "Adults", Frozen: 3, SortField: "Last", RowStyle: "AdultsRowStyle");

    public static readonly TableSpec Youth = new("Youth", "Youth",
        "Every youth signed in at this event, in sign-in order. To change a cell, double-click it, or select it and use F2. A choice, such as Board type, opens its list with one click, or with F4. A status changes on the Event page.",
        "Find a youth", DataTable.Scouts,
        [
            new("RegTime", "Time signed in", 120, ColumnKind.ReadOnly, Path: nameof(RecordRow.SignedInText), SortPath: SignedIn),
            new("RegNum", "Sign-in number", 128, ColumnKind.ReadOnly),
            new("Last", "Last name", 110), new("First", "First name", 110),
            new("UnitType", "Unit type", 85, ColumnKind.Choice, UnitTypeChoices), new("Unit", "Unit", 60), new("Leader", "Leader", 130),
            new("Email", "Email", 170), new("BoardType", "Board type", 120, ColumnKind.Choice, BoardTypeChoices),
            new("Room", "Room", 68, ColumnKind.ReadOnly, Path: nameof(RecordRow.BoardRoomText)),
            new("Status", "Status", 128, ColumnKind.Status), new("Result", "Result", 100, ColumnKind.Choice, ResultChoices),
        ], "Youth", Frozen: 4);

    public static readonly TableSpec PreRegistered = new("PreRegistered", "Pre-registered",
        "The event's sign-ups, from SignUpGenius or the district's list. A youth who signs in is matched to theirs. To change a cell, double-click it, or select it and use F2. A choice opens its list with one click, or with F4.",
        "Find a sign-up", DataTable.ScoutsScheduled,
        [
            new("Last", "Last name", 110), new("First", "First name", 110),
            new("UnitType", "Unit type", 85, ColumnKind.Choice, UnitTypeChoices), new("Unit", "Unit", 60), new("Leader", "Leader", 150),
            new("Email", "Email", 190), new("BoardType", "Board type", 120, ColumnKind.Choice, BoardTypeChoices),
        ], "YouthScheduled");

    // The adult history as it is kept, read-only (SPEC.md P-6): a sign-in
    // writes it, and a change on Adults reaches it. Its roles are in words,
    // as on Adults; the file's own values are what Export writes.
    public static readonly TableSpec AdultHistory = new("AdultHistory", "Adult history CSV",
        "Every adult who has signed in at any event, as the adult history keeps them from one event to the next. It can't be edited here: a sign-in writes it, and a change on Adults reaches it.", "Find an adult", DataTable.AdultHistory,
        [
            new("Last", "Last name", 120, ColumnKind.ReadOnly), new("First", "First name", 120, ColumnKind.ReadOnly),
            new("UnitType", "Unit type", 90, ColumnKind.ReadOnly), new("Unit", "Unit", 70, ColumnKind.ReadOnly),
            new("Email", "Email", 200, ColumnKind.ReadOnly), new("Phone", "Phone", 110, ColumnKind.ReadOnly),
            new("FinalBoard", "Final board role", 130, ColumnKind.ReadOnly, Path: nameof(RecordRow.FinalBoardText)),
            new("ProjectReview", "Project review role", 150, ColumnKind.ReadOnly, Path: nameof(RecordRow.ProjectReviewText)),
            new("BoardHistory", "Last event", 150, ColumnKind.ReadOnly, Path: nameof(RecordRow.LastEvent), SortPath: nameof(RecordRow.LastEventSort)),
        ], "AdultHistory", CanDelete: false, SortField: "Last", ReadOnly: true);

    public static readonly TableSpec Rooms = new("Rooms", "Rooms",
        "The rooms and what each is for. A board type opens its list with one click, or with F4. Rename rooms and move boards on the Event page.", "Find a room", DataTable.Rooms,
        [
            new("Room", "Room", 90, ColumnKind.ReadOnly), new("BoardType", "Board type", 140, ColumnKind.Choice, BoardTypeChoices),
            new("Scout", "Youth", 220, ColumnKind.ReadOnly),
            new("Leaders", "Members", 500, ColumnKind.ReadOnly, Path: nameof(RecordRow.LeadersText), Wrap: true),
        ], "Rooms", CanDelete: false, IsRooms: true, Frozen: 1);

    // SPEC.md D-22: whose project proposal was approved at an earlier event,
    // for a youth who comes without the signed page. Read only, from the
    // earlier events' folders, never written; no Export or Delete. No
    // birthdate, phone number or email is read into it.
    public static readonly TableSpec ApprovedProposals = new("ApprovedProposals", "Approved proposals",
        "Project proposals approved at earlier events in this data folder, for a youth who comes without the signed proposal page.", "Find a name or unit", DataTable.Scouts,
        [
            new("Last", "Last name", 120, ColumnKind.ReadOnly), new("First", "First name", 110, ColumnKind.ReadOnly),
            new("UnitType", "Unit type", 85, ColumnKind.ReadOnly), new("Unit", "Unit", 60, ColumnKind.ReadOnly),
            new("Event", "Approved on", 150, ColumnKind.ReadOnly, Path: nameof(RecordRow.ApprovedOnText), SortPath: "[Event]"),
            new("BoardChair", "Chair", 160, ColumnKind.ReadOnly),
            new("BoardMembers", "Members", 260, ColumnKind.ReadOnly, Path: nameof(RecordRow.OtherMembersText), Wrap: true),
            new("Notes", "Notes", 320, ColumnKind.ReadOnly, Wrap: true),
        ], ExportName: null, CanDelete: false, SortField: "Last", ReadOnly: true, FindIn: ["Last", "First", "UnitType", "Unit"],
        Source: svc =>
        {
            var read = svc.ReadApprovedProposals();
            var unreadable = read.Unreadable.Select(u => RecordRow.DateText(u.Event)).ToList();
            return new PageRows(read.Approvals.Select(a => a.Snapshot()).ToList(), InWords(read.About),
                unreadable.Count == 0 ? null : $"The youth from the {And(unreadable)} {(unreadable.Count == 1 ? "event" : "events")} couldn't be read, so any proposals approved there aren't listed. The other events are.");
        });

    /// <summary>The View menu's table pages, in its order after Event.</summary>
    public static readonly TableSpec[] All = [Results, Adults, Youth, PreRegistered, AdultHistory, Rooms, ApprovedProposals];

    /// <summary>Core's line about the earlier events, its dates in words as the page shows them; its wording is the other versions' too.</summary>
    private static string InWords(string text) => Regex.Replace(text, @"\d{4}-\d{2}-\d{2}", m => RecordRow.DateText(m.Value));

    private static string And(IReadOnlyList<string> items) =>
        items.Count <= 2 ? string.Join(" and ", items) : string.Join(", ", items.Take(items.Count - 1)) + ", and " + items[^1];
}

/// <summary>
/// One table as an editable page: title and commands, what it holds, and a
/// grid whose cells save as soon as they're changed, through
/// <see cref="BoardService.SaveRow"/>, the same path the website's "-update"
/// endpoints use. Edits here stay off the Undo stack. Reloads when shown and
/// whenever the event changes while it is, but never under an open cell editor.
/// </summary>
public sealed class TablePage : DockPanel
{
    /// <summary>Read with the grid, after its description: sorting and fitting a column have no button to come across.</summary>
    private const string MenuHelp = "Shift+F10 opens a shortcut menu to sort a column or fit it to its contents.";

    private readonly BoardService _svc;
    private readonly ObservableCollection<RecordRow> _rows = [];
    private readonly ListCollectionView _view;
    private readonly StackPanel _commands = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, -8, 0) };
    private readonly TextBlock _count = new() { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Bottom };
    private readonly TextBlock _description = new() { TextWrapping = TextWrapping.Wrap };

    /// <summary>Each command button's glyph, label and action, for the grid's shortcut menu.</summary>
    private readonly Dictionary<Button, (string Glyph, string Text, Action Click)> _commandItems = [];

    private readonly ContextMenu _menu = new();

    /// <summary>
    /// Find's count is read out once typing pauses, and only when it has
    /// changed: not on every key, nor when a sign-in at the door reloads.
    /// </summary>
    private readonly DispatcherTimer _findPause = new() { Interval = TimeSpan.FromMilliseconds(400) };

    private string? _countSpoken;
    private int _pageCommands = 1;
    private bool _editing;

    /// <summary>The cell being opened by a click or a key: open its list once the editor is up.</summary>
    private bool _openList;

    /// <summary>
    /// Opens a choice cell's list: on a click, F2, F4 or Alt+Down Arrow, and
    /// when an arrow key would change a closed list's value. A test replaces
    /// it: a list's popup is kept on screen by WPF, so it would open on the
    /// desktop even from a window drawn off-screen.
    /// </summary>
    internal Action<ComboBox> OpenList { get; set; } = list =>
    {
        list.Focus();
        list.IsDropDownOpen = true;
    };

    /// <param name="resource">The main window's resources: the status pill and Adults' row style.</param>
    /// <param name="addRoom">What Add room does (the Event page's dialog); Rooms only.</param>
    public TablePage(BoardService svc, TableSpec spec, Func<string, object> resource, Action? addRoom = null)
    {
        _svc = svc;
        Spec = spec;
        Margin = new Thickness(16);
        Visibility = Visibility.Collapsed;

        Find = new TextBox();
        Find.SetResourceReference(StyleProperty, "SearchBox");
        Find.Margin = new Thickness(0, 0, 8, 0);
        Placeholder.SetText(Find, spec.FindHint);
        AutomationProperties.SetName(Find, spec.FindHint);
        Find.ToolTip = "Type to narrow the table to the rows that match (Ctrl+F). Down Arrow or Enter goes to the first match; Esc clears.";
        Find.PreviewKeyDown += OnFindKey;
        _commands.Children.Add(Find);

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        SetDock(_commands, Dock.Right);
        header.Children.Add(_commands);
        var title = new TextBlock { Text = spec.Title };
        title.SetResourceReference(StyleProperty, "PageTitle");
        header.Children.Add(title);
        SetDock(header, Dock.Top);
        Children.Add(header);

        var about = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        _count.SetResourceReference(StyleProperty, "Secondary");
        AutomationProperties.SetLiveSetting(_count, AutomationLiveSetting.Polite);
        SetDock(_count, Dock.Right);
        about.Children.Add(_count);
        _description.SetResourceReference(StyleProperty, "Secondary");
        about.Children.Add(_description);
        SetDock(about, Dock.Top);
        Children.Add(about);

        SetDock(Notice, Dock.Top);
        Children.Add(Notice);

        _view = new ListCollectionView(_rows) { Filter = o => o is RecordRow r && (spec.Filter?.Invoke(r) ?? true) && (Find.Text.Trim() is var f && (f.Length == 0 || (spec.FindIn is { } fields ? r.Contains(f, fields) : r.Contains(f)))) };
        if (spec.SortField != null)
        {
            _view.SortDescriptions.Add(new SortDescription("[" + spec.SortField + "]", ListSortDirection.Ascending));
        }

        Grid = new DataGrid { ItemsSource = _view, IsReadOnly = spec.ReadOnly, FrozenColumnCount = spec.Frozen };
        AutomationProperties.SetName(Grid, spec.Title);
        SetDescription(spec.Description);
        if (spec.RowStyle != null)
        {
            Grid.RowStyle = (Style)resource(spec.RowStyle);
        }

        foreach (var col in spec.Columns)
        {
            var column = MakeColumn(col, resource);
            if (spec.SortField != null && column.SortMemberPath == "[" + spec.SortField + "]")
            {
                column.SortDirection = ListSortDirection.Ascending;
            }

            Grid.Columns.Add(column);
        }

        Grid.BeginningEdit += (_, _) => _editing = true;
        Grid.CellEditEnding += (_, _) => _editing = false;
        Grid.SelectionChanged += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, Grid))
            {
                SelectionChanged?.Invoke();
            }
        };

        // A choice in a cell (a result, a role) is saved as it's picked; end
        // the cell's edit there too, so nothing is left open for a later
        // cancel, a reload or another page to catch half done. The editor's
        // first selection, as it opens, isn't a pick: nothing is unselected
        // by it. Arrow keys never pick here (OnGridKey).
        Grid.AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, e) =>
        {
            if (e.OriginalSource is ComboBox && e.RemovedItems.Count > 0 && e.AddedItems.Count > 0)
            {
                Dispatcher.BeginInvoke(CommitEdit, DispatcherPriority.Background);
            }
        }));
        // A choice cell opens its list on the first click. The grid alone
        // takes three: one to select the cell, one to start editing it, and
        // one to open the list (eagleboards-windows#17).
        Grid.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OpenChoiceOnClick));
        Grid.AddHandler(PreviewKeyDownEvent, new KeyEventHandler(OnGridKey));

        // Typing never changes a value by itself. On a cell not being edited
        // the grid would start an edit and replace the value with the letter,
        // saved off the Undo stack as the cell is left; with the focus now put
        // in the table (a page shown, F6, Find's Down Arrow) a name typed to
        // search would overwrite one. F2 or a double-click starts an edit. A
        // closed list's type-ahead would pick and save too: it opens instead.
        Grid.AddHandler(PreviewTextInputEvent, new TextCompositionEventHandler((_, e) =>
        {
            if (e.OriginalSource is ComboBox { IsDropDownOpen: false } list && Ancestor<DataGridCell>(list) is { IsEditing: true })
            {
                OpenList(list);
                e.Handled = true;
            }
            else if (Ancestor<DataGridCell>(e.OriginalSource as DependencyObject) is { IsEditing: false })
            {
                e.Handled = true;
            }
        }));
        Grid.PreparingCellForEdit += (_, e) =>
        {
            // Named after its column: "Final board role, combo box", not just "combo box".
            if (e.EditingElement is Control editor)
            {
                AutomationProperties.SetName(editor, (string)e.Column.Header);
            }

            // The editor takes the focus, so the keys reach it: the grid's
            // own list column focuses its list, but a template column (Wood
            // Badge) left the focus on the cell, where an arrow key moved on.
            var open = _openList;
            _openList = false;
            Dispatcher.BeginInvoke(() =>
            {
                if (Descendant<ComboBox>(e.EditingElement) is not { } list)
                {
                    return;
                }

                list.DropDownClosed -= OnListClosed;
                list.DropDownClosed += OnListClosed;
                if (open)
                {
                    OpenList(list);
                }
                else
                {
                    list.Focus();
                }
            }, DispatcherPriority.Loaded);
        };
        Grid.ContextMenuOpening += OnMenuOpening;

        var card = new Border { Child = Grid };
        card.SetResourceReference(StyleProperty, "Card");
        Children.Add(card);

        Find.TextChanged += (_, _) =>
        {
            // A cell left mid-edit (F6 or Ctrl+F from it) is saved first, as
            // leaving it does: the view can't refresh with an edit open.
            if (_view.IsEditingItem || _view.IsAddingNew)
            {
                CommitEdit();
            }

            _view.Refresh();
            UpdateCount();
            _findPause.Stop();
            _findPause.Start();
        };
        _findPause.Tick += (_, _) =>
        {
            _findPause.Stop();
            if (_count.Text != _countSpoken)
            {
                _countSpoken = _count.Text;
                Announcer.LiveRegionChanged(_count);
            }
        };

        if (spec.IsRooms)
        {
            AddCommand("", "_Add room...", "Add a room", () => addRoom?.Invoke(), pageCommand: false);
            AddCommand("", "_Remove room", "Remove the selected room, if no board is in it", RemoveRoom, pageCommand: false);
        }

        if (spec.ExportName != null)
        {
            AddCommand("", "E_xport...", "Save this table as a CSV file for Excel", Export, pageCommand: false);
        }

        if (spec.CanDelete && !spec.IsRooms)
        {
            AddCommand("", "_Delete...", "Delete the selected record", Delete, pageCommand: false);
        }
    }

    public TableSpec Spec { get; }

    public DataGrid Grid { get; }

    /// <summary>The page's find box (Ctrl+F).</summary>
    public TextBox Find { get; }

    /// <summary>
    /// Put the keyboard focus in the page shown (Ctrl+number, the View menu):
    /// on the selected row's first cell, else the first row's, else Find when
    /// the table is empty. Without it focus stayed on the page just hidden.
    /// Nothing is selected by it: a page command acts on a row only once the
    /// operator has chosen one.
    /// </summary>
    public void FocusContent()
    {
        if (_view.Count == 0)
        {
            Find.Focus();
            return;
        }

        FocusRow(Grid.SelectedItem ?? _view.GetItemAt(0), select: false);
    }

    /// <summary>Messages about this table, where it is.</summary>
    public InfoBar Notice { get; } = new() { Margin = new Thickness(0, 0, 0, 12), IsClosable = true };

    public RecordRow? Selected => Grid.SelectedItem as RecordRow;

    public IEnumerable<RecordRow> Rows => _rows;

    public event Action? SelectionChanged;

    /// <summary>After each reload: for what only the window knows (whom an adult introduces).</summary>
    public event Action? Reloaded;

    /// <summary>The focus on a row's first cell, scrolled into view, and the row selected if <paramref name="select"/>.</summary>
    private void FocusRow(object item, bool select)
    {
        if (select)
        {
            Grid.SelectedItem = item;
        }

        var column = Grid.Columns[0];
        Grid.ScrollIntoView(item, column);
        Grid.CurrentCell = new DataGridCellInfo(item, column);
        Dispatcher.BeginInvoke(() =>
        {
            if (Grid.ItemContainerGenerator.ContainerFromItem(item) is DataGridRow row
                && column.GetCellContent(row)?.Parent is DataGridCell cell)
            {
                cell.Focus();
            }
            else
            {
                Grid.Focus();
            }
        }, DispatcherPriority.Loaded);
    }

    /// <summary>In Find: Down Arrow or Enter goes to the first match, Esc clears the box.</summary>
    private void OnFindKey(object sender, KeyEventArgs e)
    {
        if (e.KeyboardDevice.Modifiers != ModifierKeys.None)
        {
            return;
        }

        if (e.Key is Key.Down or Key.Enter && _view.Count > 0)
        {
            FocusRow(_view.GetItemAt(0), select: true);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && Find.Text.Length > 0)
        {
            Find.Clear();
            e.Handled = true;
        }
    }

    /// <summary>
    /// A click on a choice cell not yet being edited: select its row, make it
    /// the current cell and start editing, and have the list open as the
    /// editor appears. Text cells keep the grid's own double-click or F2.
    /// </summary>
    private void OpenChoiceOnClick(object sender, MouseButtonEventArgs e)
    {
        if (Ancestor<DataGridCell>(e.OriginalSource as DependencyObject) is { } cell && IsClosedChoice(cell))
        {
            OpenChoice(cell, e);
        }
    }

    /// <summary>
    /// The keyboard's way into a choice cell, and out of a closed list's
    /// trap. F2, F4 or Alt+Down Arrow on a choice cell opens its list, as one
    /// click does. A closed list takes Up and Down Arrow as a new value, and
    /// each value passed was saved, Chair on the way from Member to blank:
    /// those keys open the list instead, where they only move the highlight
    /// and Enter picks.
    /// </summary>
    private void OnGridKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = e.KeyboardDevice.Modifiers;
        var opens = (key == Key.F4 && modifiers == ModifierKeys.None) || (modifiers == ModifierKeys.Alt && key is Key.Up or Key.Down);
        if (e.OriginalSource is ComboBox { IsDropDownOpen: false } list && Ancestor<DataGridCell>(list) is { IsEditing: true })
        {
            // Any arrow, with Ctrl or Shift too: a closed list moves its
            // selection on Left and Right as well, and Tab from an edited
            // text cell lands on one. Left and Right do nothing here.
            if (opens || ((modifiers & ModifierKeys.Alt) == 0 && key is Key.Up or Key.Down or Key.Home or Key.End))
            {
                OpenList(list);
                e.Handled = true;
            }
            else if ((modifiers & ModifierKeys.Alt) == 0 && key is Key.Left or Key.Right or Key.PageUp or Key.PageDown)
            {
                e.Handled = true;
            }

            return;
        }

        if ((opens || (key == Key.F2 && modifiers == ModifierKeys.None))
            && Ancestor<DataGridCell>(e.OriginalSource as DependencyObject) is { } cell && IsClosedChoice(cell))
        {
            OpenChoice(cell, e);
        }
    }

    /// <summary>A choice cell not being edited yet, which a click or a key opens.</summary>
    private static bool IsClosedChoice(DataGridCell cell) =>
        cell is { IsEditing: false, IsReadOnly: false }
        && cell.Column is DataGridComboBoxColumn or DataGridTemplateColumn { CellEditingTemplate: not null };

    /// <summary>Select the cell's row, start editing it, and open its list once the editor is up.</summary>
    private void OpenChoice(DataGridCell cell, RoutedEventArgs e)
    {
        Grid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);
        Grid.SelectedItem = cell.DataContext;
        Grid.CurrentCell = new DataGridCellInfo(cell);
        cell.Focus();
        _openList = true;
        Grid.BeginEdit(e);
        _openList = _openList && cell.IsEditing;
        e.Handled = true;
    }

    /// <summary>
    /// A cell's list closed with nothing picked (Esc, or a click away): end
    /// the edit, so one Esc is enough and the focus is back on the cell. A
    /// pick has already ended it by then (the commit runs first).
    /// </summary>
    private void OnListClosed(object? sender, EventArgs e)
    {
        if (sender is not ComboBox list || Ancestor<DataGridCell>(list) is not { } cell)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (cell.IsEditing && !list.IsDropDownOpen)
            {
                // Back to the cell only if the focus is still in the table: a
                // list closed by F6 or Ctrl+F leaves it where it went.
                var stay = Grid.IsKeyboardFocusWithin || Keyboard.FocusedElement is null;
                CommitEdit();
                if (stay)
                {
                    cell.Focus();
                }
            }
        }, DispatcherPriority.ContextIdle);
    }

    private static T? Ancestor<T>(DependencyObject? from) where T : DependencyObject
    {
        for (var at = from; at != null; at = at is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at))
        {
            if (at is T found)
            {
                return found;
            }
        }

        return null;
    }

    private static T? Descendant<T>(DependencyObject? from) where T : DependencyObject
    {
        if (from is null or T)
        {
            return from as T;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(from); i++)
        {
            if (Descendant<T>(VisualTreeHelper.GetChild(from, i)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// A command for this page, before Export and Delete, and in the grid's
    /// shortcut menu too, enabled when its button is.
    /// </summary>
    /// <param name="text">The label; an "_" before a letter is its access key ("E_xport..."). Pages are shown one at a time, so letters may repeat between pages but not clash with the menu bar's F, E, V and H, nor the shortcut menu's T, E and F.</param>
    public Button AddCommand(string glyph, string text, string tooltip, Action click, bool pageCommand = true)
    {
        var label = new StackPanel { Orientation = Orientation.Horizontal };
        label.Children.Add(new Glyph { Text = glyph, Margin = new Thickness(0, 0, 8, 0) });
        label.Children.Add(AccessKeyText.Content(text));
        var button = new Button { Content = label, ToolTip = tooltip };
        button.SetResourceReference(StyleProperty, "CommandButton");
        AutomationProperties.SetName(button, AccessKeyText.Strip(text).TrimEnd('.'));
        button.Click += (_, _) => click();
        _commands.Children.Insert(pageCommand ? _pageCommands++ : _commands.Children.Count, button);
        _commandItems[button] = (glyph, text, click);
        return button;
    }

    /// <summary>
    /// The grid's shortcut menu (Shift+F10, the Menu key, or a right-click
    /// on a cell or a heading): sort the column or fit it to its contents,
    /// then the page's commands. A column heading takes no keyboard focus, so
    /// without it sorting and widening were the mouse's alone. Built here for
    /// every page, and opened here, so nothing else replaces it.
    /// </summary>
    private void OnMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        var cell = Ancestor<DataGridCell>(source);
        var heading = Ancestor<DataGridColumnHeader>(source);
        var menu = MenuFor(cell?.Column ?? heading?.Column ?? Grid.CurrentCell.Column);
        e.Handled = true;
        if (menu.Items.Count == 0)
        {
            return;
        }

        // From the keyboard, under the cell it's about, as Windows does.
        var keyboard = e.CursorLeft < 0 && e.CursorTop < 0;
        menu.PlacementTarget = (UIElement?)cell ?? (UIElement?)heading ?? Grid;
        menu.Placement = keyboard ? PlacementMode.Bottom : PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    /// <summary>The shortcut menu's items for a column (none when null), filled but not opened.</summary>
    internal ContextMenu MenuFor(DataGridColumn? column)
    {
        _menu.Items.Clear();
        if (column?.Header is string header)
        {
            // Access keys clear of every page's commands (Sign in, Delete, Export...).
            var name = header.Replace("_", "__", StringComparison.Ordinal);
            if (column.CanUserSort && !string.IsNullOrEmpty(column.SortMemberPath))
            {
                _menu.Items.Add(MenuCommand("", $"Sor_t by {name}", () => SortBy(column, ListSortDirection.Ascending)));
                _menu.Items.Add(MenuCommand("", $"Sort by {name}, d_escending", () => SortBy(column, ListSortDirection.Descending)));
            }

            if (column.CanUserResize)
            {
                _menu.Items.Add(MenuCommand("", $"_Fit {name} to its contents", () => column.Width = DataGridLength.Auto));
            }
        }

        var commands = _commands.Children.OfType<Button>().Where(b => b.Visibility == Visibility.Visible && _commandItems.ContainsKey(b)).ToList();
        if (commands.Count > 0 && _menu.Items.Count > 0)
        {
            _menu.Items.Add(new Separator());
        }

        foreach (var button in commands)
        {
            var (glyph, text, click) = _commandItems[button];
            var item = MenuCommand(glyph, text, click);
            item.IsEnabled = button.IsEnabled;
            _menu.Items.Add(item);
        }

        return _menu;
    }

    private static MenuItem MenuCommand(string glyph, string text, Action click)
    {
        var item = new MenuItem { Header = text };
        if (glyph.Length > 0)
        {
            item.Icon = new Glyph { Text = glyph };
        }

        item.Click += (_, _) => click();
        return item;
    }

    /// <summary>Sort as a click on the heading does: this column only, its arrow shown.</summary>
    private void SortBy(DataGridColumn column, ListSortDirection direction)
    {
        CommitEdit();
        using (_view.DeferRefresh())
        {
            _view.SortDescriptions.Clear();
            _view.SortDescriptions.Add(new SortDescription(column.SortMemberPath, direction));
        }

        foreach (var c in Grid.Columns)
        {
            c.SortDirection = ReferenceEquals(c, column) ? direction : null;
        }

        if (Grid.SelectedItem is { } selected)
        {
            Grid.ScrollIntoView(selected);
        }
    }

    /// <summary>
    /// Finish any cell still being edited, saving it: before the page is left
    /// or the window closed, so the other pages show what was typed.
    /// </summary>
    public void CommitEdit()
    {
        Grid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);
    }

    /// <summary>Reload from the service, keeping the selection. A quiet reload waits out an open cell editor.</summary>
    public void Reload(bool quiet)
    {
        // A page read from elsewhere is read as it's shown, not at every change
        // to this event, which can't change it (SPEC.md D-15, D-22).
        if (quiet && (_editing || Spec.Source != null))
        {
            return;
        }

        var selected = Selected?.Id;
        List<Dictionary<string, string>> rows;
        if (Spec.Source is { } source)
        {
            var read = source(_svc);
            rows = read.Rows;
            SetDescription(read.About is { } about ? $"{Spec.Description} {about}" : Spec.Description);
            if (read.Problem is { } problem)
            {
                Notice.Show(Severity.Warning, "Some earlier events couldn't be read", problem);
            }
            else
            {
                Notice.Close();
            }
        }
        else
        {
            rows = _svc.Snapshot(Spec.Table);
        }

        RowSync.Sync(_rows, rows, r => new RecordRow(r) { Edited = OnEdited }, (row, r) => row.Update(r));
        Reloaded?.Invoke();

        // A row updated in place isn't filtered again by itself (a youth just
        // seated joins Results); refresh only then, since it resets the grid.
        if (_rows.Any(r => _view.PassesFilter(r) != _view.Contains(r)))
        {
            _view.Refresh();
        }

        if (selected != null && _rows.FirstOrDefault(r => r.Id == selected) is { } again && _view.Contains(again))
        {
            Grid.SelectedItem = again;
        }

        UpdateCount();
    }

    /// <summary>The description over the grid, read with it too, so a screen reader entering the grid hears what it holds.</summary>
    private void SetDescription(string text)
    {
        _description.Text = text;
        AutomationProperties.SetHelpText(Grid, text + " " + MenuHelp);
    }

    private void UpdateCount()
    {
        var all = Spec.Filter is { } f ? _rows.Count(f) : _rows.Count;
        var records = Display.Count(all, "record", "records");
        _count.Text = _view.Count == all ? records : $"{_view.Count.ToString(CultureInfo.CurrentCulture)} of {records}";
    }

    /// <summary>
    /// Save one changed cell. An adult's name, unit, contact and roles
    /// change in the adult history too (<see cref="BoardService.SaveRow"/>).
    /// </summary>
    private void OnEdited(RecordRow row, string field, string value)
    {
        var action = _svc.SaveRow(Spec.Table, "updated", row.Id, new Dictionary<string, string> { [field] = value }, out var refusal);
        if (refusal != null)
        {
            Notice.Show(Severity.Warning, "That can't be changed here", refusal);
            Dispatcher.BeginInvoke(() => Reload(quiet: false));
        }
        else if (action == "invalid")
        {
            Notice.Show(Severity.Warning, "That record is gone", "It may have been deleted. The table has been reloaded.");
            Dispatcher.BeginInvoke(() => Reload(quiet: false));
        }
    }

    private void Delete()
    {
        if (Selected is not { } row)
        {
            Notice.Show(Severity.Informational, "Select a row first", "Select the row to delete, and then select Delete.");
            return;
        }

        if (AppDialog.Confirm(Window.GetWindow(this), "Delete this record?",
                $"**{row.Describe()}** will be removed from {Spec.Title}. This can't be undone.", "Delete"))
        {
            _svc.SaveRow(Spec.Table, "deleted", row.Id, new Dictionary<string, string>());
            Reload(quiet: false);
        }
    }

    private void RemoveRoom()
    {
        if (Selected is not { } row)
        {
            Notice.Show(Severity.Informational, "Select a room first", "Select the room to remove, and then select Remove room.");
            return;
        }

        var result = _svc.RemoveRoom(row.Id);
        if (!result.Ok)
        {
            Notice.Show(Severity.Error, "Couldn't remove the room", result.Message.StartsWith("ERROR: ", StringComparison.Ordinal) ? result.Message[7..] : result.Message);
        }

        Reload(quiet: false);
    }

    private void Export()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export " + Spec.Title,
            Filter = "CSV (opens in Excel) (*.csv)|*.csv",
            FileName = Spec.ExportName + ".csv",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        try
        {
            // With a byte-order mark so Excel reads accented names correctly.
            File.WriteAllText(dialog.FileName, _svc.ExportCsv(Spec.Table, Spec.Columns.Select(c => c.Field).ToList()), new UTF8Encoding(true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notice.Show(Severity.Error, "Couldn't export the table", ErrorText.For(ex, dialog.FileName));
        }
    }

    private static DataGridColumn MakeColumn(TableColumn col, Func<string, object> resource)
    {
        var path = col.Path ?? "[" + col.Field + "]";
        var sort = col.SortPath ?? (col.Path is { } p && !p.StartsWith('[') ? p : "[" + col.Field + "]");
        DataGridColumn column;
        switch (col.Kind)
        {
            case ColumnKind.Choice:
                column = new DataGridComboBoxColumn
                {
                    Header = col.Header,
                    Width = col.Width,
                    ItemsSource = col.Choices,
                    DisplayMemberPath = "Value",
                    SelectedValuePath = "Key",
                    SelectedValueBinding = new Binding(path) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
                    SortMemberPath = sort,

                    // The column's own editing combo is the classic one:
                    // a white list with invisible items in dark mode.
                    EditingElementStyle = (Style)Application.Current.FindResource("DefaultComboBoxStyle"),
                };
                break;
            case ColumnKind.Status:
                column = new DataGridTemplateColumn
                {
                    Header = col.Header,
                    Width = col.Width,
                    SortMemberPath = sort,
                    CellTemplate = (DataTemplate)resource("StatusCell"),
                    IsReadOnly = true,
                };
                path = nameof(RecordRow.StatusText);
                break;
            case ColumnKind.WoodBadge:
                var marks = new FrameworkElementFactory(typeof(AdultMarks));
                marks.SetBinding(AdultMarks.WoodBadgeProperty, new Binding(nameof(RecordRow.IsWoodBadge)) { Mode = BindingMode.OneWay });
                marks.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Left);
                marks.SetValue(MarginProperty, new Thickness(-6, 0, 0, 0));
                column = new DataGridTemplateColumn
                {
                    Header = col.Header,
                    Width = col.Width,
                    SortMemberPath = sort,
                    CellTemplate = new DataTemplate { VisualTree = marks },
                    CellEditingTemplate = ChoiceEditor(path, col.Choices!, col.Header),
                };
                break;
            default:
                var readOnly = col.Kind == ColumnKind.ReadOnly;
                var text = new DataGridTextColumn
                {
                    Header = col.Header,
                    Width = col.Width,
                    Binding = new Binding(path) { Mode = readOnly ? BindingMode.OneWay : BindingMode.TwoWay },
                    IsReadOnly = readOnly,
                    SortMemberPath = sort,
                    ElementStyle = TextStyle(path, col.Wrap),
                };
                if (col.Wrap && !readOnly)
                {
                    text.EditingElementStyle = new Style(typeof(TextBox), DataGridTextColumn.DefaultEditingElementStyle)
                    {
                        Setters = { new Setter(TextBox.TextWrappingProperty, TextWrapping.Wrap) },
                    };
                }

                column = text;
                break;
        }

        // Every cell named with what it shows: a cell whose content has no
        // text of its own (the status pill, the Wood Badge mark, a list's
        // value, an empty cell) was read as "Item: EagleBoards.App.RecordRow,
        // Column Display Index: 6", and an adult without Wood Badge as nothing.
        var cell = new Style(typeof(DataGridCell), (Style)resource("GridCell"));
        cell.Setters.Add(new Setter(AutomationProperties.NameProperty, new Binding(path) { Mode = BindingMode.OneWay, Converter = new CellName(col.Choices) }));
        column.CellStyle = cell;
        return column;
    }

    /// <summary>
    /// A text cell's look. Based on the column's default, which keeps
    /// Fluent's own TextBlock colour off it, so Adults' greyed rows stay grey.
    /// A long column wraps, its row growing to fit; any other ends in an
    /// ellipsis when cut short, with the whole value in its tooltip, shown
    /// only then. A cut-off value used to just stop, mid-name.
    /// </summary>
    private static Style TextStyle(string path, bool wrap)
    {
        var style = new Style(typeof(TextBlock), DataGridTextColumn.DefaultElementStyle);
        if (wrap)
        {
            // Off the row's edges, where the selected row's outline is drawn.
            style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
            style.Setters.Add(new Setter(MarginProperty, new Thickness(2, 4, 2, 4)));
            return style;
        }

        style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        style.Setters.Add(new Setter(ToolTipProperty, new Binding(path) { Mode = BindingMode.OneWay }));
        style.Setters.Add(new EventSetter(ToolTipOpeningEvent, new ToolTipEventHandler((sender, e) =>
        {
            if (sender is not TextBlock block || !IsCut(block))
            {
                e.Handled = true;
            }
        })));
        return style;
    }

    /// <summary>Whether a text is too long for its block, and so drawn with an ellipsis.</summary>
    private static bool IsCut(TextBlock block)
    {
        if (block.Text.Length == 0 || block.ActualWidth <= 0)
        {
            return false;
        }

        var full = new FormattedText(block.Text, CultureInfo.CurrentCulture, block.FlowDirection,
            new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch), block.FontSize, block.Foreground,
            VisualTreeHelper.GetDpi(block).PixelsPerDip);
        return full.WidthIncludingTrailingWhitespace > block.ActualWidth + 0.5 || full.Height > block.ActualHeight + 0.5;
    }

    /// <summary>A choice while the Wood Badge column is being edited, saved as soon as it's picked, and named after its column.</summary>
    private static DataTemplate ChoiceEditor(string path, Option[] choices, string header)
    {
        var combo = new FrameworkElementFactory(typeof(ComboBox));
        combo.SetValue(StyleProperty, Application.Current.FindResource("DefaultComboBoxStyle"));
        combo.SetValue(ItemsControl.ItemsSourceProperty, choices);
        combo.SetValue(ItemsControl.DisplayMemberPathProperty, "Value");
        combo.SetValue(Selector.SelectedValuePathProperty, "Key");
        combo.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        combo.SetValue(AutomationProperties.NameProperty, header);
        combo.SetBinding(Selector.SelectedValueProperty, new Binding(path) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        return new DataTemplate { VisualTree = combo };
    }

    /// <summary>
    /// What a cell says to a screen reader: its value as shown (a choice's
    /// words, not the key the file keeps), or "Blank".
    /// </summary>
    private sealed class CellName(Option[]? choices) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var text = value as string ?? "";
            if (choices?.FirstOrDefault(c => c.Key == text) is { } choice)
            {
                text = choice.Value;
            }

            return text.Length > 0 ? text : "Blank";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
