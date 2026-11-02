using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using EagleBoards.Core;
using EagleBoards.Core.Records;

namespace EagleBoards.App;

/// <summary>
/// An adult seen from one youth's board: a picked member (with the chair
/// choice), an eligible adult to add, a seated board's member, or a choice
/// in Replace and Add member. Carries the marks after the name (SPEC.md D-20).
/// </summary>
public sealed class PickRow : Row
{
    private bool _isChair;

    public PickRow(AdultRow adult, ScoutRow scout, Action<PickRow>? chairChosen = null)
    {
        Adult = adult;
        Id = adult.Id;

        // Taken now, as the rest are: an edit on the Adults page then changes
        // the row's Signature, so the list is rebuilt rather than kept stale.
        Name = adult.FullName;
        WoodBadge = adult.IsWoodBadge;
        var role = adult.Info.RoleFor(scout.BoardType);
        CanChair = role == BoardRoles.Chair;
        SameUnit = scout.UnitName.Length > 0 && adult.UnitName == scout.UnitName;
        SameUnitAs = SameUnit ? scout.FullName : null;
        Unit = Display.Unit(adult.UnitName);
        Role = Display.Role(role);
        Detail = string.Join(" · ", new[] { Unit, Role }.Where(s => s.Length > 0));
        ChairChosen = chairChosen;
    }

    public AdultRow Adult { get; }

    public string Name { get; }

    /// <summary>"Troop 2001 · Chair": the unit in full and the role in words.</summary>
    public string Detail { get; }

    /// <summary>The same in commas, as it's read aloud.</summary>
    public string DetailSpoken => Display.Spoken(Detail);

    /// <summary>The unit in full ("Troop 2001").</summary>
    public string Unit { get; }

    /// <summary>The role for this board in words: "No thanks" for the file's "Unavailable".</summary>
    public string Role { get; }

    public bool CanChair { get; }

    /// <summary>In the youth's own unit: a warning, with an override to seat (SPEC.md D-4).</summary>
    public bool SameUnit { get; }

    /// <summary>The youth's name when <see cref="SameUnit"/>, for the mark's tooltip; otherwise null.</summary>
    public string? SameUnitAs { get; }

    public bool WoodBadge { get; }

    /// <summary>What a screen reader says for the adult as a choice in a list, marks included, in commas.</summary>
    public override string ToString() =>
        string.Join(", ", new[] { Name, WoodBadge ? "Wood Badge" : "", SameUnit ? "Same unit" : "", Unit, Role }.Where(s => s.Length > 0));

    /// <summary>The same, for a binding: the help text of the row's buttons, since the marks' own tooltips can't take focus.</summary>
    public string Spoken => ToString();

    /// <summary>What the marks mean, for the Add button's tooltip on keyboard focus; null with no mark.</summary>
    public string? MarksTip => string.Join(" ", new[]
    {
        WoodBadge ? "Counting today toward a Wood Badge ticket item." : "",
        SameUnitAs is { } youth ? $"Same unit as {youth}." : "",
    }.Where(s => s.Length > 0)) is { Length: > 0 } tip ? tip : null;

    /// <summary>Everything the row shows: the same for two rows, and a list of them needn't be rebuilt.</summary>
    internal string Signature => $"{Id}|{Name}|{Detail}|{CanChair}|{IsChair}|{WoodBadge}|{SameUnitAs}";

    private Action<PickRow>? ChairChosen { get; }

    public bool IsChair
    {
        get => _isChair;
        set
        {
            if (Set(ref _isChair, value) && value)
            {
                ChairChosen?.Invoke(this);
            }
        }
    }

    internal void SetChairQuietly(bool value) => Set(ref _isChair, value, nameof(IsChair));
}
