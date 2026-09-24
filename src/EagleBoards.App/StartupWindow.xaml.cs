using System.Globalization;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Media;
using EagleBoards.Core.Records;
using EagleBoards.Web;
using Microsoft.Win32;

namespace EagleBoards.App;

/// <summary>Where's the data, which network, then start the night.</summary>
public partial class StartupWindow : Window
{
    private sealed record NetworkChoice(string Label, IPAddress? Address);

    private readonly AppSettings _settings;

    public StartupWindow()
        : this(AppSettings.Load())
    {
    }

    /// <summary>With given settings rather than the saved ones (the snapshot harness uses a sandbox).</summary>
    public StartupWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        Title = $"Eagle Board Scheduler {AppVersion.Text} - Start an event night";
        DataFolderBox.Text = _settings.DataFolder.Length > 0 ? _settings.DataFolder : SuggestDataFolder();
        HistoryBox.Text = _settings.AdultHistoryFile;
        PortBox.Text = _settings.Port.ToString(CultureInfo.InvariantCulture);
        EventDate.SelectedDate = DateTime.Today;
        LoadNetworks(_settings.BindAddress);
        Refresh();
    }

    /// <summary>Set when Start succeeds.</summary>
    public EventSession? Session { get; private set; }

    /// <summary>
    /// Beside the exe if it looks like a data folder (the Java jar was run
    /// from its data folder), else C:\eagleboards if present, else Documents.
    /// </summary>
    private static string SuggestDataFolder()
    {
        var exeDir = AppContext.BaseDirectory.TrimEnd('\\');
        if (File.Exists(Path.Combine(exeDir, "config.properties")) || File.Exists(Path.Combine(exeDir, "Master_AdultHistory.csv")))
        {
            return exeDir;
        }

        if (Directory.Exists(@"C:\eagleboards"))
        {
            return @"C:\eagleboards";
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Review Boards");
    }

    private void LoadNetworks(string preferred)
    {
        var choices = LanAddresses.Find()
            .Select((a, i) => new NetworkChoice(
                $"{a.Address}  -  {a.InterfaceName}{(i == 0 && !a.LooksVirtual ? "  (recommended)" : "")}{(a.LooksVirtual ? "  (virtual)" : "")}",
                a.Address))
            .ToList();
        choices.Add(new NetworkChoice("All networks on this computer", null));
        NetworkBox.ItemsSource = choices;
        NetworkBox.SelectedItem = choices.FirstOrDefault(c => c.Address?.ToString() == preferred) ?? choices[0];
    }

    private string DataFolder => DataFolderBox.Text.Trim();

    private string EventFolder => Path.Combine(DataFolder,
        (EventDate.SelectedDate ?? DateTime.Today).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    private string HistoryPath
    {
        get
        {
            var h = HistoryBox.Text.Trim();
            return Path.IsPathRooted(h) ? h : Path.Combine(DataFolder, h);
        }
    }

    private void Refresh()
    {
        if (!IsInitialized || DataFolder.Length == 0)
        {
            return;
        }

        var folder = EventFolder;
        EventFolderText.Text = Directory.Exists(folder)
            ? $"continues {Path.GetFileName(folder)}\\ (already has data)"
            : $"creates {Path.GetFileName(folder)}\\";

        try
        {
            if (File.Exists(HistoryPath))
            {
                var adults = File.ReadLines(HistoryPath).Skip(1).Count(l => l.Length > 0);
                HistoryStatus.Text = $"Found: {adults} adult{(adults == 1 ? "" : "s")} on record.";
                HistoryStatus.Foreground = Brushes.DimGray;
            }
            else
            {
                HistoryStatus.Text = "Not found. A new, empty adult history will be started there.";
                HistoryStatus.Foreground = Brushes.DarkOrange;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            HistoryStatus.Text = "Can't read it: " + ex.Message;
            HistoryStatus.Foreground = Brushes.Firebrick;
        }

        var (key, source) = AppSettings.FindSignUpGeniusKey(DataFolder);
        // Only reset the tick when a key appears or disappears, so typing in
        // the path boxes doesn't undo the operator's own choice.
        if (SugCheck.IsEnabled != (key != null) || SugCheck.IsChecked == null)
        {
            SugCheck.IsEnabled = key != null;
            SugCheck.IsChecked = key != null && _settings.ImportSignUpGenius;
        }
        SugStatus.Text = key != null
            ? $"Using the API key from {source}."
            : "No API key found. Put a line SUG_KEY=your-key in a file named .env in the data folder, or set the SUG_KEY environment variable.";
    }

    private void OnPathsChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => Refresh();

    private void OnDateChanged(object? sender, System.Windows.Controls.SelectionChangedEventArgs e) => Refresh();

    private void OnRescan(object sender, RoutedEventArgs e) =>
        LoadNetworks((NetworkBox.SelectedItem as NetworkChoice)?.Address?.ToString() ?? "");

    private void OnBrowseDataFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the data folder", InitialDirectory = Directory.Exists(DataFolder) ? DataFolder : null };
        if (dialog.ShowDialog(this) == true)
        {
            DataFolderBox.Text = dialog.FolderName;
        }
    }

    private void OnBrowseHistory(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose the adult history file",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            InitialDirectory = Directory.Exists(DataFolder) ? DataFolder : null,
        };
        if (dialog.ShowDialog(this) == true)
        {
            var chosen = dialog.FileName;
            HistoryBox.Text = string.Equals(Path.GetDirectoryName(chosen), DataFolder, StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileName(chosen)
                : chosen;
        }
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (DataFolder.Length == 0)
        {
            ShowError("Choose a data folder.");
            return;
        }

        if (!int.TryParse(PortBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        {
            ShowError("The port must be a number from 1 to 65535.");
            return;
        }

        LaunchPlan plan;
        try
        {
            Directory.CreateDirectory(DataFolder);
            if (!File.Exists(HistoryPath))
            {
                var answer = MessageBox.Show(this,
                    $"There is no adult history at\n{HistoryPath}\n\nStart a new, empty one? (Choose No to pick a different file.)",
                    Title, MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes)
                {
                    return;
                }

                File.WriteAllText(HistoryPath, string.Join(',', AdultRecord.AllColumns) + "\n");
            }

            var config = Path.Combine(DataFolder, "config.properties");
            if (!File.Exists(config))
            {
                File.WriteAllText(config, DefaultConfig.Text);
            }

            var (key, _) = AppSettings.FindSignUpGeniusKey(DataFolder);
            plan = new LaunchPlan
            {
                DataDirectory = EventFolder,
                AdultHistoryPath = HistoryPath,
                ConfigPath = config,
                SignUpGeniusKey = SugCheck.IsChecked == true ? key : null,
                Port = port,
                BindAddress = (NetworkBox.SelectedItem as NetworkChoice)?.Address,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowError(ex.Message);
            return;
        }

        Form.IsEnabled = false;
        StartButton.IsEnabled = false;
        ProgressText.Foreground = Brushes.DimGray;
        try
        {
            Session = await EventSession.StartAsync(plan, new Progress<string>(m => ProgressText.Text = m));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            var message = ex.Message;
            if (ex is IOException && message.Contains("bind", StringComparison.OrdinalIgnoreCase))
            {
                message = $"Port {port} is already in use -- is the scheduler (or the Java version) already running? Close it or pick another port.";
            }

            ShowError(message);
            Form.IsEnabled = true;
            StartButton.IsEnabled = true;
            return;
        }

        _settings.DataFolder = DataFolder;
        _settings.AdultHistoryFile = HistoryBox.Text.Trim();
        _settings.Port = port;
        _settings.BindAddress = (NetworkBox.SelectedItem as NetworkChoice)?.Address?.ToString() ?? "";
        _settings.ImportSignUpGenius = SugCheck.IsChecked == true || !SugCheck.IsEnabled && _settings.ImportSignUpGenius;
        _settings.Save();
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ProgressText.Text = message;
        ProgressText.Foreground = Brushes.Firebrick;
    }
}
