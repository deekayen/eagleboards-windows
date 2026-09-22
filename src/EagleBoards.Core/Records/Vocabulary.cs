namespace EagleBoards.Core.Records;

/// <summary>
/// A scout's board status. Lifecycle:
/// Registered → Seated → InProgress → Completed, or Registered → Postponed.
/// Seat and Start are two steps so the convening phase (members reading the
/// paperwork, scout outside) and the interview are timed apart.
/// </summary>
public static class BoardStatus
{
    public const string Registered = "Registered";

    /// <summary>Legacy only: nothing sets it, but carried-over records stay usable.</summary>
    public const string Verified = "Verified";

    public const string Seated = "Seated";
    public const string InProgress = "InProgress";
    public const string Completed = "Completed";
    public const string Postponed = "Postponed";

    public static readonly IReadOnlyList<string> All = [Registered, Verified, Seated, InProgress, Completed, Postponed];

    /// <summary>Sort rank, lifecycle order; unknown statuses first.</summary>
    public static int Rank(string status)
    {
        var i = ((IList<string>)All).IndexOf(status);
        return i;
    }

    public static bool IsWaiting(string status) => status is Registered or Verified;

    public static bool IsActive(string status) => status is Seated or InProgress;

    public static bool IsFinished(string status) => status is Completed or Postponed;
}

public static class BoardTypes
{
    /// <summary>Eagle Scout board of review.</summary>
    public const string Final = "Final";

    /// <summary>Service project proposal review (not a board of review, GTA 9.0.2.4).</summary>
    public const string Project = "Project";

    public static string Label(string boardType) => boardType switch
    {
        Final => "Final Board",
        Project => "Proposal Review",
        _ => boardType,
    };
}

public static class BoardRoles
{
    public const string Chair = "Chair";
    public const string Member = "Member";
    public const string Unavailable = "Unavailable";

    public static readonly IReadOnlyList<string> All = [Chair, Member, Unavailable];
}

public static class BoardResults
{
    public const string Approved = "Approved";
    public const string Adjourned = "Adjourned";
    public const string NotApproved = "NotApproved";

    public static readonly IReadOnlyList<string> All = [Approved, Adjourned, NotApproved];
}

public static class UnitTypes
{
    public static readonly IReadOnlyList<string> All = ["Troop", "Post", "Crew", "Ship", "Pack", "District", "Council", "Community"];
}

/// <summary>Room value written by the Disable button: the adult has gone home.</summary>
public static class AdultRoom
{
    public const string Disabled = "N/A";
}
