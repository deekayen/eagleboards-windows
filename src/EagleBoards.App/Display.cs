using System.Globalization;
using EagleBoards.Core;
using EagleBoards.Core.Records;

namespace EagleBoards.App;

/// <summary>
/// Words for the values the data files store in their Java form. Display
/// only: the files keep "InProgress", "NotApproved", "N/A", and the Java
/// escapes ("," written as "~").
/// </summary>
public static class Display
{
    /// <summary>
    /// The file format writes a comma in a value as "~" and never turns it back
    /// (the Java version's format, kept so both can read the files). Undo it
    /// for display; IDs and the files themselves are left alone.
    /// </summary>
    public static string Text(string value) => value.Replace('~', ',');

    /// <summary>The same list, one name per line.</summary>
    public static string Lines(string value) =>
        string.Join(Environment.NewLine, value.Split([',', '~'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>A list of names, joined with "," in memory or "~" once saved, as "A, B, C".</summary>
    public static string List(string value) =>
        string.Join(", ", value.Split([',', '~'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    public static string Status(string status) => status switch
    {
        BoardStatus.Registered => "Waiting",
        BoardStatus.InProgress => "In review",
        _ => status,
    };

    public static string Result(string result) => result == BoardResults.NotApproved ? "Not approved" : result;

    public static string BoardType(string type) => type switch
    {
        BoardTypes.Final => "Final board",
        BoardTypes.Project => "Project review",
        _ => type,
    };

    /// <summary>A role in words: "No thanks", as the tablet's form and Add adult say it, where the file keeps "Unavailable".</summary>
    public static string Role(string role) => role == BoardRoles.Unavailable ? "No thanks" : role;

    /// <summary>
    /// A unit in full, "Troop 1001" for the file's "Troop1001": the short
    /// form (<see cref="SchedulerLogic.UnitLabel"/>, "T1001") is read aloud
    /// as a code, and a Post or Pack's "P" looks like a pre-registered
    /// sign-in number.
    /// </summary>
    public static string Unit(string unitName)
    {
        var i = 0;
        while (i < unitName.Length && char.IsAsciiLetter(unitName[i]))
        {
            i++;
        }

        var number = unitName[i..];
        return i > 0 && number.Length > 0 && number.All(char.IsAsciiDigit) ? unitName[..i] + " " + number : Text(unitName);
    }

    /// <summary>
    /// Text joined with middle dots for the eye ("W1 · Troop 1001 · Final
    /// board"), with commas for the ear: a screen reader says "dot", or runs
    /// the parts together.
    /// </summary>
    public static string Spoken(string text) => text.Replace(" · ", ", ", StringComparison.Ordinal);

    /// <summary>"1st", "2nd", "3rd", "11th", "22nd": a place in words, where "#3" would be read "number sign 3".</summary>
    public static string Ordinal(int n)
    {
        var suffix = (n % 100) is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        return n.ToString(CultureInfo.CurrentCulture) + suffix;
    }

    /// <summary>"1 minute", "12 minutes": in words where there's room, and in what a screen reader says.</summary>
    public static string Minutes(int minutes) => Count(minutes, "minute", "minutes");

    /// <summary>A count with its noun, singular or plural: never "1 adults" or "member(s)".</summary>
    public static string Count(int n, string one, string many) =>
        n.ToString(CultureInfo.CurrentCulture) + " " + (n == 1 ? one : many);
}
