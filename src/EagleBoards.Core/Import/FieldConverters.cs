using EagleBoards.Core.Records;

namespace EagleBoards.Core.Import;

/// <summary>
/// Clean-ups applied to pre-registration fields, shared by the SignUpGenius
/// import and the district-website CSV import. Each returns false to reject
/// the row.
/// </summary>
internal static class FieldConverters
{
    /// <summary>Capitalize the first letter, leave the rest alone.</summary>
    public static string CapitalizeName(string s) =>
        s.Length > 0 && char.IsLetter(s[0]) ? char.ToUpperInvariant(s[0]) + s[1..] : s;

    private static string[] Words(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>First word only.</summary>
    public static bool FirstName(DataRecord r, string value)
    {
        var words = Words(value);
        if (words.Length == 0)
        {
            return false;
        }

        r.Put("First", CapitalizeName(words[0]));
        return true;
    }

    /// <summary>First word only.</summary>
    public static bool LastName(DataRecord r, string value)
    {
        var words = Words(value);
        if (words.Length == 0)
        {
            return false;
        }

        r.Put("Last", CapitalizeName(words[0]));
        return true;
    }

    /// <summary>"First Middle Last": first word is First, the last word is Last.</summary>
    public static bool FullName(DataRecord r, string value)
    {
        var words = Words(value);
        if (words.Length == 0)
        {
            return false;
        }

        if (words.Length == 1)
        {
            r.Put("Last", CapitalizeName(words[0]));
            return true;
        }

        r.Put("First", CapitalizeName(words[0]));
        r.Put("Last", CapitalizeName(words[^1]));
        return true;
    }

    /// <summary>Stores a phone number as <see cref="FormatPhone"/> writes it.</summary>
    public static bool Phone(DataRecord r, string value)
    {
        r.SetValue("Phone", FormatPhone(value));
        return true;
    }

    /// <summary>
    /// Writes a phone number as 555-123-4567, so "(555) 123-4567", "5551234567",
    /// "555.123.4567", and "+1 (555) 123-4567" all come out that way. Seven
    /// digits are a local number and come out as 123-4567. Anything else, such
    /// as a number with an extension or too few digits, is kept as typed (minus
    /// the blanks around it): guessing where its dashes go only garbles it.
    /// The Mac version and the Java version write the same.
    /// </summary>
    public static string FormatPhone(string typed)
    {
        var digits = new string([.. typed.Where(char.IsAsciiDigit)]);
        if (digits.Length == 11 && digits[0] == '1')
        {
            digits = digits[1..];
        }

        return digits.Length switch
        {
            10 => $"{digits[..3]}-{digits[3..6]}-{digits[6..]}",
            7 => $"{digits[..3]}-{digits[3..]}",
            _ => typed.Trim(),
        };
    }

    /// <summary>Digits are the unit number; the first letter picks Crew, Pack, or Troop.</summary>
    public static bool Unit(DataRecord r, string value)
    {
        r.SetValue("Unit", new string([.. value.Where(char.IsDigit)]));
        var upper = value.Trim().ToUpperInvariant();
        r.SetValue("UnitType", upper.StartsWith('C') ? "Crew" : upper.StartsWith('P') ? "Pack" : "Troop");
        return true;
    }

    /// <summary>Appends to Leader ("Coach/Scoutmaster"), capitalizing each word.</summary>
    public static bool Leader(DataRecord r, string value)
    {
        value = value.Trim();
        if (value.Length == 0)
        {
            return true;
        }

        var existing = r.GetValue("Leader");
        var joined = string.Join(' ', Words(value).Select(CapitalizeName));
        r.SetValue("Leader", existing.Length > 0 ? existing + "/" + joined : joined);
        return true;
    }

    /// <summary>Anything canceled is dropped.</summary>
    public static bool NotCancelled(string value) => !value.Contains("cancel", StringComparison.OrdinalIgnoreCase);

    /// <summary>A scout's slot names the board: "... review ..." is Final, "... project ..." is Project.</summary>
    public static bool ScoutBoardType(DataRecord r, string value)
    {
        if (value.Contains("review", StringComparison.OrdinalIgnoreCase))
        {
            r.Put("BoardType", BoardTypes.Final);
            return true;
        }

        if (value.Contains("project", StringComparison.OrdinalIgnoreCase))
        {
            r.Put("BoardType", BoardTypes.Project);
            return true;
        }

        return false;
    }

    /// <summary>An adult slot mentions "adult"; they join as a Member of both kinds of board.</summary>
    public static bool AdultSlot(DataRecord r, string value)
    {
        if (!value.Contains("adult", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        r.Put("ProjectReview", BoardRoles.Member);
        r.Put("FinalBoard", BoardRoles.Member);
        return true;
    }
}
