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

namespace EagleBoards.App;

public partial class MainWindow
{
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
}
