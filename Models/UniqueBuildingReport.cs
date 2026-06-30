namespace minecraft_nbt_tool.Models;

/// <summary>
/// Unique block counts grouped by building type.
/// </summary>
internal sealed class UniqueBuildingReport
{
    public string BuildingType { get; set; } = string.Empty;
    public List<UniqueLevelReport> Levels { get; set; } = [];
}
