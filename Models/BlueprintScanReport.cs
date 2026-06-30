namespace minecraft_nbt_tool.Models;

/// <summary>
/// Root result model produced by a blueprint scan.
/// </summary>
internal sealed class BlueprintScanReport
{
    public string RootPath { get; set; } = string.Empty;
    public List<BlueprintFileReport> Files { get; set; } = [];
    public List<GroupedBlueprintReport> BlockCountsByBlueprint { get; set; } = [];
    public List<UniqueBlockGroupReport> UniqueBlocksByGroup { get; set; } = [];
}
