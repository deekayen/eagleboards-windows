using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EagleBoards.Core;
using EagleBoards.Core.Records;

namespace EagleBoards.App;

public partial class MainWindow
{
    // ------------------------------------------------------------------
    // The Board menu
    // ------------------------------------------------------------------

    /// <summary>
    /// The Board menu as the details pane stands (SPEC.md P-1, amended
    /// 2026-10-24): its first item named for the step shown, "Seat anyway"
    /// included, or plain Seat board when the pane shows none, and every
    /// item enabled again as the buttons are. The buttons change on a
    /// sign-in at the door as well as on a click, which WPF wouldn't notice
    /// by itself.
    /// </summary>
    private void SyncBoardMenu()
    {
        var (header, glyph) = StartButton.Visibility == Visibility.Visible ? ("_Start review", "")
            : CompleteButton.Visibility == Visibility.Visible ? ("_Complete", "")
            : SeatButton.Visibility == Visibility.Visible ? (SeatText.Text, "")
            : ("_Seat board", "");
        BoardStep.Header = header;
        BoardStepIcon.Text = glyph;
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>
    /// The Board menu acts on the Event page, where its buttons are; on a
    /// table page it's unavailable. Nor while the window is still being
    /// built: a button bound to a command asks it as its XAML is read.
    /// </summary>
    private bool OnEventPage => IsInitialized && _page == "Event";

    /// <summary>A button of the details pane shown and enabled on the Event page, as its Board menu item is.</summary>
    private bool Usable(UIElement button) => OnEventPage && button.Visibility == Visibility.Visible && button.IsEnabled;

    /// <summary>The open youth's next step, Seat board, Start review, or Complete, if the pane has one to take.</summary>
    private Button? NextStepButton() => new[] { SeatButton, StartButton, CompleteButton }.FirstOrDefault(Usable);

    private void CanNextStep(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = NextStepButton() != null;

    /// <summary>Board > the step, or Ctrl+Enter: the step's button, as if it were selected.</summary>
    private void OnNextStep(object sender, ExecutedRoutedEventArgs e)
    {
        if (NextStepButton() is { } step)
        {
            step.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, step));
        }
    }

    private void CanPostpone(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = Usable(PostponeButton);

    private void CanReset(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = Usable(ResetButton);

    private void CanLinkAdult(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = Usable(LocateSection) && LinkButton.IsEnabled;

    private void CanUnlink(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = Usable(LocateSection) && Usable(UnlinkButton);

    private void CanAddRoom(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = OnEventPage;

    /// <summary>Move or swap, Switch room type and Remove room: once a room is selected.</summary>
    private void CanUseRoom(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = OnEventPage && SelectedRoom != null;

    /// <summary>Rename room, and F2: the card with the focus, else the one selected.</summary>
    private void CanRenameRoom(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = OnEventPage && FocusedRoom() != null;

    private void OnRoomSelected(object sender, SelectionChangedEventArgs e)
    {
        // The room commands follow the selection, however it changed.
        CommandManager.InvalidateRequerySuggested();
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

            // Clicking a free room is the operator's own choice, so it stays
            // even when it is set up for the other board type (SPEC.md D-24).
            _roomChosenByOperator = true;
            ShowDetails();
        }
    }

    /// <summary>The room card with the keyboard focus, else the one selected: what F2 renames.</summary>
    private RoomCard? FocusedRoom() => Keyboard.FocusedElement is ListBoxItem { Content: RoomCard card } ? card : SelectedRoom;

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
    /// progress and survive clicking around, kept for that youth (SPEC.md
    /// D-12, amended 2026-11-08); only Start over and seating clear them.
    /// A proposal they left alone is made afresh for this youth.
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
        var free = _rooms.Where(r => r.IsFree).Select(r => r.Info).ToList();

        // The picks on screen belong to one youth (SPEC.md D-12, amended
        // 2026-11-08): another youth gets their own board, and the first
        // youth's is kept for when the operator comes back to them.
        if (_boardOwnerId is { } owner && owner != scout.Id)
        {
            SetAsideBoardOf(owner);
        }

        _boardOwnerId = scout.Id;
        if (_drafts.Take(scout.Id) is { } draft)
        {
            RestoreDraft(scout, free, draft);
            return;
        }

        if (_adults.Any(IsPicked) && !ProposalUntouched())
        {
            // Keep their picks; only make sure there's a sensible room.
            var freeRoomOfTheirType = RoomProposal.FirstFreeRoomOfType(scout.BoardType, free);
            KeepOrChooseRoom(scout, free, freeRoomOfTheirType, mayKeepCurrentRoom: true);
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

        // Another youth's room was chosen for them, so it is proposed afresh.
        var sameYouthAsLastProposal = _proposal?.ScoutId == scout.Id;
        KeepOrChooseRoom(scout, free, pick.RoomId, mayKeepCurrentRoom: sameYouthAsLastProposal);
        _proposal = new ProposedBoard(scout.Id, ids, _chairId);
    }

    /// <summary>
    /// Sets the room for the board being built by <see cref="RoomProposal"/>'s
    /// rule (SPEC.md D-24), which the window only applies to its two fields.
    /// </summary>
    private void KeepOrChooseRoom(ScoutRow scout, List<RoomInfo> free, string? proposed, bool mayKeepCurrentRoom)
    {
        var room = RoomProposal.KeepOrChoose(scout.BoardType, free, new RoomChoice(_builderRoomId, _roomChosenByOperator),
            proposed, mayKeepCurrentRoom);
        (_builderRoomId, _roomChosenByOperator) = (room.RoomId, room.ChosenByOperator);
    }

    /// <summary>
    /// Puts away the board on screen, which belongs to <paramref name="ownerId"/>:
    /// kept as a draft if it is the operator's, dropped if the app proposed it
    /// (it is proposed afresh on return) or the youth is no longer waiting.
    /// </summary>
    private void SetAsideBoardOf(string ownerId)
    {
        var stillWaiting = _scouts.Any(s => s.Id == ownerId && BoardStatus.IsWaiting(s.Status));
        var operatorChangedIt = _proposal is { } proposal && proposal.ScoutId == ownerId
            ? !ProposalUntouched()
            : _adults.Any(IsPicked);   // picks found in the file at start-up have no proposal behind them
        if (stillWaiting && operatorChangedIt)
        {
            _drafts.Keep(ownerId, new BoardDraft([.. _adults.Where(IsPicked).Select(a => a.Id)], _chairId,
                new RoomChoice(_builderRoomId, _roomChosenByOperator), _proposal));
        }

        ClearBoardBuilder();
    }

    /// <summary>Nobody picked, no chair or room, and no board on screen belongs to anyone.</summary>
    private void ClearBoardBuilder()
    {
        foreach (var adult in _adults.Where(a => a.Sel).ToList())
        {
            adult.Sel = false;
        }

        _chairId = null;
        _builderRoomId = null;
        _roomChosenByOperator = false;
        _proposal = null;
        _boardOwnerId = null;
    }

    /// <summary>
    /// The operator's board for this youth, as they left it. An adult who
    /// has since gone home or joined another board drops out, and the room
    /// is kept only while it is still free.
    /// </summary>
    private void RestoreDraft(ScoutRow scout, List<RoomInfo> free, BoardDraft draft)
    {
        foreach (var adult in _adults.Where(a => a.CanPick && a.Sel != draft.AdultIds.Contains(a.Id)))
        {
            adult.Sel = draft.AdultIds.Contains(adult.Id);
        }

        _chairId = draft.ChairId;
        _proposal = draft.Proposal;
        (_builderRoomId, _roomChosenByOperator) = (draft.Room.RoomId, draft.Room.ChosenByOperator);
        KeepOrChooseRoom(scout, free, RoomProposal.FirstFreeRoomOfType(scout.BoardType, free), mayKeepCurrentRoom: true);
    }

    /// <summary>The picks are still the board last proposed: the operator hasn't changed it.</summary>
    private bool ProposalUntouched() =>
        _proposal is { } proposal
        && proposal.IsUntouched([.. _adults.Where(IsPicked).Select(a => a.Id)],
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
}
