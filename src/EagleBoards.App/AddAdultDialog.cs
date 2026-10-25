using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using EagleBoards.Core;
using EagleBoards.Core.Records;

namespace EagleBoards.App;

/// <summary>
/// Add adult, on the Adults page: signs in an adult who would rather not use
/// the tablet, as Java's Add adult… and the Mac's Adult › Add Adult… do. It
/// goes through <see cref="BoardService.RegisterAdult"/>, the tablet's own
/// sign-in, so someone in the adult history is recognized and the history
/// gets today's date. Picking them from a search of the history fills the
/// form in and carries their ID, as the tablet's email lookup does, so a
/// name corrected here still signs in the same person.
/// </summary>
internal static class AddAdultDialog
{
    private const int MaxMatches = 5;

    // "No thanks", as the tablet's form says it; the file keeps Unavailable.
    private static readonly Option[] Roles =
        [new(BoardRoles.Chair, "Chair"), new(BoardRoles.Member, "Member"), new(BoardRoles.Unavailable, "No thanks")];

    /// <summary>A match in the history: shown, and read out, as its <see cref="Text"/>.</summary>
    private sealed record Match(Dictionary<string, string> Row, string Text)
    {
        public override string ToString() => Text;
    }

    /// <summary>Ask, and sign them in. True once someone was.</summary>
    public static bool Show(Window owner, BoardService svc)
    {
        var (dialog, _, fields) = Build(owner, svc);
        if (!dialog.ShowDialog())
        {
            return false;
        }

        svc.RegisterAdult(fields());
        return true;
    }

    /// <summary>
    /// The dialog, not yet shown; its search box; and what it would sign in.
    /// Apart from <see cref="Show"/> so the snapshot tool can draw it.
    /// </summary>
    internal static (AppDialog Dialog, TextBox Search, Func<Dictionary<string, string>> Fields) Build(Window? owner, BoardService svc)
    {
        var history = svc.Snapshot(DataTable.AdultHistory);
        var dialog = new AppDialog(owner, "Add an adult", "Add adult");
        dialog.AddMessage("Sign in an adult who would rather not use the tablet. They're signed in for today, as if they had used it.");

        // Access keys unique within the dialog (C10): S, F, L, T, U, E, H, B,
        // J and W; the buttons answer Enter and Esc.
        var search = dialog.AddText("_Signed in before?");
        Placeholder.SetText(search, "Search the adult history by name, email or unit");
        AutomationProperties.SetHelpText(search,
            "Search the adult history by name, email or unit. Use the Down arrow key to move to the matches, and then select Enter to fill in the form.");

        // How many matched, read out as it changes (K6): the list appears
        // without a sound otherwise.
        var found = new TextBlock { Margin = new Thickness(0, -12, 0, 0), Visibility = Visibility.Collapsed };
        found.SetResourceReference(FrameworkElement.StyleProperty, "Hint");
        AutomationProperties.SetLiveSetting(found, AutomationLiveSetting.Polite);
        dialog.AddContent(found);
        var matches = new ListBox { MaxHeight = 180, Margin = new Thickness(0, -8, 0, 0), Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(matches, "Adults in the history");
        dialog.AddContent(matches);
        var known = new InfoBar { IsClosable = false };
        dialog.AddContent(known);

        // Only the names are needed: said on screen, and to a screen reader
        // by the boxes themselves (K4).
        var required = new TextBlock { Text = "First and last name are required. Everything else is optional." };
        required.SetResourceReference(FrameworkElement.StyleProperty, "Hint");
        dialog.AddContent(required);
        var first = new TextBox();
        var last = new TextBox();
        AutomationProperties.SetIsRequiredForForm(first, true);
        AutomationProperties.SetIsRequiredForForm(last, true);
        dialog.AddRow(("_First name", first), ("_Last name", last));
        var unitType = ChoiceBox(UnitTypes.All.Select(t => new Option(t, t)), UnitTypes.All[0]);
        var unit = new TextBox();
        dialog.AddRow(("Unit _type", unitType), ("_Unit", unit));
        var email = new TextBox();
        var phone = new TextBox();
        dialog.AddRow(("_Email", email), ("P_hone", phone));
        var finalBoard = ChoiceBox(Roles, BoardRoles.Member);
        var projectReview = ChoiceBox(Roles, BoardRoles.Member);
        dialog.AddRow(("Final _board", finalBoard), ("Pro_ject review", projectReview));
        var woodBadge = new CheckBox { Content = "Counting today toward a _Wood Badge ticket item" };
        dialog.AddContent(woodBadge);

        var knownId = "";
        void Fill(Dictionary<string, string> row)
        {
            string V(string field) => Display.Text(row.GetValueOrDefault(field, ""));
            knownId = row["ID"];
            first.Text = V("First");
            last.Text = V("Last");
            unit.Text = V("Unit");
            email.Text = V("Email");
            phone.Text = V("Phone");
            if (UnitTypes.All.Contains(V("UnitType")))
            {
                unitType.SelectedValue = V("UnitType");
            }

            if (BoardRoles.All.Contains(V("FinalBoard")))
            {
                finalBoard.SelectedValue = V("FinalBoard");
            }

            if (BoardRoles.All.Contains(V("ProjectReview")))
            {
                projectReview.SelectedValue = V("ProjectReview");
            }

            known.Show(Severity.Informational, "", $"From the adult history: **{V("First")} {V("Last")}**, {V("UnitType")} {V("Unit")}");
            search.Text = "";
            first.Focus();
        }

        search.TextChanged += (_, _) =>
        {
            var words = search.Text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var all = history
                .Select(r => (Row: r, Name: $"{Display.Text(r["First"])} {Display.Text(r["Last"])}", Unit: $"{r["UnitType"]} {Display.Text(r["Unit"])}", Email: Display.Text(r["Email"])))
                .Where(m => words.Length > 0 && words.All(w => $"{m.Name} {m.Email} {m.Unit} {m.Row["UnitType"]}{m.Row["Unit"]}".Contains(w, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            matches.ItemsSource = words.Length == 0 ? null : all
                .OrderBy(m => Display.Text(m.Row["Last"]), StringComparer.CurrentCultureIgnoreCase).ThenBy(m => Display.Text(m.Row["First"]), StringComparer.CurrentCultureIgnoreCase)
                .Take(MaxMatches)
                // Commas, not " · ": read aloud as pauses (D7).
                .Select(m => new Match(m.Row, m.Email.Length > 0 ? $"{m.Name}, {m.Unit}, {m.Email}" : $"{m.Name}, {m.Unit}"))
                .ToList();
            matches.Visibility = matches.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            var note = words.Length == 0 ? ""
                : all.Count == 0 ? "No one in the adult history matches."
                : all.Count == 1 ? "1 adult found. Select it, or select Enter, to fill in the form."
                : all.Count > MaxMatches ? $"{Display.Count(all.Count, "adult", "adults")} found, the first {MaxMatches} shown. Type more of the name to narrow them down."
                : $"{Display.Count(all.Count, "adult", "adults")} found. Select one to fill in the form, or use the Down arrow key and Enter.";
            if (note != found.Text)
            {
                found.Text = note;
                found.Visibility = note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                if (note.Length > 0)
                {
                    Announcer.LiveRegionChanged(found);
                }
            }
        };

        // Arrow down into the matches; a click or Enter there fills the form
        // in. Enter in the box takes the one match, or goes into the list,
        // rather than pressing Add adult with the form still empty (C14).
        search.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && matches.Items.Count == 1)
            {
                Fill(((Match)matches.Items[0]).Row);
                e.Handled = true;
            }
            else if (e.Key is (Key.Down or Key.Enter) && matches.Items.Count > 0)
            {
                matches.SelectedIndex = 0;
                (matches.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
                e.Handled = true;
            }
        };
        matches.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && matches.SelectedItem is Match m)
            {
                Fill(m.Row);
                e.Handled = true;
            }
        };
        matches.PreviewMouseLeftButtonUp += (_, _) =>
        {
            if (matches.SelectedItem is Match m)
            {
                Fill(m.Row);
            }
        };

        // The empty name takes the focus, the message as its help text (K4).
        dialog.Check(first, () => first.Text.Trim().Length == 0 ? "Enter a first name." : null);
        dialog.Check(last, () => last.Text.Trim().Length == 0 ? "Enter a last name." : null);
        return (dialog, search, () => new Dictionary<string, string>
        {
            ["ID"] = knownId,
            ["First"] = first.Text.Trim(),
            ["Last"] = last.Text.Trim(),
            ["UnitType"] = (string)unitType.SelectedValue,
            ["Unit"] = unit.Text.Trim(),
            ["Email"] = email.Text.Trim(),
            ["Phone"] = phone.Text.Trim(),
            ["FinalBoard"] = (string)finalBoard.SelectedValue,
            ["ProjectReview"] = (string)projectReview.SelectedValue,
            ["WoodBadge"] = woodBadge.IsChecked == true ? "Y" : "",
        });
    }

    /// <summary>
    /// Sign in someone from the adult history for today, as if at the tablet:
    /// the Adult history CSV page's Sign in for today.
    /// </summary>
    public static void SignInFromHistory(BoardService svc, RecordRow row)
    {
        var fields = new Dictionary<string, string> { ["ID"] = row.Id };
        foreach (var field in new[] { "First", "Last", "UnitType", "Unit", "Email", "Phone", "FinalBoard", "ProjectReview" })
        {
            fields[field] = row[field];
        }

        svc.RegisterAdult(fields);
    }

    private static ComboBox ChoiceBox(IEnumerable<Option> items, string selected) => new()
    {
        ItemsSource = items.ToList(),
        DisplayMemberPath = "Value",
        SelectedValuePath = "Key",
        SelectedValue = selected,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };
}
