using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using EagleBoards.Core;
using EagleBoards.Core.Records;

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
///
/// The class is one type in many files, one per part of what the window does
/// (<c>MainWindow.Rooms.cs</c>, <c>MainWindow.Picks.cs</c> and so on); this
/// file holds its fields, its constructor and what they set up. See
/// docs/decisions/0001-split-the-main-window-into-partial-files.md.
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
    private IReadOnlySet<string>? _foundRooms;

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

    /// <summary>The operator chose <see cref="_builderRoomId"/> themselves, so it stays even if it isn't the board's type.</summary>
    private bool _roomChosenByOperator;

    /// <summary>The board last proposed, to tell it from the operator's own (SPEC.md D-12).</summary>
    private ProposedBoard? _proposal;

    /// <summary>
    /// Shows a question (Remove room's) and says whether its primary button
    /// was pressed. A test replaces it, to read the question without showing
    /// it: a dialog would open on the desktop of whoever runs the tests.
    /// </summary>
    internal Func<AppDialog, bool> Ask { get; set; } = dialog => dialog.ShowDialog();

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
        // Rooms adds and removes a room as the Event page does.
        foreach (var spec in TableSpecs.All)
        {
            var table = new TablePage(_svc, spec, key => FindResource(key),
                spec.IsRooms ? () => OnAddRoom(this, new RoutedEventArgs()) : null, spec.IsRooms ? RemoveRoom : null);
            _tables[spec.Name] = table;
            PageHost.Children.Add(table);
        }

        // The page's commands, with access keys that clash neither with the
        // menu bar's F, E, V, B and H nor with the footer's Q. The grid's
        // shortcut menu repeats them (TablePage).
        var results = _tables[TableSpecs.Results.Name];
        _openButton = results.AddCommand("", "_Open on Event page", "Show the selected board on the Event page", OpenBoard);
        results.AddCommand("", "Save _report...", "Save the event's results as a CSV file for Excel", () => OnReport(this, new RoutedEventArgs()));
        results.SelectionChanged += UpdatePageButtons;

        // Verbs, as the page's other commands are: a button called just
        // "Back" is heard as going back a page. Mark back's C, since B is
        // the Board menu's.
        var adults = _tables[TableSpecs.Adults.Name];
        adults.AddCommand("", "_Add adult...", "Sign in an adult who would rather not use the tablet", OnAddAdult);
        _goneHomeButton = adults.AddCommand("", "Mark _gone home", "Mark the selected adult as gone home", OnGoneHome);
        _backButton = adults.AddCommand("", "Mark ba_ck", "Mark the selected adult as back and available", OnBack);
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
}
