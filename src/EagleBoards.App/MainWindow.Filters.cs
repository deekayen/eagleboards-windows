using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using EagleBoards.Core;

namespace EagleBoards.App;

public partial class MainWindow
{
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
}
