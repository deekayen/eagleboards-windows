using System.Globalization;
using System.Text.Json;
using EagleBoards.Core.Records;
using EagleBoards.Core.Storage;

namespace EagleBoards.Core.Import;

public sealed record SignUpGeniusSummary(string SignupId, int AdultsAdded, int AdultsUpdated, int ScoutsAdded, int Skipped);

/// <summary>One filled slot from the SignUpGenius report.</summary>
public sealed record SignUpEntry(string StartDate, string FirstName, string LastName, string Item, string Email, IReadOnlyList<string> CustomFields);

/// <summary>
/// Import from the SignUpGenius API (v2, user-key auth). Two calls: find the
/// active sign-up whose title contains "eagle" and "board" and whose date
/// range covers today, then read its filled-slots report.
///
/// Slots whose item mentions "adult" become adult-history records, merged by
/// email (a match gets its phone updated; two matches means the email is
/// ambiguous and the entry is skipped). Everything else is a youth
/// pre-registration, which is what makes a sign-in a "P" rather than a "W";
/// a youth's phone number is not imported (SPEC.md D-8).
/// Entries are filtered by calendar month, not by day, as they always were.
/// </summary>
public sealed class SignUpGenius(HttpClient http, string key, Action<string> log, Action<string> trace)
{
    private const string Api = "https://api.signupgenius.com/v2/k/signups";

    public async Task<string> FindSignupIdAsync(CancellationToken cancel)
    {
        log("Looking up the sign-up ID from SignUpGenius...");
        using var doc = await GetAsync($"{Api}/created/active/", cancel).ConfigureAwait(false);
        var today = DataRecord.Clock().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        foreach (var signup in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            var id = Text(signup, "signupid");
            var title = Text(signup, "title");
            var start = Text(signup, "startdatestring");
            var end = Text(signup, "enddatestring");

            // Compared on the date alone: the API's strings carry a time too,
            // and "2026-09-22" sorts before "2026-09-22 18:30:00", which missed
            // a sign-up on its first day.
            var inRange = string.CompareOrdinal(today, Day(end)) <= 0 && string.CompareOrdinal(today, Day(start)) >= 0;
            if (inRange && title.Contains("board", StringComparison.OrdinalIgnoreCase)
                && title.Contains("eagle", StringComparison.OrdinalIgnoreCase))
            {
                log($"Using sign-up {id}: {title} ({start} to {end})");
                return id;
            }

            trace($"out of date range: {title} ({id})");
        }

        throw new InvalidOperationException("No SignUpGenius sign-up titled with \"Eagle\" and \"Board\" covers today.");
    }

    public async Task<IReadOnlyList<SignUpEntry>> FetchFilledSlotsAsync(string signupId, CancellationToken cancel)
    {
        using var doc = await GetAsync($"{Api}/report/filled/{Uri.EscapeDataString(signupId)}/", cancel).ConfigureAwait(false);
        var entries = new List<SignUpEntry>();
        foreach (var slot in doc.RootElement.GetProperty("data").GetProperty("signup").EnumerateArray())
        {
            var custom = new List<string>();
            if (slot.TryGetProperty("customfields", out var fields) && fields.ValueKind == JsonValueKind.Array)
            {
                foreach (var field in fields.EnumerateArray())
                {
                    custom.Add(Text(field, "value"));
                }
            }

            entries.Add(new SignUpEntry(
                Text(slot, "startdatestring"),
                Text(slot, "firstname"),
                Text(slot, "lastname"),
                Text(slot, "item"),
                Text(slot, "email"),
                custom));
        }

        return entries;
    }

    /// <summary>Merge this month's entries. Caller holds the service lock and stores.</summary>
    internal SignUpGeniusSummary Merge(IEnumerable<SignUpEntry> entries, DataRecordFile<AdultRecord> history, DataRecordFile<ScoutRecord> scheduled)
    {
        var month = DataRecord.Clock().ToString("yyyy-MM", CultureInfo.InvariantCulture);
        int adultsAdded = 0, adultsUpdated = 0, scoutsAdded = 0, skipped = 0;
        foreach (var e in entries)
        {
            if (!e.StartDate.StartsWith(month, StringComparison.Ordinal))
            {
                continue;
            }

            string Custom(int i) => i < e.CustomFields.Count ? e.CustomFields[i] : "";
            var unit = Custom(0);
            var phone = Custom(1);
            var leader = Custom(2);
            trace($" EVALUATING: {e.FirstName} {e.LastName} ({e.Item})");

            if (e.Item.Contains("adult", StringComparison.OrdinalIgnoreCase))
            {
                var adult = history.CreateNew();
                FieldConverters.FirstName(adult, e.FirstName);
                FieldConverters.LastName(adult, e.LastName);
                FieldConverters.Phone(adult, phone);
                adult.SetValue("ProjectReview", BoardRoles.Member);
                adult.SetValue("FinalBoard", BoardRoles.Member);
                FieldConverters.Unit(adult, unit);
                adult.SetValue("Email", e.Email);

                // The ID was fixed as "ADULT:::" when the empty record was made;
                // derive the real one now. (The Java import didn't, so every
                // adult it added shared that ID until the next restart, and a
                // sign-in that evening created a duplicate history record.)
                adult.PostLoadUpdate();

                var matches = history.Where("Email", e.Email);
                if (matches.Count == 0)
                {
                    trace("SIGNUP-GENIUS/ADDING ADULT " + adult);
                    history.Add(adult, false);
                    adultsAdded++;
                }
                else if (matches.Count == 1)
                {
                    trace("SIGNUP-GENIUS/UPDATING ADULT : " + matches[0]);
                    matches[0].UpdateFrom(adult, ["Phone"]);
                    adultsUpdated++;
                }
                else
                {
                    skipped++;
                }
            }
            else
            {
                var scout = scheduled.CreateNew();
                FieldConverters.FirstName(scout, e.FirstName);
                FieldConverters.LastName(scout, e.LastName);
                // No phone number: a youth's isn't kept (SPEC.md D-8).
                FieldConverters.ScoutBoardType(scout, e.Item);
                FieldConverters.Unit(scout, unit);
                FieldConverters.Leader(scout, leader);
                scout.SetValue("Email", e.Email);
                scout.SetValue("ID", "SCOUT:" + scout.Last + ":" + scout.First + ":" + scout.Unit);
                scout.UpdateFields(false);
                if (scheduled.Where("Email", e.Email).Count == 0)
                {
                    trace("SIGNUP-GENIUS/ADDING SCOUT " + scout);
                    scheduled.Add(scout, false);
                    scoutsAdded++;
                }
                else
                {
                    skipped++;
                }
            }
        }

        return new SignUpGeniusSummary("", adultsAdded, adultsUpdated, scoutsAdded, skipped);
    }

    private async Task<JsonDocument> GetAsync(string url, CancellationToken cancel)
    {
        // The key is a credential: never logged, only appended at send time.
        trace("REQUESTING: " + url);
        using var response = await http.GetAsync(url + "?user_key=" + Uri.EscapeDataString(key), cancel).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"SignUpGenius answered HTTP {(int)response.StatusCode} for {url}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancel).ConfigureAwait(false);
    }

    /// <summary>The <c>yyyy-MM-dd</c> a SignUpGenius date string starts with.</summary>
    private static string Day(string stamp) => stamp.Length > 10 ? stamp[..10] : stamp;

    /// <summary>A property as text whatever its JSON type, "" when absent or null (Jackson's asText()).</summary>
    private static string Text(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var v))
        {
            return "";
        }

        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Null or JsonValueKind.Undefined => "",
            _ => v.GetRawText(),
        };
    }
}
