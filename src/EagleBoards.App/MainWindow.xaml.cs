using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using EagleBoards.Core;
using EagleBoards.Core.Records;
using Microsoft.Win32;

namespace EagleBoards.App;

/// <summary>
/// The operator's window: a menu bar over the pages Event (the youth queue,
/// the rooms, and a details pane that builds and runs the selected youth's
/// board), and a page per table (Results, Adults and the rest), editable in
/// place, besides the read-only Adult history CSV. Settings, Help and the QR code open windows of their own.
///
/// Data is read from and written to <see cref="BoardService"/> in-process,
/// and every change, including a sign-in on the website, raises
/// <see cref="BoardService.Changed"/>, which refreshes the screen at once.
/// Nothing is polled. The only thing that moves by itself is time: a timer
/// on each minute recounts waiting times and room timers.
/// </summary>
public partial class MainWindow : Window
{
    // No Phone or DOB: a youth's are never exported (SPEC.md D-7, D-8).
    private static readonly string[] ReportColumns =
        ["RegNum", "Last", "First", "Email", "BoardType", "UnitType", "Unit", "Leader", "Status", "Result", "BoardChair", "BoardMembers", "Notes"];

    private readonly EventSession _session;
    private readonly BoardService _svc;
    private readonly ObservableCollection<ScoutRow> _scouts = [];
    private readonly ObservableCollection<AdultRow> _adults = [];
    private readonly ObservableCollection<RoomCard> _rooms = [];
    private readonly ListCollectionView _queueView;
    private readonly ListCollectionView _roomView;

    /// <summary>The table pages by name (<see cref="TableSpec.Name"/>): Results, Adults and the rest.</summary>
    private readonly Dictionary<string, TablePage> _tables = new(StringComparer.Ordinal);

    /// <summary>Every page and its View menu item, in the menu's order (Ctrl+1 and on).</summary>
    private readonly List<(MenuItem Item, FrameworkElement Page)> _pages = [];

    private readonly Button _goneHomeButton;
    private readonly Button _signInButton;
    private readonly Button _backButton;
    private readonly DispatcherTimer _minute = new();
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly DispatcherTimer _copied = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly Dictionary<string, RadioButton> _resultButtons = [];

    private ConfigRecord _config = new();
    private bool _quiet;
    private QrWindow? _qr;
    private SettingsWindow? _settings;

    /// <summary>The rooms Find a person narrows the Rooms card to, or null when it's empty.</summary>
    private HashSet<string>? _foundRooms;

    /// <summary>The youth the details pane shows, chosen by the operator.</summary>
    private ScoutRow? _current;

    /// <summary>The builder's choices that aren't saved until Seat: chair and room.</summary>
    private string? _chairId;

    private string? _builderRoomId;

    public MainWindow(EventSession session)
    {
        _session = session;
        _svc = session.Service;
        InitializeComponent();
        Title = "Eagle Board Scheduler " + AppVersion.Text;

        // Every youth in one list, stacked in three groups that are always
        // there, each headed with its count, even at nought (SPEC.md O-3).
        // No filter: Find a person, over the rooms, finds anyone (D-21).
        _queueView = new ListCollectionView(_scouts)
        {
            CustomSort = Comparer<object>.Create((a, b) => QueueOrder((ScoutRow)a, (ScoutRow)b)),
            IsLiveSorting = true,
            IsLiveGrouping = true,
        };
        var groups = new PropertyGroupDescription(nameof(ScoutRow.QueueGroup));
        foreach (var name in ScoutRow.QueueGroups)
        {
            groups.GroupNames.Add(name);
        }

        _queueView.GroupDescriptions.Add(groups);
        foreach (var p in new[] { nameof(ScoutRow.Status), nameof(ScoutRow.QueueGroup), nameof(ScoutRow.QueueSort), nameof(ScoutRow.Room) })
        {
            _queueView.LiveSortingProperties.Add(p);
            _queueView.LiveGroupingProperties.Add(p);
        }

        QueueList.ItemsSource = _queueView;

        // Every table a page of its own, editable in place (SPEC.md P-6).
        foreach (var spec in TableSpecs.All)
        {
            var table = new TablePage(_svc, spec, key => FindResource(key), spec.IsRooms ? () => OnAddRoom(this, new RoutedEventArgs()) : null);
            _tables[spec.Name] = table;
            PageHost.Children.Add(table);
        }

        var results = _tables[TableSpecs.Results.Name];
        results.AddCommand("", "Open on Event page", "Show the selected board on the Event page", OpenBoard);
        results.AddCommand("", "Save report...", "Save the event's results as a CSV file for Excel", () => OnReport(this, new RoutedEventArgs()));
        var open = new MenuItem { Header = "_Open on Event page" };
        open.Click += (_, _) => OpenBoard();
        results.Grid.ContextMenu = new ContextMenu { Items = { open } };

        var adults = _tables[TableSpecs.Adults.Name];
        adults.AddCommand("", "Add adult...", "Sign in an adult who would rather not use the tablet", OnAddAdult);
        _goneHomeButton = adults.AddCommand("", "Gone home", "The selected adult has gone home", OnGoneHome);
        _backButton = adults.AddCommand("", "Back", "The selected adult is back and available", OnBack);
        adults.SelectionChanged += UpdateAdultButtons;
        adults.Reloaded += () =>
        {
            var youth = _scouts.ToDictionary(s => s.Id, s => s.FullName, StringComparer.Ordinal);
            foreach (var adult in adults.Rows)
            {
                adult.SupportingNames = string.Join(", ", adult.Raw("Supporting").Split('|', StringSplitOptions.RemoveEmptyEntries)
                    .Select(id => youth.GetValueOrDefault(id, "")).Where(n => n.Length > 0));
            }
        };

        var history = _tables[TableSpecs.AdultHistory.Name];
        _signInButton = history.AddCommand("", "Sign in for today", "Put the selected adult on today's list, as if they had signed in at the tablet", OnSignInFromHistory);
        history.SelectionChanged += UpdateAdultButtons;

        _pages.Add((ViewEvent, EventPage));
        foreach (var item in ((MenuItem)ViewEvent.Parent).Items.OfType<MenuItem>())
        {
            if (item.Tag is string name && _tables.TryGetValue(name, out var page))
            {
                _pages.Add((item, page));
            }
        }

        _roomView = new ListCollectionView(_rooms) { Filter = o => _foundRooms?.Contains(((RoomCard)o).Room) ?? true };
        RoomList.ItemsSource = _roomView;

        foreach (var r in BoardResults.All)
        {
            var button = new RadioButton { Content = Display.Result(r), GroupName = "Result", Margin = new Thickness(0, 0, 0, 4), MinWidth = 0 };
            _resultButtons[r] = button;
            ResultChoices.Children.Add(button);
        }

        UrlList.ItemsSource = session.CheckInUrls;
        DataText.Text = "Data: " + session.Plan.DataDirectory;
        DataText.ToolTip = session.Plan.DataDirectory;
        ImportText.Text = session.ImportError != null ? "SignUpGenius import failed" : session.ImportSummary ?? "";
        if (session.ImportError != null)
        {
            ImportText.ToolTip = session.ImportError;
            ImportText.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCautionBrush");
        }
        // The address is read off this screen and typed into every station, so
        // say plainly when it's one the stations can't use.
        if (session.IsLocalOnly)
        {
            UrlLabel.Text = "This computer only: ";
            UrlLabel.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCautionBrush");
            Notice.Show(Severity.Warning, "Check-in stations can't connect",
                "The scheduler was started for this computer only (-bind 127.0.0.1), so other computers can't reach the sign-in page. "
                + "To use check-in stations, close the scheduler and start it again with the venue Wi-Fi as the check-in network.");
        }
        else if (session.CheckInUrls.Count == 0)
        {
            UrlLabel.Text = "No network found";
            UrlLabel.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCautionBrush");
            Notice.Show(Severity.Warning, "Check-in stations can't connect",
                "This computer isn't on a network, so there's no address for the stations. Connect to the venue Wi-Fi, then close the scheduler and start it again.");
        }
        else if (session.ImportError != null)
        {
            Notice.Show(Severity.Warning, "SignUpGenius import failed",
                "Pre-registrations aren't loaded, so everyone signs in as a walk-in. " + session.ImportError);
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
        _minute.Tick += (_, _) => OnMinute();
        _copied.Tick += (_, _) =>
        {
            _copied.Stop();
            CopiedText.Text = "";
        };

        InputBindings.Add(new KeyBinding(new RelayCommand(() => OnUndo(this, new RoutedEventArgs())), Key.Z, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(RefreshAll), Key.F5, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => OnHelp(this, new RoutedEventArgs())), Key.F1, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(new RelayCommand(FocusFind), Key.F, ModifierKeys.Control));
        for (var i = 0; i < _pages.Count && i < 9; i++)
        {
            var name = (string)_pages[i].Item.Tag;
            InputBindings.Add(new KeyBinding(new RelayCommand(() => ShowPage(name)), Key.D1 + i, ModifierKeys.Control));
        }

        InputBindings.Add(new KeyBinding(new RelayCommand(() => OnReport(this, new RoutedEventArgs())), Key.S, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => OnSettings(this, new RoutedEventArgs())), Key.OemComma, ModifierKeys.Control));
        PreviewKeyDown += OnPreviewKey;
        Closing += OnClosing;

        ShowPage("Event");
        RefreshAll();
        ScheduleMinute();
    }

    // ------------------------------------------------------------------
    // Refresh
    // ------------------------------------------------------------------

    private void RefreshAll()
    {
        _config = _svc.GetConfig();
        RowSync.Sync(_scouts, _svc.Snapshot(DataTable.Scouts), r => new ScoutRow(r), (row, r) => row.Update(r));
        RowSync.Sync(_adults, _svc.Snapshot(DataTable.Adults), r => new AdultRow(r) { OnSelChanged = OnPickChanged }, (row, r) => row.Update(r));
        RowSync.Sync(_rooms, _svc.Snapshot(DataTable.Rooms), r => new RoomCard(r), (row, r) => row.Update(r));
        if (_current != null && !_scouts.Contains(_current))
        {
            _current = null;
        }

        UpdateRoomTimers();
        ShowDetails();
        foreach (var table in _tables.Values.Where(t => t.Visibility == Visibility.Visible))
        {
            table.Reload(quiet: true);
        }

        UpdateAdultButtons();
        if (_foundRooms != null)
        {
            UpdatePersonFind();
        }

        // Edit > Undo (and Ctrl+Z) is the one way to undo, and names the
        // step, as the Mac's does. A name's own underscore mustn't become an
        // access key.
        UndoMenu.IsEnabled = _svc.CanUndo;
        UndoMenu.Header = _svc.UndoDescription is { } step ? "_Undo " + step.Replace("_", "__", StringComparison.Ordinal) : "_Undo";
    }

    /// <summary>Times are stamped to the minute, so counts change on the clock's minute: tick just after it.</summary>
    private void ScheduleMinute()
    {
        var now = DateTime.Now;
        _minute.Interval = TimeSpan.FromSeconds(60 - now.Second) - TimeSpan.FromMilliseconds(now.Millisecond) + TimeSpan.FromMilliseconds(250);
        _minute.Start();
    }

    private void OnMinute()
    {
        _minute.Stop();
        foreach (var s in _scouts)
        {
            s.Tick();
        }

        UpdateRoomTimers();
        if (_current is { } scout && !BoardStatus.IsWaiting(scout.Status))
        {
            ShowBoard(scout);
        }

        ScheduleMinute();
    }

    /// <summary>
    /// Minutes on each room holding a board, caution then overdue at the
    /// board-type thresholds. Runs on the youth's minutes since the last
    /// change, so the clock restarts by itself at Seat and at Start review.
    /// </summary>
    private void UpdateRoomTimers()
    {
        foreach (var card in _rooms)
        {
            var scout = _scouts.FirstOrDefault(s => s.Room == card.Room && BoardStatus.IsActive(s.Status));
            if (scout?.Mins is { } mins)
            {
                card.TimerText = $"{mins} min";
                card.TimerState = SchedulerLogic.TimerFor(scout.Status, scout.BoardType, mins, _config);
            }
            else
            {
                card.TimerText = "";
                card.TimerState = TimerState.None;
            }
        }
    }

    // ------------------------------------------------------------------
    // Navigation
    // ------------------------------------------------------------------

    private void OnViewPage(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string page })
        {
            ShowPage(page);
        }
    }

    /// <summary>The Event page or a table page, with the View menu's check on the page shown.</summary>
    private void ShowPage(string page)
    {
        // A cell still being edited on the page being left is saved first, so
        // the page shown next has it (the menu doesn't end the grid's edit).
        foreach (var table in _tables.Values.Where(t => t.Visibility == Visibility.Visible))
        {
            table.CommitEdit();
        }

        foreach (var (item, element) in _pages)
        {
            var shown = (string)item.Tag == page;
            item.IsChecked = shown;
            element.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            if (shown && element is TablePage table)
            {
                table.Reload(quiet: false);
            }
        }

        UpdateAdultButtons();
    }

    /// <summary>The board selected on Results, on the Event page.</summary>
    private void OpenBoard()
    {
        if (_tables[TableSpecs.Results.Name].Selected is { } row && _scouts.FirstOrDefault(s => s.Id == row.Id) is { } scout)
        {
            ShowPage("Event");
            Open(scout);
        }
    }

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

    // ------------------------------------------------------------------
    // Filters
    // ------------------------------------------------------------------

    private static bool Matches(string filter, params string[] fields) =>
        filter.Length == 0 || fields.Any(f => f.Contains(filter, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Waiting by sign-in number, On a board by room, Finished the most recent
    /// first (<see cref="ScoutRow.QueueSort"/>); sign-in number breaks a tie.
    /// </summary>
    private static int QueueOrder(ScoutRow a, ScoutRow b)
    {
        var order = string.CompareOrdinal(a.QueueSort, b.QueueSort);
        return order != 0 ? order : string.CompareOrdinal(a.RegNumSort, b.RegNumSort);
    }

    private void OnAvailableFilter(object sender, TextChangedEventArgs e) => ShowDetails();

    private void OnPersonFind(object sender, TextChangedEventArgs e) => UpdatePersonFind();

    /// <summary>
    /// Which room is someone in (SPEC.md D-21)? The rooms narrow to those
    /// holding a youth or an adult by that name, or a room by that name; for
    /// anyone in no room, the note says where they are. Kept up to date as
    /// the event changes (<see cref="RefreshAll"/>).
    /// </summary>
    private void UpdatePersonFind()
    {
        var query = PersonFind.Text.Trim();
        if (query.Length == 0)
        {
            _foundRooms = null;
            PersonFindNote.Text = "";
        }
        else
        {
            var found = SchedulerLogic.FindPeople(query, _scouts.Select(s => s.Info), _adults.Select(a => a.Info));
            _foundRooms = found.Where(p => p.Room != null).Select(p => p.Room!)
                .Concat(_rooms.Where(r => r.Room.Contains(query, StringComparison.OrdinalIgnoreCase)).Select(r => r.Room))
                .ToHashSet(StringComparer.Ordinal);
            var elsewhere = found.Where(p => p.Room == null).ToList();
            PersonFindNote.Text = _foundRooms.Count == 0 && elsewhere.Count == 0 ? "No one by that name has signed in."
                : string.Join(" ", elsewhere.Take(4).Select(p => $"{p.Name} {p.Where}."))
                  + (elsewhere.Count > 4 ? $" And {elsewhere.Count - 4} more." : "");
        }

        _roomView.Refresh();
        if (UIElementAutomationPeer.CreatePeerForElement(PersonFindNote) is { } peer)
        {
            peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }

    /// <summary>Enter opens the first room found (or the youth found, if in no room); Esc clears.</summary>
    private void OnPersonFindKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && PersonFind.Text.Length > 0)
        {
            PersonFind.Text = "";
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && _foundRooms != null)
        {
            if (_roomView.Cast<RoomCard>().FirstOrDefault() is { } room)
            {
                RoomList.SelectedItem = room;
            }
            else if (SchedulerLogic.FindPeople(PersonFind.Text, _scouts.Select(s => s.Info), []).FirstOrDefault() is { } youth
                     && _scouts.FirstOrDefault(s => s.Id == youth.Id) is { } row)
            {
                Open(row);
            }

            e.Handled = true;
        }
    }

    /// <summary>Ctrl+F: the find box of the page shown.</summary>
    private void FocusFind()
    {
        var box = EventPage.IsVisible ? PersonFind : _tables.Values.FirstOrDefault(t => t.IsVisible)?.Find;
        box?.Focus();
        box?.SelectAll();
    }

    // ------------------------------------------------------------------
    // Selection
    // ------------------------------------------------------------------

    private void OnQueueSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_quiet && QueueList.SelectedItem is ScoutRow s)
        {
            Open(s);
        }
    }

    /// <summary>Clicking the youth already open opens them again, as a fresh click would.</summary>
    private void OnQueueClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(QueueList, (DependencyObject)e.OriginalSource) is ListBoxItem { Content: ScoutRow row }
            && ReferenceEquals(row, _current))
        {
            Dispatcher.BeginInvoke(() => Open(row), DispatcherPriority.Input);
        }
    }

    private void OnRoomSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_quiet || RoomList.SelectedItem is not RoomCard card)
        {
            return;
        }

        if (!card.IsFree && _scouts.FirstOrDefault(s => s.Room == card.Room && BoardStatus.IsActive(s.Status)) is { } inRoom)
        {
            Open(inRoom);
        }
        else if (card.IsFree && _current is { } s && BoardStatus.IsWaiting(s.Status))
        {
            // Building a board: a free room clicked is the room for it.
            _builderRoomId = card.Id;
            ShowDetails();
        }
    }

    /// <summary>
    /// Show a youth in the details pane. A waiting youth gets a proposed board
    /// unless the operator already has picks: picks are their work in
    /// progress and survive clicking around; only Start over and seating
    /// clear them.
    /// </summary>
    private void Open(ScoutRow scout)
    {
        if (!ReferenceEquals(_current, scout))
        {
            DetailNotice.Close();
            NotesBox.Text = "";
            AvailableFilter.Text = "";
            _resultButtons[BoardResults.Approved].IsChecked = true;
        }

        _current = scout;
        Quietly(() =>
        {
            QueueList.SelectedItem = _queueView.Contains(scout) ? scout : null;
            if (QueueList.SelectedItem != null)
            {
                QueueList.ScrollIntoView(scout);
            }

            RoomList.SelectedItem = BoardStatus.IsActive(scout.Status) ? _rooms.FirstOrDefault(r => r.Room == scout.Room) : null;
        });

        if (BoardStatus.IsWaiting(scout.Status))
        {
            ProposeBoard(scout);
        }

        ShowDetails();
    }

    private void ProposeBoard(ScoutRow scout)
    {
        var free = _rooms.Where(r => r.IsFree).ToList();
        if (_adults.Any(IsPicked))
        {
            // Keep their picks; just make sure there's a sensible room.
            if (free.All(r => r.Id != _builderRoomId))
            {
                _builderRoomId = (free.FirstOrDefault(r => r.BoardType == scout.BoardType) ?? free.FirstOrDefault())?.Id;
            }

            return;
        }

        var (adults, waiting) = SelectionContext(scout);
        var pick = SchedulerLogic.AutoSelect(scout.Info, adults, _rooms.Select(r => r.Info), waiting);
        foreach (var id in pick.AllAdultIds)
        {
            if (_adults.FirstOrDefault(a => a.Id == id) is { } adult)
            {
                adult.Sel = true;
            }
        }

        _chairId = pick.ChairIds.FirstOrDefault();
        _builderRoomId = pick.RoomId ?? free.FirstOrDefault()?.Id;
    }

    /// <summary>
    /// What the proposal weighs beside this youth: the other waiting youth in
    /// queue order (pre-registered first), so chairs and adults are kept for
    /// the boards to come, and how long each adult has waited to volunteer
    /// (since sign-in, or since their last board was completed).
    /// </summary>
    private (List<AdultInfo> Adults, List<ScoutInfo> Waiting) SelectionContext(ScoutRow scout)
    {
        var waiting = _scouts
            .Where(s => s.Id != scout.Id && BoardStatus.IsWaiting(s.Status))
            .OrderBy(s => s.RegNumSort, StringComparer.Ordinal)
            .Select(s => s.Info)
            .ToList();
        var since = SchedulerLogic.FreeSinceTimes(
            _svc.Snapshot(DataTable.Adults).Select(r => (r.GetValueOrDefault("ID", ""), r.GetValueOrDefault("RegTime", ""))),
            _svc.Snapshot(DataTable.Scouts).Select(r => (r.GetValueOrDefault("Status", ""),
                r.GetValueOrDefault("BoardMembersIDs", ""), r.GetValueOrDefault("LastUpdateTime", ""))));
        var adults = _adults.Select(a => a.Info with { FreeSince = since.GetValueOrDefault(a.Id, "") }).ToList();
        return (adults, waiting);
    }

    // ------------------------------------------------------------------
    // Details pane
    // ------------------------------------------------------------------

    private void ShowDetails()
    {
        var scout = _current;
        DetailsEmpty.Visibility = scout == null ? Visibility.Visible : Visibility.Collapsed;
        DetailsHead.Visibility = scout != null ? Visibility.Visible : Visibility.Collapsed;
        LocateSection.Visibility = DetailsHead.Visibility;
        BuilderSection.Visibility = scout != null && BoardStatus.IsWaiting(scout.Status) ? Visibility.Visible : Visibility.Collapsed;
        BoardSection.Visibility = scout != null && !BoardStatus.IsWaiting(scout.Status) ? Visibility.Visible : Visibility.Collapsed;
        ShowActions(scout);
        if (scout == null)
        {
            return;
        }

        DetailName.Text = scout.FullName;
        DetailSub.Text = string.Join(" · ", new[] { scout.UnitLabel, Display.BoardType(scout.BoardType), scout.RegNum }.Where(s => s.Length > 0));
        DetailStatus.Content = null;
        DetailStatus.Content = scout;

        if (BoardStatus.IsWaiting(scout.Status))
        {
            ShowBuilder(scout);
        }
        else
        {
            ShowBoard(scout);
        }

        ShowLocate(scout);
    }

    /// <summary>
    /// The buttons for the youth's next step, in the bar pinned to the foot
    /// of the pane: Seat board and Postpone while they wait, Start review or
    /// Complete once seated, and Reset. The bar goes when there's no step.
    /// </summary>
    private void ShowActions(ScoutRow? scout)
    {
        var status = scout?.Status;
        var waiting = status != null && BoardStatus.IsWaiting(status);
        var buttons = new (Button Button, bool Shown)[]
        {
            (SeatButton, waiting),
            (PostponeButton, waiting),
            (StartButton, status == BoardStatus.Seated),
            (CompleteButton, status == BoardStatus.InProgress),
            (ResetButton, status != null && !waiting && SchedulerLogic.ActionsFor(status).HasFlag(ScoutActions.Reset)),
        };
        foreach (var (button, shown) in buttons)
        {
            button.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        }

        ActionBar.Visibility = buttons.Any(b => b.Shown) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Ctrl+Enter runs the youth's next step (SPEC.md P-3): Seat board, Start
    /// review or Complete, whichever the pane shows, from anywhere on the
    /// Event page, the Notes box included (hence a preview: the box would
    /// otherwise take the key for a new line).
    /// </summary>
    private void OnPreviewKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.Control || !EventPage.IsVisible)
        {
            return;
        }

        if (new[] { SeatButton, StartButton, CompleteButton }.FirstOrDefault(b => b.IsVisible && b.IsEnabled) is { } step)
        {
            step.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, step));
            e.Handled = true;
        }
    }

    private void ShowBuilder(ScoutRow scout)
    {
        // Rooms: free ones, this board's type first.
        var free = _rooms.Where(r => r.IsFree).OrderBy(r => r.BoardType == scout.BoardType ? 0 : 1).ThenBy(r => r.Room, StringComparer.OrdinalIgnoreCase).ToList();
        Quietly(() =>
        {
            BuilderRoom.ItemsSource = free.Select(r => new KeyValuePair<string, string>(r.Id, $"{r.Room} · {r.BoardTypeText}")).ToList();
            BuilderRoom.SelectedValue = _builderRoomId;
        });

        var picked = _adults.Where(IsPicked).Select(a => new PickRow(a, scout, OnChairChosen)).ToList();
        if (picked.Where(p => p.CanChair).All(p => p.Id != _chairId))
        {
            _chairId = picked.FirstOrDefault(p => p.CanChair)?.Id;
        }

        foreach (var p in picked)
        {
            p.SetChairQuietly(p.Id == _chairId);
        }

        PickList.ItemsSource = picked.OrderByDescending(p => p.Id == _chairId).ToList();
        NoPicksText.Visibility = picked.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var filter = AvailableFilter.Text.Trim();
        var available = _adults
            .Where(a => !a.Sel && a.CanPick && a.Info.RoleFor(scout.BoardType) != BoardRoles.Unavailable)
            .Where(a => Matches(filter, a.Last, a.First, a.FullName, a.UnitName, a.UnitLabel))
            .Select(a => new PickRow(a, scout))
            .OrderBy(p => p.SameUnit)
            .ThenByDescending(p => p.CanChair && _chairId == null)
            .ThenBy(p => p.Adult.Last, StringComparer.OrdinalIgnoreCase)
            .ToList();
        AvailableList.ItemsSource = available;
        NoAvailableText.Visibility = available.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var room = _rooms.FirstOrDefault(r => r.Id == _builderRoomId);
        var issues = BoardCheck.Review(scout.Info, picked.Select(p => p.Adult.Info).ToList(), _chairId, room?.Info);
        IssueList.Children.Clear();
        foreach (var issue in issues.OrderBy(i => i.Level))
        {
            var bar = new InfoBar { IsClosable = false, Margin = new Thickness(0, 0, 0, 8) };
            bar.Show(issue.Level == IssueLevel.Block ? Severity.Error : Severity.Warning, issue.Title, issue.Message);
            IssueList.Children.Add(bar);
        }

        SeatButton.IsEnabled = issues.All(i => i.Level != IssueLevel.Block);
        FillButton.IsEnabled = picked.Count < BoardRules.MinMembers(scout.BoardType) || picked.All(p => !p.CanChair);
        SeatText.Text = issues.Any(i => i.Level == IssueLevel.Warn) ? "_Seat anyway" : "_Seat board";
    }

    private void ShowBoard(ScoutRow scout)
    {
        var facts = new List<KeyValuePair<string, string>>();
        if (BoardStatus.IsActive(scout.Status))
        {
            var what = scout.Status == BoardStatus.Seated ? "Convening" : "In review";
            facts.Add(new("Room", $"{scout.Room} · {what} for {scout.Mins ?? 0} min"));
        }

        // A finished board's members are history; a running board's can change.
        var active = BoardStatus.IsActive(scout.Status);
        if (scout.BoardChair.Length > 0 && !active)
        {
            facts.Add(new("Chair", scout.BoardChair));
            facts.Add(new("Members", scout.BoardMembersText));
        }

        MembersSection.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        if (active)
        {
            var members = _adults.Where(a => a.Room == scout.Room)
                .Select(a => new PickRow(a, scout)).ToList();
            foreach (var m in members)
            {
                m.SetChairQuietly(m.Id == scout.BoardChairId);
            }

            BoardMemberList.ItemsSource = members.OrderByDescending(m => m.IsChair).ThenBy(m => m.Adult.Last, StringComparer.OrdinalIgnoreCase).ToList();
        }

        if (scout.Status == BoardStatus.Completed)
        {
            facts.Add(new("Result", scout.ResultText));
            if (scout.Notes.Length > 0)
            {
                facts.Add(new("Notes", scout.Notes));
            }
        }

        FactList.ItemsSource = facts;
        ResultSection.Visibility = scout.Status == BoardStatus.InProgress ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Where the youth's leaders and parents are: for fetching them, and after the board.</summary>
    private void ShowLocate(ScoutRow scout)
    {
        var found = SchedulerLogic.Locate(scout.Info, _adults.Select(a => a.Info), includeParents: true);
        LocateList.ItemsSource = found.Select(f => new KeyValuePair<string, string>(
            f.IsSupporting ? "Came to support them" : f.IsLeader ? "Leader" : "Parent",
            $"{f.Adult.First} {f.Adult.Last} · {(f.Adult.Room == AdultRoom.Disabled ? "gone home" : f.Adult.Room.Length == 0 ? "main room" : "room " + f.Adult.Room)}")).ToList();
        LocateNone.Text = found.Count > 0 ? ""
            : scout.Leader.Length > 0 ? $"{scout.Leader} hasn't signed in, and no parent has."
            : "No leader was given at sign-in, and no parent has signed in.";
        LocateNone.Visibility = found.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        UnlinkButton.Visibility = _adults.Any(a => a.Info.Supports(scout.Id)) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Link an adult to this youth as someone who came to support them, for
    /// the adult who didn't say so at sign-in. They're listed under Leaders
    /// and parents from then on, with where to find them. Works for an adult
    /// on a board too: a Scoutmaster often is by then.
    /// </summary>
    private void OnLinkAdult(object sender, RoutedEventArgs e)
    {
        if (_current is not { } scout)
        {
            return;
        }

        var choices = _adults.Where(a => !a.Info.Supports(scout.Id))
            .OrderBy(a => a.Last, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.First, StringComparer.OrdinalIgnoreCase)
            .Select(a => new KeyValuePair<string, string>(a.Id, $"{a.FullName} · {a.UnitLabel}")).ToList();
        if (choices.Count == 0)
        {
            return;
        }

        var dialog = new AppDialog(this, $"Link an adult to {scout.FullName}?", "Link");
        dialog.AddMessage($"For someone who came to support **{scout.FullName}** but didn't say so at sign-in. "
            + "They'll be listed here, with where to find them, when it's time to bring the youth in.");
        var pick = dialog.AddChoice("Adult", choices, null);
        if (dialog.ShowDialog() && pick.SelectedValue is string adultId)
        {
            Report(_svc.SetSupporting(adultId, scout.Id, linked: true), "Couldn't link them");
        }
    }

    private void OnUnlinkAdult(object sender, RoutedEventArgs e)
    {
        if (_current is not { } scout)
        {
            return;
        }

        var linked = _adults.Where(a => a.Info.Supports(scout.Id))
            .Select(a => new KeyValuePair<string, string>(a.Id, a.FullName)).ToList();
        if (linked.Count == 0)
        {
            return;
        }

        var dialog = new AppDialog(this, $"Unlink an adult from {scout.FullName}?", "Unlink");
        var pick = dialog.AddChoice("Adult", linked, null);
        if (dialog.ShowDialog() && pick.SelectedValue is string adultId)
        {
            Report(_svc.SetSupporting(adultId, scout.Id, linked: false), "Couldn't unlink them");
        }
    }

    private void OnChairChosen(PickRow row)
    {
        _chairId = row.Id;
        Dispatcher.BeginInvoke(ShowDetails, DispatcherPriority.Input);
    }

    private void OnBuilderRoomChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_quiet)
        {
            _builderRoomId = BuilderRoom.SelectedValue as string;
            ShowDetails();
        }
    }

    // ------------------------------------------------------------------
    // Picks
    // ------------------------------------------------------------------

    /// <summary>
    /// Ticked for the board being built. Only a free adult counts: the Java
    /// version leaves a seated board's members ticked in the file, so after a
    /// hand-off from it those stale ticks must not reappear on the next board.
    /// </summary>
    private static bool IsPicked(AdultRow a) => a.Sel && a.CanPick;

    /// <summary>A pick is saved at once, so nothing can undo it behind the operator's back.</summary>
    private void OnPickChanged(AdultRow adult, bool picked) =>
        _svc.SaveRow(DataTable.Adults, "updated", adult.Id, new Dictionary<string, string> { ["Sel"] = picked ? "1" : "0" });

    private void OnAddPick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PickRow row })
        {
            row.Adult.Sel = true;
            ShowDetails();
        }
    }

    private void OnRemovePick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PickRow row })
        {
            row.Adult.Sel = false;
            ShowDetails();
        }
    }

    /// <summary>
    /// The override: keep the adults the operator chose and let auto-select
    /// complete the board around them (a chair if none of them can chair,
    /// then members up to the minimum).
    /// </summary>
    private void OnFillBoard(object sender, RoutedEventArgs e)
    {
        if (_current is not { } scout || !BoardStatus.IsWaiting(scout.Status))
        {
            return;
        }

        var picked = _adults.Where(IsPicked).Select(a => a.Id).ToList();
        var (adults, waiting) = SelectionContext(scout);
        var fill = SchedulerLogic.FillBoard(scout.Info, adults, picked, waiting);
        foreach (var id in fill.AllAdultIds)
        {
            if (_adults.FirstOrDefault(a => a.Id == id) is { } adult)
            {
                adult.Sel = true;
            }
        }

        _chairId ??= fill.ChairIds.FirstOrDefault();
        ShowDetails();
    }

    /// <summary>Untick everyone; for a waiting youth, propose a fresh board.</summary>
    private void OnClearPicks(object sender, RoutedEventArgs e)
    {
        foreach (var a in _adults.Where(a => a.Sel).ToList())
        {
            a.Sel = false;
        }

        _chairId = null;
        if (_current is { } s && BoardStatus.IsWaiting(s.Status))
        {
            ProposeBoard(s);
        }

        ShowDetails();
    }

    // ------------------------------------------------------------------
    // Board lifecycle
    // ------------------------------------------------------------------

    /// <summary>
    /// Waiting → Seated. The builder has already listed every problem and
    /// only enables Seat when nothing blocks it; warnings (same unit, a larger
    /// board, the other type's room) make it "Seat anyway". The server
    /// re-checks the hard rules.
    /// </summary>
    private void OnSeat(object sender, RoutedEventArgs e)
    {
        if (_current is not { } scout || !BoardStatus.IsWaiting(scout.Status) || _builderRoomId is not { } roomId)
        {
            return;
        }

        var picked = _adults.Where(IsPicked).ToList();
        var result = _svc.SeatBoard(roomId, scout.Id, _chairId, string.Join(",", picked.Select(a => a.Id)));
        if (!result.Ok)
        {
            DetailNotice.Show(Severity.Error, "Couldn't seat the board", Plain(result.Message));
        }

        _chairId = null;
        _builderRoomId = null;
        RefreshAll();
        Open(scout);
    }

    /// <summary>Seated → In review. No confirmation: Reset undoes it.</summary>
    private void OnStart(object sender, RoutedEventArgs e)
    {
        if (_current is { Status: BoardStatus.Seated } scout)
        {
            Report(_svc.StartReview(scout.Id), "Couldn't start the review");
        }
    }

    private void OnComplete(object sender, RoutedEventArgs e)
    {
        if (_current is not { Status: BoardStatus.InProgress } scout)
        {
            return;
        }

        var outcome = _resultButtons.First(b => b.Value.IsChecked == true).Key;
        Report(_svc.CompleteBoard(scout.Id, outcome, NotesBox.Text.Trim()), "Couldn't complete the board");
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        if (_current is not { } scout)
        {
            return;
        }

        if (AppDialog.Confirm(this, "Reset this board?",
                $"**{scout.FullName}** goes back to waiting. Room {scout.Room} and its members are freed, and the board would need seating again.",
                "Reset board"))
        {
            Report(_svc.ResetBoard(scout.Id), "Couldn't reset the board");
        }
    }

    private void OnPostpone(object sender, RoutedEventArgs e)
    {
        if (_current is not { } scout || !BoardStatus.IsWaiting(scout.Status))
        {
            return;
        }

        if (AppDialog.Confirm(this, "Postpone this board?",
                $"**{scout.FullName}** won't have a board at this event. This is usually because the paperwork isn't in order.", "Postpone"))
        {
            Report(_svc.PostponeBoard(scout.Id), "Couldn't postpone the board");
        }
    }

    // ------------------------------------------------------------------
    // Changing a board already seated or in review
    // ------------------------------------------------------------------

    /// <summary>The seated board's members as they stand, and its chair.</summary>
    private (List<AdultRow> Members, string ChairId)? RunningBoard() =>
        _current is { } s && BoardStatus.IsActive(s.Status)
            ? (_adults.Where(a => a.Room == s.Room).ToList(), s.BoardChairId)
            : null;

    /// <summary>Free adults who could join this youth's board, as dropdown choices; same-unit ones last and marked.</summary>
    private List<PickRow> JoinChoices(ScoutRow scout) =>
        _adults.Where(a => a.CanPick && a.Info.RoleFor(scout.BoardType) != BoardRoles.Unavailable)
            .Select(a => new PickRow(a, scout))
            .OrderBy(p => p.SameUnit).ThenBy(p => p.Adult.Last, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>A dropdown of adults, each shown with the marks after their name (SPEC.md D-20); its value is the adult's ID.</summary>
    private ComboBox AddAdultChoice(AppDialog dialog, string label, IReadOnlyList<PickRow> adults) =>
        dialog.AddChoice(label, adults, nameof(PickRow.Id), (DataTemplate)FindResource("AdultChoice"));

    private void ChangeMembers(ScoutRow scout, IEnumerable<string> memberIds, string chairId)
    {
        var ids = memberIds.Distinct().ToList();
        Report(_svc.ChangeBoardMembers(scout.Id, chairId, string.Join(",", ids)), "Couldn't change the board");
        var conflicts = BoardRules.FindUnitConflicts(scout.UnitName,
            _adults.Where(a => ids.Contains(a.Id)).Select(a => new BoardCandidate(a.Id, a.Last, a.First, a.UnitName)));
        if (conflicts.Count > 0 && !DetailNotice.IsOpen)
        {
            DetailNotice.Show(Severity.Warning, "Same unit",
                $"{string.Join(", ", conflicts.Select(c => $"{c.First} {c.Last}"))} {(conflicts.Count == 1 ? "is" : "are")} in the youth's own unit.");
        }
    }

    /// <summary>The chair after a change: the same one if they stay, else the first qualified member left.</summary>
    private string ChairAfter(ScoutRow scout, IReadOnlyCollection<string> ids, string currentChair) =>
        ids.Contains(currentChair) ? currentChair
        : _adults.FirstOrDefault(a => ids.Contains(a.Id) && a.Info.RoleFor(scout.BoardType) == BoardRoles.Chair)?.Id ?? currentChair;

    private void OnReplaceMember(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: PickRow leaving } || _current is not { } scout || RunningBoard() is not { } board)
        {
            return;
        }

        var choices = JoinChoices(scout);
        if (choices.Count == 0)
        {
            DetailNotice.Show(Severity.Informational, "No one is free", "Everyone else is on a board, gone home, or unavailable for this board type.");
            return;
        }

        var dialog = new AppDialog(this, $"Replace {leaving.Name}?", "Replace");
        dialog.AddMessage($"**{leaving.Name}** leaves room {scout.Room} and is free for another board. The board carries on; its time isn't reset.");
        var pick = AddAdultChoice(dialog, "Replace with", choices);
        if (leaving.IsChair)
        {
            dialog.AddInfo(Severity.Informational, "", "They're the chair. The new member takes the chair if they're qualified; otherwise another qualified member on the board does.");
        }

        if (!dialog.ShowDialog() || pick.SelectedValue is not string joining)
        {
            return;
        }

        var ids = board.Members.Select(m => m.Id == leaving.Id ? joining : m.Id).ToList();
        var chair = leaving.IsChair && _adults.FirstOrDefault(a => a.Id == joining)?.Info.RoleFor(scout.BoardType) == BoardRoles.Chair
            ? joining
            : ChairAfter(scout, ids, board.ChairId);
        ChangeMembers(scout, ids, chair);
    }

    /// <summary>No confirmation: adding them back is as easy.</summary>
    private void OnRemoveMember(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PickRow leaving } && _current is { } scout && RunningBoard() is { } board)
        {
            var ids = board.Members.Where(m => m.Id != leaving.Id).Select(m => m.Id).ToList();
            ChangeMembers(scout, ids, ChairAfter(scout, ids, board.ChairId));
        }
    }

    private void OnAddMember(object sender, RoutedEventArgs e)
    {
        if (_current is not { } scout || RunningBoard() is not { } board)
        {
            return;
        }

        var choices = JoinChoices(scout);
        if (choices.Count == 0)
        {
            DetailNotice.Show(Severity.Informational, "No one is free", "Everyone else is on a board, gone home, or unavailable for this board type.");
            return;
        }

        var dialog = new AppDialog(this, "Add a member?", "Add");
        var pick = AddAdultChoice(dialog, "Add to the board in room " + scout.Room, choices);
        if (dialog.ShowDialog() && pick.SelectedValue is string joining)
        {
            ChangeMembers(scout, board.Members.Select(m => m.Id).Append(joining), board.ChairId);
        }
    }

    private void OnChangeChair(object sender, RoutedEventArgs e)
    {
        if (_current is not { } scout || RunningBoard() is not { } board)
        {
            return;
        }

        var qualified = board.Members.Where(m => m.Info.RoleFor(scout.BoardType) == BoardRoles.Chair && m.Id != board.ChairId)
            .Select(m => new PickRow(m, scout)).ToList();
        if (qualified.Count == 0)
        {
            DetailNotice.Show(Severity.Informational, "No one else can chair",
                "No other member of this board is qualified to chair. Replace someone with a qualified chair, or make a member a chair on the Adults page.");
            return;
        }

        var dialog = new AppDialog(this, "Change the chair?", "Change chair");
        var pick = AddAdultChoice(dialog, "New chair", qualified);
        if (dialog.ShowDialog() && pick.SelectedValue is string chair)
        {
            ChangeMembers(scout, board.Members.Select(m => m.Id), chair);
        }
    }

    private void Report(ActionResult result, string failure)
    {
        if (!result.Ok)
        {
            DetailNotice.Show(Severity.Error, failure, Plain(result.Message));
        }

        RefreshAll();
    }

    /// <summary>The server's refusals start "ERROR: " for the check-in pages; the window says it once, in the title.</summary>
    private static string Plain(string message) => message.StartsWith("ERROR: ", StringComparison.Ordinal) ? message[7..] : message;

    // ------------------------------------------------------------------
    // Adults
    // ------------------------------------------------------------------

    private TablePage Adults => _tables[TableSpecs.Adults.Name];

    private TablePage History => _tables[TableSpecs.AdultHistory.Name];

    private void UpdateAdultButtons()
    {
        var adult = Adults.Selected;
        _backButton.IsEnabled = adult is { IsDisabled: true };
        _goneHomeButton.IsEnabled = adult is { IsDisabled: false, IsBusy: false };
        _signInButton.IsEnabled = History.Selected is { } known && !_adults.Any(a => a.Id == known.Id);
    }

    /// <summary>Add adult: someone who would rather not use the tablet signs in here.</summary>
    private void OnAddAdult()
    {
        if (AddAdultDialog.Show(this, _svc))
        {
            RefreshAll();
        }
    }

    /// <summary>
    /// Sign in for today, from the adult history. Their Last event becomes
    /// Today, which shows it worked (SPEC.md D-14).
    /// </summary>
    private void OnSignInFromHistory()
    {
        if (History.Selected is { } known && !_adults.Any(a => a.Id == known.Id))
        {
            AddAdultDialog.SignInFromHistory(_svc, known);
            RefreshAll();
        }
    }

    /// <summary>Gone home. No confirmation: Undo (or Back) reverses it.</summary>
    private void OnGoneHome()
    {
        if (Adults.Selected is { IsDisabled: false, IsBusy: false } adult)
        {
            ReportOn(Adults, _svc.DisableAdult(adult.Id), "Couldn't mark them gone home");
        }
    }

    private void OnBack()
    {
        if (Adults.Selected is { IsDisabled: true } adult)
        {
            ReportOn(Adults, _svc.EnableAdult(adult.Id), "Couldn't mark them back");
        }
    }

    /// <summary>A step taken on a table page: a refusal is said there, not in the details pane.</summary>
    private void ReportOn(TablePage page, ActionResult result, string failure)
    {
        if (!result.Ok)
        {
            page.Notice.Show(Severity.Error, failure, Plain(result.Message));
        }

        RefreshAll();
    }

    // ------------------------------------------------------------------
    // Rooms
    // ------------------------------------------------------------------

    private static readonly KeyValuePair<string, string>[] BoardTypeChoices =
    [
        new(BoardTypes.Final, Display.BoardType(BoardTypes.Final)),
        new(BoardTypes.Project, Display.BoardType(BoardTypes.Project)),
    ];

    private RoomCard? SelectedRoom => RoomList.SelectedItem as RoomCard;

    private void OnAddRoom(object sender, RoutedEventArgs e)
    {
        var dialog = new AppDialog(this, "Add a room", "Add room");
        var roomBox = dialog.AddText("Room number");
        var typeBox = dialog.AddChoice("Used for", BoardTypeChoices, BoardTypes.Final);
        dialog.AddMessage("For project reviews sharing one room, add one entry per table, like 200A and 200B.");
        dialog.Validate = () =>
        {
            var room = roomBox.Text.Trim();
            if (room.Length == 0)
            {
                return "Enter a room number.";
            }

            return _rooms.Any(r => r.Room == room) ? $"There's already a room {room}." : null;
        };
        if (dialog.ShowDialog())
        {
            var result = _svc.AddRoom(roomBox.Text.Trim(), (string)typeBox.SelectedValue);
            if (!result.Ok)
            {
                Notice.Show(Severity.Error, "Couldn't add the room", Plain(result.Message));
            }

            RefreshAll();
        }
    }

    /// <summary>No confirmation: a free room is added back just as easily.</summary>
    private void OnRemoveRoom(object sender, RoutedEventArgs e)
    {
        if (SelectedRoom is not { } room)
        {
            Notice.Show(Severity.Informational, "Select a room first", "Click the room to remove, then Remove room.");
            return;
        }

        if (!room.IsFree)
        {
            Notice.Show(Severity.Warning, $"Room {room.Room} is in use", "Complete, reset or move its board first.");
            return;
        }

        var result = _svc.RemoveRoom(room.Id);
        if (!result.Ok)
        {
            Notice.Show(Severity.Error, "Couldn't remove the room", Plain(result.Message));
        }

        RefreshAll();
    }

    /// <summary>Rename the selected room, on the room card itself.</summary>
    private void OnRenameRoom(object sender, RoutedEventArgs e)
    {
        if (SelectedRoom is not { } room)
        {
            Notice.Show(Severity.Informational, "Select a room first", "Click the room to rename, then Rename.");
            return;
        }

        var dialog = new AppDialog(this, $"Rename room {room.Room}", "Rename");
        var roomBox = dialog.AddText("Room number", room.Room);
        dialog.Validate = () =>
        {
            var value = roomBox.Text.Trim();
            if (value.Length == 0)
            {
                return "Enter a room number.";
            }

            return value != room.Room && _rooms.Any(r => r.Room == value) ? $"There's already a room {value}." : null;
        };
        if (dialog.ShowDialog())
        {
            var result = _svc.RenameRoom(room.Id, roomBox.Text.Trim());
            if (!result.Ok)
            {
                Notice.Show(Severity.Error, "Couldn't rename the room", Plain(result.Message));
            }

            RefreshAll();
        }
    }

    /// <summary>Switch the selected room between final boards and project reviews. No confirmation: switching back is as easy.</summary>
    private void OnSwitchRoomType(object sender, RoutedEventArgs e)
    {
        if (SelectedRoom is not { } room)
        {
            Notice.Show(Severity.Informational, "Select a room first", "Click the room to switch, then Switch type.");
            return;
        }

        var newType = room.BoardType == BoardTypes.Project ? BoardTypes.Final : BoardTypes.Project;
        var result = _svc.SetRoomType(room.Id, newType);
        if (!result.Ok)
        {
            Notice.Show(Severity.Error, "Couldn't switch the room", Plain(result.Message));
        }

        RefreshAll();
    }

    /// <summary>Move the selected room's board to another room, or swap two boards.</summary>
    private void OnChangeRoom(object sender, RoutedEventArgs e)
    {
        if (SelectedRoom is not { } first)
        {
            Notice.Show(Severity.Informational, "Select a room first", "Click the room whose board you want to move.");
            return;
        }

        var others = _rooms.Where(r => !ReferenceEquals(r, first)).ToList();
        if (others.Count == 0)
        {
            Notice.Show(Severity.Informational, "There's no other room", "Add a room to move this board to.");
            return;
        }

        static string Label(RoomCard r) => r.IsFree ? $"{r.Room} · {r.BoardTypeText} · free" : $"{r.Room} · {r.BoardTypeText} · {r.Scout}";

        var dialog = new AppDialog(this, $"Move room {first.Room}'s board", "Move");
        dialog.AddMessage(first.IsFree ? $"Room {first.Room} is free." : $"**{first.Scout}**'s board is in room {first.Room}.");
        var target = dialog.AddChoice("Move to", others.Select(r => new KeyValuePair<string, string>(r.Id, Label(r))), null);
        dialog.AddMessage("If that room has a board too, the two boards swap rooms.");
        if (!dialog.ShowDialog() || target.SelectedValue is not string secondId)
        {
            return;
        }

        var second = _rooms.First(r => r.Id == secondId);
        if (first.BoardType != second.BoardType
            && !AppDialog.Confirm(this, "Use a room set up for the other board type?",
                $"Room {first.Room} is for {first.BoardTypeText.ToLowerInvariant()}s and room {second.Room} is for {second.BoardTypeText.ToLowerInvariant()}s.",
                "Move anyway", Severity.Warning))
        {
            return;
        }

        var result = _svc.ChangeRoom(first.Id, second.Id);
        if (!result.Ok)
        {
            Notice.Show(Severity.Error, "Couldn't move the board", Plain(result.Message));
        }

        RefreshAll();
    }

    // ------------------------------------------------------------------
    // Other commands
    // ------------------------------------------------------------------

    private void OnReport(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save the event's results",
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
            var path = dialog.FileName;
            Notice.Show(Severity.Success, "Report saved", Path.GetFileName(path), "Show in folder",
                () => OpenUrl("explorer.exe", $"/select,\"{path}\""));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notice.Show(Severity.Error, "Couldn't save the report", ex.Message);
        }
    }

    /// <summary>
    /// Reverses the most recent board step, room change, Disable/Enable or
    /// Link/Unlink (Ctrl+Z). No message on success: the screen already shows
    /// the result. Reset and Postpone keep their own confirmation dialog
    /// rather than relying on this.
    /// </summary>
    private void OnUndo(object sender, RoutedEventArgs e)
    {
        if (_svc.CanUndo)
        {
            Report(_svc.Undo(), "Couldn't undo");
        }
    }

    /// <summary>The check-in addresses as QR codes, for a tablet's camera instead of typing one in by hand.</summary>
    private void OnShowQr(object sender, RoutedEventArgs e)
    {
        if (_qr is { IsLoaded: true })
        {
            _qr.Activate();
            return;
        }

        if (_session.CheckInUrls.Count == 0)
        {
            Notice.Show(Severity.Informational, "No check-in address yet",
                "Connect to the venue Wi-Fi, then close the scheduler and start it again.");
            return;
        }

        _qr = new QrWindow(_session.CheckInUrls) { Owner = this };
        _qr.Show();
    }

    private void OnHelp(object sender, RoutedEventArgs e) => new HelpWindow { Owner = this }.Show();

    /// <summary>File > Settings: the room timers and About, in a window beside the event.</summary>
    private void OnSettings(object sender, RoutedEventArgs e) => OpenSettings();

    /// <summary>Help > Donate: Settings at its Support card (SPEC.md D-17).</summary>
    private void OnDonate(object sender, RoutedEventArgs e) => OpenSettings().Page.ShowSupport();

    private SettingsWindow OpenSettings()
    {
        if (_settings is { IsLoaded: true })
        {
            _settings.Page.Load();
            _settings.Activate();
            return _settings;
        }

        _settings = new SettingsWindow(new SettingsPage(_svc, RefreshAll)) { Owner = this };
        _settings.Show();
        return _settings;
    }

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
            CopiedText.Text = "Copied";
            _copied.Stop();
            _copied.Start();
        }
    }

    internal static void OpenUrl(string target, string? arguments = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, Arguments = arguments ?? "" });
        }
        catch (Win32Exception)
        {
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        foreach (var table in _tables.Values)
        {
            table.CommitEdit();
        }

        if (!AppDialog.Confirm(this, "Close the scheduler?",
                "Closing also stops the check-in website. Stations can't sign anyone in until the scheduler is started again.", "Close"))
        {
            e.Cancel = true;
            return;
        }

        _minute.Stop();
        _qr?.Close();
        _settings?.Close();
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
