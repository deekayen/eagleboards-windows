namespace EagleBoards.Core;

/// <summary>
/// A board the operator changed for one youth, as they left it: who is
/// picked, the chair, the room, and the proposal it started from (so a board
/// they emptied is still known to be theirs).
/// </summary>
public sealed record BoardDraft(
    IReadOnlyList<string> AdultIds,
    string? ChairId,
    RoomChoice Room,
    ProposedBoard? Proposal);

/// <summary>
/// The boards the operator changed, one per waiting youth (SPEC.md D-12,
/// amended 2026-11-08): opening another youth shows that youth's own board,
/// and coming back shows this one as it was left. A board the operator has
/// not changed is never kept here, since it is proposed afresh.
/// </summary>
public sealed class BoardDrafts
{
    private readonly Dictionary<string, BoardDraft> _byScoutId = new(StringComparer.Ordinal);

    public bool Has(string scoutId) => _byScoutId.ContainsKey(scoutId);

    public void Keep(string scoutId, BoardDraft draft) => _byScoutId[scoutId] = draft;

    /// <summary>The youth's draft, handed back and forgotten: the open board is the one place a draft lives.</summary>
    public BoardDraft? Take(string scoutId) => _byScoutId.Remove(scoutId, out var draft) ? draft : null;

    /// <summary>Start over, Seat board, or a youth no longer waiting ends the operator's board.</summary>
    public void Forget(string scoutId) => _byScoutId.Remove(scoutId);

    /// <summary>Drops the drafts of youth who are not waiting any more, such as one seated from another computer.</summary>
    public void ForgetAllExcept(IReadOnlySet<string> waitingScoutIds)
    {
        foreach (var scoutId in _byScoutId.Keys.Where(id => !waitingScoutIds.Contains(id)).ToList())
        {
            _byScoutId.Remove(scoutId);
        }
    }
}
