using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
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

    /// <summary>
    /// The last event an adult signed in at, from the history's
    /// "(2026-08-25)(2026-09-27)"; "Today" once they have signed in today.
    /// </summary>
    public string LastEvent
    {
        get
        {
            var dates = System.Text.RegularExpressions.Regex.Matches(Raw("BoardHistory"), @"\d{4}-\d{2}-\d{2}");
            if (dates.Count == 0)
            {
                return "";
            }

            var last = dates[^1].Value;
            return last == DataRecord.Clock().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ? "Today" : last;
        }
    }

    /// <summary>Who an adult came to support, by name; set by the window, which knows the youth.</summary>
    public string SupportingNames { get => _supportingNames; set => Set(ref _supportingNames, value); }

    public string Describe() => this["Last"].Length > 0 ? $"{this["First"]} {this["Last"]}" : this["Room"].Length > 0 ? "room " + this["Room"] : Id;

    /// <summary>Any value as shown, the status in words, or who an adult came to support.</summary>
    public bool Contains(string text) =>
        _values.Keys.Select(k => this[k]).Append(StatusText).Append(SupportingNames)
            .Any(v => v.Contains(text, StringComparison.OrdinalIgnoreCase));

    /// <summary>Only these fields, as shown.</summary>
    public bool Contains(string text, IEnumerable<string> fields) =>
        fields.Any(f => this[f].Contains(text, StringComparison.OrdinalIgnoreCase));

    public void Update(IReadOnlyDictionary<string, string> values)
    {
        _values = new Dictionary<string, string>(values, StringComparer.Ordinal);
        Raise(string.Empty);
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
/// </summary>
public sealed record TableColumn(string Field, string Header, double Width, ColumnKind Kind = ColumnKind.Text,
    KeyValuePair<string, string>[]? Choices = null, string? Path = null);

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
/// Results is the Boards table, and Adults tonight's adults, with what the
/// operator does there (Save report, Gone home, Add adult) beside them.
/// </summary>
public static class TableSpecs
{
    private static KeyValuePair<string, string>[] Same(params string[] values) => values.Select(v => new KeyValuePair<string, string>(v, v)).ToArray();

    public static readonly KeyValuePair<string, string>[] BoardTypeChoices =
        [new("", ""), new(BoardTypes.Final, Display.BoardType(BoardTypes.Final)), new(BoardTypes.Project, Display.BoardType(BoardTypes.Project))];
    private static readonly KeyValuePair<string, string>[] UnitTypeChoices = Same(["", .. UnitTypes.All]);
    // A board's three decisions only. Not "Postponed": that is the Status of a
    // scout sent away unprepared before any board met them, who has no Result.
    private static readonly KeyValuePair<string, string>[] ResultChoices = [new("", ""), .. BoardResults.All.Select(v => new KeyValuePair<string, string>(v, Display.Result(v)))];
    private static readonly KeyValuePair<string, string>[] WoodBadgeChoices = [new("", "No"), new("Y", "Yes")];
    private static readonly KeyValuePair<string, string>[] RoleChoices = Same(["", .. BoardRoles.All]);

    // The youth tables, and so their exports, have no Phone or DOB column: a
    // youth's number and birthdate are never shown or exported, though one may
    // be on file from an older event (SPEC.md D-7, D-8). Adults' numbers are.
    public static readonly TableSpec Results = new("Results", "Results",
        "Every board at this event and its result. Double-click a cell to correct it; a status changes on the Event page.", "Find a board", DataTable.Scouts,
        [
            new("RegNum", "Sign-in", 76, ColumnKind.ReadOnly), new("Last", "Last", 110), new("First", "First", 100),
            new("UnitType", "Unit type", 85, ColumnKind.Choice, UnitTypeChoices), new("Unit", "Unit", 60),
            new("BoardType", "Board", 110, ColumnKind.Choice, BoardTypeChoices),
            new("Status", "Status", 128, ColumnKind.Status), new("Room", "Room", 68, ColumnKind.ReadOnly, Path: nameof(RecordRow.BoardRoomText)),
            new("Result", "Result", 110, ColumnKind.Choice, ResultChoices),
            // Who sat is changed on the Event page while the board runs, under the
            // rules for seating; never typed into a list here.
            new("BoardChair", "Chair", 160, ColumnKind.ReadOnly), new("BoardMembers", "Members", 300, ColumnKind.ReadOnly, Path: nameof(RecordRow.MembersText)),
            new("Leader", "Leader", 140), new("Notes", "Notes", 320),
        ], ExportName: null, CanDelete: false, Frozen: 3, Filter: r => !BoardStatus.IsWaiting(r.Status));

    // The adults page lists only adults, so it is Adults, not People (SPEC.md
    // P-6). A change to someone's name, unit, contact or roles is made in the
    // adult history too (BoardService.SaveRow).
    public static readonly TableSpec Adults = new("Adults", "Adults",
        "The adults signed in at this event. Double-click a cell to change it: someone's roles, unit or Wood Badge. Their name, unit, contact and roles change in the adult history too.", "Find an adult", DataTable.Adults,
        [
            new("RegTime", "Signed in", 88, ColumnKind.ReadOnly, Path: "[" + DataRecord.RegTimeHmField + "]"),
            new("Last", "Last", 120), new("First", "First", 110), new("UnitType", "Unit type", 90, ColumnKind.Choice, UnitTypeChoices),
            new("Unit", "Unit", 64), new("Email", "Email", 180), new("Phone", "Phone", 110),
            new("Room", "Room", 90, ColumnKind.ReadOnly, Path: nameof(RecordRow.RoomText)),
            new("FinalBoard", "Final board role", 130, ColumnKind.Choice, RoleChoices),
            new("ProjectReview", "Project review role", 150, ColumnKind.Choice, RoleChoices),
            new("WoodBadge", "Wood Badge", 104, ColumnKind.WoodBadge, WoodBadgeChoices),
            new("Supporting", "Came to support", 200, ColumnKind.ReadOnly, Path: nameof(RecordRow.SupportingNames)),
        ], "Adults", Frozen: 3, SortField: "Last", RowStyle: "AdultsRowStyle");

    public static readonly TableSpec Youth = new("Youth", "Youth",
        "Every youth signed in at this event, in sign-in order. Double-click a cell to change it; a status changes on the Event page.", "Find a youth", DataTable.Scouts,
        [
            new("RegTime", "Signed in", 88, ColumnKind.ReadOnly, Path: "[" + DataRecord.RegTimeHmField + "]"), new("RegNum", "Sign-in", 76, ColumnKind.ReadOnly),
            new("Last", "Last", 110), new("First", "First", 110),
            new("UnitType", "Unit type", 85, ColumnKind.Choice, UnitTypeChoices), new("Unit", "Unit", 60), new("Leader", "Leader", 130),
            new("Email", "Email", 170), new("BoardType", "Board", 110, ColumnKind.Choice, BoardTypeChoices),
            new("Room", "Room", 68, ColumnKind.ReadOnly, Path: nameof(RecordRow.BoardRoomText)),
            new("Status", "Status", 128, ColumnKind.Status), new("Result", "Result", 100, ColumnKind.Choice, ResultChoices),
        ], "Youth", Frozen: 4);

    public static readonly TableSpec PreRegistered = new("PreRegistered", "Pre-registered",
        "The event's sign-ups, from SignUpGenius or the district's list. A youth who signs in is matched to theirs.", "Find a sign-up", DataTable.ScoutsScheduled,
        [
            new("Last", "Last", 110), new("First", "First", 110),
            new("UnitType", "Unit type", 85, ColumnKind.Choice, UnitTypeChoices), new("Unit", "Unit", 60), new("Leader", "Leader", 150),
            new("Email", "Email", 190), new("BoardType", "Board", 110, ColumnKind.Choice, BoardTypeChoices),
        ], "YouthScheduled");

    // The adult history as it is kept, read-only (SPEC.md P-6): a sign-in
    // writes it, and a change on Adults reaches it.
    public static readonly TableSpec AdultHistory = new("AdultHistory", "Adult history CSV",
        "Every adult who has signed in at any event, as the adult history keeps them from one event to the next. It can't be edited here: a sign-in writes it, and a change on Adults reaches it.", "Find an adult", DataTable.AdultHistory,
        [
            new("Last", "Last", 120, ColumnKind.ReadOnly), new("First", "First", 120, ColumnKind.ReadOnly),
            new("UnitType", "Unit type", 90, ColumnKind.ReadOnly), new("Unit", "Unit", 70, ColumnKind.ReadOnly),
            new("Email", "Email", 200, ColumnKind.ReadOnly), new("Phone", "Phone", 110, ColumnKind.ReadOnly),
            new("FinalBoard", "Final board role", 130, ColumnKind.ReadOnly), new("ProjectReview", "Project review role", 150, ColumnKind.ReadOnly),
            new("BoardHistory", "Last event", 110, ColumnKind.ReadOnly, Path: nameof(RecordRow.LastEvent)),
        ], "AdultHistory", CanDelete: false, SortField: "Last", ReadOnly: true);

    public static readonly TableSpec Rooms = new("Rooms", "Rooms",
        "The rooms and what each is for. Rename, switch and move boards from the Event page.", "Find a room", DataTable.Rooms,
        [
            new("Room", "Room", 90, ColumnKind.ReadOnly), new("BoardType", "Board type", 140, ColumnKind.Choice, BoardTypeChoices),
            new("Scout", "Youth", 220, ColumnKind.ReadOnly), new("Leaders", "Members", 500, ColumnKind.ReadOnly, Path: nameof(RecordRow.LeadersText)),
        ], "Rooms", CanDelete: false, IsRooms: true, Frozen: 1);

    // SPEC.md D-22: whose project proposal was approved at an earlier event,
    // for a youth who comes without the signed page. Read only, from the
    // earlier events' folders, never written; no Export or Delete. No
    // birthdate, phone number or email is read into it.
    public static readonly TableSpec ApprovedProposals = new("ApprovedProposals", "Approved proposals",
        "Project proposals approved at earlier events in this data folder, for a youth who comes without the signed proposal page.", "Find a name or unit", DataTable.Scouts,
        [
            new("Last", "Last", 120, ColumnKind.ReadOnly), new("First", "First", 110, ColumnKind.ReadOnly),
            new("UnitType", "Unit type", 85, ColumnKind.ReadOnly), new("Unit", "Unit", 60, ColumnKind.ReadOnly),
            new("Event", "Approved on", 104, ColumnKind.ReadOnly),
            new("BoardChair", "Chair", 160, ColumnKind.ReadOnly), new("BoardMembers", "Members", 260, ColumnKind.ReadOnly, Path: nameof(RecordRow.OtherMembersText)),
            new("Notes", "Notes", 320, ColumnKind.ReadOnly),
        ], ExportName: null, CanDelete: false, SortField: "Last", ReadOnly: true, FindIn: ["Last", "First", "UnitType", "Unit"],
        Source: svc =>
        {
            var read = svc.ReadApprovedProposals();
            return new PageRows(read.Approvals.Select(a => a.Snapshot()).ToList(), read.About,
                read.Unreadable.Count == 0 ? null : $"The youth in {string.Join(", ", read.Unreadable)} couldn't be read, so any proposals approved there aren't listed. The other events are.");
        });

    /// <summary>The View menu's table pages, in its order after Event.</summary>
    public static readonly TableSpec[] All = [Results, Adults, Youth, PreRegistered, AdultHistory, Rooms, ApprovedProposals];
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
    private readonly BoardService _svc;
    private readonly ObservableCollection<RecordRow> _rows = [];
    private readonly ListCollectionView _view;
    private readonly StackPanel _commands = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, -8, 0) };
    private readonly TextBlock _count = new() { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Bottom };
    private readonly TextBlock _description = new() { TextWrapping = TextWrapping.Wrap };
    private int _pageCommands = 1;
    private bool _editing;

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
        SetDock(_count, Dock.Right);
        about.Children.Add(_count);
        _description.Text = spec.Description;
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
        if (spec.RowStyle != null)
        {
            Grid.RowStyle = (Style)resource(spec.RowStyle);
        }

        foreach (var col in spec.Columns)
        {
            Grid.Columns.Add(MakeColumn(col, resource));
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

        // A choice in a cell (a status, a result, a role) is saved as it's
        // picked; end the cell's edit there too, so nothing is left open for
        // a later cancel, a reload or another page to catch half done. The
        // editor's first selection, as it opens, isn't a pick: nothing is
        // unselected by it.
        Grid.AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, e) =>
        {
            if (e.OriginalSource is ComboBox && e.RemovedItems.Count > 0 && e.AddedItems.Count > 0)
            {
                Dispatcher.BeginInvoke(CommitEdit, System.Windows.Threading.DispatcherPriority.Background);
            }
        }));
        var card = new Border { Child = Grid };
        card.SetResourceReference(StyleProperty, "Card");
        Children.Add(card);

        Find.TextChanged += (_, _) =>
        {
            _view.Refresh();
            UpdateCount();
        };

        if (spec.IsRooms)
        {
            AddCommand("", "Add room...", "Add a room", () => addRoom?.Invoke(), pageCommand: false);
            AddCommand("", "Remove room", "Remove the selected room, if no board is in it", RemoveRoom, pageCommand: false);
        }

        if (spec.ExportName != null)
        {
            AddCommand("", "Export...", "Save this table as a CSV file for Excel", Export, pageCommand: false);
        }

        if (spec.CanDelete && !spec.IsRooms)
        {
            AddCommand("", "Delete...", "Delete the selected record", Delete, pageCommand: false);
        }
    }

    public TableSpec Spec { get; }

    public DataGrid Grid { get; }

    /// <summary>The page's find box (Ctrl+F).</summary>
    public TextBox Find { get; }

    /// <summary>Messages about this table, where it is.</summary>
    public InfoBar Notice { get; } = new() { Margin = new Thickness(0, 0, 0, 12), IsClosable = true };

    public RecordRow? Selected => Grid.SelectedItem as RecordRow;

    public IEnumerable<RecordRow> Rows => _rows;

    public event Action? SelectionChanged;

    /// <summary>After each reload: for what only the window knows (who an adult came to support).</summary>
    public event Action? Reloaded;

    /// <summary>A command for this page, before Export and Delete.</summary>
    public Button AddCommand(string glyph, string text, string tooltip, Action click, bool pageCommand = true)
    {
        var label = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = new TextBlock { Text = glyph, Margin = new Thickness(0, 0, 8, 0) };
        icon.SetResourceReference(StyleProperty, "Glyph");
        label.Children.Add(icon);
        label.Children.Add(new TextBlock { Text = text });
        var button = new Button { Content = label, ToolTip = tooltip };
        button.SetResourceReference(StyleProperty, "CommandButton");
        AutomationProperties.SetName(button, text.TrimEnd('.'));
        button.Click += (_, _) => click();
        _commands.Children.Insert(pageCommand ? _pageCommands++ : _commands.Children.Count, button);
        return button;
    }

    /// <summary>Reload from the service, keeping the selection. A quiet reload waits out an open cell editor.</summary>
    /// <summary>
    /// Finish any cell still being edited, saving it: before the page is left
    /// or the window closed, so the other pages show what was typed.
    /// </summary>
    public void CommitEdit()
    {
        Grid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);
    }

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
            _description.Text = read.About is { } about ? $"{Spec.Description} {about}" : Spec.Description;
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

    private void UpdateCount()
    {
        var all = Spec.Filter is { } f ? _rows.Count(f) : _rows.Count;
        _count.Text = _view.Count == all ? $"{all} {(all == 1 ? "record" : "records")}" : $"{_view.Count} of {all} records";
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
            Notice.Show(Severity.Informational, "Select a row first", "Click the row to delete, then Delete.");
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
            Notice.Show(Severity.Informational, "Select a room first", "Click the room to remove, then Remove room.");
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
            Notice.Show(Severity.Error, "Couldn't export it", ex.Message);
        }
    }

    private static DataGridColumn MakeColumn(TableColumn col, Func<string, object> resource)
    {
        var path = col.Path ?? "[" + col.Field + "]";
        var sort = col.Path is { } p && !p.StartsWith('[') ? p : "[" + col.Field + "]";
        switch (col.Kind)
        {
            case ColumnKind.Choice:
                return new DataGridComboBoxColumn
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
            case ColumnKind.Status:
                return new DataGridTemplateColumn
                {
                    Header = col.Header,
                    Width = col.Width,
                    SortMemberPath = sort,
                    CellTemplate = (DataTemplate)resource("StatusCell"),
                    IsReadOnly = true,
                };
            case ColumnKind.WoodBadge:
                var marks = new FrameworkElementFactory(typeof(AdultMarks));
                marks.SetBinding(AdultMarks.WoodBadgeProperty, new Binding(nameof(RecordRow.IsWoodBadge)) { Mode = BindingMode.OneWay });
                marks.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Left);
                marks.SetValue(MarginProperty, new Thickness(-6, 0, 0, 0));
                return new DataGridTemplateColumn
                {
                    Header = col.Header,
                    Width = col.Width,
                    SortMemberPath = sort,
                    CellTemplate = new DataTemplate { VisualTree = marks },
                    CellEditingTemplate = ChoiceEditor(path, col.Choices!),
                };
            default:
                var readOnly = col.Kind == ColumnKind.ReadOnly;
                return new DataGridTextColumn
                {
                    Header = col.Header,
                    Width = col.Width,
                    Binding = new Binding(path) { Mode = readOnly ? BindingMode.OneWay : BindingMode.TwoWay },
                    IsReadOnly = readOnly,
                    SortMemberPath = sort,
                };
        }
    }

    /// <summary>A choice while the Wood Badge column is being edited, saved as soon as it's picked.</summary>
    private static DataTemplate ChoiceEditor(string path, KeyValuePair<string, string>[] choices)
    {
        var combo = new FrameworkElementFactory(typeof(ComboBox));
        combo.SetValue(StyleProperty, Application.Current.FindResource("DefaultComboBoxStyle"));
        combo.SetValue(ItemsControl.ItemsSourceProperty, choices);
        combo.SetValue(ItemsControl.DisplayMemberPathProperty, "Value");
        combo.SetValue(Selector.SelectedValuePathProperty, "Key");
        combo.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        combo.SetBinding(Selector.SelectedValueProperty, new Binding(path) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        return new DataTemplate { VisualTree = combo };
    }
}
