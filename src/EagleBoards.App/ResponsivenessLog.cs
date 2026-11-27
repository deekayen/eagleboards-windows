using System.Diagnostics;
using System.Globalization;

namespace EagleBoards.App;

/// <summary>
/// Whether the window keeps up with the event, written to the event's log
/// only when it falls behind, and a line with the event's size every few
/// minutes. Counts and times only: nothing about a person.
/// </summary>
/// <remarks>
/// At the 2026-11-17 event adults who had signed in didn't appear in the
/// window for minutes. The log couldn't say whether the window had been slow
/// to show them, frozen, or never told, nor how many people were at the
/// event (docs/decisions/0004).
/// </remarks>
internal sealed class ResponsivenessLog(Action<string> log)
{
    /// <summary>A change shown later than this is worth a line.</summary>
    internal static readonly TimeSpan SlowChange = TimeSpan.FromSeconds(1);

    /// <summary>The minute timer running this late means the window was held up.</summary>
    internal static readonly TimeSpan LateTick = TimeSpan.FromSeconds(15);

    /// <summary>How often the event's size is written.</summary>
    internal static readonly TimeSpan SummaryEvery = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan SummarySlack = TimeSpan.FromSeconds(30);

    /// <summary>When the oldest change not yet shown was made (a Stopwatch timestamp), or 0.</summary>
    private long _unshownSince;

    private DateTime? _lastSummary;

    /// <summary>
    /// The service made a change. Called on the thread that made it, before
    /// the window's dispatcher sees it, so time spent waiting in the
    /// dispatcher's queue counts.
    /// </summary>
    public void ChangeMade(long timestamp) => Interlocked.CompareExchange(ref _unshownSince, timestamp, 0);

    /// <summary>
    /// The window is about to read the service, so the changes made so far
    /// will be on screen when it is done. Returns when the oldest was made,
    /// or 0, for <see cref="ChangesShown"/>. Taken before the read: a change
    /// made during it starts the clock again and is measured next time.
    /// </summary>
    public long TakeUnshown() => Interlocked.Exchange(ref _unshownSince, 0);

    /// <summary>The changes <see cref="TakeUnshown"/> returned are on screen.</summary>
    public void ChangesShown(long since, long timestamp)
    {
        if (since != 0 && Stopwatch.GetElapsedTime(since, timestamp) is var lag && lag >= SlowChange)
        {
            log($"the window took {Seconds(lag)} s to show a change");
        }
    }

    /// <summary>The whole refresh, the board proposal with it, took this long.</summary>
    public void RefreshTook(TimeSpan elapsed)
    {
        if (elapsed >= SlowChange)
        {
            log($"the window's refresh, with the board proposal, took {Seconds(elapsed)} s");
        }
    }

    /// <summary>
    /// The minute timer ran this late. It runs at the same priority as the
    /// window's refresh, so running late means the refresh was held up as
    /// long, or that the computer was asleep (a line from
    /// <see cref="ComputerWatch"/> says so). Measured on the Stopwatch, not
    /// the wall clock, which a daylight-saving change or a clock correction
    /// moves.
    /// </summary>
    public void MinuteTicked(TimeSpan late)
    {
        if (late >= LateTick)
        {
            log($"the window was held up for {Seconds(late)} s (its minute update ran late)");
        }
    }

    /// <summary>The event's size, at most every <see cref="SummaryEvery"/>.</summary>
    public void Summary(DateTime now, Func<string> describe)
    {
        // The minute timer runs a few milliseconds either side of the
        // minute, so a summary due on the fifth tick could miss by a hair
        // and slip to the sixth.
        if (_lastSummary is { } last && now - last < SummaryEvery - SummarySlack)
        {
            return;
        }

        _lastSummary = now;
        log("event: " + describe());
    }

    private static string Seconds(TimeSpan span) => span.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);
}
