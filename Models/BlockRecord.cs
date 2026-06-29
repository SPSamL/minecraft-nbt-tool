using System.Text.Json.Serialization;

/// <summary>
/// Represents a normalized block identity and count.
/// </summary>
internal sealed class BlockRecord
{
    [JsonIgnore]
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string Mod { get; set; } = string.Empty;
    public int Count { get; set; }

    /// <summary>
    /// Returns a copy of this record with a new count value.
    /// </summary>
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
