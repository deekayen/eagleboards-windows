using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using EagleBoards.Core;
using EagleBoards.Core.Records;

namespace EagleBoards.App;

public partial class MainWindow
{
    // ------------------------------------------------------------------
    // Rooms
    // ------------------------------------------------------------------

    private static readonly Option[] BoardTypeChoices =
    [
        new(BoardTypes.Final, Display.BoardType(BoardTypes.Final)),
        new(BoardTypes.Project, Display.BoardType(BoardTypes.Project)),
    ];

    private RoomCard? SelectedRoom => RoomList.SelectedItem as RoomCard;

    /// <summary>A room command that worked, said; a refusal in the window's message bar, or the page's. True if it worked.</summary>
    private bool ReportRoom(ActionResult result, string failure, string success, InfoBar? bar = null)
    {
        if (!result.Ok)
        {
            (bar ?? Notice).Show(Severity.Error, failure, Plain(result.Message));
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

    private void OnRemoveRoom(object sender, RoutedEventArgs e)
    {
        if (SelectedRoom is not { } room)
        {
            Notice.Show(Severity.Informational, "Select a room first", "Select the room to remove, then select Remove room.");
            return;
        }

        RemoveRoom(room.Id, Notice);
    }

    /// <summary>
    /// Remove room, from the Event page or the Rooms page, with what's said
    /// in <paramref name="bar"/>. A room with a board in it is refused first.
    /// A free one is removed only once the operator says so, the room named
    /// (SPEC.md O-2, amended 2026-10-24), though adding it back is as easy.
    /// </summary>
    private void RemoveRoom(string roomId, InfoBar bar)
    {
        if (_rooms.FirstOrDefault(r => r.Id == roomId) is not { } room)
        {
            RefreshAll();
            return;
        }

        if (!room.IsFree)
        {
            bar.Show(Severity.Warning, $"Room {room.Room} is in use", "Complete, reset or move its board first.");
            return;
        }

        if (Ask(RemoveRoomQuestion(this, room.Room)))
        {
            ReportRoom(_svc.RemoveRoom(room.Id), "Couldn't remove the room", $"Room {room.Room} removed.", bar);
        }
    }

    /// <summary>
    /// Remove room's question, in the Java version's words, which all three
    /// versions now ask (SPEC.md O-2, amended 2026-10-24). Not yet shown, so
    /// a test and the snapshot tool can read it.
    /// </summary>
    internal static AppDialog RemoveRoomQuestion(Window? owner, string room)
    {
        var dialog = new AppDialog(owner, $"Remove room {room}?", "Remove room");
        dialog.AddMessage("It comes off the list for the rest of this event. You can add it again.");
        return dialog;
    }

    /// <summary>Rename a room: the card with the focus, else the one selected (F2, or Rename on the Board menu or a room's shortcut menu).</summary>
    private void OnRenameRoom(object sender, RoutedEventArgs e)
    {
        if (FocusedRoom() is not { } room)
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
}
