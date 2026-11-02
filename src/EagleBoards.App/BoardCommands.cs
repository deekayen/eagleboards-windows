using System.Windows.Input;

namespace EagleBoards.App;

/// <summary>
/// The Board menu's commands (SPEC.md P-1, amended 2026-10-24), each with its
/// shortcut, defined once: a menu item shows the shortcut and is enabled from
/// the command, and the main window's CommandBindings say when each applies
/// and what it does. Ctrl+Enter is SPEC.md P-3's; F2 renames, as it does in
/// File Explorer.
/// </summary>
public static class BoardCommands
{
    /// <summary>The open youth's next step: Seat board, Start review or Complete. "Enter", as the keyboard says, where WPF writes "Return".</summary>
    public static readonly RoutedUICommand NextStep = Command("Next step", nameof(NextStep), new KeyGesture(Key.Enter, ModifierKeys.Control, "Ctrl+Enter"));

    public static readonly RoutedUICommand Postpone = Command("Postpone", nameof(Postpone));

    public static readonly RoutedUICommand Reset = Command("Reset", nameof(Reset));

    public static readonly RoutedUICommand LinkAdult = Command("Link an adult", nameof(LinkAdult));

    public static readonly RoutedUICommand Unlink = Command("Unlink", nameof(Unlink));

    public static readonly RoutedUICommand AddRoom = Command("Add room", nameof(AddRoom));

    public static readonly RoutedUICommand MoveRoom = Command("Move or swap board", nameof(MoveRoom));

    public static readonly RoutedUICommand RenameRoom = Command("Rename room", nameof(RenameRoom), new KeyGesture(Key.F2));

    public static readonly RoutedUICommand SwitchRoomType = Command("Switch room type", nameof(SwitchRoomType));

    public static readonly RoutedUICommand RemoveRoom = Command("Remove room", nameof(RemoveRoom));

    private static RoutedUICommand Command(string text, string name, params InputGesture[] gestures) =>
        new(text, name, typeof(BoardCommands), new InputGestureCollection(gestures));
}
