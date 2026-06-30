namespace minecraft_nbt_tool.Models;

/// <summary>
/// File collection grouped by optional level value.
/// </summary>
internal sealed class LevelGroupReport
{
    public int? Level { get; set; }
    public List<BlueprintFileReport> Files { get; set; } = [];
}
