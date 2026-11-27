namespace EagleBoards.Core;

/// <summary>
/// How many of the waiting youth, in queue order, could each still get a
/// full board at once from the free adults a proposed board leaves over:
/// <see cref="SchedulerLogic.AutoSelect"/>'s first preference. Each youth in
/// turn takes the first free chair who may chair for them, then the first
/// members who may sit, in the pool's order of preference; a youth who can't
/// get a full board takes no one.
/// </summary>
/// <remarks>
/// AutoSelect asks this once for every legal board, and at an event of 40
/// adults and 25 waiting youth that was a second of work on the window's
/// thread for each sign-in. Who may chair or sit for whom doesn't change
/// between those boards, so it is worked out once here, as each youth's list
/// of the pool's indexes, best first, and each count walks those lists. The
/// answer is the one the Java and Mac versions give (SPEC.md D-5).
/// </remarks>
internal sealed class SeatableCounter
{
    private readonly int[][] _chairsFor;
    private readonly int[][] _sittersFor;
    private readonly int[] _membersNeeded;
    private readonly bool[] _used;
    private readonly int[] _members;

    /// <param name="pool">The free adults, best first.</param>
    /// <param name="waiting">The other waiting youth, in queue order.</param>
    public SeatableCounter(IReadOnlyList<AdultInfo> pool, IReadOnlyList<ScoutInfo> waiting)
    {
        _chairsFor = [.. waiting.Select(scout => Enumerable.Range(0, pool.Count).Where(i => SchedulerLogic.CanChairFor(pool[i], scout)).ToArray())];
        _sittersFor = [.. waiting.Select(scout => Enumerable.Range(0, pool.Count).Where(i => SchedulerLogic.CanSitFor(pool[i], scout)).ToArray())];
        _membersNeeded = [.. waiting.Select(scout => SchedulerLogic.MembersBesideChair(scout.BoardType))];
        _used = new bool[pool.Count];
        _members = new int[_membersNeeded.Length == 0 ? 0 : _membersNeeded.Max()];
    }

    /// <summary>The count, with the pool's adults at the indexes <paramref name="taken"/> marks already on the proposed board.</summary>
    public int Count(bool[] taken)
    {
        Array.Clear(_used);
        var seated = 0;
        for (var scout = 0; scout < _chairsFor.Length; scout++)
        {
            var chair = FirstFree(_chairsFor[scout], taken);
            if (chair < 0)
            {
                continue;
            }

            var need = _membersNeeded[scout];
            var found = 0;
            foreach (var adult in _sittersFor[scout])
            {
                if (found == need)
                {
                    break;
                }

                if (adult != chair && !taken[adult] && !_used[adult])
                {
                    _members[found++] = adult;
                }
            }

            // Short of members, this youth waits and the adults stay in the
            // pool for the youth behind them.
            if (found < need)
            {
                continue;
            }

            _used[chair] = true;
            for (var member = 0; member < found; member++)
            {
                _used[_members[member]] = true;
            }

            seated++;
        }

        return seated;
    }

    private int FirstFree(int[] candidates, bool[] taken)
    {
        foreach (var adult in candidates)
        {
            if (!taken[adult] && !_used[adult])
            {
                return adult;
            }
        }

        return -1;
    }
}
