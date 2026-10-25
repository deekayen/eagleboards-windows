using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
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

    private readonly Button _openButton;
    private readonly Button _goneHomeButton;
    private readonly Button _signInButton;
    private readonly Button _backButton;
    private readonly DispatcherTimer _minute = new();
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly DispatcherTimer _copied = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly Dictionary<string, RadioButton> _resultButtons = [];

    /// <summary>Rooms whose timer has been counted once: a room is said to run long only on a change, never on its first count.</summary>
    private readonly HashSet<string> _timedRooms = new(StringComparer.Ordinal);

    /// <summary>The youth and adults already told of, so a door sign-in is said once (K2).</summary>
    private readonly HashSet<string> _known = new(StringComparer.Ordinal);

    private ConfigRecord _config = new();
    private bool _quiet;
    private QrWindow? _qr;
    private SettingsWindow? _settings;
    private HelpWindow? _help;

    /// <summary>The page shown (<see cref="ShowPage"/>): "Event" or a table's name.</summary>
    private string _page = "Event";

    /// <summary>The rooms Find a person narrows the Rooms card to, or null when it's empty.</summary>
    private HashSet<string>? _foundRooms;

    /// <summary>What Find a person last read out, so a sign-in that changes nothing isn't read again.</summary>
    private string _personFindSpoken = "";

    /// <summary>The seating review's problems as last shown, and for whom: rebuilt, and read out, only when they change.</summary>
    private string _issueKey = "";

    private string? _issueScoutId;

    /// <summary>The youth the details pane shows, chosen by the operator.</summary>
    private ScoutRow? _current;

    /// <summary>The builder's choices that aren't saved until Seat: chair and room.</summary>
    private string? _chairId;

    private string? _builderRoomId;

    /// <summary>The board last proposed, to tell it from the operator's own (SPEC.md D-12).</summary>
    private ProposedBoard? _proposal;

    public MainWindow(EventSession session)
    {
        _session = session;
        _svc = session.Service;
        InitializeComponent();

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

        // The page's commands, with access keys that clash neither with the
        // menu bar's F, E, V and H nor with the footer's Q. The grid's
        // shortcut menu repeats them (TablePage).
        var results = _tables[TableSpecs.Results.Name];
        _openButton = results.AddCommand("", "_Open on Event page", "Show the selected board on the Event page", OpenBoard);
        results.AddCommand("", "Save _report...", "Save the event's results as a CSV file for Excel", () => OnReport(this, new RoutedEventArgs()));
        results.SelectionChanged += UpdatePageButtons;

        // Verbs, as the page's other commands are: a button called just
        // "Back" is heard as going back a page.
        var adults = _tables[TableSpecs.Adults.Name];
        adults.AddCommand("", "_Add adult...", "Sign in an adult who would rather not use the tablet", OnAddAdult);
        _goneHomeButton = adults.AddCommand("", "Mark _gone home", "Mark the selected adult as gone home", OnGoneHome);
        _backButton = adults.AddCommand("", "Mark _back", "Mark the selected adult as back and available", OnBack);
        adults.SelectionChanged += UpdatePageButtons;
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
        _signInButton = history.AddCommand("", "_Sign in for today", "Put the selected adult on today's list, as if they had signed in at the tablet", OnSignInFromHistory);
        history.SelectionChanged += UpdatePageButtons;

        _pages.Add((ViewEvent, EventView));
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
        ShowImportText();

        // The address is read off this screen and typed into every station, so
        // say plainly when it's one the stations can't use. A failed import is
        // said with it, rather than lost behind it.
        var importToo = session.ImportError != null
            ? " The SignUpGenius import failed too, so everyone signs in as a walk-in: " + session.ImportError
            : "";
        if (session.IsLocalOnly)
        {
            UrlLabel.Text = "This computer only: ";
            UrlLabel.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCautionBrush");
            Notice.Show(Severity.Warning, "Check-in stations can't connect",
                "The scheduler was started for this computer only, so other computers can't reach the sign-in page. "
                + "To use check-in stations, close the scheduler and start it again with the venue Wi-Fi as the check-in network." + importToo);
        }
        else if (session.CheckInUrls.Count == 0)
        {
            UrlLabel.Text = "No network found";
            UrlLabel.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCautionBrush");
            Notice.Show(Severity.Warning, "Check-in stations can't connect",
                "This computer isn't on a network, so there's no address for the stations. Connect to the venue Wi-Fi, then close the scheduler and start it again." + importToo);
        }
        else if (session.ImportError != null)
        {
            ShowImportError();
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
            SayArrivals();
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

        // A letter typed without Alt runs no button here (K1): WPF otherwise
        // treats it as the access key when the focused control doesn't take
        // text, so a "c" typed on a Result choice completed the board. Only
        // text no control used reaches the window; menus keep their letters,
        // and dialogs, other windows, theirs.
        AddHandler(TextCompositionManager.TextInputEvent, new TextCompositionEventHandler(OnBareLetter));
        Loaded += (_, _) => FocusPage();
        Closing += OnClosing;

        ShowPage("Event");
        RefreshAll();
        Remember();
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

        // A board proposed for the open youth, and not changed since, follows
        // the event: an adult who signs in or frees up is weighed at once,
        // without Start over (SPEC.md D-12).
        if (_current is { } open && BoardStatus.IsWaiting(open.Status) && _proposal?.ScoutId == open.Id && ProposalUntouched())
        {
            ProposeBoard(open);
        }

        UpdateRoomTimers();
        ShowDetails();
        foreach (var table in _tables.Values.Where(t => t.Visibility == Visibility.Visible))
        {
            table.Reload(quiet: true);
        }

        UpdatePageButtons();
        if (PersonFind.Text.Trim().Length > 0)
        {
            UpdatePersonFind();
        }

        // Edit > Undo (and Ctrl+Z) is the one way to undo, and names the
        // step, as the Mac's does. A name's own underscore mustn't become an
        // access key.
        UndoMenu.IsEnabled = _svc.CanUndo;
        UndoMenu.Header = _svc.UndoDescription is { } step ? "_Undo " + step.Replace("_", "__", StringComparison.Ordinal) : "_Undo";
    }

    /// <summary>Note everyone signed in now, so only those who come later are said to have arrived.</summary>
    private void Remember()
    {
        _known.UnionWith(_scouts.Select(s => s.Id));
        _known.UnionWith(_adults.Select(a => a.Id));
    }

    /// <summary>
    /// Who signed in at the door since the last refresh, said once and
    /// politely (K2): one sentence per debounce however many came, never one
    /// per person during the rush. Speech only: the queue shows them.
    /// </summary>
    private void SayArrivals()
    {
        var youth = _scouts.Where(s => !_known.Contains(s.Id)).Select(s => s.FullName).ToList();
        var adults = _adults.Where(a => !_known.Contains(a.Id)).Select(a => a.FullName).ToList();
        Remember();
        var text = (youth.Count, adults.Count) switch
        {
            (0, 0) => "",
            (1, 0) => $"{youth[0]} signed in.",
            (0, 1) => $"{adults[0]} signed in.",
            (_, 0) => $"{Display.Count(youth.Count, "youth", "youth")} signed in.",
            (0, _) => $"{Display.Count(adults.Count, "adult", "adults")} signed in.",
            _ => $"{Display.Count(youth.Count, "youth", "youth")} and {Display.Count(adults.Count, "adult", "adults")} signed in.",
        };
        Announcer.Say(QueueList, text, "EagleBoards.SignIn");
    }

    /// <summary>Times are stamped to the minute, so counts change on the clock's minute: tick just after it.</summary>
    private void ScheduleMinute()
    {
        var now = DateTime.Now;
        _minute.Interval = TimeSpan.FromSeconds(60 - now.Second) - TimeSpan.FromMilliseconds(now.Millisecond) + TimeSpan.FromMilliseconds(250);
        _minute.Start();
    }

    /// <summary>
    /// Only what the minute changes: the counts, the room timers and the
    /// details pane's Room fact. The member lists are left as they are, so a
    /// keyboard user on a Replace or Remove button keeps their place (F2).
    /// </summary>
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
            ShowFacts(scout);
        }

        ScheduleMinute();
    }

    /// <summary>
    /// Minutes on each room holding a board, caution then overdue at the
    /// board-type thresholds. Runs on the youth's minutes since the last
    /// change, so the clock restarts by itself at Seat and at Start review.
    /// A room turning running long or overdue is said, once, as the tint is
    /// seen in passing (F4); speech only, nothing more on screen.
    /// </summary>
    private void UpdateRoomTimers()
    {
        var turned = new List<string>();
        foreach (var card in _rooms)
        {
            var scout = _scouts.FirstOrDefault(s => s.Room == card.Room && BoardStatus.IsActive(s.Status));
            var mins = scout?.Mins;
            var state = mins is { } m ? SchedulerLogic.TimerFor(scout!.Status, scout.BoardType, m, _config) : TimerState.None;
            var was = card.TimerState;
            card.Mins = mins;
            card.TimerText = mins is { } n ? $"{n} min" : "";
            card.TimerState = state;
            if (_timedRooms.Contains(card.Id) && state != was && state is TimerState.Warning or TimerState.Overdue)
            {
                turned.Add($"Room {card.Room} is {(state == TimerState.Overdue ? "overdue" : "running long")}: {scout!.FullName}, {Display.Minutes(mins!.Value)}.");
            }

            _timedRooms.Add(card.Id);
        }

        Announcer.Say(RoomList, string.Join(" ", turned), "EagleBoards.RoomTimer");
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

    /// <summary>
    /// The Event page or a table page, with the View menu's check on the page
    /// shown and its name in the title (what a screen reader's "read title"
    /// and Alt+Tab say). The focus goes into the page shown: left on the page
    /// just hidden, it fell to the window and the next Tab started at the top.
    /// </summary>
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

        _page = page;
        var name = _tables.TryGetValue(page, out var shownTable) ? shownTable.Spec.Title : "Event";
        Title = $"{name} - Eagle Board Scheduler {AppVersion.Text}";
        UpdatePageButtons();
        if (IsLoaded)
        {
            Dispatcher.BeginInvoke(FocusPage, DispatcherPriority.Loaded);
        }
    }

    /// <summary>Ctrl+Tab and Ctrl+Shift+Tab: the next or previous page, in the View menu's order.</summary>
    private void ShowNextPage(int step)
    {
        var at = _pages.FindIndex(p => (string)p.Item.Tag == _page);
        ShowPage((string)_pages[(at + step + _pages.Count) % _pages.Count].Item.Tag);
    }

    /// <summary>The focus into the page shown: the open youth on the Event page, a table's selected row.</summary>
    private void FocusPage()
    {
        if (EventPage.IsVisible)
        {
            FocusQueue();
        }
        else
        {
            _tables.Values.FirstOrDefault(t => t.IsVisible)?.FocusContent();
        }
    }

    /// <summary>The open youth's item in the queue, else the first, else the list.</summary>
    private void FocusQueue()
    {
        if (!FocusItem(QueueList))
        {
            QueueList.Focus();
        }
    }

    /// <summary>A list's selected item, or its first, takes the focus.</summary>
    private static bool FocusItem(ListBox list)
    {
        var item = list.SelectedItem ?? (list.Items.Count > 0 ? list.Items[0] : null);
        if (item == null)
        {
            return false;
        }

        list.ScrollIntoView(item);
        return list.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container && container.Focus();
    }

    /// <summary>A list given the focus itself (Alt+Y, Alt+O, F6) passes it to its selected item, where the arrow keys start.</summary>
    private void OnListFocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is ListBox list && ReferenceEquals(e.NewFocus, list))
        {
            Dispatcher.BeginInvoke(() => FocusItem(list), DispatcherPriority.Loaded);
        }
    }

    /// <summary>
    /// F6 and Shift+F6: the next or previous pane. On the Event page the
    /// Youth list, the Rooms card (Find a person first), the details pane and
    /// the check-in address; on a table page Find and its commands, then the
    /// grid. Windows apps move between panes this way; Tab alone took a dozen
    /// stops from the queue to the board.
    /// </summary>
    private void CyclePane(bool back)
    {
        var focused = Keyboard.FocusedElement as DependencyObject;
        (DependencyObject Area, Action Focus)[] panes;
        if (EventPage.IsVisible)
        {
            panes =
            [
                (QueuePane, FocusQueue),
                (RoomsPane, () => PersonFind.Focus()),
                (DetailsPane, FocusDetails),
                (Footer, () => Footer.MoveFocus(new TraversalRequest(FocusNavigationDirection.First))),
            ];
        }
        else if (_tables.Values.FirstOrDefault(t => t.IsVisible) is { } page)
        {
            // A cell being edited is saved as it's left, as ShowPage does.
            page.CommitEdit();
            panes = [(page, () => page.Find.Focus()), (page.Grid, page.FocusContent)];
        }
        else
        {
            return;
        }

        // The innermost pane holding the focus: a table page holds its grid.
        var at = -1;
        for (var i = 0; i < panes.Length; i++)
        {
            if (Within(panes[i].Area, focused))
            {
                at = i;
            }
        }

        var next = at < 0 ? (back ? panes.Length - 1 : 0) : (at + (back ? -1 : 1) + panes.Length) % panes.Length;
        panes[next].Focus();
    }

    /// <summary>The details pane's first control, else its step buttons, else the pane itself to scroll.</summary>
    private void FocusDetails()
    {
        if (!Details.MoveFocus(new TraversalRequest(FocusNavigationDirection.First))
            && !(ActionBar.IsVisible && ActionBar.MoveFocus(new TraversalRequest(FocusNavigationDirection.First))))
        {
            DetailsScroll.Focus();
        }
    }

    /// <summary>Is <paramref name="element"/> <paramref name="area"/> or inside it? A link in text counts by the text holding it.</summary>
    private static bool Within(DependencyObject area, DependencyObject? element)
    {
        while (element is not null and not Visual)
        {
            element = LogicalTreeHelper.GetParent(element);
        }

        return element != null && (ReferenceEquals(area, element) || (area is Visual a && a.IsAncestorOf(element)));
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
    // Keys
    // ------------------------------------------------------------------

    /// <summary>
    /// Ctrl+Enter runs the youth's next step (SPEC.md P-3): Seat board, Start
    /// review or Complete, whichever the pane shows, from anywhere on the
    /// Event page, the Notes box included (hence a preview: the box would
    /// otherwise take the key for a new line). Ctrl+Tab and F6 here too, so a
    /// grid or a list that uses Tab itself can't keep them.
    /// </summary>
    private void OnPreviewKey(object sender, KeyEventArgs e)
    {
        // An open menu keeps its keys.
        if (InMenu(Keyboard.FocusedElement as DependencyObject))
        {
            return;
        }

        var modifiers = Keyboard.Modifiers;
        if (e.Key == Key.Tab && modifiers.HasFlag(ModifierKeys.Control))
        {
            ShowNextPage(modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
            e.Handled = true;
        }
        else if (e.Key == Key.F6 && (modifiers & ~ModifierKeys.Shift) == ModifierKeys.None)
        {
            CyclePane(back: modifiers == ModifierKeys.Shift);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && modifiers == ModifierKeys.Control && EventPage.IsVisible
                 && new[] { SeatButton, StartButton, CompleteButton }.FirstOrDefault(b => b.IsVisible && b.IsEnabled) is { } step)
        {
            step.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, step));
            e.Handled = true;
        }
    }

    /// <summary>
    /// A letter or digit typed with no Alt that no control took: kept from
    /// WPF's access keys, which would run the button it underlines (K1).
    /// Alt plus the letter comes as system text, and still does.
    /// </summary>
    private void OnBareLetter(object sender, TextCompositionEventArgs e)
    {
        if (e.Text.Length == 1 && char.IsLetterOrDigit(e.Text[0]) && !InMenu(e.OriginalSource as DependencyObject))
        {
            e.Handled = true;
        }
    }

    /// <summary>In the menu bar, a submenu or a shortcut menu, whose letters choose their items.</summary>
    private static bool InMenu(DependencyObject? element)
    {
        for (var at = element; at != null; at = at is Visual ? VisualTreeHelper.GetParent(at) ?? LogicalTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at))
        {
            if (at is MenuItem or System.Windows.Controls.Primitives.MenuBase)
            {
                return true;
            }
        }

        return false;
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
    /// the event changes (<see cref="RefreshAll"/>). The note is read out
    /// with whom the rooms hold, which only the cards show (F3), and only
    /// when that changes: not again on every sign-in at the door.
    /// </summary>
    private void UpdatePersonFind()
    {
        var query = PersonFind.Text.Trim();
        HashSet<string>? found = null;
        var note = "";
        var spoken = "";
        if (query.Length > 0)
        {
            var people = SchedulerLogic.FindPeople(query, _scouts.Select(s => s.Info), _adults.Select(a => a.Info));
            var inRooms = people.Where(p => p.Room != null).ToList();
            var named = _rooms.Select(r => r.Room).Where(r => r.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
            found = inRooms.Select(p => p.Room!).Concat(named).ToHashSet(StringComparer.Ordinal);
            var elsewhere = people.Where(p => p.Room == null).ToList();
            note = found.Count == 0 && elsewhere.Count == 0 ? "No one by that name has signed in."
                : string.Join(" ", elsewhere.Take(4).Select(p => $"{p.Name} {p.Where}."))
                  + (elsewhere.Count > 4 ? $" And {elsewhere.Count - 4} more." : "");
            var rooms = string.Join(" ", inRooms.Take(4).Select(p => $"{p.Name} {p.Where}."))
                + (inRooms.Count > 4 ? $" And {inRooms.Count - 4} more in rooms." : "")
                + (named.Count == 0 ? "" : $" {Display.Count(named.Count, "room", "rooms")} by that name: {string.Join(", ", named)}.");
            spoken = (rooms.Trim() + " " + note).Trim();
        }

        var changed = found == null ? _foundRooms != null : _foundRooms == null || !_foundRooms.SetEquals(found);
        _foundRooms = found;
        PersonFindNote.Text = note;
        AutomationProperties.SetName(PersonFindNote, spoken);
        if (changed)
        {
            // Refreshing resets the cards: put the focus back on the one that had it.
            var focused = Keyboard.FocusedElement is ListBoxItem { Content: RoomCard card } ? card : null;
            _roomView.Refresh();
            if (focused != null && _roomView.Contains(focused))
            {
                Dispatcher.BeginInvoke(() => (RoomList.ItemContainerGenerator.ContainerFromItem(focused) as ListBoxItem)?.Focus(), DispatcherPriority.Loaded);
            }
        }

        if (spoken.Length > 0 && spoken != _personFindSpoken)
        {
            Announcer.LiveRegionChanged(PersonFindNote);
        }

        _personFindSpoken = spoken;
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

    /// <summary>
    /// The youth list's shortcut menu: the open youth's steps, as the
    /// details pane's buttons have them. Shift+F10 on a youth the arrow keys
    /// reached without opening (Ctrl+arrow) opens them first. Nothing to do,
    /// no menu.
    /// </summary>
    private void OnQueueMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (Keyboard.FocusedElement is ListBoxItem { Content: ScoutRow row } && !ReferenceEquals(row, _current))
        {
            Open(row);
        }

        SyncQueueMenu();
        if (_current == null || QueueMenuItems().All(i => i.Item.Visibility != Visibility.Visible))
        {
            e.Handled = true;
        }
    }

    private (MenuItem Item, Button Button)[] QueueMenuItems() =>
    [
        (QueueSeat, SeatButton), (QueuePostpone, PostponeButton), (QueueStart, StartButton), (QueueComplete, CompleteButton), (QueueReset, ResetButton),
    ];

    /// <summary>The queue's shortcut menu as the step buttons stand: shown, enabled, and "Seat anyway" when Seat says so.</summary>
    private void SyncQueueMenu()
    {
        foreach (var (item, button) in QueueMenuItems())
        {
            item.Visibility = button.Visibility;
            item.IsEnabled = button.IsEnabled;
        }

        QueueSeat.Header = SeatText.Text;
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

    /// <summary>The room card with the keyboard focus, else the one selected.</summary>
    private RoomCard? FocusedRoom() => Keyboard.FocusedElement is ListBoxItem { Content: RoomCard card } ? card : SelectedRoom;

    /// <summary>F2 on a room card renames it, as F2 renames in File Explorer.</summary>
    private void OnRoomKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2 && Keyboard.Modifiers == ModifierKeys.None && FocusedRoom() is { } room)
        {
            RenameRoom(room);
            e.Handled = true;
        }
    }

    /// <summary>Shift+F10 on a card reached with Ctrl+arrow, not yet selected: the menu acts on that card, as a right-click would.</summary>
    private void OnRoomMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (Keyboard.FocusedElement is ListBoxItem { Content: RoomCard card } && !ReferenceEquals(card, SelectedRoom))
        {
            RoomList.SelectedItem = card;
        }
    }

    /// <summary>
    /// Show a youth in the details pane. A waiting youth gets a proposed board
    /// unless the operator has changed the picks: then they are their work in
    /// progress and survive clicking around; only Start over and seating
    /// clear them. A proposal they left alone is made afresh for this youth.
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

    /// <summary>
    /// Propose a board for a waiting youth, unless the operator has one of
    /// their own: picks they changed, or found in the file at start-up.
    /// A proposal they haven't changed is made again (SPEC.md D-12), for
    /// another youth opened and as the event changes (<see cref="RefreshAll"/>).
    /// </summary>
    private void ProposeBoard(ScoutRow scout)
    {
        var free = _rooms.Where(r => r.IsFree).ToList();
        if (_adults.Any(IsPicked) && !ProposalUntouched())
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
        var ids = pick.AllAdultIds.ToHashSet(StringComparer.Ordinal);
        foreach (var adult in _adults.Where(a => a.CanPick && a.Sel != ids.Contains(a.Id)))
        {
            adult.Sel = ids.Contains(adult.Id);
        }

        _chairId = pick.ChairIds.FirstOrDefault();

        // The same youth keeps the room chosen for them while it's free.
        if (_proposal?.ScoutId != scout.Id || free.All(r => r.Id != _builderRoomId))
        {
            _builderRoomId = pick.RoomId ?? free.FirstOrDefault()?.Id;
        }

        _proposal = new ProposedBoard(scout.Id, ids, _chairId);
    }

    /// <summary>The picks are still the board last proposed: the operator hasn't changed it.</summary>
    private bool ProposalUntouched() =>
        _proposal is { } proposal
        && proposal.IsUntouched(_adults.Where(IsPicked).Select(a => a.Id).ToList(),
            id => _adults.FirstOrDefault(a => a.Id == id)?.CanPick == true, _chairId);

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
        // The member lists are rebuilt on every change, a sign-in at the door
        // included: put the focus back where it was (C1).
        if (MarkFocus() is { } mark)
        {
            Dispatcher.BeginInvoke(() => RestoreFocus(mark), DispatcherPriority.Loaded);
        }

        var scout = _current;
        DetailsEmpty.Visibility = scout == null ? Visibility.Visible : Visibility.Collapsed;
        DetailsHead.Visibility = scout != null ? Visibility.Visible : Visibility.Collapsed;
        LocateSection.Visibility = DetailsHead.Visibility;
        BuilderSection.Visibility = scout != null && BoardStatus.IsWaiting(scout.Status) ? Visibility.Visible : Visibility.Collapsed;
        BoardSection.Visibility = scout != null && !BoardStatus.IsWaiting(scout.Status) ? Visibility.Visible : Visibility.Collapsed;
        ShowActions(scout);
        if (scout != null)
        {
            DetailName.Text = scout.FullName;
            DetailSub.Text = string.Join(" · ", new[] { Display.Unit(scout.UnitName), Display.BoardType(scout.BoardType), scout.RegNum }.Where(s => s.Length > 0));
            AutomationProperties.SetName(DetailSub, Display.Spoken(DetailSub.Text));
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

        SyncQueueMenu();
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

    private void ShowBuilder(ScoutRow scout)
    {
        // Rooms: free ones, this board's type first.
        var free = _rooms.Where(r => r.IsFree).OrderBy(r => r.BoardType == scout.BoardType ? 0 : 1).ThenBy(r => r.Room, StringComparer.OrdinalIgnoreCase).ToList();
        var rooms = free.Select(r => new Option(r.Id, $"{r.Room} · {r.BoardTypeText}")).ToList();
        Quietly(() =>
        {
            if (BuilderRoom.ItemsSource is not List<Option> shown || !shown.SequenceEqual(rooms))
            {
                BuilderRoom.ItemsSource = rooms;
            }

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

        SetRows(PickList, picked.OrderByDescending(p => p.Id == _chairId).ToList());
        NoPicksText.Visibility = picked.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Found by the unit in full or as the file's short form ("T1001").
        var filter = AvailableFilter.Text.Trim();
        var available = _adults
            .Where(a => !a.Sel && a.CanPick && a.Info.RoleFor(scout.BoardType) != BoardRoles.Unavailable)
            .Where(a => Matches(filter, a.Last, a.First, a.FullName, a.UnitName, a.UnitLabel, Display.Unit(a.UnitName)))
            .Select(a => new PickRow(a, scout))
            .OrderBy(p => p.SameUnit)
            .ThenByDescending(p => p.CanChair && _chairId == null)
            .ThenBy(p => p.Adult.Last, StringComparer.OrdinalIgnoreCase)
            .ToList();
        SetRows(AvailableList, available);
        NoAvailableText.Visibility = available.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var room = _rooms.FirstOrDefault(r => r.Id == _builderRoomId);
        var issues = BoardCheck.Review(scout.Info, picked.Select(p => p.Adult.Info).ToList(), _chairId, room?.Info);
        ShowIssues(scout, issues);

        var warn = issues.Any(i => i.Level == IssueLevel.Warn);
        SeatButton.IsEnabled = issues.All(i => i.Level != IssueLevel.Block);
        FillButton.IsEnabled = picked.Count < BoardRules.MinMembers(scout.BoardType) || picked.All(p => !p.CanChair);
        SeatText.Text = warn ? "_Seat anyway" : "_Seat board";
        AutomationProperties.SetName(SeatButton, warn ? "Seat anyway" : "Seat board");
    }

    /// <summary>
    /// The seating review's problems, each an InfoBar, rebuilt only when they
    /// change: every sign-in refreshes the pane, and a new bar each time was
    /// a new message to read out. A problem that appears while the operator
    /// builds this youth's board is read out; those already there when a
    /// youth is opened are only shown.
    /// </summary>
    private void ShowIssues(ScoutRow scout, IReadOnlyList<BoardIssue> issues)
    {
        static string Key(BoardIssue i) => $"{i.Level}|{i.Title}|{i.Message}";
        var ordered = issues.OrderBy(i => i.Level).ToList();
        var key = string.Join("\n", ordered.Select(Key));
        if (key == _issueKey && scout.Id == _issueScoutId)
        {
            return;
        }

        var sameYouth = scout.Id == _issueScoutId;
        var before = _issueKey.Split('\n').ToHashSet(StringComparer.Ordinal);
        IssueList.Children.Clear();
        foreach (var issue in ordered)
        {
            var bar = new InfoBar { IsClosable = false, Margin = new Thickness(0, 0, 0, 8) };
            bar.Show(issue.Level == IssueLevel.Block ? Severity.Error : Severity.Warning, issue.Title, issue.Message,
                announce: sameYouth && !before.Contains(Key(issue)));
            IssueList.Children.Add(bar);
        }

        _issueKey = key;
        _issueScoutId = scout.Id;
    }

    /// <summary>Show these rows, unless the list already shows the same: a rebuild loses the focus and the place in the list.</summary>
    private static void SetRows(ItemsControl list, List<PickRow> rows)
    {
        if (list.ItemsSource is not List<PickRow> shown || !shown.Select(p => p.Signature).SequenceEqual(rows.Select(p => p.Signature)))
        {
            list.ItemsSource = rows;
        }
    }

    /// <summary>A seated, running or finished board: its facts, and the members of one still running.</summary>
    private void ShowBoard(ScoutRow scout)
    {
        ShowFacts(scout);
        var active = BoardStatus.IsActive(scout.Status);
        MembersSection.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        if (active)
        {
            var members = _adults.Where(a => a.Room == scout.Room)
                .Select(a => new PickRow(a, scout)).ToList();
            foreach (var m in members)
            {
                m.SetChairQuietly(m.Id == scout.BoardChairId);
            }

            SetRows(BoardMemberList, members.OrderByDescending(m => m.IsChair).ThenBy(m => m.Adult.Last, StringComparer.OrdinalIgnoreCase).ToList());
        }

        ResultSection.Visibility = scout.Status == BoardStatus.InProgress ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The board's facts, the room's minutes among them; nothing in them takes the focus, so the minute can replace them.</summary>
    private void ShowFacts(ScoutRow scout)
    {
        var facts = new List<Fact>();
        if (BoardStatus.IsActive(scout.Status))
        {
            var what = scout.Status == BoardStatus.Seated ? "convening" : "in review";
            facts.Add(new("Room", $"{scout.Room}, {what} for {Display.Minutes(scout.Mins ?? 0)}"));
        }

        // A finished board's members are history; a running board's can change.
        if (scout.BoardChair.Length > 0 && !BoardStatus.IsActive(scout.Status))
        {
            facts.Add(new("Chair", scout.BoardChair));
            facts.Add(new("Members", scout.BoardMembersText));
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
    }

    /// <summary>Where the youth's leaders and parents are: for fetching them, and after the board.</summary>
    private void ShowLocate(ScoutRow scout)
    {
        var found = SchedulerLogic.Locate(scout.Info, _adults.Select(a => a.Info), includeParents: true);
        LocateList.ItemsSource = found.Select(f => new Fact(
            f.IsSupporting ? "Introduces them" : f.IsLeader ? "Leader" : "Parent",
            $"{f.Adult.First} {f.Adult.Last}, {(f.Adult.Room == AdultRoom.Disabled ? "gone home" : f.Adult.Room.Length == 0 ? "in the main room" : "in room " + f.Adult.Room)}")).ToList();
        LocateNone.Text = found.Count > 0 ? ""
            : scout.Leader.Length > 0 ? $"{scout.Leader} hasn't signed in, and no parent has."
            : "No leader was given at sign-in, and no parent has signed in.";
        LocateNone.Visibility = found.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        UnlinkButton.Visibility = _adults.Any(a => a.Info.Supports(scout.Id)) ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------
    // Focus
    // ------------------------------------------------------------------

    /// <summary>The focus on a row's button in one of the details pane's lists: which list, whose row, which button, and where.</summary>
    private sealed record FocusMark(ItemsControl List, string RowId, string Role, int Index, UIElement Element);

    /// <summary>The focused element, or, while a dialog of this window has just closed, the one the window will give it back to.</summary>
    private IInputElement? FocusedHere() =>
        Keyboard.FocusedElement is DependencyObject d && GetWindow(d) == this ? Keyboard.FocusedElement : FocusManager.GetFocusedElement(this);

    private FocusMark? MarkFocus()
    {
        if (FocusedHere() is not FrameworkElement { DataContext: PickRow row } focused)
        {
            return null;
        }

        foreach (var list in new ItemsControl[] { PickList, AvailableList, BoardMemberList })
        {
            if (list.IsAncestorOf(focused))
            {
                return new FocusMark(list, row.Id, AutomationProperties.GetAutomationId(focused), list.Items.IndexOf(row), focused);
            }
        }

        return null;
    }

    /// <summary>
    /// Back on the same button of the same adult's row once the list is
    /// rebuilt; if they left it (added, removed), on the row now in their
    /// place, or the last; if the list is empty, on Find an adult or Add
    /// member. Nothing moves if the list wasn't rebuilt, or the operator has
    /// gone somewhere else meanwhile.
    /// </summary>
    private void RestoreFocus(FocusMark mark)
    {
        var now = Keyboard.FocusedElement;
        if (mark.Element.IsVisible || !Lost(now, mark.Element) || !mark.List.IsVisible)
        {
            return;
        }

        var rows = mark.List.Items.OfType<PickRow>().ToList();
        if (rows.Count == 0)
        {
            (ReferenceEquals(mark.List, BoardMemberList) ? AddMemberButton : (UIElement)AvailableFilter).Focus();
            return;
        }

        var row = rows.FirstOrDefault(p => p.Id == mark.RowId) ?? rows[Math.Clamp(mark.Index, 0, rows.Count - 1)];
        if (mark.List.ItemContainerGenerator.ContainerFromItem(row) is DependencyObject container
            && (Find(container, e => AutomationProperties.GetAutomationId(e) == mark.Role) ?? Find(container, _ => true)) is { } target)
        {
            target.Focus();
        }
    }

    /// <summary>Focus has gone nowhere useful: to the window, a scroll area, or an element no longer shown.</summary>
    private bool Lost(IInputElement? now, IInputElement? was) =>
        now == null || ReferenceEquals(now, this) || ReferenceEquals(now, was) || now is ScrollViewer || now is UIElement { IsVisible: false } or UIElement { IsEnabled: false };

    /// <summary>The first control under <paramref name="from"/> that can take the focus and passes <paramref name="test"/>.</summary>
    private static UIElement? Find(DependencyObject from, Func<UIElement, bool> test)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(from); i++)
        {
            var child = VisualTreeHelper.GetChild(from, i);
            if (child is UIElement { Focusable: true, IsVisible: true, IsEnabled: true } element && test(element))
            {
                return element;
            }

            if (Find(child, test) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// After a step, when the control that had the focus has gone (a step's
    /// button collapses once its step is done; Gone home is disabled once
    /// pressed), the focus goes to the first of these that can take it, the
    /// next step's button usually, rather than to the window, where the next
    /// Tab starts again at the top (C2). A step run with Ctrl+Enter from a
    /// control still shown leaves the focus where it was.
    /// </summary>
    private void FocusAfter(IInputElement? before, params Func<IInputElement?>[] candidates)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var now = Keyboard.FocusedElement is DependencyObject d && GetWindow(d) == this ? Keyboard.FocusedElement : null;
            var gone = before is UIElement { IsVisible: false } or UIElement { IsEnabled: false };
            if (!gone && !Lost(now, null))
            {
                return;
            }

            foreach (var candidate in candidates)
            {
                if (candidate() is UIElement { IsVisible: true, IsEnabled: true } target && target.Focus())
                {
                    return;
                }
            }
        }, DispatcherPriority.Loaded);
    }

    /// <summary>A youth's item in the queue.</summary>
    private ListBoxItem? QueueItemFor(ScoutRow? scout) =>
        scout == null ? null : QueueList.ItemContainerGenerator.ContainerFromItem(scout) as ListBoxItem;

    // ------------------------------------------------------------------
    // Linking the adult who introduces a youth
    // ------------------------------------------------------------------

    /// <summary>
    /// Link an adult to this youth as the one who introduces them to their
    /// board (SPEC.md D-23), for the adult who didn't say so at sign-in.
    /// They're listed under Leaders and parents from then on, with where to
    /// find them, and Start review reminds the operator to fetch them. Works
    /// for an adult on a board too: a Scoutmaster often is by then. With no
    /// one to link, it says so rather than doing nothing (K10).
    /// </summary>
    private void OnLinkAdult(object sender, RoutedEventArgs e)
    {
        if (_current is not { } scout)
        {
            return;
        }

        var choices = _adults.Where(a => !a.Info.Supports(scout.Id))
            .OrderBy(a => a.Last, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.First, StringComparer.OrdinalIgnoreCase)
            .Select(a => new Option(a.Id, string.Join(" · ", new[] { a.FullName, Display.Unit(a.UnitName) }.Where(s => s.Length > 0)))).ToList();
        if (choices.Count == 0)
        {
            DetailNotice.Show(Severity.Informational, "No adult to link",
                _adults.Count == 0 ? "No adult has signed in yet." : $"Every adult who has signed in is already linked to {scout.FullName}.");
            return;
        }

        var dialog = new AppDialog(this, $"Link an adult to {scout.FullName}?", "Link");
        dialog.AddMessage($"Choose the adult who will introduce **{scout.FullName}** to their board. "
            + "This is usually their Scoutmaster or a leader standing in. Start review will remind you to fetch them.");
        var pick = dialog.AddChoice("_Adult", choices, null);
        if (dialog.ShowDialog() && pick.SelectedValue is string adultId)
        {
            var name = _adults.FirstOrDefault(a => a.Id == adultId)?.FullName;
            Report(_svc.SetSupporting(adultId, scout.Id, linked: true), "Couldn't link them", $"{name} is linked to {scout.FullName}.");
        }
    }

    private void OnUnlinkAdult(object sender, RoutedEventArgs e)
    {
        if (_current is not { } scout)
        {
            return;
        }

        var linked = _adults.Where(a => a.Info.Supports(scout.Id))
            .Select(a => new Option(a.Id, a.FullName)).ToList();
        if (linked.Count == 0)
        {
            return;
        }

        var before = FocusedHere();
        var dialog = new AppDialog(this, $"Unlink an adult from {scout.FullName}?", "Unlink");
        var pick = dialog.AddChoice("_Adult", linked, null);
        if (dialog.ShowDialog() && pick.SelectedValue is string adultId)
        {
            var name = _adults.FirstOrDefault(a => a.Id == adultId)?.FullName;
            Report(_svc.SetSupporting(adultId, scout.Id, linked: false), "Couldn't unlink them", $"{name} is no longer linked to {scout.FullName}.");

            // Unlink goes with the last link.
            FocusAfter(before, () => LinkButton);
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

        var before = FocusedHere();
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

        // Fill the rest is disabled once the board is full: Seat is next.
        FocusAfter(before, () => SeatButton);
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

        var before = FocusedHere();
        var room = _rooms.FirstOrDefault(r => r.Id == roomId)?.Room;
        var picked = _adults.Where(IsPicked).ToList();
        var result = _svc.SeatBoard(roomId, scout.Id, _chairId, string.Join(",", picked.Select(a => a.Id)));
        if (!result.Ok)
        {
            DetailNotice.Show(Severity.Error, "Couldn't seat the board", Plain(result.Message));
        }

        _chairId = null;
        _builderRoomId = null;
        _proposal = null;
        RefreshAll();
        Open(scout);
        if (result.Ok)
        {
            Say($"{scout.FullName}'s board is seated in room {room}. Start review is next.");
            FocusAfter(before, () => StartButton, () => QueueItemFor(scout));
        }
    }

    /// <summary>
    /// Seated → In review. On a board of review, first the reminder to fetch
    /// the adult who introduces the youth, saying where they are (SPEC.md
    /// D-23); a project review has no introduction and starts at once.
    /// Reset undoes either.
    /// </summary>
    private void OnStart(object sender, RoutedEventArgs e)
    {
        if (_current is not { Status: BoardStatus.Seated } scout)
        {
            return;
        }

        var before = FocusedHere();
        if (SchedulerLogic.IntroductionFor(scout.Info, _adults.Select(a => a.Info)) is { } introduction
            && !StartReviewReminder(this, scout, introduction).ShowDialog())
        {
            return;
        }

        if (Report(_svc.StartReview(scout.Id), "Couldn't start the review", $"{scout.FullName}'s review has started in room {scout.Room}. Complete is next."))
        {
            FocusAfter(before, () => _resultButtons.Values.FirstOrDefault(b => b.IsChecked == true), () => CompleteButton);
        }
    }

    /// <summary>
    /// The reminder Start review shows on a board of review (SPEC.md D-23).
    /// It's the owner's reminder, not a confirmation (CLAUDE.md), so it stays;
    /// its buttons answer the title with a verb, and Cancel (P-4).
    /// </summary>
    internal static AppDialog StartReviewReminder(Window owner, ScoutRow scout, Introduction introduction)
    {
        var dialog = new AppDialog(owner, "Start the review?", "Start review");
        dialog.AddMessage(IntroductionReminder(scout, introduction));
        dialog.AddMessage($"Then bring {scout.First} in to room {scout.Room}.");
        return dialog;
    }

    /// <summary>Whom to fetch for the introduction, and where they are; never a parent.</summary>
    internal static string IntroductionReminder(ScoutRow scout, Introduction introduction)
    {
        static string Name(AdultInfo a) => $"**{a.First} {a.Last}**";
        var youth = $"**{scout.FullName}**";
        return introduction switch
        {
            { Introducers: [var one] } => $"Fetch {Name(one)} to introduce {youth} to the board. They're {SchedulerLogic.Whereabouts(one)}.",
            { Introducers.Count: > 1 } => $"Fetch whoever introduces {youth} to the board:\n"
                + string.Join("\n", introduction.Introducers.Select(a => $"{Name(a)}, {SchedulerLogic.Whereabouts(a)}")),
            { Leaders: [var one] } => $"No one has said they'll introduce {youth}. Their leader, {Name(one)}, is {SchedulerLogic.Whereabouts(one)}.",
            { Leaders.Count: > 1 } => $"No one has said they'll introduce {youth}. Their leaders:\n"
                + string.Join("\n", introduction.Leaders.Select(a => $"{Name(a)}, {SchedulerLogic.Whereabouts(a)}")),
            _ => $"No one has said they'll introduce {youth}, and their leader hasn't signed in. Ask {scout.First} who will.",
        };
    }

    private void OnComplete(object sender, RoutedEventArgs e)
    {
        if (_current is not { Status: BoardStatus.InProgress } scout)
        {
            return;
        }

        var before = FocusedHere();
        var room = scout.Room;
        var outcome = _resultButtons.First(b => b.Value.IsChecked == true).Key;
        if (Report(_svc.CompleteBoard(scout.Id, outcome, NotesBox.Text.Trim()), "Couldn't complete the board",
                $"{scout.FullName}'s board is complete: {Display.Result(outcome)}. Room {room} is free."))
        {
            FocusAfter(before, () => QueueItemFor(scout), () => QueueList);
        }
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        if (_current is not { } scout)
        {
            return;
        }

        var before = FocusedHere();
        var room = scout.Room;
        if (AppDialog.Confirm(this, "Reset this board?",
                $"**{scout.FullName}** goes back to waiting. Room {room} and its members are freed, and the board would need seating again.",
                "Reset board")
            && Report(_svc.ResetBoard(scout.Id), "Couldn't reset the board", $"{scout.FullName} is waiting again. Room {room} is free."))
        {
            FocusAfter(before, () => QueueItemFor(scout), () => QueueList);
        }
    }

    private void OnPostpone(object sender, RoutedEventArgs e)
    {
        if (_current is not { } scout || !BoardStatus.IsWaiting(scout.Status))
        {
            return;
        }

        var before = FocusedHere();
        if (AppDialog.Confirm(this, "Postpone this board?",
                $"**{scout.FullName}** won't have a board at this event. This is usually because the paperwork isn't in order.", "Postpone")
            && Report(_svc.PostponeBoard(scout.Id), "Couldn't postpone the board", $"{scout.FullName}'s board is postponed."))
        {
            FocusAfter(before, () => QueueItemFor(scout), () => QueueList);
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

    private void ChangeMembers(ScoutRow scout, IEnumerable<string> memberIds, string chairId, string success)
    {
        var ids = memberIds.Distinct().ToList();
        Report(_svc.ChangeBoardMembers(scout.Id, chairId, string.Join(",", ids)), "Couldn't change the board", success);
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
        var pick = AddAdultChoice(dialog, "Replace _with", choices);
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
        var name = _adults.FirstOrDefault(a => a.Id == joining)?.FullName;
        ChangeMembers(scout, ids, chair, $"{name} replaces {leaving.Name} on the board.");
    }

    /// <summary>
    /// No confirmation: adding them back is as easy. Except the only member
    /// who can chair: the board would have no chair, which the service
    /// refuses in file words; say what to do instead (D2).
    /// </summary>
    private void OnRemoveMember(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: PickRow leaving } || _current is not { } scout || RunningBoard() is not { } board)
        {
            return;
        }

        if (leaving.Id == board.ChairId
            && !board.Members.Any(m => m.Id != leaving.Id && m.Info.RoleFor(scout.BoardType) == BoardRoles.Chair))
        {
            DetailNotice.Show(Severity.Warning, $"{leaving.Name} is the only chair",
                "No one else on this board can chair it. Use Replace to swap in a qualified chair, or add one with Add member first.");
            return;
        }

        var ids = board.Members.Where(m => m.Id != leaving.Id).Select(m => m.Id).ToList();
        ChangeMembers(scout, ids, ChairAfter(scout, ids, board.ChairId), $"{leaving.Name} is off the board.");
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
        var pick = AddAdultChoice(dialog, "_Add to the board in room " + scout.Room.Replace("_", "__", StringComparison.Ordinal), choices);
        if (dialog.ShowDialog() && pick.SelectedValue is string joining)
        {
            var name = _adults.FirstOrDefault(a => a.Id == joining)?.FullName;
            ChangeMembers(scout, board.Members.Select(m => m.Id).Append(joining), board.ChairId, $"{name} is on the board.");
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
        var pick = AddAdultChoice(dialog, "_New chair", qualified);
        if (dialog.ShowDialog() && pick.SelectedValue is string chair)
        {
            var name = _adults.FirstOrDefault(a => a.Id == chair)?.FullName;
            ChangeMembers(scout, board.Members.Select(m => m.Id), chair, $"{name} is now the chair.");
        }
    }

    /// <summary>
    /// A step's result: a refusal in the details pane, and a step that
    /// worked said aloud, speech only, since the screen shows it (SPEC.md
    /// D-14) but the button used has often gone (K2). True if it worked.
    /// </summary>
    private bool Report(ActionResult result, string failure, string? success = null)
    {
        if (!result.Ok)
        {
            DetailNotice.Show(Severity.Error, failure, Plain(result.Message));
        }

        RefreshAll();
        if (result.Ok && success != null)
        {
            Say(success);
        }

        return result.Ok;
    }

    /// <summary>A step that worked, said to a screen reader and put nowhere on screen.</summary>
    private void Say(string text) => Announcer.Say(this, text, "EagleBoards.Step", important: true);

    /// <summary>
    /// The server's refusals start "ERROR: " for the check-in pages; the window
    /// says it once, in the title. Names in them are as stored, so a comma
    /// saved as "~" is put back.
    /// </summary>
    private static string Plain(string message) => Display.Text(message.StartsWith("ERROR: ", StringComparison.Ordinal) ? message[7..] : message);

    // ------------------------------------------------------------------
    // Adults
    // ------------------------------------------------------------------

    private TablePage Adults => _tables[TableSpecs.Adults.Name];

    private TablePage History => _tables[TableSpecs.AdultHistory.Name];

    /// <summary>The table pages' commands that need a row, enabled only with one (K10): never a command that does nothing.</summary>
    private void UpdatePageButtons()
    {
        var adult = Adults.Selected;
        _backButton.IsEnabled = adult is { IsDisabled: true };
        _goneHomeButton.IsEnabled = adult is { IsDisabled: false, IsBusy: false };
        _signInButton.IsEnabled = History.Selected is { } known && !_adults.Any(a => a.Id == known.Id);
        _openButton.IsEnabled = _tables[TableSpecs.Results.Name].Selected is { } board && _scouts.Any(s => s.Id == board.Id);
    }

    /// <summary>Add adult: someone who would rather not use the tablet signs in here.</summary>
    private void OnAddAdult()
    {
        var before = _adults.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        if (AddAdultDialog.Show(this, _svc))
        {
            RefreshAll();
            SayAdded(before);
        }
    }

    /// <summary>
    /// Sign in for today, from the adult history. Their Last event becomes
    /// Today, which shows it worked (SPEC.md D-14); a screen reader is told.
    /// The command is disabled once they're on today's list: the grid's row
    /// keeps the focus.
    /// </summary>
    private void OnSignInFromHistory()
    {
        if (History.Selected is { } known && !_adults.Any(a => a.Id == known.Id))
        {
            var before = _adults.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
            var focus = FocusedHere();
            AddAdultDialog.SignInFromHistory(_svc, known);
            RefreshAll();
            SayAdded(before);
            FocusAfter(focus, () => { History.FocusContent(); return null; });
        }
    }

    /// <summary>Who the operator just signed in, said as a step, and not again as an arrival at the door.</summary>
    private void SayAdded(HashSet<string> before)
    {
        var added = _adults.Where(a => !before.Contains(a.Id)).ToList();
        _known.UnionWith(added.Select(a => a.Id));
        if (added.Count == 1)
        {
            Say($"{added[0].FullName} is signed in for today.");
        }
    }

    /// <summary>Mark gone home. No confirmation: Undo (or Mark back) reverses it.</summary>
    private void OnGoneHome()
    {
        if (Adults.Selected is { IsDisabled: false, IsBusy: false } adult)
        {
            var before = FocusedHere();
            if (ReportOn(Adults, _svc.DisableAdult(adult.Id), "Couldn't mark them gone home", $"{adult.Describe()} is marked gone home."))
            {
                FocusAfter(before, () => _backButton, () => { Adults.FocusContent(); return null; });
            }
        }
    }

    private void OnBack()
    {
        if (Adults.Selected is { IsDisabled: true } adult)
        {
            var before = FocusedHere();
            if (ReportOn(Adults, _svc.EnableAdult(adult.Id), "Couldn't mark them back", $"{adult.Describe()} is marked back."))
            {
                FocusAfter(before, () => _goneHomeButton, () => { Adults.FocusContent(); return null; });
            }
        }
    }

    /// <summary>A step taken on a table page: a refusal is said there, not in the details pane. True if it worked.</summary>
    private bool ReportOn(TablePage page, ActionResult result, string failure, string? success = null)
    {
        if (!result.Ok)
        {
            page.Notice.Show(Severity.Error, failure, Plain(result.Message));
        }

        RefreshAll();
        if (result.Ok && success != null)
        {
            Say(success);
        }

        return result.Ok;
    }

    // ------------------------------------------------------------------
    // Rooms
    // ------------------------------------------------------------------

    private static readonly Option[] BoardTypeChoices =
    [
        new(BoardTypes.Final, Display.BoardType(BoardTypes.Final)),
        new(BoardTypes.Project, Display.BoardType(BoardTypes.Project)),
    ];

    private RoomCard? SelectedRoom => RoomList.SelectedItem as RoomCard;

    /// <summary>A room command that worked, said; a refusal in the window's message bar. True if it worked.</summary>
    private bool ReportRoom(ActionResult result, string failure, string success)
    {
        if (!result.Ok)
        {
            Notice.Show(Severity.Error, failure, Plain(result.Message));
        }

        RefreshAll();
        if (result.Ok)
        {
            Say(success);
        }

        return result.Ok;
    }

    private void OnAddRoom(object sender, RoutedEventArgs e)
    {
        var dialog = new AppDialog(this, "Add a room", "Add room");
        var roomBox = dialog.AddText("_Room number");
        var typeBox = dialog.AddChoice("_Used for", BoardTypeChoices, BoardTypes.Final);
        dialog.AddMessage("For project reviews sharing one room, add one entry per table, like 200A and 200B.");
        dialog.Check(roomBox, () =>
        {
            var room = roomBox.Text.Trim();
            if (room.Length == 0)
            {
                return "Enter a room number.";
            }

            return _rooms.Any(r => r.Room == room) ? $"There's already a room {room}." : null;
        });
        if (dialog.ShowDialog())
        {
            var room = roomBox.Text.Trim();
            ReportRoom(_svc.AddRoom(room, (string)typeBox.SelectedValue), "Couldn't add the room", $"Room {room} added.");
        }
    }

    /// <summary>No confirmation: a free room is added back just as easily.</summary>
    private void OnRemoveRoom(object sender, RoutedEventArgs e)
    {
        if (SelectedRoom is not { } room)
        {
            Notice.Show(Severity.Informational, "Select a room first", "Select the room to remove, then select Remove room.");
            return;
        }

        if (!room.IsFree)
        {
            Notice.Show(Severity.Warning, $"Room {room.Room} is in use", "Complete, reset or move its board first.");
            return;
        }

        ReportRoom(_svc.RemoveRoom(room.Id), "Couldn't remove the room", $"Room {room.Room} removed.");
    }

    /// <summary>Rename the selected room (F2 on its card, or Rename on its shortcut menu).</summary>
    private void OnRenameRoom(object sender, RoutedEventArgs e)
    {
        if (SelectedRoom is not { } room)
        {
            Notice.Show(Severity.Informational, "Select a room first", "Select the room to rename, then select Rename.");
            return;
        }

        RenameRoom(room);
    }

    private void RenameRoom(RoomCard room)
    {
        var dialog = new AppDialog(this, $"Rename room {room.Room}", "Rename");
        var roomBox = dialog.AddText("_Room number", room.Room);
        dialog.Check(roomBox, () =>
        {
            var value = roomBox.Text.Trim();
            if (value.Length == 0)
            {
                return "Enter a room number.";
            }

            return value != room.Room && _rooms.Any(r => r.Room == value) ? $"There's already a room {value}." : null;
        });
        if (dialog.ShowDialog())
        {
            var old = room.Room;
            var name = roomBox.Text.Trim();
            ReportRoom(_svc.RenameRoom(room.Id, name), "Couldn't rename the room", $"Room {old} is now room {name}.");
        }
    }

    /// <summary>Switch the selected room between final boards and project reviews. No confirmation: switching back is as easy.</summary>
    private void OnSwitchRoomType(object sender, RoutedEventArgs e)
    {
        if (SelectedRoom is not { } room)
        {
            Notice.Show(Severity.Informational, "Select a room first", "Select the room to switch, then select Switch type.");
            return;
        }

        var newType = room.BoardType == BoardTypes.Project ? BoardTypes.Final : BoardTypes.Project;
        ReportRoom(_svc.SetRoomType(room.Id, newType), "Couldn't switch the room",
            $"Room {room.Room} is now for {Display.BoardType(newType).ToLowerInvariant()}s.");
    }

    /// <summary>Move the selected room's board to another room, or swap two boards.</summary>
    private void OnChangeRoom(object sender, RoutedEventArgs e)
    {
        if (SelectedRoom is not { } first)
        {
            Notice.Show(Severity.Informational, "Select a room first", "Select the room whose board you want to move, then select Move.");
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
        var target = dialog.AddChoice("_Move to", others.Select(r => new Option(r.Id, Label(r))), null);
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

        var success = (first.IsFree, second.IsFree) switch
        {
            (false, false) => $"The boards in rooms {first.Room} and {second.Room} swapped rooms.",
            (false, true) => $"{first.Scout}'s board moved to room {second.Room}.",
            (true, false) => $"{second.Scout}'s board moved to room {first.Room}.",
            _ => $"Rooms {first.Room} and {second.Room} are both free.",
        };
        ReportRoom(_svc.ChangeRoom(first.Id, second.Id), "Couldn't move the board", success);
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
            Notice.Show(Severity.Success, "Report saved", Path.GetFileName(path), "Show in folder", () =>
            {
                if (!OpenUrl("explorer.exe", $"/select,\"{path}\""))
                {
                    Notice.Show(Severity.Warning, "Couldn't open the folder", $"The report is saved in {Path.GetDirectoryName(path)}.");
                }
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notice.Show(Severity.Error, "Couldn't save the report", ErrorText.For(ex, dialog.FileName));
        }
    }

    /// <summary>
    /// Reverses the most recent board step, room change, Gone home or Back,
    /// or Link or Unlink (Ctrl+Z). On the Event page a step undone is said
    /// aloud, since the screen shows it. On a table page, where it may not
    /// (table edits stay off the stack, so Ctrl+Z there undoes a board step),
    /// the page's message bar says what was undone, or why it couldn't be
    /// (C12). Reset and Postpone keep their own confirmation dialog rather
    /// than relying on this.
    /// </summary>
    private void OnUndo(object sender, RoutedEventArgs e)
    {
        if (!_svc.CanUndo)
        {
            return;
        }

        var step = _svc.UndoDescription;
        var result = _svc.Undo();
        var page = _tables.Values.FirstOrDefault(t => t.IsVisible);
        if (!result.Ok)
        {
            var bar = page?.Notice ?? (DetailsHead.IsVisible ? DetailNotice : Notice);
            bar.Show(Severity.Error, "Couldn't undo", Plain(result.Message));
        }
        else if (page != null)
        {
            page.Notice.Show(Severity.Informational, "Undone", $"Undid {step}.");
        }

        RefreshAll();
        if (result.Ok && page == null)
        {
            Say($"Undid {step}.");
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

    /// <summary>F1 or Help > Eagle Boards help: one Help window, brought forward if it's open, as Settings is (K12).</summary>
    private void OnHelp(object sender, RoutedEventArgs e)
    {
        if (_help is { IsLoaded: true })
        {
            _help.Activate();
            return;
        }

        _help = new HelpWindow { Owner = this };
        _help.Show();
    }

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

    /// <summary>
    /// The footer's import result. A failed import is a link that shows why
    /// in the window's message bar, so the reason can be reached from the
    /// keyboard after that message is closed, not only in a tooltip (C13).
    /// </summary>
    private void ShowImportText()
    {
        ImportText.Inlines.Clear();
        if (_session.ImportError is not { } error)
        {
            ImportText.Text = _session.ImportSummary ?? "";
            return;
        }

        var link = new Hyperlink(new Run("SignUpGenius import failed")) { ToolTip = error };
        link.SetResourceReference(TextElement.ForegroundProperty, "SystemFillColorCautionBrush");
        AutomationProperties.SetHelpText(link, "Shows why in the message bar");
        link.Click += (_, _) => ShowImportError();
        ImportText.Inlines.Add(link);
    }

    private void ShowImportError() =>
        Notice.Show(Severity.Warning, "SignUpGenius import failed",
            "Pre-registrations aren't loaded, so everyone signs in as a walk-in. " + _session.ImportError);

    private void OnUrlClicked(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        if (!OpenUrl(e.Uri.ToString()))
        {
            Notice.Show(Severity.Warning, "Couldn't open the check-in address",
                "Windows couldn't open a browser for it. To copy it instead, open the address's shortcut menu (Shift+F10) and select Copy address.");
        }

        e.Handled = true;
    }

    /// <summary>Copy address: "Copied" beside the address for a moment, and read out (a live region).</summary>
    private void OnCopyUrl(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string url })
        {
            Clipboard.SetText(url);
            CopiedText.Text = "Copied";
            Announcer.LiveRegionChanged(CopiedText);
            _copied.Stop();
            _copied.Start();
        }
    }

    /// <summary>Open a link or a program the way Windows would. False if it couldn't, for the caller to say so (K10).</summary>
    internal static bool OpenUrl(string target, string? arguments = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, Arguments = arguments ?? "" });
            return true;
        }
        catch (Win32Exception)
        {
            return false;
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
        _help?.Close();
    }

    // The window's size changes, or its scroll bars come and go: fit the
    // Event page to what's shown, never below its minimum (F6).
    private void OnEventViewChanged(object sender, SizeChangedEventArgs e) => FitEventPage();

    private void OnEventViewScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0)
        {
            FitEventPage();
        }
    }

    /// <summary>
    /// The Event page exactly as big as the window shows, down to its
    /// minimum, below which it scrolls. A ScrollViewer alone would measure
    /// it unbounded: the rooms would line up in one row and the lists would
    /// lose their own scrolling.
    /// </summary>
    private void FitEventPage()
    {
        var margin = EventPage.Margin;
        EventPage.Width = Math.Max(EventPage.MinWidth, EventView.ViewportWidth - margin.Left - margin.Right);
        EventPage.Height = Math.Max(EventPage.MinHeight, EventView.ViewportHeight - margin.Top - margin.Bottom);
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
