/// <summary>
/// Block counts grouped by style and then by building.
/// </summary>
internal sealed class GroupedBlueprintReport
{
    public string Style { get; set; } = string.Empty;
    public List<BuildingGroupReport> Buildings { get; set; } = [];
}
