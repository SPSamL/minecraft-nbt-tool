using System.Globalization;
using System.Text;
using minecraft_nbt_tool.Models;

namespace minecraft_nbt_tool.Export;

internal static class CsvExporter
{
    /// <summary>
    /// Write reports.
    /// </summary>
    public static void WriteReports(BlueprintScanReport report, string prefixPath)
    {
        var prefix = Path.GetFullPath(prefixPath);
        var basePath = Path.ChangeExtension(prefix, null) ?? prefix;
        var directory = Path.GetDirectoryName(basePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(basePath + ".block-counts.csv", BuildBlockCountsCsv(report), Encoding.UTF8);
        File.WriteAllText(basePath + ".unique-blocks.csv", BuildUniqueBlocksCsv(report), Encoding.UTF8);
    }

    /// <summary>
    /// Build block counts csv.
    /// </summary>
    private static string BuildBlockCountsCsv(BlueprintScanReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("style,buildingType,level,fileName,relativePath,blockName,mod,count");

        foreach (var file in report.Files)
        {
            foreach (var block in file.Blocks)
            {
                builder.AppendLine(string.Join(",", new[]
                {
                    Escape(file.Style),
                    Escape(file.BuildingType),
                    Escape(file.Level?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
                    Escape(file.FileName),
                    Escape(file.RelativePath),
                    Escape(block.Name),
                    Escape(block.Mod),
                    Escape(block.Count.ToString(CultureInfo.InvariantCulture))
                }));
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Build unique blocks csv.
    /// </summary>
    private static string BuildUniqueBlocksCsv(BlueprintScanReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("style,buildingType,level,blockName,mod,count");

        foreach (var styleGroup in report.UniqueBlocksByGroup)
        {
            foreach (var buildingGroup in styleGroup.Buildings)
            {
                foreach (var levelGroup in buildingGroup.Levels)
                {
                    foreach (var block in levelGroup.Blocks)
                    {
                        builder.AppendLine(string.Join(",", new[]
                        {
                            Escape(styleGroup.Style),
                            Escape(buildingGroup.BuildingType),
                            Escape(levelGroup.Level?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
                            Escape(block.Name),
                            Escape(block.Mod),
                            Escape(block.Count.ToString(CultureInfo.InvariantCulture))
                        }));
                    }
                }
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Escape.
    /// </summary>
    private static string Escape(string value)
    {
        return value.Contains('"', StringComparison.Ordinal) || value.Contains(',', StringComparison.Ordinal) || value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal)
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }
}
