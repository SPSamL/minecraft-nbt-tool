using System.Globalization;

namespace minecraft_nbt_tool.Cli;

/// <summary>
/// Represents parsed command line arguments for the scanner application.
/// </summary>
internal sealed record CliOptions(
    string Command,
    string RootPath,
    string? OutputPath,
    string? CsvPath,
    string? StyleFilter,
    string? BuildingFilter,
    int? LevelFilter,
    int Port,
    bool ShowHelp)
{
    /// <summary>
    /// Parses raw CLI arguments into a normalized <see cref="CliOptions"/> instance.
    /// </summary>
    public static CliOptions Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return new CliOptions("scan", Directory.GetCurrentDirectory(), null, null, null, null, null, 5050, true);
        }

        var command = args[0].Trim().ToLowerInvariant();
        if (command is "help" or "--help" or "-h")
        {
            return new CliOptions("scan", Directory.GetCurrentDirectory(), null, null, null, null, null, 5050, true);
        }

        var rootPath = args.Length > 1 && !args[1].StartsWith('-')
            ? args[1]
            : Directory.GetCurrentDirectory();

        string? outputPath = null;
        string? csvPath = null;
        string? styleFilter = null;
        string? buildingFilter = null;
        int? levelFilter = null;
        var port = 5050;

        for (var index = 1; index < args.Length; index++)
        {
            var current = args[index];
            if (!current.StartsWith('-'))
            {
                continue;
            }

            var value = index + 1 < args.Length ? args[index + 1] : null;
            switch (current.ToLowerInvariant())
            {
                case "--output":
                case "-o":
                    if (value is not null)
                    {
                        outputPath = value;
                        index++;
                    }
                    break;
                case "--csv":
                    if (value is not null)
                    {
                        csvPath = value;
                        index++;
                    }
                    break;
                case "--style":
                    if (value is not null)
                    {
                        styleFilter = value;
                        index++;
                    }
                    break;
                case "--building":
                    if (value is not null)
                    {
                        buildingFilter = value;
                        index++;
                    }
                    break;
                case "--level":
                    if (value is not null && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLevel))
                    {
                        levelFilter = parsedLevel;
                        index++;
                    }
                    break;
                case "--port":
                    if (value is not null && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPort))
                    {
                        port = parsedPort;
                        index++;
                    }
                    break;
                case "--help":
                case "-h":
                    return new CliOptions(command, rootPath, outputPath, csvPath, styleFilter, buildingFilter, levelFilter, port, true);
            }
        }

        return new CliOptions(command, rootPath, outputPath, csvPath, styleFilter, buildingFilter, levelFilter, port, false);
    }
}
