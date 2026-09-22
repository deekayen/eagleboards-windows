using EagleBoards.Core;
using EagleBoards.Core.Records;

namespace EagleBoards.Tests;

/// <summary>Tests that read or move <see cref="DataRecord.Clock"/> run one at a time.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ClockCollection
{
    public const string Name = "clock";
}

/// <summary>A throwaway data folder with synthetic names only. Never real data.</summary>
public sealed class Sandbox : IDisposable
{
    public Sandbox()
    {
        Root = Path.Combine(Path.GetTempPath(), "eb-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        HistoryPath = Path.Combine(Root, "AdultHistory.csv");
        File.WriteAllText(HistoryPath, string.Join(',', AdultRecord.AllColumns) + "\n");
        ConfigPath = Path.Combine(Root, "config.properties");
        File.WriteAllText(ConfigPath, "Type=CONFIG\nID=DEFAULT\nName=DEFAULT\n");
    }

    public string Root { get; }

    public string HistoryPath { get; }

    public string ConfigPath { get; }

    public string DataDir => Path.Combine(Root, "run");

    public BoardService Open() => BoardService.Open(new EventOptions
    {
        DataDirectory = DataDir,
        AdultHistoryPath = HistoryPath,
        ConfigPath = ConfigPath,
    });

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal static class Seed
{
    public static Dictionary<string, string> Scout(string last, string first, string unit, string boardType, string email = "") => new()
    {
        ["Last"] = last,
        ["First"] = first,
        ["UnitType"] = "Troop",
        ["Unit"] = unit,
        ["BoardType"] = boardType,
        ["Email"] = email,
    };

    public static Dictionary<string, string> Adult(string last, string first, string unit, string project, string final) => new()
    {
        ["Last"] = last,
        ["First"] = first,
        ["UnitType"] = "Troop",
        ["Unit"] = unit,
        ["ProjectReview"] = project,
        ["FinalBoard"] = final,
        ["Email"] = first.ToLowerInvariant() + "@example.org",
    };

    public static string ScoutId(string last, string first, string unit) => $"SCOUT:{last}:{first}:{unit}";

    public static string AdultId(string last, string first, string unit) => $"ADULT:{last}:{first}:{unit}";
}
