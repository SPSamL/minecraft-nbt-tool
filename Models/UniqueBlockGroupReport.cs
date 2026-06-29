/// <summary>
/// Unique block counts grouped by style.
/// </summary>
internal sealed class UniqueBlockGroupReport
{
    public string Style { get; set; } = string.Empty;
    public List<UniqueBuildingReport> Buildings { get; set; } = [];
}
