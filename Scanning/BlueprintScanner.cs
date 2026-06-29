using SharpNBT;
using SharpNBT.SNBT;

/// <summary>
/// Scans blueprint inputs and produces grouped reports.
/// </summary>
internal sealed class BlueprintScanner
{
    private static readonly string[] SupportedExtensions = [".nbt", ".blueprint", ".dat", ".schem", ".schematic", ".snbt"];

    /// <summary>
    /// Executes a scan for the provided root path and applies CLI filters.
    /// </summary>
    public BlueprintScanReport Scan(string rootPath, CliOptions options)
    {
        var inputRoots = SplitInputRoots(rootPath);
        var effectiveRoot = DetermineEffectiveRoot(rootPath, inputRoots);

        var filePaths = EnumerateInputFiles(rootPath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var files = ScanFiles(filePaths, effectiveRoot);

        var filtered = files
            .Where(file => file is not null)
            .Cast<BlueprintFileReport>()
            .Where(file => MatchesFilter(file.Style, options.StyleFilter)
                && MatchesFilter(file.BuildingType, options.BuildingFilter)
                && (!options.LevelFilter.HasValue || file.Level == options.LevelFilter.Value))
            .ToList();

        var grouped = GroupReports(filtered);
        var uniqueBlocks = BuildUniqueBlocks(grouped);

        return new BlueprintScanReport
        {
            RootPath = effectiveRoot,
            Files = filtered,
            UniqueBlocksByGroup = uniqueBlocks,
            BlockCountsByBlueprint = grouped
        };
    }

    /// <summary>
    /// Scans all input files, using parallel execution when beneficial.
    /// </summary>
    private static List<BlueprintFileReport> ScanFiles(IReadOnlyList<string> filePaths, string effectiveRoot)
    {
        if (filePaths.Count <= 1)
        {
            return filePaths.Select(path => ScanFile(effectiveRoot, path)).ToList();
        }

        var results = new BlueprintFileReport[filePaths.Count];
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount)
        };

        Parallel.For(0, filePaths.Count, options, index =>
        {
            results[index] = ScanFile(effectiveRoot, filePaths[index]);
        });

        return results.ToList();
    }

    /// <summary>
    /// Enumerates supported files from one or more input roots.
    /// </summary>
    private static IEnumerable<string> EnumerateInputFiles(string rootPath)
    {
        foreach (var input in SplitInputRoots(rootPath))
        {
            if (File.Exists(input))
            {
                var ext = Path.GetExtension(input);
                if (SupportedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                {
                    yield return Path.GetFullPath(input);
                }

                continue;
            }

            if (Directory.Exists(input))
            {
                foreach (var filePath in Directory.EnumerateFiles(input, "*.*", SearchOption.AllDirectories)
                             .Where(path => SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)))
                {
                    yield return filePath;
                }
            }
        }
    }

    /// <summary>
    /// Splits a pipe-delimited root path into normalized absolute paths.
    /// </summary>
    private static List<string> SplitInputRoots(string rootPath)
    {
        return rootPath
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Computes a stable common root used for relative path generation.
    /// </summary>
    private static string DetermineEffectiveRoot(string originalRootPath, IReadOnlyList<string> inputRoots)
    {
        if (inputRoots.Count == 0)
        {
            return Path.GetFullPath(originalRootPath);
        }

        if (inputRoots.Count == 1)
        {
            return inputRoots[0];
        }

        var expandedRoots = inputRoots
            .Select(path => File.Exists(path) ? Path.GetDirectoryName(path) ?? path : path)
            .Select(Path.GetFullPath)
            .ToList();

        var firstRoot = expandedRoots[0].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var common = firstRoot;

        foreach (var root in expandedRoots.Skip(1))
        {
            common = GetCommonDirectoryPrefix(common, root);
            if (string.IsNullOrEmpty(common))
            {
                break;
            }
        }

        return string.IsNullOrEmpty(common) ? firstRoot : common;
    }

    /// <summary>
    /// Returns the shared directory prefix for two absolute paths.
    /// </summary>
    private static string GetCommonDirectoryPrefix(string left, string right)
    {
        var leftParts = left.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        var rightParts = right.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        var count = Math.Min(leftParts.Length, rightParts.Length);
        var matched = 0;

        while (matched < count && string.Equals(leftParts[matched], rightParts[matched], StringComparison.OrdinalIgnoreCase))
        {
            matched += 1;
        }

        if (matched == 0)
        {
            return string.Empty;
        }

        var root = Path.GetPathRoot(left) ?? string.Empty;
        var relative = string.Join(Path.DirectorySeparatorChar, leftParts.Take(matched));
        return string.IsNullOrEmpty(root) ? relative : Path.Combine(root, relative);
    }

    /// <summary>
    /// Scans one file and captures block extraction or parse errors.
    /// </summary>
    private static BlueprintFileReport ScanFile(string rootPath, string filePath)
    {
        try
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            var root = ext == ".snbt"
                ? ParseSnbt(filePath)
                : ParseBinary(filePath);

            var blocks = BlueprintBlockExtractor.Extract(root)
                .OrderBy(block => block.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return BuildReport(rootPath, filePath, blocks);
        }
        catch (Exception ex)
        {
            return new BlueprintFileReport
            {
                RelativePath = Path.GetRelativePath(rootPath, filePath),
                Style = PathMetadata.GetStyle(rootPath, filePath),
                BuildingType = PathMetadata.GetBuildingType(filePath),
                Level = PathMetadata.GetLevel(filePath),
                FileName = Path.GetFileName(filePath),
                Error = ex.Message,
                Blocks = []
            };
        }
    }

    /// <summary>
    /// Builds a successful per-file report instance.
    /// </summary>
    private static BlueprintFileReport BuildReport(string rootPath, string filePath, List<BlockRecord> blocks)
    {
        return new BlueprintFileReport
        {
            RelativePath = Path.GetRelativePath(rootPath, filePath),
            Style = PathMetadata.GetStyle(rootPath, filePath),
            BuildingType = PathMetadata.GetBuildingType(filePath),
            Level = PathMetadata.GetLevel(filePath),
            FileName = Path.GetFileName(filePath),
            Blocks = blocks
        };
    }

    /// <summary>
    /// Evaluates a case-insensitive equality filter with empty-as-match behavior.
    /// </summary>
    private static bool MatchesFilter(string? candidate, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        return string.Equals(candidate, filter, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Groups flat file reports by style, building type, and level.
    /// </summary>
    private static List<GroupedBlueprintReport> GroupReports(List<BlueprintFileReport> files)
    {
        return files
            .GroupBy(file => file.Style, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(styleGroup => new GroupedBlueprintReport
            {
                Style = styleGroup.Key,
                Buildings = styleGroup
                    .GroupBy(file => file.BuildingType, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(buildingGroup => new BuildingGroupReport
                    {
                        BuildingType = buildingGroup.Key,
                        Levels = buildingGroup
                            .GroupBy(file => file.Level)
                            .OrderBy(group => group.Key ?? int.MaxValue)
                            .Select(levelGroup => new LevelGroupReport
                            {
                                Level = levelGroup.Key,
                                Files = levelGroup.OrderBy(file => file.FileName, StringComparer.OrdinalIgnoreCase).ToList()
                            })
                            .ToList()
                    })
                    .ToList()
            })
            .ToList();
    }

    /// <summary>
    /// Builds unique block summaries from grouped file results.
    /// </summary>
    private static List<UniqueBlockGroupReport> BuildUniqueBlocks(List<GroupedBlueprintReport> groups)
    {
        var result = new List<UniqueBlockGroupReport>();

        foreach (var styleGroup in groups)
        {
            var styleReport = new UniqueBlockGroupReport
            {
                Style = styleGroup.Style,
                Buildings = []
            };

            foreach (var buildingGroup in styleGroup.Buildings)
            {
                var buildingReport = new UniqueBuildingReport
                {
                    BuildingType = buildingGroup.BuildingType,
                    Levels = []
                };

                foreach (var levelGroup in buildingGroup.Levels)
                {
                    var counts = levelGroup.Files
                        .SelectMany(file => file.Blocks)
                        .GroupBy(block => block.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(group => group.First().WithCount(group.Sum(item => item.Count)))
                        .OrderBy(block => block.Name, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    buildingReport.Levels.Add(new UniqueLevelReport
                    {
                        Level = levelGroup.Level,
                        Blocks = counts
                    });
                }

                styleReport.Buildings.Add(buildingReport);
            }

            result.Add(styleReport);
        }

        return result;
    }

    /// <summary>
    /// Attempts to parse binary NBT content using known format options.
    /// </summary>
    private static object ParseBinary(string filePath)
    {
        foreach (var options in new[] { FormatOptions.Java, FormatOptions.BedrockFile, FormatOptions.BedrockNetwork })
        {
            try
            {
                return NbtFile.Read(filePath, options, CompressionType.AutoDetect);
            }
            catch
            {
                // Try the next format.
            }
        }

        throw new InvalidDataException($"Unable to parse binary NBT file: {filePath}");
    }

    /// <summary>
    /// Parses SNBT text, falling back to list parsing when needed.
    /// </summary>
    private static object ParseSnbt(string filePath)
    {
        var text = File.ReadAllText(filePath);

        try
        {
            return StringNbt.Parse(text);
        }
        catch
        {
            return StringNbt.ParseList(text);
        }
    }
}
