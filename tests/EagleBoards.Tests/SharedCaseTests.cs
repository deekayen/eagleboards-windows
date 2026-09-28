using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EagleBoards.Core;
using EagleBoards.Core.Records;

namespace EagleBoards.Tests;

/// <summary>
/// The board rules, auto-select and fill the rest, as the cases all three
/// versions share (SPEC.md D-5). cases/ is a byte-for-byte copy of
/// eagleboards-shared/cases, pinned by test-cases.lock and checked in CI; a
/// new case goes there, never only here (its README sets the format).
/// </summary>
/// <remarks>
/// Each case runs through Windows' own code and the answer is mapped into the
/// cases' shapes. A comparison is never loosened to make a case pass: a case
/// that fails means Windows, or the case, is wrong, and a real difference is
/// settled in eagleboards-shared first. Each case is its own test, named by
/// its op and name, so a failure reads the same in every version.
/// </remarks>
public class SharedCaseTests
{
    private const int Format = 1;

    private static readonly string[] Ops =
        ["unit-conflicts", "outside-member", "board-size", "suggest", "fill", "free-since", "seat-down-the-queue", "support-link"];

    private static readonly string[] ProblemKinds = ["no-chair", "too-few-members"];

    private static readonly string[] ProposalKeys = ["chair", "members", "problems"];

    private static readonly Lazy<IReadOnlyList<CaseFile>> Files = new(ReadFiles);

    private sealed record CaseFile(string Name, JsonObject? Suite, string? Error)
    {
        /// <summary>A format this runner reads, a known op, and named for it.</summary>
        public bool IsReadable => Suite != null
            && Suite["format"] is JsonValue format && format.TryGetValue<double>(out var number) && number <= Format
            && Suite["op"] is JsonValue op && op.TryGetValue<string>(out var name) && Ops.Contains(name)
            && Name == name + ".json";

        public JsonArray Cases => Suite?["cases"] as JsonArray ?? [];
    }

    private static IReadOnlyList<CaseFile> ReadFiles()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "cases");
        if (!Directory.Exists(folder))
        {
            return [];
        }

        return Directory.GetFiles(folder, "*.json").Order(StringComparer.Ordinal).Select(path =>
        {
            try
            {
                return new CaseFile(Path.GetFileName(path), JsonNode.Parse(File.ReadAllText(path)) as JsonObject, null);
            }
            catch (JsonException e)
            {
                return new CaseFile(Path.GetFileName(path), null, e.Message);
            }
        }).ToList();
    }

    /// <summary>Every case in every file this runner reads; the others fail <see cref="EveryCaseFileIsOneThisRunnerReads"/>.</summary>
    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var file in Files.Value.Where(f => f.IsReadable))
        {
            foreach (var c in file.Cases)
            {
                data.Add(Text(file.Suite!["op"]), Text(c?["name"]));
            }
        }

        return data;
    }

    [Fact]
    public void EveryCaseFileIsOneThisRunnerReads()
    {
        Assert.True(Files.Value.Count > 0, "no case files beside the tests: is cases/ copied to the output directory?");
        foreach (var file in Files.Value)
        {
            Assert.True(file.Suite != null, $"{file.Name} is not a readable JSON object: {file.Error}");
            Assert.True(file.IsReadable,
                $"{file.Name}: format {Show(file.Suite!["format"])} or op {Show(file.Suite["op"])} is not one this runner reads");

            var names = file.Cases.Select(c => Text(c?["name"])).ToList();
            Assert.DoesNotContain("", names);
            Assert.Equal(names.Distinct().Count(), names.Count);
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Case(string op, string name)
    {
        var file = Files.Value.Single(f => f.IsReadable && Text(f.Suite!["op"]) == op);
        var body = file.Cases.Single(c => Text(c?["name"]) == name)!.AsObject();
        var expect = body["expect"];

        switch (op)
        {
            case "unit-conflicts":
                var conflicts = BoardRules.FindUnitConflicts(Text(body["youth"]?["unit"]), Items(body["adults"]).Select(Candidate));
                Same(expect, Strings(conflicts.Select(c => c.Id)));
                break;

            case "outside-member":
                var outside = BoardRules.HasNonUnitMember(Text(body["youth"]?["unit"]), Items(body["adults"]).Select(Candidate));
                Same(expect, JsonValue.Create(outside));
                break;

            case "board-size":
                var count = body["count"]!.GetValue<int>();
                switch (Text(body["boardType"]))
                {
                    case BoardTypes.Final:
                        Same(expect, JsonValue.Create(Word(BoardRules.CheckBoardSize(count))));
                        break;
                    case BoardTypes.Project:
                        Same(expect, JsonValue.Create(Word(BoardRules.CheckProjectSize(count))));
                        break;
                    default:
                        Assert.Fail($"unknown boardType {Show(body["boardType"])}");
                        break;
                }

                Same(expect, JsonValue.Create(Word(BoardRules.CheckSize(Text(body["boardType"]), count))), "CheckSize");
                break;

            case "suggest":
            {
                var youth = Youth(body["youth"]);
                var pick = SchedulerLogic.AutoSelect(youth, Items(body["adults"]).Select(Adult).ToList(), [FreeRoom(youth)],
                    Items(body["waiting"]).Select(Youth).ToList());
                Compare(Proposal(youth, pick), expect);
                break;
            }

            case "fill":
            {
                var youth = Youth(body["youth"]);
                var fill = SchedulerLogic.FillBoard(youth, Items(body["adults"]).Select(Adult).ToList(),
                    Items(body["picked"]).Select(Text).ToList(), Items(body["waiting"]).Select(Youth).ToList());
                Compare(Proposal(youth, fill), expect);
                break;
            }

            case "free-since":
                var since = SchedulerLogic.FreeSinceTimes(
                    Items(body["adults"]).Select(a => (Text(a?["id"]), Text(a?["regTime"]))),
                    Items(body["boards"]).Select(b => (Text(b?["status"]), Text(b?["members"]), Text(b?["lastUpdate"]))));
                var expected = Assert.IsType<JsonObject>(expect);
                Assert.NotEmpty(expected);
                foreach (var (id, time) in expected)
                {
                    Same(time, since.TryGetValue(id, out var got) ? JsonValue.Create(got) : null, id);
                }

                break;

            case "seat-down-the-queue":
                // The procedure in the cases' README: down the queue in order,
                // each youth's proposal weighing every other youth not yet
                // seated; a proposal with no problems seats a board and puts
                // its adults in a room.
                var adults = Items(body["adults"]).Select(Adult).ToList();
                var queue = Items(body["queue"]).Select(Youth).ToList();
                var seated = new HashSet<string>();
                var boards = new Dictionary<string, int> { [BoardTypes.Final] = 0, [BoardTypes.Project] = 0 };
                for (var n = 0; n < queue.Count; n++)
                {
                    var youth = queue[n];
                    var waiting = queue.Where(t => t.Id != youth.Id && !seated.Contains(t.Id)).ToList();
                    var pick = SchedulerLogic.AutoSelect(youth, adults, [FreeRoom(youth)], waiting);
                    if (pick.Problems.Count == 0)
                    {
                        seated.Add(youth.Id);
                        boards[youth.BoardType]++;
                        var room = "R" + n;
                        adults = adults.Select(a => pick.AllAdultIds.Contains(a.Id) ? a with { Room = room } : a).ToList();
                    }
                }

                Same(expect, new JsonObject(boards.Select(b => KeyValuePair.Create(b.Key, (JsonNode?)JsonValue.Create(b.Value)))));
                break;

            case "support-link":
                var linked = body["linked"]!.GetValue<bool>();
                Same(expect, JsonValue.Create(SchedulerLogic.WithSupportLink(Text(body["supporting"]), Text(body["youth"]), linked)));
                break;

            default:
                Assert.Fail($"unknown op {op}");
                break;
        }
    }

    // ---- the cases' vocabulary into Windows' ----

    /// <summary>A string field; one left out (or null) is blank, as the cases' README says.</summary>
    private static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var s) ? s : "";

    private static IEnumerable<JsonNode?> Items(JsonNode? node) => node as JsonArray ?? [];

    private static BoardCandidate Candidate(JsonNode? a) => new(Text(a?["id"]), "", "", Text(a?["unit"]));

    private static AdultInfo Adult(JsonNode? a) =>
        new(Text(a?["id"]), "", "", Text(a?["unit"]), Text(a?["room"]), Text(a?["final"]), Text(a?["project"]),
            FreeSince: Text(a?["freeSince"]), WoodBadge: Text(a?["woodBadge"]), Supporting: Text(a?["supporting"]));

    private static ScoutInfo Youth(JsonNode? y) =>
        new(Text(y?["id"]), "", "", Text(y?["unit"]), Text(y?["boardType"]), "", BoardStatus.Registered, "");

    /// <summary>
    /// Rooms are not part of the cases: auto-select is given one free room of
    /// the youth's kind, so a room shortage never shows up as a problem.
    /// </summary>
    private static RoomInfo FreeRoom(ScoutInfo youth) => new("ROOM:1", "1", youth.BoardType, "");

    private static string Word(SizeVerdict verdict) => verdict switch
    {
        SizeVerdict.TooFew => "too-few",
        SizeVerdict.Ok => "ok",
        SizeVerdict.OverPreferred => "over-preferred",
        SizeVerdict.TooMany => "too-many",
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, null),
    };

    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());

    /// <summary>
    /// Windows words its shortages its own way, so they are compared as the
    /// cases' structures. A message this does not know is a failure.
    /// </summary>
    private static JsonObject Proposal(ScoutInfo youth, AutoSelection pick)
    {
        Assert.True(pick.ChairIds.Count <= 1, $"more than one chair: {string.Join(", ", pick.ChairIds)}");
        var chair = pick.ChairIds.FirstOrDefault();
        Assert.True(chair == null || !pick.MemberIds.Contains(chair), $"the chair {chair} is also a member");

        var type = Regex.Escape(youth.BoardType);
        var problems = new JsonArray();
        foreach (var text in pick.Problems)
        {
            if (Regex.IsMatch(text, $@"^No {type} Chairs Available\.$"))
            {
                problems.Add(new JsonObject { ["kind"] = "no-chair" });
            }
            else if (Regex.Match(text, $@"^Only (\d+) {type} Members Available$") is { Success: true } only)
            {
                problems.Add(new JsonObject { ["kind"] = "too-few-members", ["available"] = int.Parse(only.Groups[1].Value) });
            }
            else
            {
                Assert.Fail($"unknown problem message \"{text}\"");
            }
        }

        return new JsonObject
        {
            ["chair"] = chair,
            ["members"] = Strings(pick.MemberIds),
            ["problems"] = problems,
        };
    }

    /// <summary>
    /// Only the keys the case names are compared; any other key, or a problem
    /// kind the README does not define, is a mistake in the case.
    /// </summary>
    private static void Compare(JsonObject got, JsonNode? expect)
    {
        var expected = Assert.IsType<JsonObject>(expect);
        foreach (var problem in Items(expected["problems"]))
        {
            Assert.True(ProblemKinds.Contains(Text(problem?["kind"])), $"unknown problem kind {Show(problem?["kind"])} in the case");
        }

        foreach (var (key, want) in expected)
        {
            Assert.True(ProposalKeys.Contains(key), $"unknown key \"{key}\" in expect");
            Same(want, got[key], key);
        }
    }

    private static void Same(JsonNode? expected, JsonNode? actual, string what = "result") =>
        Assert.True(JsonNode.DeepEquals(expected, actual), $"{what}: expected {Show(expected)}, got {Show(actual)}");

    private static string Show(JsonNode? node) => node?.ToJsonString() ?? "null";
}
