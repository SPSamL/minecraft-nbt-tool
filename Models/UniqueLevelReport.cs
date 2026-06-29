/// <summary>
/// Unique blocks for a single level grouping.
/// </summary>
internal sealed class UniqueLevelReport
{
    public int? Level { get; set; }
    public List<BlockRecord> Blocks { get; set; } = [];
}
