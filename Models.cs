using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed class BlueprintScanReport
{
    public string RootPath { get; set; } = string.Empty;
    public List<BlueprintFileReport> Files { get; set; } = [];
    public List<GroupedBlueprintReport> BlockCountsByBlueprint { get; set; } = [];
    public List<UniqueBlockGroupReport> UniqueBlocksByGroup { get; set; } = [];
}

internal sealed class BlueprintFileReport
{
    public string FileName { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string Style { get; set; } = string.Empty;
    public string BuildingType { get; set; } = string.Empty;
    public int? Level { get; set; }
    public string? Error { get; set; }
    public List<BlockRecord> Blocks { get; set; } = [];
}

internal sealed class BlockRecord
{
    [JsonIgnore]
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string Mod { get; set; } = string.Empty;
    public int Count { get; set; }

    public BlockRecord WithCount(int count)
    {
        return new BlockRecord
        {
            Key = Key,
            Name = Name,
            Id = Id,
            Mod = Mod,
            Count = count
        };
    }
}

internal sealed class GroupedBlueprintReport
{
    public string Style { get; set; } = string.Empty;
    public List<BuildingGroupReport> Buildings { get; set; } = [];
}

internal sealed class BuildingGroupReport
{
    public string BuildingType { get; set; } = string.Empty;
    public List<LevelGroupReport> Levels { get; set; } = [];
}

internal sealed class LevelGroupReport
{
    public int? Level { get; set; }
    public List<BlueprintFileReport> Files { get; set; } = [];
}

internal sealed class UniqueBlockGroupReport
{
    public string Style { get; set; } = string.Empty;
    public List<UniqueBuildingReport> Buildings { get; set; } = [];
}

internal sealed class UniqueBuildingReport
{
    public string BuildingType { get; set; } = string.Empty;
    public List<UniqueLevelReport> Levels { get; set; } = [];
}

internal sealed class UniqueLevelReport
{
    public int? Level { get; set; }
    public List<BlockRecord> Blocks { get; set; } = [];
}

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}