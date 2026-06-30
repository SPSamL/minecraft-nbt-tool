using minecraft_nbt_tool.Models;

/// <summary>
/// Tree node used for hierarchical folder display in the web UI.
/// </summary>
internal sealed class FolderNode
{
    public FolderNode(string name)
    {
        Name = name;
    }

    public string Name { get; }
    public Dictionary<string, FolderNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<BlueprintFileReport> Files { get; } = [];
}
