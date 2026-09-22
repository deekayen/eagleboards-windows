namespace EagleBoards.Core.Records;

/// <summary>Shared behaviour of scouts and adults.</summary>
public abstract class PersonRecord : DataRecord
{
    protected PersonRecord(string type, IReadOnlyList<string> columns, IEnumerable<KeyValuePair<string, string>>? source)
        : base(type, columns, source)
    {
        PopulateId();
    }

    public string First => GetValue("First");

    public string Last => GetValue("Last");

    public string Email => GetValue("Email");

    public string Phone => GetValue("Phone");

    public string Unit => GetValue("Unit");

    public string UnitType => GetValue("UnitType");

    /// <summary>Unit type and number as one label, e.g. "Troop1776".</summary>
    public string UnitName => GetValue("UnitName");

    public string Room
    {
        get => GetValue("Room");
        set => SetValue("Room", value);
    }

    public string Flags
    {
        get => GetValue("Flags");
        set => SetValue("Flags", value);
    }

    public string FullName => First + " " + Last;

    public string ShortName => (First.Length > 0 ? First[0] + ". " : "") + Last;

    public override void UpdateFields(bool touch)
    {
        base.UpdateFields(touch);

        // The whole unit type, not its first letter: Pack and Post both
        // abbreviate to "P", and Council, Community and Crew all to "C".
        SetValue("UnitName", UnitType + Unit);
        SetValue("ShortName", ShortName);
    }

    /// <summary>
    /// The ID is "TYPE:Last:First:Unit" unless one was supplied that already
    /// belongs to this record type. It is how a returning person is matched.
    /// </summary>
    public void PopulateId()
    {
        var id = Id;
        if (id.Length == 0 || !id.StartsWith(Type, StringComparison.Ordinal) || id == Type + ":::")
        {
            Put(IdField, Type + ":" + Last + ":" + First + ":" + Unit);
        }
    }

    /// <summary>
    /// People keep their stored times -- the base class's reset of day-old
    /// stamps is for rooms, not for who signed in when.
    /// </summary>
    public override void PostLoadUpdate()
    {
        PopulateId();
        UpdateFields(false);
    }

    /// <summary>Copy the named fields from <paramref name="other"/>, skipping blanks.</summary>
    public void UpdateFrom(DataRecord other, IEnumerable<string> fields)
    {
        foreach (var field in fields)
        {
            var value = other.Get(field);
            if (!string.IsNullOrEmpty(value))
            {
                Put(field, value);
            }
        }
    }
}
