/// <summary>
/// Represents per-file scan output including grouped metadata and block counts.
/// </summary>
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
