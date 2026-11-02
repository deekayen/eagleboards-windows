using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using EagleBoards.Core;
using Microsoft.Win32;

namespace EagleBoards.App;

public partial class MainWindow
{
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
