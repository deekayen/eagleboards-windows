using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using EagleBoards.Core;
using EagleBoards.Core.Records;
using Microsoft.Win32;

namespace EagleBoards.App;

/// <summary>
/// The operator's screen: youth and adults side by side, the room cards,
/// and the list of boards. Replaces the Java app's scheduler.html and its
/// scheduler_*.js / process_*.js, with the same workflow.
///
/// Data is read from and written to <see cref="BoardService"/> in-process.
/// A sign-in on the website raises <see cref="BoardService.Changed"/>, which
/// refreshes the screen at once; a timer also refreshes every
/// RefreshTimeSecs so the minute counts and room timers keep moving.
/// </summary>
public partial class MainWindow : Window
{
    private static readonly string[] ReportColumns =
        ["RegNum", "Last", "First", "Phone", "Email", "BoardType", "DOB", "UnitType", "Unit", "Leader", "Status", "Result", "BoardChair", "BoardMembers", "Notes"];

    private readonly EventSession _session;
    private readonly BoardService _svc;
    private readonly ObservableCollection<ScoutRow> _scouts = [];
    private readonly ObservableCollection<AdultRow> _adults = [];
    private readonly ObservableCollection<RoomCard> _rooms = [];
    private readonly ObservableCollection<Toast> _toasts = [];
    private readonly ListCollectionView _scoutView;
    private readonly ListCollectionView _boardView;
    private readonly ListCollectionView _adultView;
    private readonly ListCollectionView _roomView;
    private readonly DispatcherTimer _poll = new();
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(150) };

    private ConfigRecord _config = new();
    private bool _quiet;
    private string? _focusRoom;
    private AdminWindow? _admin;

    public MainWindow(EventSession session)
    {
        _session = session;
        _svc = session.Service;
        InitializeComponent();
        Title = "Eagle Board Scheduler " + AppVersion.Text;

        _scoutView = new ListCollectionView(_scouts) { Filter = o => ScoutVisible((ScoutRow)o), IsLiveFiltering = true, IsLiveSorting = true };
        _scoutView.LiveFilteringProperties.Add(nameof(ScoutRow.Status));
        _scoutView.SortDescriptions.Add(new SortDescription(nameof(ScoutRow.RegNumSort), ListSortDirection.Ascending));
        _scoutView.LiveSortingProperties.Add(nameof(ScoutRow.RegNumSort));
        ScoutGrid.ItemsSource = _scoutView;

        _boardView = new ListCollectionView(_scouts) { Filter = o => BoardVisible((ScoutRow)o), IsLiveFiltering = true };
        _boardView.LiveFilteringProperties.Add(nameof(ScoutRow.Status));
        BoardGrid.ItemsSource = _boardView;

        _adultView = new ListCollectionView(_adults) { Filter = o => AdultVisible((AdultRow)o), IsLiveFiltering = true };
        _adultView.LiveFilteringProperties.Add(nameof(AdultRow.Room));
        _adultView.LiveFilteringProperties.Add(nameof(AdultRow.FinalBoard));
        AdultGrid.ItemsSource = _adultView;

        _roomView = new ListCollectionView(_rooms) { Filter = o => RoomVisible((RoomCard)o) };
        RoomList.ItemsSource = _roomView;
        ToastList.ItemsSource = _toasts;

        UrlList.ItemsSource = session.CheckInUrls;
        DataText.Text = "   Data: " + session.Plan.DataDirectory;
        if (session.ImportError != null)
        {
            ImportText.Text = "SignUpGenius import failed";
            ImportText.ToolTip = session.ImportError;
            ImportText.Foreground = Brushes.Firebrick;
        }
        else
        {
            ImportText.Text = session.ImportSummary ?? "";
        }

        _svc.Changed += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            _debounce.Stop();
            _debounce.Start();
        });
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            RefreshAll();
        };
        _poll.Tick += (_, _) => RefreshAll();

        InputBindings.Add(new KeyBinding(new RelayCommand(RefreshAll), Key.F5, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => OnHelp(this, new RoutedEventArgs())), Key.F1, ModifierKeys.None));
        Closing += OnClosing;

        RefreshAll();
        UpdateButtons();
        if (session.ImportError != null)
        {
            Notify("SignUpGenius", "The sign-up import failed, so pre-registrations are not loaded:\n" + session.ImportError, ToastKind.Warn, 20000);
        }
    }

    // ------------------------------------------------------------------
    // Refresh
    // ------------------------------------------------------------------

    private void RefreshAll()
    {
        _config = _svc.GetConfig();
        RowSync.Sync(_scouts, _svc.Snapshot(DataTable.Scouts), r => new ScoutRow(r), (row, r) => row.Update(r));
        foreach (var s in _scouts)
        {
            s.ApplyColors(_config);
        }

        RowSync.Sync(_adults, _svc.Snapshot(DataTable.Adults), r => new AdultRow(r) { OnSelChanged = OnPickChanged }, (row, r) => row.Update(r));
        RowSync.Sync(_rooms, _svc.Snapshot(DataTable.Rooms), r => new RoomCard(r), (row, r) => row.Update(r));

        ApplyFocusRoom();
        UpdateRoomTimers();
        UpdateButtons();
        UpdatePickCount();
        ClockText.Text = DateTime.Now.ToString("h:mm tt");

        var interval = TimeSpan.FromSeconds(Math.Max(5, _config.RefreshTimeSecs));
        if (_poll.Interval != interval || !_poll.IsEnabled)
        {
            _poll.Interval = interval;
            _poll.Start();
        }
    }

    /// <summary>
    /// "[12m]" on each room holding a board, orange then red at the
    /// board-type thresholds. Runs on the youth's minutes-since-last-change,
    /// so the clock restarts by itself at Seat and again at Start Review.
    /// </summary>
    private void UpdateRoomTimers()
    {
        foreach (var card in _rooms)
        {
            var scout = _scouts.FirstOrDefault(s => s.Room == card.Room && BoardStatus.IsActive(s.Status));
            if (scout?.Mins is { } mins)
            {
                card.TimerText = $"[{mins}m]";
                card.TimerState = SchedulerLogic.TimerFor(scout.Status, scout.BoardType, mins, _config);
            }
            else
            {
                card.TimerText = "";
                card.TimerState = TimerState.None;
            }
        }
    }

    private void UpdateButtons()
    {
        var actions = SelectedScout is { } s ? SchedulerLogic.ActionsFor(s.Status) : ScoutActions.None;
        SeatButton.IsEnabled = actions.HasFlag(ScoutActions.Seat);
        StartButton.IsEnabled = actions.HasFlag(ScoutActions.Start);
        CompleteButton.IsEnabled = actions.HasFlag(ScoutActions.Complete);
        LocateButton.IsEnabled = actions.HasFlag(ScoutActions.Locate);
        ResetButton.IsEnabled = actions.HasFlag(ScoutActions.Reset);
        PostponeButton.IsEnabled = actions.HasFlag(ScoutActions.Postpone);

        var adult = AdultGrid.SelectedItem as AdultRow;
        EnableButton.IsEnabled = adult is { IsDisabled: true };
        DisableButton.IsEnabled = adult is { Room: "" };
        LinkButton.IsEnabled = adult != null;
    }

    private void UpdatePickCount()
    {
        var n = _adults.Count(a => a.Sel);
        PickCount.Text = n == 0 ? "" : $"{n} picked";
    }

    // ------------------------------------------------------------------
    // Filters
    // ------------------------------------------------------------------

    private static bool Matches(string filter, params string[] fields) =>
        filter.Length == 0 || fields.Any(f => f.Contains(filter, StringComparison.OrdinalIgnoreCase));

    private bool ScoutVisible(ScoutRow s) =>
        (ShowFinishedToggle.IsChecked == true || !BoardStatus.IsFinished(s.Status))
        && Matches(ScoutFilter.Text.Trim(), s.RegNum, s.Last, s.First, s.UnitName, s.BoardType, s.Room, s.Status, s.Leader);

    private bool BoardVisible(ScoutRow s) =>
        s.Status is BoardStatus.Seated or BoardStatus.InProgress or BoardStatus.Completed or BoardStatus.Postponed
        && Matches(BoardFilter.Text.Trim(), s.RegNum, s.Last, s.First, s.UnitName, s.Leader, s.Status, s.Room, s.Result, s.BoardChair, s.BoardMembers, s.Notes);

    /// <summary>
    /// By default only adults free to sit: not on a board, not gone home, not
    /// unavailable for final boards. The board in focus stays visible so you
    /// can see who is on it.
    /// </summary>
    private bool AdultVisible(AdultRow a) =>
        (ShowAllAdultsToggle.IsChecked == true || (a.Room.Length == 0 && a.FinalBoard != BoardRoles.Unavailable)
            || (_focusRoom != null && a.Room == _focusRoom))
        && Matches(AdultFilter.Text.Trim(), a.Last, a.First, a.UnitName, a.Room, a.FinalBoard, a.ProjectReview);

    private bool RoomVisible(RoomCard r) => Matches(RoomFilter.Text.Trim(), r.Room, r.Scout, r.Leaders);

    private void OnScoutFilter(object sender, TextChangedEventArgs e) => _scoutView?.Refresh();

    private void OnBoardFilter(object sender, TextChangedEventArgs e) => _boardView?.Refresh();

    private void OnAdultFilter(object sender, TextChangedEventArgs e) => _adultView?.Refresh();

    private void OnRoomFilter(object sender, TextChangedEventArgs e) => _roomView?.Refresh();

    private void OnClearRoomFilter(object sender, RoutedEventArgs e) => RoomFilter.Text = "";

    private void OnScoutViewToggle(object sender, RoutedEventArgs e) => _scoutView.Refresh();

    private void OnAdultViewToggle(object sender, RoutedEventArgs e) => _adultView.Refresh();

    // ------------------------------------------------------------------
    // Selection cascade
    // ------------------------------------------------------------------

    private ScoutRow? SelectedScout => ScoutGrid.SelectedItem as ScoutRow;

    private RoomCard? SelectedRoom => _rooms.FirstOrDefault(r => r.IsSelected);

    /// <summary>Change a selection without running the cascade that a user's click runs.</summary>
    private void Quietly(Action action)
    {
        var was = _quiet;
        _quiet = true;
        try
        {
            action();
        }
        finally
        {
            _quiet = was;
        }
    }

    private void OnScoutSelected(object sender, SelectionChangedEventArgs e)
    {
        UpdateButtons();
        if (!_quiet && e.AddedItems.Count > 0 && SelectedScout is { } s)
        {
            AutoSelect(s);
        }
    }

    /// <summary>Clicking the row that is already selected runs the cascade again, as a fresh click would.</summary>
    private void OnScoutGridClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(ScoutGrid, (DependencyObject)e.OriginalSource) is DataGridRow { Item: ScoutRow row }
            && ReferenceEquals(row, SelectedScout))
        {
            Dispatcher.BeginInvoke(() => AutoSelect(row), DispatcherPriority.Input);
        }
    }

    private void OnBoardSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_quiet || e.AddedItems.Count == 0 || BoardGrid.SelectedItem is not ScoutRow row)
        {
            return;
        }

        Quietly(() =>
        {
            ScoutGrid.SelectedItem = _scoutView.Contains(row) ? row : null;
            ScoutGrid.ScrollIntoView(row);
        });
        UpdateButtons();
        AutoSelect(row);
    }

    private void OnAdultSelected(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void OnRoomCardClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RoomCard card })
        {
            SelectRoomCard(card);
            if (!card.IsFree)
            {
                FocusRoom(card.Room);
            }
        }
    }

    private void SelectRoomCard(RoomCard? card)
    {
        foreach (var r in _rooms)
        {
            r.IsSelected = ReferenceEquals(r, card);
        }
    }

    /// <summary>
    /// What clicking a youth does. Picks the operator has already made are
    /// their work in progress and are never thrown away by clicking around --
    /// only Clear picks does that. Otherwise: a waiting youth gets a proposed
    /// board (a qualified chair, members from other units, a free room of the
    /// right type); a youth whose board is running brings its room into focus.
    /// </summary>
    private void AutoSelect(ScoutRow scout)
    {
        Quietly(() => AdultGrid.SelectedItem = null);
        var picksMade = _adults.Any(a => a.Sel);
        if (picksMade)
        {
            if (BoardStatus.IsActive(scout.Status))
            {
                FocusRoom(scout.Room);
            }

            return;
        }

        SelectRoomCard(null);
        SetFocusRoom(null);

        if (BoardStatus.IsActive(scout.Status))
        {
            FocusRoom(scout.Room);
        }
        else if (BoardStatus.IsWaiting(scout.Status))
        {
            // The other waiting youth in queue order (pre-registered first), so
            // the proposal keeps chairs and adults free for the boards to come.
            var waiting = _scouts
                .Where(s => s.Id != scout.Id && BoardStatus.IsWaiting(s.Status))
                .OrderBy(s => s.RegNumSort, StringComparer.Ordinal)
                .Select(s => s.Info)
                .ToList();
            // How long each adult has waited to volunteer: since sign-in, or
            // since the last board they sat on was completed.
            var since = SchedulerLogic.FreeSinceTimes(
                _svc.Snapshot(DataTable.Adults).Select(r => (r.GetValueOrDefault("ID", ""), r.GetValueOrDefault("RegTime", ""))),
                _svc.Snapshot(DataTable.Scouts).Select(r => (r.GetValueOrDefault("Status", ""),
                    r.GetValueOrDefault("BoardMembersIDs", ""), r.GetValueOrDefault("LastUpdateTime", ""))));
            var adults = _adults.Select(a => a.Info with { FreeSince = since.GetValueOrDefault(a.Id, "") }).ToList();
            var pick = SchedulerLogic.AutoSelect(scout.Info, adults, _rooms.Select(r => r.Info), waiting);
            foreach (var id in pick.AllAdultIds)
            {
                if (_adults.FirstOrDefault(a => a.Id == id) is { } adult)
                {
                    adult.Sel = true;
                }
            }

            SortPicksToTop();
            if (pick.RoomId != null)
            {
                var card = _rooms.FirstOrDefault(r => r.Id == pick.RoomId);
                SelectRoomCard(card);
                if (card != null)
                {
                    RoomList.UpdateLayout();
                    (RoomList.ItemContainerGenerator.ContainerFromItem(card) as FrameworkElement)?.BringIntoView();
                }
            }

            foreach (var problem in pick.Problems)
            {
                Notify("Auto Select", problem, ToastKind.Error);
            }

            if (pick.Problems.Count == 0)
            {
                Notify("Auto Select OK", $"Ready to seat:\n**{scout.Last}, {scout.First}**", ToastKind.Ok);
            }
        }
        else if (scout.Room.Length > 0 && scout.Room != AdultRoom.Disabled)
        {
            FocusRoom(scout.Room);
        }
        else
        {
            Quietly(() => BoardGrid.SelectedItem = _boardView.Contains(scout) ? scout : null);
        }
    }

    /// <summary>Bring a room's board into view everywhere: its card, its youth, its board row, its adults.</summary>
    private void FocusRoom(string room)
    {
        if (room.Length == 0 || room == AdultRoom.Disabled)
        {
            return;
        }

        SetFocusRoom(room);
        SelectRoomCard(_rooms.FirstOrDefault(r => r.Room == room));
        Quietly(() =>
        {
            var inRoom = _scouts.FirstOrDefault(s => s.Room == room && _boardView.Contains(s));
            BoardGrid.SelectedItem = inRoom;
            if (inRoom != null)
            {
                BoardGrid.ScrollIntoView(inRoom);
            }

            var scout = _scouts.FirstOrDefault(s => s.Room == room && _scoutView.Contains(s));
            if (scout != null && !ReferenceEquals(SelectedScout, scout))
            {
                ScoutGrid.SelectedItem = scout;
                ScoutGrid.ScrollIntoView(scout);
            }
        });
        UpdateButtons();
    }

    private void SetFocusRoom(string? room)
    {
        _focusRoom = room;
        ApplyFocusRoom();
        _adultView.Refresh();
    }

    private void ApplyFocusRoom()
    {
        foreach (var a in _adults)
        {
            a.IsHighlighted = _focusRoom != null && a.Room == _focusRoom;
        }
    }

    // ------------------------------------------------------------------
    // Adults
    // ------------------------------------------------------------------

    /// <summary>A tick, by hand or by auto-select, is saved at once so nothing can undo it behind the operator's back.</summary>
    private void OnPickChanged(AdultRow adult, bool picked)
    {
        _svc.SaveRow(DataTable.Adults, "updated", adult.Id, new Dictionary<string, string> { ["Sel"] = picked ? "1" : "0" });
        if (picked)
        {
            SortPicksToTop();
        }

        UpdatePickCount();
    }

    private void SortPicksToTop()
    {
        using (_adultView.DeferRefresh())
        {
            _adultView.SortDescriptions.Clear();
            _adultView.SortDescriptions.Add(new SortDescription(nameof(AdultRow.SelSort), ListSortDirection.Descending));
        }

        foreach (var column in AdultGrid.Columns)
        {
            column.SortDirection = column.SortMemberPath == nameof(AdultRow.SelSort) ? ListSortDirection.Descending : null;
        }

        if (_adultView.Count > 0)
        {
            AdultGrid.ScrollIntoView(_adultView.GetItemAt(0));
        }
    }

    private void OnClearPicks(object sender, RoutedEventArgs e)
    {
        foreach (var a in _adults.Where(a => a.Sel).ToList())
        {
            a.Sel = false;
        }
    }

    private void OnDisableAdult(object sender, RoutedEventArgs e)
    {
        if (AdultGrid.SelectedItem is not AdultRow a)
        {
            return;
        }

        if (a.Room.Length > 0)
        {
            Notify("Disable", $"{a.FullName} is {(a.IsDisabled ? "already disabled" : "currently in room " + a.Room)}.", ToastKind.Error);
            return;
        }

        if (Ask.Confirm(this, "Disable", $"{a.FullName} has gone home for tonight?\n\nThey won't be offered for boards until enabled again."))
        {
            if (a.Sel)
            {
                a.Sel = false;
            }

            _svc.SaveRow(DataTable.Adults, "updated", a.Id, new Dictionary<string, string> { ["Room"] = AdultRoom.Disabled });
        }
    }

    /// <summary>
    /// Link the selected adult to the selected youth as someone who came to
    /// support them, or unlink them -- for the adult who did not check the
    /// youth at sign-in. Start Review and Locate name them from then on.
    /// Works for an adult on a board too: a Scoutmaster often is by then.
    /// </summary>
    private void OnLinkAdult(object sender, RoutedEventArgs e)
    {
        if (AdultGrid.SelectedItem is not AdultRow a)
        {
            return;
        }

        if (SelectedScout is not { } scout)
        {
            Notify("Link", "Select the youth first, then the adult, and press Link.", ToastKind.Error);
            return;
        }

        var youth = $"{scout.First} {scout.Last}";
        var linked = a.Info.Supports(scout.Id);
        var question = linked
            ? $"{a.FullName} is linked as supporting {youth}. Unlink them?"
            : $"Link {a.FullName} as supporting {youth}?\n\nStart Review will then say where to find them.";
        if (!Ask.Confirm(this, linked ? "Unlink" : "Link", question))
        {
            return;
        }

        var result = _svc.SetSupporting(a.Id, scout.Id, !linked);
        Notify(linked ? "Unlinked" : "Linked",
            result.Ok ? $"{a.FullName} {(linked ? "is no longer linked to" : "is linked to")} {youth}." : result.Message,
            result.Ok ? ToastKind.Ok : ToastKind.Error);
    }

    private void OnEnableAdult(object sender, RoutedEventArgs e)
    {
        if (AdultGrid.SelectedItem is not AdultRow { IsDisabled: true } a)
        {
            return;
        }

        if (Ask.Confirm(this, "Enable", $"{a.FullName} is back and available for boards?"))
        {
            _svc.SaveRow(DataTable.Adults, "updated", a.Id, new Dictionary<string, string> { ["Room"] = "" });
        }
    }

    // ------------------------------------------------------------------
    // Board lifecycle
    // ------------------------------------------------------------------

    private ScoutRow? RequireScout()
    {
        if (SelectedScout is { } s)
        {
            return s;
        }

        Notify("Error", "No youth selected.", ToastKind.Error);
        return null;
    }

    /// <summary>
    /// Registered → Seated. Everything the server refuses is caught here
    /// first with an explanation; the same-unit rule is caught ONLY here,
    /// because it is a judgement call the operator may override down to the
    /// national floor. Order: who (unit), how many, chair, room.
    /// </summary>
    private void OnSeat(object sender, RoutedEventArgs e)
    {
        if (RequireScout() is not { } scout)
        {
            return;
        }

        var name = $"{scout.First} {scout.Last}";
        switch (scout.Status)
        {
            case BoardStatus.Seated:
                Notify("Seat", $"{name}'s board is already seated.", ToastKind.Error);
                return;
            case BoardStatus.InProgress:
                Notify("Seat", $"{name} is in a board now, in room {scout.Room}.", ToastKind.Error);
                return;
            case BoardStatus.Completed or BoardStatus.Postponed:
                Notify("Seat", $"{name}'s {scout.BoardType} board is already {scout.Status.ToLowerInvariant()}.", ToastKind.Error);
                return;
            case BoardStatus.Registered or BoardStatus.Verified:
                break;
            default:
                Notify("Seat", "Unknown status: " + scout.Status, ToastKind.Error);
                return;
        }

        if (scout.BoardType is not (BoardTypes.Final or BoardTypes.Project))
        {
            Notify("Seat", $"No board type (Final or Project) is set for {name}. Fix it on the Admin tables.", ToastKind.Error);
            return;
        }

        var picked = _adults.Where(a => a.Sel).ToList();
        if (picked.Count == 0)
        {
            Notify("Seat", "No board members picked. Tick them in the Adult Board Members list.", ToastKind.Error);
            return;
        }

        foreach (var a in picked)
        {
            if (a.IsDisabled)
            {
                Notify("Seat", $"**{a.Last}, {a.First}** has been disabled for tonight.\nUse Enable if they are back.", ToastKind.Error);
                return;
            }

            if (a.Room.Length > 0)
            {
                Notify("Seat", $"**{a.Last}, {a.First}** is already on the board in room {a.Room}.", ToastKind.Error);
                return;
            }

            if (a.Info.RoleFor(scout.BoardType) == BoardRoles.Unavailable)
            {
                Notify("Seat", $"**{a.Last}, {a.First}** is Unavailable for {scout.BoardType} boards. Untick them and pick someone else.", ToastKind.Error);
                return;
            }
        }

        var names = string.Join(", ", picked.Select(a => a.FullName));
        var candidates = picked.Select(a => new BoardCandidate(a.Id, a.Last, a.First, a.UnitName)).ToList();

        // Who: the council forbids adults from the youth's own unit; the
        // operator may fall back to the national rule, which still needs one
        // member from outside the unit (GTA 8.0.3.0 #2).
        var conflicts = BoardRules.FindUnitConflicts(scout.UnitName, candidates);
        if (conflicts.Count > 0)
        {
            var list = string.Join("\n", conflicts.Select(c => $"    {c.Last}, {c.First}"));
            if (!BoardRules.HasNonUnitMember(scout.UnitName, candidates))
            {
                Notify("Seat", $"**Every picked member is in {scout.UnitName}, the same unit as {name}:**\n{list}\n\n"
                    + "A board must include at least one member who is not affiliated with the unit (Guide to Advancement 8.0.3.0). "
                    + $"Add someone from outside {scout.UnitName}.", ToastKind.Error, 15000);
                return;
            }

            var n = conflicts.Count;
            if (!Ask.Confirm(this, "Unit conflict",
                    $"{n} picked member{(n == 1 ? " is" : "s are")} in {scout.UnitName}, the same unit as {name}:\n{list}\n\n"
                    + "This council does not permit adults from the youth's own unit on a board of review.\n\n"
                    + $"Continuing falls back to the national requirement, which this board still meets: at least one member is not affiliated with {scout.UnitName}.\n\n"
                    + "Seat this board anyway?", MessageBoxImage.Warning))
            {
                return;
            }
        }

        // Chair: binding. If no picked member may chair this board type, the
        // fix is to promote someone on the Admin tables -- not to seat a Member.
        var chairs = picked.Where(a => a.Info.RoleFor(scout.BoardType) == BoardRoles.Chair).ToList();
        if (chairs.Count == 0)
        {
            var col = scout.BoardType == BoardTypes.Project ? "Project" : "Final";
            Notify("Seat", $"**None of the picked members may chair a {scout.BoardType} board:**\n    {names}\n\n"
                + $"Pick someone whose {col} role is Chair, or, if someone here should be chairing, promote them on the Admin tables "
                + $"(Adults tab: set {col} to Chair) first.", ToastKind.Error, 15000);
            return;
        }

        // How many: GTA 8.0.0.3 for a board of review; two for a project review.
        var min = BoardRules.MinMembers(scout.BoardType);
        var kind = scout.BoardType == BoardTypes.Project ? "project review" : "board of review";
        switch (BoardRules.CheckSize(scout.BoardType, picked.Count))
        {
            case SizeVerdict.TooFew:
                Notify("Seat", $"Only {picked.Count} member(s) picked:\n    {names}\n\n{min} required for a {kind}. Pick {min - picked.Count} more.", ToastKind.Error);
                return;
            case SizeVerdict.TooMany:
                Notify("Seat", $"{picked.Count} members picked:\n    {names}\n\nA {kind} may have no more than six (Guide to Advancement 8.0.0.3). "
                    + $"Untick {picked.Count - BoardRules.BoardMaxMembers}.", ToastKind.Error);
                return;
            case SizeVerdict.OverPreferred:
                if (!Ask.Confirm(this, "Seat", $"{picked.Count} members picked:\n\n    {names}\n\nOnly {min} are required. Is this correct?"))
                {
                    return;
                }

                break;
        }

        // Where.
        if (SelectedRoom is not { } room)
        {
            Notify("Seat", "No room selected. Click a room card, then Seat Board again.", ToastKind.Error);
            return;
        }

        if (!room.IsFree)
        {
            Notify("Seat", $"Room {room.Room} is in use. Pick a different room.", ToastKind.Error);
            return;
        }

        if (room.BoardType != scout.BoardType
            && !Ask.Confirm(this, "Seat", $"Room {room.Room} is a {room.BoardType} room and this is a {scout.BoardType} board.\n\nIs this correct?"))
        {
            return;
        }

        // Confirm the chair among the qualified ones.
        var dialog = new FormDialog(this, "Seat Board: " + name, "Seat");
        dialog.AddNote($"Room **{room.Room}**, {scout.BoardType} board.\nMembers: {names}");
        var chairBox = dialog.AddChoice("Chair:", chairs.Select(c => new KeyValuePair<string, string>(c.Id, c.FullName)), chairs[0].Id);
        if (!dialog.ShowDialog())
        {
            return;
        }

        var result = _svc.SeatBoard(room.Id, scout.Id, chairBox.SelectedValue as string, string.Join(",", picked.Select(a => a.Id)));
        if (result.Ok)
        {
            Notify("Seated", $"**{name}**'s board is convening in room {room.Room}.", ToastKind.Ok);
            SetFocusRoom(room.Room);
        }
        else
        {
            Notify("Seat failed", result.Message, ToastKind.Error);
        }

        RefreshAll();
    }

    /// <summary>Seated → InProgress: the members have read the paperwork; bring the youth in.</summary>
    private void OnStart(object sender, RoutedEventArgs e)
    {
        if (RequireScout() is not { } scout)
        {
            return;
        }

        var name = $"{scout.First} {scout.Last}";
        if (scout.Status != BoardStatus.Seated)
        {
            Notify("Start Review", scout.Status == BoardStatus.InProgress
                ? $"{name}'s review has already started in room {scout.Room}."
                : $"{name}'s board has not been seated yet. Use Seat Board first.\n\nCurrent status: {scout.Status}", ToastKind.Error);
            return;
        }

        // Whoever came to support this youth -- often their Scoutmaster, who
        // may be on another board right now -- introduces them. Name the room
        // so someone can step in and fetch them for a moment.
        var fetch = new StringBuilder();
        foreach (var a in SchedulerLogic.SupportingAdults(scout.Id, _adults.Select(row => row.Info)))
        {
            fetch.Append(fetch.Length == 0 ? "\n\nBring out to introduce them:" : "")
                .Append($"\n    {a.First} {a.Last} — ")
                .Append(a.IsFree ? "main room" : a.Room == AdultRoom.Disabled ? "marked as gone home" : $"on the board in room {a.Room}");
        }

        if (!Ask.Confirm(this, "Start Review", $"Bring {name} in to room {scout.Room} and start the review?\n\n"
                + "Do this once the board members have finished reading the application, references and project workbook."
                + fetch))
        {
            return;
        }

        var result = _svc.StartReview(scout.Id);
        Notify(result.Ok ? "Review Started" : "Start failed", result.Ok ? $"{name}: review started." : result.Message,
            result.Ok ? ToastKind.Ok : ToastKind.Error);
        RefreshAll();
    }

    /// <summary>InProgress → Completed, with the result and notes.</summary>
    private void OnComplete(object sender, RoutedEventArgs e)
    {
        if (RequireScout() is not { } scout)
        {
            return;
        }

        var name = $"{scout.First} {scout.Last}";
        if (scout.Status != BoardStatus.InProgress)
        {
            Notify("Complete", scout.Status == BoardStatus.Completed ? $"{name}'s board is already completed."
                : $"{name}'s review has not started yet.", ToastKind.Error);
            return;
        }

        var dialog = new FormDialog(this, "Complete Board: " + name, "Complete");
        var resultBox = dialog.AddChoice("Result:", BoardResults.All.Select(r => new KeyValuePair<string, string>(r, r == BoardResults.NotApproved ? "Not Approved" : r)),
            BoardResults.Approved);
        var notesBox = dialog.AddMultiline("Notes:");
        if (!dialog.ShowDialog())
        {
            return;
        }

        var result = _svc.CompleteBoard(scout.Id, resultBox.SelectedValue as string, notesBox.Text.Trim());
        if (result.Ok)
        {
            Notify("Completed", $"**{name}**: {resultBox.SelectedValue}.", ToastKind.Ok);

            // Whoever brings the youth back out needs to know where their people are.
            ShowLocate(scout, includeParents: true);
        }
        else
        {
            Notify("Complete failed", result.Message, ToastKind.Error);
        }

        RefreshAll();
    }

    private void OnPostpone(object sender, RoutedEventArgs e)
    {
        if (RequireScout() is not { } scout)
        {
            return;
        }

        var name = $"{scout.First} {scout.Last}";
        if (!BoardStatus.IsWaiting(scout.Status))
        {
            Notify("Postpone", $"Only a waiting youth can be postponed; {name} is {scout.Status}.", ToastKind.Error);
            return;
        }

        if (Ask.Confirm(this, "Postpone", $"Postpone the board for {name}?"))
        {
            var result = _svc.PostponeBoard(scout.Id);
            Notify(result.Ok ? "Postponed" : "Postpone failed", result.Ok ? name : result.Message, result.Ok ? ToastKind.Ok : ToastKind.Error);
            RefreshAll();
        }
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        if (RequireScout() is not { } scout)
        {
            return;
        }

        var name = $"{scout.First} {scout.Last}";
        if (scout.Status is not (BoardStatus.Seated or BoardStatus.InProgress or BoardStatus.Verified))
        {
            Notify("Reset", $"There is no board to reset for {name} ({scout.Status}).", ToastKind.Error);
            return;
        }

        if (Ask.Confirm(this, "Reset", $"Reset the board for {name}?\n\nThey go back to Registered and room {scout.Room} and its members are freed."))
        {
            var result = _svc.ResetBoard(scout.Id);
            Notify(result.Ok ? "Reset" : "Reset failed", result.Ok ? name : result.Message, result.Ok ? ToastKind.Ok : ToastKind.Error);
            SetFocusRoom(null);
            RefreshAll();
        }
    }

    private void OnLocate(object sender, RoutedEventArgs e)
    {
        if (RequireScout() is { } scout)
        {
            ShowLocate(scout, includeParents: true);
        }
    }

    private void ShowLocate(ScoutRow scout, bool includeParents)
    {
        var found = SchedulerLogic.Locate(scout.Info, _adults.Select(a => a.Info), includeParents);
        if (found.Count == 0)
        {
            Notify("Cannot Locate", $"Youth: **{scout.First} {scout.Last}**\nLeader(s) '{scout.Leader}' **not signed in.**", ToastKind.Warn, 5000);
            return;
        }

        var text = new StringBuilder($"Youth: **{scout.First} {scout.Last}** [{scout.Room}]");
        foreach (var f in found)
        {
            var where = f.Adult.Room.Length == 0 ? "Main" : f.Adult.Room;
            var kind = f.IsSupporting ? "Supporting" : f.IsLeader ? "Leader" : "Parent";
            text.Append($"\n{kind}: **{f.Adult.First} {f.Adult.Last}** [{where}]");
        }

        Notify("Located", text.ToString(), ToastKind.Info, 60000);
    }

    // ------------------------------------------------------------------
    // Rooms
    // ------------------------------------------------------------------

    private static readonly KeyValuePair<string, string>[] BoardTypeChoices =
    [
        new(BoardTypes.Final, "Final Board"),
        new(BoardTypes.Project, "Proposal Review"),
    ];

    private void OnAddRoom(object sender, RoutedEventArgs e)
    {
        var dialog = new FormDialog(this, "Add Room", "Add");
        var roomBox = dialog.AddText("Room #:");
        var typeBox = dialog.AddChoice("Board type:", BoardTypeChoices, BoardTypes.Final);
        dialog.AddNote("For project reviews sharing one room, add one entry per table, e.g. 200A and 200B.");
        dialog.Validate = () =>
        {
            var room = roomBox.Text.Trim();
            if (room.Length == 0)
            {
                return "Room # is required.";
            }

            return _rooms.Any(r => r.Room == room) ? $"Room '{room}' already exists." : null;
        };
        if (dialog.ShowDialog())
        {
            var result = _svc.AddRoom(roomBox.Text.Trim(), (string)typeBox.SelectedValue);
            if (!result.Ok)
            {
                Notify("Add Room", result.Message, ToastKind.Error);
            }

            RefreshAll();
        }
    }

    private void OnRemoveRoom(object sender, RoutedEventArgs e)
    {
        if (SelectedRoom is not { } room)
        {
            Notify("Remove Room", "No room selected. Click a room card first.", ToastKind.Error);
            return;
        }

        if (!room.IsFree)
        {
            Notify("Remove Room", $"Room '{room.Room}' is in use and can't be removed.", ToastKind.Error);
            return;
        }

        if (Ask.Confirm(this, "Remove Room", $"Remove room '{room.Room}'?"))
        {
            var result = _svc.RemoveRoom(room.Id);
            if (!result.Ok)
            {
                Notify("Remove Room", result.Message, ToastKind.Error);
            }

            RefreshAll();
        }
    }

    /// <summary>Move the selected room's board to another room, or swap two boards.</summary>
    private void OnChangeRoom(object sender, RoutedEventArgs e)
    {
        if (SelectedRoom is not { } first)
        {
            Notify("Change Room", "No room selected. Click the room card to move from.", ToastKind.Error);
            return;
        }

        static string Label(RoomCard r) => r.IsFree ? $"{r.Room} ({r.BoardType})" : $"{r.Room} ({r.BoardType}) [{r.Scout}]";

        var others = _rooms.Where(r => !ReferenceEquals(r, first)).ToList();
        if (others.Count == 0)
        {
            Notify("Change Room", "There is no other room to move to.", ToastKind.Error);
            return;
        }

        var dialog = new FormDialog(this, "Change / Swap Rooms", "Change");
        dialog.AddText("From room:", Label(first), readOnly: true);
        var target = dialog.AddChoice("To room:", others.Select(r => new KeyValuePair<string, string>(r.Id, Label(r))), null);
        dialog.AddNote("If the other room has a board too, the two boards swap rooms.");
        if (!dialog.ShowDialog() || target.SelectedValue is not string secondId)
        {
            return;
        }

        var second = _rooms.First(r => r.Id == secondId);
        if (first.BoardType != second.BoardType
            && !Ask.Confirm(this, "Change Room", $"Room {first.Room} is a {first.BoardType} room and room {second.Room} is a {second.BoardType} room.\n\nIs this OK?"))
        {
            return;
        }

        var result = _svc.ChangeRoom(first.Id, second.Id);
        if (!result.Ok)
        {
            Notify("Change Room", result.Message, ToastKind.Error);
        }
        else if (_focusRoom == first.Room || _focusRoom == second.Room)
        {
            SetFocusRoom(null);
        }

        RefreshAll();
    }

    // ------------------------------------------------------------------
    // Menus and toolbar
    // ------------------------------------------------------------------

    private void OnRefresh(object sender, RoutedEventArgs e) => RefreshAll();

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        if (new SettingsWindow(_svc) { Owner = this }.ShowDialog() == true)
        {
            RefreshAll();
        }
    }

    private void OnReport(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save the board results",
            Filter = "CSV (opens in Excel) (*.csv)|*.csv",
            FileName = "Board_Results_" + Path.GetFileName(_session.Plan.DataDirectory.TrimEnd('\\', '/')) + ".csv",
            InitialDirectory = _session.Plan.DataDirectory,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            // With a byte-order mark so Excel reads accented names correctly.
            File.WriteAllText(dialog.FileName, _svc.ExportCsv(DataTable.Scouts, ReportColumns), new UTF8Encoding(true));
            Notify("Report", "Saved " + Path.GetFileName(dialog.FileName), ToastKind.Ok);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notify("Report", "Couldn't save it: " + ex.Message, ToastKind.Error);
        }
    }

    private void OnAdmin(object sender, RoutedEventArgs e)
    {
        if (_admin is { IsLoaded: true })
        {
            _admin.Activate();
            return;
        }

        _admin = new AdminWindow(_svc) { Owner = this };
        _admin.Show();
    }

    private void OnOpenCheckIn(object sender, RoutedEventArgs e) => OpenUrl(_session.LocalUrl);

    private void OnHelp(object sender, RoutedEventArgs e) => new HelpWindow { Owner = this }.Show();

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private void OnUrlClicked(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        OpenUrl(e.Uri.ToString());
        e.Handled = true;
    }

    private void OnCopyUrl(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string url })
        {
            Clipboard.SetText(url);
            Notify("Copied", url, ToastKind.Info, 2500);
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!Ask.Confirm(this, "Close the scheduler", "Closing the scheduler also stops the check-in website.\n\nClose it now?"))
        {
            e.Cancel = true;
            return;
        }

        _poll.Stop();
        _admin?.Close();
    }

    // ------------------------------------------------------------------
    // Notices
    // ------------------------------------------------------------------

    /// <summary>Show a notice top-right. Errors stay 8s by default, others 6s; click to dismiss sooner.</summary>
    private void Notify(string title, string body, ToastKind kind, int expireMs = 0)
    {
        var toast = new Toast(title, body, kind);
        _toasts.Add(toast);
        while (_toasts.Count > 6)
        {
            _toasts.RemoveAt(0);
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(expireMs > 0 ? expireMs : kind == ToastKind.Error ? 8000 : 6000) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _toasts.Remove(toast);
        };
        timer.Start();
    }

    private void OnToastClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Toast t })
        {
            _toasts.Remove(t);
        }
    }
}

internal sealed class RelayCommand(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute();
}
