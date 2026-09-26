using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using EagleBoards.Core;
using EagleBoards.Core.Records;
using Microsoft.Win32;

namespace EagleBoards.App;

/// <summary>A record in an Admin table, with an indexer the grid binds to ("[Last]").</summary>
public sealed class RecordRow : Row
{
    private Dictionary<string, string> _values;

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
            Raise("Item[]");
            Edited?.Invoke(this, field, value);
        }
    }

    public string Describe() => this["Last"].Length > 0 ? $"{this["Last"]}, {this["First"]}" : this["Room"].Length > 0 ? "room " + this["Room"] : Id;

    public bool Contains(string text) => _values.Values.Any(v => v.Contains(text, StringComparison.OrdinalIgnoreCase));

    public void Update(IReadOnlyDictionary<string, string> values)
    {
        _values = new Dictionary<string, string>(values, StringComparer.Ordinal);
        Raise("Item[]");
    }
}

/// <summary>
/// View and edit every table: the Java app's admin.html. Edits go straight
/// to <see cref="BoardService.SaveRow"/>, the same path the website's
/// "-update" endpoints use.
/// </summary>
public partial class AdminWindow : Window
{
    private enum Kind
    {
        Text,
        Choice,
        ReadOnly,
    }

    private sealed record Col(string Field, string Header, double Width, Kind Kind = Kind.Text, KeyValuePair<string, string>[]? Choices = null, string? Display = null);

    private sealed record TabSpec(string Title, DataTable Table, Col[] Columns, string ExportName, bool Rooms = false);

    private static KeyValuePair<string, string>[] Same(params string[] values) => values.Select(v => new KeyValuePair<string, string>(v, v)).ToArray();

    private static readonly KeyValuePair<string, string>[] BoardTypeChoices =
        [new("", ""), new(BoardTypes.Final, Display.BoardType(BoardTypes.Final)), new(BoardTypes.Project, Display.BoardType(BoardTypes.Project))];
    private static readonly KeyValuePair<string, string>[] UnitTypeChoices = Same(["", .. UnitTypes.All]);
    private static readonly KeyValuePair<string, string>[] StatusChoices = [new("", ""), .. BoardStatus.All.Select(v => new KeyValuePair<string, string>(v, Display.Status(v)))];
    // A board's three decisions only. Not "Postponed": that is the Status of a
    // scout sent away unprepared before any board met them, who has no Result.
    private static readonly KeyValuePair<string, string>[] ResultChoices = [new("", ""), .. BoardResults.All.Select(v => new KeyValuePair<string, string>(v, Display.Result(v)))];
    private static readonly KeyValuePair<string, string>[] RoleChoices = Same(["", .. BoardRoles.All]);

    private static readonly TabSpec[] Specs =
    [
        new("Boards", DataTable.Scouts,
        [
            new("Last", "Last", 100), new("First", "First", 100), new("Phone", "Phone", 105), new("Email", "Email", 150),
            new("BoardType", "Board", 110, Kind.Choice, BoardTypeChoices), new("DOB", "Birth date", 85),
            new("UnitType", "Unit type", 85, Kind.Choice, UnitTypeChoices), new("Unit", "Unit", 60), new("Leader", "Leader", 110),
            new("Status", "Status", 95, Kind.Choice, StatusChoices), new("Result", "Result", 100, Kind.Choice, ResultChoices),
            new("BoardChair", "Chair", 120), new("BoardMembers", "Members", 180), new("Notes", "Notes", 250),
        ], "Report"),
        new("Youth", DataTable.Scouts,
        [
            new("RegTime", "Signed in", 72, Kind.ReadOnly, Display: DataRecord.RegTimeHmField), new("RegNum", "Sign-in", 64, Kind.ReadOnly),
            new("Last", "Last", 110), new("First", "First", 110), new("DOB", "Birth date", 85),
            new("UnitType", "Unit type", 85, Kind.Choice, UnitTypeChoices), new("Unit", "Unit", 60), new("Leader", "Leader", 130),
            new("Email", "Email", 170), new("Phone", "Phone", 110), new("BoardType", "Board", 110, Kind.Choice, BoardTypeChoices),
            new("Room", "Room", 60), new("Status", "Status", 95, Kind.Choice, StatusChoices), new("Result", "Result", 100, Kind.Choice, ResultChoices),
        ], "Youth"),
        new("Pre-registered", DataTable.ScoutsScheduled,
        [
            new("Last", "Last", 110), new("First", "First", 110), new("DOB", "Birth date", 85),
            new("UnitType", "Unit type", 85, Kind.Choice, UnitTypeChoices), new("Unit", "Unit", 60), new("Leader", "Leader", 150),
            new("Email", "Email", 190), new("Phone", "Phone", 110), new("BoardType", "Board", 110, Kind.Choice, BoardTypeChoices),
        ], "YouthScheduled"),
        new("Adults", DataTable.Adults,
        [
            new("RegTime", "Signed in", 72, Kind.ReadOnly, Display: DataRecord.RegTimeHmField),
            new("Last", "Last", 120), new("First", "First", 120), new("UnitType", "Unit type", 90, Kind.Choice, UnitTypeChoices),
            new("Unit", "Unit", 70), new("Email", "Email", 190), new("Phone", "Phone", 110),
            new("FinalBoard", "Final role", 100, Kind.Choice, RoleChoices), new("ProjectReview", "Project role", 110, Kind.Choice, RoleChoices),
            new("Room", "Room", 70),
        ], "Adults"),
        new("Adult history", DataTable.AdultHistory,
        [
            new("Last", "Last", 120), new("First", "First", 120), new("UnitType", "Unit type", 90, Kind.Choice, UnitTypeChoices),
            new("Unit", "Unit", 70), new("Email", "Email", 200), new("Phone", "Phone", 110),
            new("FinalBoard", "Final role", 100, Kind.Choice, RoleChoices), new("ProjectReview", "Project role", 110, Kind.Choice, RoleChoices),
            new("BoardHistory", "Events signed in", 300, Kind.ReadOnly),
        ], "AdultHistory"),
        new("Rooms", DataTable.Rooms,
        [
            new("Room", "Room", 90, Kind.ReadOnly), new("BoardType", "Board type", 140, Kind.Choice, BoardTypeChoices),
            new("Scout", "Youth", 220, Kind.ReadOnly), new("Leaders", "Members", 500, Kind.ReadOnly),
        ], "Rooms", Rooms: true),
    ];

    private readonly BoardService _svc;
    private readonly List<AdminTab> _tabs = [];
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public AdminWindow(BoardService svc)
    {
        _svc = svc;
        InitializeComponent();
        foreach (var spec in Specs)
        {
            var tab = new AdminTab(this, spec);
            _tabs.Add(tab);
            Tabs.Items.Add(tab.Item);
        }

        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Current?.Reload(quiet: true);
        };
        EventHandler<DataChangedEventArgs> onChanged = (_, e) => Dispatcher.BeginInvoke(() =>
        {
            if (Current is { } t && e.Tables.Contains(t.Spec.Table))
            {
                _debounce.Stop();
                _debounce.Start();
            }
        });
        _svc.Changed += onChanged;
        Closed += (_, _) => _svc.Changed -= onChanged;
        Tabs.SelectedIndex = 0;
    }

    private AdminTab? Current => Tabs.SelectedIndex >= 0 ? _tabs[Tabs.SelectedIndex] : null;

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, Tabs))
        {
            Current?.Reload(quiet: true);
        }
    }

    /// <summary>One tab: toolbar, filter, grid.</summary>
    private sealed class AdminTab
    {
        private readonly AdminWindow _owner;
        private readonly ObservableCollection<RecordRow> _rows = [];
        private readonly ListCollectionView _view;
        private readonly DataGrid _grid;
        private readonly TextBox _filter = new() { Width = 200, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        private readonly TextBlock _count = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        private bool _editing;

        public AdminTab(AdminWindow owner, TabSpec spec)
        {
            _owner = owner;
            Spec = spec;
            _view = new ListCollectionView(_rows) { Filter = o => _filter.Text.Trim() is var f && (f.Length == 0 || ((RecordRow)o).Contains(f)) };
            _grid = new DataGrid
            {
                ItemsSource = _view,
                IsReadOnly = false,
                SelectionMode = DataGridSelectionMode.Single,
                FrozenColumnCount = spec.Rooms ? 1 : 2,
            };
            foreach (var col in spec.Columns)
            {
                _grid.Columns.Add(MakeColumn(col));
            }

            _grid.BeginningEdit += (_, _) => _editing = true;
            _grid.CellEditEnding += (_, _) => _editing = false;
            _count.SetResourceReference(FrameworkElement.StyleProperty, "Secondary");
            System.Windows.Automation.AutomationProperties.SetName(_filter, "Find in " + spec.Title);
            _filter.TextChanged += (_, _) =>
            {
                _view.Refresh();
                UpdateCount();
            };

            var bar = new WrapPanel { Margin = new Thickness(0, 12, 0, 12) };
            bar.Children.Add(Button("Refresh", () => Reload(quiet: false)));
            bar.Children.Add(Button("Export CSV...", Export));
            if (spec.Rooms)
            {
                var room = new TextBox { Width = 80, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0), ToolTip = "Room number" };
                System.Windows.Automation.AutomationProperties.SetName(room, "Room number");
                var type = new ComboBox { ItemsSource = BoardTypeChoices.Skip(1).ToList(), DisplayMemberPath = "Value", SelectedValuePath = "Key", SelectedIndex = 0, Width = 150, Margin = new Thickness(0, 0, 8, 0) };
                bar.Children.Add(room);
                bar.Children.Add(type);
                bar.Children.Add(Button("Add room", () =>
                {
                    var result = _owner._svc.AddRoom(room.Text, (string)type.SelectedValue);
                    if (result.Ok)
                    {
                        room.Text = "";
                    }
                    else
                    {
                        _owner.Notice.Show(Severity.Error, "Couldn't add the room", result.Message);
                    }

                    Reload(quiet: true);
                }));
                bar.Children.Add(Button("Remove room", Delete));
            }
            else
            {
                bar.Children.Add(Button("Delete...", Delete));
            }

            bar.Children.Add(new TextBlock { Text = "Find", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) });
            bar.Children.Add(_filter);
            bar.Children.Add(_count);

            var panel = new DockPanel();
            DockPanel.SetDock(bar, Dock.Top);
            panel.Children.Add(bar);
            panel.Children.Add(_grid);
            Item = new TabItem { Header = spec.Title, Content = panel };
        }

        public TabSpec Spec { get; }

        public TabItem Item { get; }

        private static Button Button(string text, Action click)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0) };
            b.Click += (_, _) => click();
            return b;
        }

        private static DataGridColumn MakeColumn(Col col)
        {
            var path = "[" + (col.Display ?? col.Field) + "]";
            switch (col.Kind)
            {
                case Kind.Choice:
                    return new DataGridComboBoxColumn
                    {
                        Header = col.Header,
                        Width = col.Width,
                        ItemsSource = col.Choices,
                        DisplayMemberPath = "Value",
                        SelectedValuePath = "Key",
                        SelectedValueBinding = new Binding(path) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
                        SortMemberPath = path,

                        // The column's own editing combo is the classic one:
                        // a white list with invisible items in dark mode.
                        EditingElementStyle = (Style)Application.Current.FindResource("DefaultComboBoxStyle"),
                    };
                default:
                    return new DataGridTextColumn
                    {
                        Header = col.Header,
                        Width = col.Width,
                        Binding = new Binding(path) { Mode = col.Kind == Kind.ReadOnly ? BindingMode.OneWay : BindingMode.TwoWay },
                        IsReadOnly = col.Kind == Kind.ReadOnly,
                        SortMemberPath = "[" + col.Field + "]",
                    };
            }
        }

        /// <summary>Reload from the service, keeping selection. A quiet reload waits out an open cell editor.</summary>
        public void Reload(bool quiet)
        {
            if (quiet && _editing)
            {
                return;
            }

            var selected = (_grid.SelectedItem as RecordRow)?.Id;
            RowSync.Sync(_rows, _owner._svc.Snapshot(Spec.Table), r => new RecordRow(r) { Edited = OnEdited }, (row, r) => row.Update(r));
            if (selected != null && _rows.FirstOrDefault(r => r.Id == selected) is { } again)
            {
                _grid.SelectedItem = again;
            }

            UpdateCount();
        }

        private void UpdateCount() => _count.Text = _view.Count == _rows.Count ? $"{_rows.Count} rows" : $"{_view.Count} of {_rows.Count} rows";

        private void OnEdited(RecordRow row, string field, string value)
        {
            var action = _owner._svc.SaveRow(Spec.Table, "updated", row.Id, new Dictionary<string, string> { [field] = value });
            if (action == "invalid")
            {
                _owner.Notice.Show(Severity.Warning, "That record is gone", "It may have been deleted. The table has been reloaded.");
                _owner.Dispatcher.BeginInvoke(() => Reload(quiet: false));
            }
        }

        private void Delete()
        {
            if (_grid.SelectedItem is not RecordRow row)
            {
                _owner.Notice.Show(Severity.Informational, "Select a row first", "Click the row to delete, then Delete.");
                return;
            }

            if (Spec.Rooms)
            {
                var result = _owner._svc.RemoveRoom(row.Id);
                if (!result.Ok)
                {
                    _owner.Notice.Show(Severity.Error, "Couldn't remove the room", result.Message);
                }
            }
            else if (AppDialog.Confirm(_owner, "Delete this record?", $"**{row.Describe()}** will be removed from {Spec.Title}. This can't be undone.", "Delete"))
            {
                _owner._svc.SaveRow(Spec.Table, "deleted", row.Id, new Dictionary<string, string>());
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
            if (dialog.ShowDialog(_owner) != true)
            {
                return;
            }

            try
            {
                var columns = Spec.Columns.Select(c => c.Field).ToList();
                File.WriteAllText(dialog.FileName, _owner._svc.ExportCsv(Spec.Table, columns), new UTF8Encoding(true));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _owner.Notice.Show(Severity.Error, "Couldn't export it", ex.Message);
            }
        }
    }
}
