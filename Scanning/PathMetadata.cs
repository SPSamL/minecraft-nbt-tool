using System.Globalization;
using System.Text.RegularExpressions;

/// <summary>
/// Extracts style, building type, and level metadata from paths and filenames.
/// </summary>
internal static class PathMetadata
{
    private static readonly Regex LevelSuffix = new(@"^(?<base>.*?)(?<level>\d+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Resolves the style group from the first directory segment under the root.
    /// </summary>
    public static string GetStyle(string rootPath, string filePath)
    {
        var relative = Path.GetRelativePath(rootPath, filePath);
        var parts = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 ? parts[0] : "unknown";
    }

    /// <summary>
    /// Resolves the building type from filename stem, removing trailing level digits.
    /// </summary>
    public static string GetBuildingType(string filePath)
    {
        var stem = Path.GetFileNameWithoutExtension(filePath);
        var match = LevelSuffix.Match(stem);
        return match.Success ? match.Groups["base"].Value : stem;
    }

    /// <summary>
    /// Resolves the optional level from trailing filename digits.
    /// </summary>
    public static int? GetLevel(string filePath)
    {
        var stem = Path.GetFileNameWithoutExtension(filePath);
        var match = LevelSuffix.Match(stem);
        return match.Success && int.TryParse(match.Groups["level"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var level)
            ? level
            : null;
    }
}
