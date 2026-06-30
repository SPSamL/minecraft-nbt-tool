namespace minecraft_nbt_tool.Models;

/// <summary>
/// Block counts grouped by building type.
/// </summary>
internal sealed class BuildingGroupReport
{
    public string BuildingType { get; set; } = string.Empty;
    public List<LevelGroupReport> Levels { get; set; } = [];
}
