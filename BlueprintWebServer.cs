using System.Net;
using System.Text;
using System.Globalization;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;

internal static class BlueprintWebServer
{
    private static readonly string[] SupportedExtensions = [".nbt", ".blueprint", ".dat", ".schem", ".schematic", ".snbt"];
    private static readonly TimeSpan ScanCacheTtl = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan HotCacheValidationInterval = TimeSpan.FromSeconds(8);
    private const int MaxInMemoryScanCacheEntries = 8;
    private static readonly ConcurrentDictionary<string, CachedScanReport> ScanCache = new(StringComparer.OrdinalIgnoreCase);

    public static async Task RunAsync(string rootPath, int port)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();

        Console.WriteLine($"Open http://localhost:{port}/");

        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (HttpListenerException)
            {
                break;
            }

            _ = Task.Run(() => HandleRequestAsync(context, rootPath, port));
        }
    }

    private static async Task HandleRequestAsync(HttpListenerContext context, string rootPath, int port)
    {
        try
        {
            var requestPath = context.Request.Url?.AbsolutePath ?? "/";
            if (requestPath.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = 204;
                return;
            }

            var query = ParseQuery(context.Request.Url?.Query ?? string.Empty);
            var styleRaw = query.TryGetValue("style", out var styleValue) ? styleValue : null;
            var buildingRaw = query.TryGetValue("building", out var buildingValue) ? buildingValue : null;
            var levelRaw = query.TryGetValue("level", out var levelValue) ? levelValue : null;
            var localPath = query.TryGetValue("localPath", out var localPathValue) ? localPathValue : null;
            var repoUrl = query.TryGetValue("repoUrl", out var repoUrlValue) ? repoUrlValue : null;
            var clearCacheRequested = query.TryGetValue("clearCache", out var clearCacheValue)
                && string.Equals(clearCacheValue, "1", StringComparison.OrdinalIgnoreCase);

            var selectedStyles = ParseMultiValues(styleRaw);
            var selectedBuildings = ParseMultiValues(buildingRaw);
            var selectedLevels = ParseMultiValues(levelRaw)
                .Select(value => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : (int?)null)
                .Where(value => value.HasValue)
                .Select(value => value!.Value)
                .ToHashSet();

            string? cacheMessage = null;
            if (clearCacheRequested)
            {
                ScanInputResolver.TryClearCache(out var clearMessage);
                cacheMessage = clearMessage;
                ScanCache.Clear();
                TryClearPersistentScanCache();
            }

            var source = await ScanInputResolver.ResolveAsync(rootPath, localPath, repoUrl);
            var sourceKey = NormalizeSourceKey(source.ScanPath);

            var scanStopwatch = Stopwatch.StartNew();
            var cacheStatus = "Re-scanned";
            BlueprintScanReport report;
            if (TryGetHotCachedScanReport(sourceKey, out var hotCachedReport))
            {
                cacheStatus = "Memory cache hit";
                report = hotCachedReport;
            }
            else
            {
                var sourceFingerprint = ComputeSourceFingerprint(source.ScanPath);
                if (TryGetCachedScanReport(sourceKey, sourceFingerprint, out var cachedReport, out var cacheLayer))
                {
                    cacheStatus = cacheLayer;
                    report = cachedReport;
                }
                else
                {
                    var scanner = new BlueprintScanner();
                    report = scanner.Scan(source.ScanPath, new CliOptions("scan", source.ScanPath, null, null, null, null, null, port, false));
                    SetCachedScanReport(sourceKey, sourceFingerprint, report);
                }
            }
            scanStopwatch.Stop();

            var scanStatus = $"{cacheStatus} ({scanStopwatch.ElapsedMilliseconds} ms)";

            var filteredFiles = report.Files
                .Where(file => selectedStyles.Count == 0 || selectedStyles.Contains(file.Style))
                .Where(file => selectedBuildings.Count == 0 || selectedBuildings.Contains(file.BuildingType))
                .Where(file => selectedLevels.Count == 0 || (file.Level.HasValue && selectedLevels.Contains(file.Level.Value)))
                .ToList();

            if (requestPath.Equals("/export", StringComparison.OrdinalIgnoreCase))
            {
                var format = query.TryGetValue("format", out var requestedFormat) ? requestedFormat : "json";
                await HandleExportAsync(context.Response, format, filteredFiles, source.DisplayPath, selectedStyles, selectedBuildings, selectedLevels);
                return;
            }

            var combinedWarning = JoinMessages(source.Warning, cacheMessage);
            var html = BuildHtml(report, filteredFiles, rootPath, selectedStyles, selectedBuildings, selectedLevels, localPath, repoUrl, source.DisplayPath, combinedWarning, scanStatus, port);

            await WriteStringAsync(context.Response, html, "text/html; charset=utf-8");
        }
        catch (Exception ex)
        {
            await WriteStringAsync(context.Response, $"<pre>{WebUtility.HtmlEncode(ex.ToString())}</pre>", "text/html; charset=utf-8", 500);
        }
        finally
        {
            context.Response.OutputStream.Close();
        }
    }

    private static string BuildHtml(
        BlueprintScanReport report,
        List<BlueprintFileReport> filteredFiles,
        string rootPath,
        HashSet<string> selectedStyles,
        HashSet<string> selectedBuildings,
        HashSet<int> selectedLevels,
        string? localPath,
        string? repoUrl,
        string resolvedPath,
        string? warning,
        string scanStatus,
        int port)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<!doctype html>");
        builder.AppendLine("<html lang=\"en\">");
        builder.AppendLine("<head>");
        builder.AppendLine("<meta charset=\"utf-8\" />");
        builder.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />");
        builder.AppendLine("<title>Minecraft Blueprint Scanner</title>");
        builder.AppendLine("<style>");
        builder.AppendLine(StyleBlock());
        builder.AppendLine("</style>");
        builder.AppendLine("</head>");
        builder.AppendLine("<body>");
        builder.AppendLine("<header>");
        builder.AppendLine("<h1>Minecraft Blueprint Scanner</h1>");
        builder.AppendLine("<div class=\"sub\">Provide either a local folder/file path or a GitHub folder URL. The scanner resolves it to a local path, then groups output by style, building type, and level.</div>");
        builder.AppendLine("</header>");
        builder.AppendLine("<main>");

        var allStyles = report.Files
            .Select(file => file.Style)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var allBuildings = report.Files
            .Select(file => file.BuildingType)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var allLevels = report.Files
            .Where(file => file.Level.HasValue)
            .Select(file => file.Level!.Value)
            .Distinct()
            .OrderBy(value => value)
            .ToList();

        builder.AppendLine("<form class=\"toolbar\" method=\"get\">");
        builder.AppendLine($"<label>GitHub folder URL<input name=\"repoUrl\" value=\"{WebUtility.HtmlEncode(repoUrl ?? string.Empty)}\" placeholder=\"https://github.com/owner/repo/tree/branch/path\" /></label>");
        builder.AppendLine($"<label>Local folder or file<input id=\"local-path\" name=\"localPath\" value=\"{WebUtility.HtmlEncode(localPath ?? string.Empty)}\" placeholder=\"Enter local folder path or | separated file paths\" /></label>");
        builder.AppendLine("<div class=\"top-export-actions\"><button id=\"export-json-btn\" type=\"button\" class=\"secondary-btn\">Export JSON</button><button id=\"export-csv-btn\" type=\"button\" class=\"secondary-btn\">Export CSV</button></div>");
        builder.AppendLine($"<input type=\"hidden\" id=\"style-hidden\" name=\"style\" value=\"{WebUtility.HtmlEncode(string.Join(',', selectedStyles.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)))}\" />");
        builder.AppendLine($"<input type=\"hidden\" id=\"building-hidden\" name=\"building\" value=\"{WebUtility.HtmlEncode(string.Join(',', selectedBuildings.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)))}\" />");
        builder.AppendLine($"<input type=\"hidden\" id=\"level-hidden\" name=\"level\" value=\"{WebUtility.HtmlEncode(string.Join(',', selectedLevels.OrderBy(value => value).Select(value => value.ToString(CultureInfo.InvariantCulture))))}\" />");

        builder.AppendLine("<div class=\"top-filter-controls\">");
        builder.AppendLine("<div class=\"control-row\"><label class=\"control\">Style<select id=\"top-filter-style-select\"><option value=\"\">Select style...</option>");
        foreach (var option in allStyles)
        {
            builder.AppendLine($"<option value=\"{WebUtility.HtmlEncode(option)}\">{WebUtility.HtmlEncode(option)}</option>");
        }
        builder.AppendLine("</select></label><button id=\"top-add-style\" type=\"button\" class=\"add-filter-btn\">Add</button></div>");

        builder.AppendLine("<div class=\"control-row\"><label class=\"control\">Building<select id=\"top-filter-building-select\"><option value=\"\">Select building...</option>");
        foreach (var option in allBuildings)
        {
            builder.AppendLine($"<option value=\"{WebUtility.HtmlEncode(option)}\">{WebUtility.HtmlEncode(option)}</option>");
        }
        builder.AppendLine("</select></label><button id=\"top-add-building\" type=\"button\" class=\"add-filter-btn\">Add</button></div>");

        builder.AppendLine("<div class=\"control-row\"><label class=\"control\">Level<select id=\"top-filter-level-select\"><option value=\"\">Select level...</option>");
        foreach (var option in allLevels)
        {
            builder.AppendLine($"<option value=\"{option}\">{option}</option>");
        }
        builder.AppendLine("</select></label><button id=\"top-add-level\" type=\"button\" class=\"add-filter-btn\">Add</button></div>");
        builder.AppendLine("</div>");

        builder.AppendLine("<div class=\"top-filter-tags\">");
        builder.AppendLine("<div class=\"tag-group\"><div class=\"tag-group-title\">Selected Styles</div><div id=\"top-selected-style-tags\" class=\"selected-tags\"></div></div>");
        builder.AppendLine("<div class=\"tag-group\"><div class=\"tag-group-title\">Selected Buildings</div><div id=\"top-selected-building-tags\" class=\"selected-tags\"></div></div>");
        builder.AppendLine("<div class=\"tag-group\"><div class=\"tag-group-title\">Selected Levels</div><div id=\"top-selected-level-tags\" class=\"selected-tags\"></div></div>");
        builder.AppendLine("</div>");

        builder.AppendLine("<div class=\"toolbar-actions\"><button type=\"submit\">Refresh</button><button id=\"top-clear-filters\" type=\"button\" class=\"secondary-btn\">Clear filters</button><button type=\"submit\" name=\"clearCache\" value=\"1\" class=\"secondary-btn\">Clear cache</button></div>");
        builder.AppendLine("</form>");

        if (!string.IsNullOrWhiteSpace(warning))
        {
            builder.AppendLine($"<div class=\"warning\">{WebUtility.HtmlEncode(warning)}</div>");
        }

        builder.AppendLine("<section class=\"panel\">");
        builder.AppendLine("<div class=\"summary\">");
        builder.AppendLine($"<span class=\"pill\">Files: {filteredFiles.Count}</span>");
        builder.AppendLine($"<span class=\"pill\">Building Categories: {filteredFiles.Select(file => file.Style).Distinct(StringComparer.OrdinalIgnoreCase).Count()}</span>");
        builder.AppendLine($"<span class=\"pill\">Resolved path: {WebUtility.HtmlEncode(resolvedPath)}</span>");
        builder.AppendLine($"<span class=\"pill\">{WebUtility.HtmlEncode(scanStatus)}</span>");
        builder.AppendLine($"<span class=\"pill\">Port: {port}</span>");
        builder.AppendLine("</div>");

        var totalDistinctBlocks = filteredFiles
            .SelectMany(file => file.Blocks)
            .Select(block => block.Key)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var totalBuildings = filteredFiles
            .Select(file => file.BuildingType)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var totalLevels = filteredFiles
            .Where(file => file.Level.HasValue)
            .Select(file => file.Level!.Value)
            .Distinct()
            .Count();

        var folderTree = BuildFolderTree(filteredFiles);
        var showUnique = filteredFiles.Count > 1;
        var uniqueRows = showUnique ? BuildUniqueMinimumLevelRows(filteredFiles) : [];
        builder.AppendLine(showUnique ? "<div class=\"grid\">" : "<div class=\"grid single\">");
        builder.AppendLine("<div id=\"counts-section\" class=\"card\">");
        builder.AppendLine("<h2 id=\"counts-title\" class=\"section-toggle\" role=\"button\" tabindex=\"0\" aria-expanded=\"true\">Block Requirements</h2>");
        builder.AppendLine($"<div class=\"card-summary\"><span class=\"summary-pill\">Buildings: {totalBuildings}</span><span class=\"summary-pill\">Levels: {totalLevels}</span><span class=\"summary-pill\">Unique Blocks: {totalDistinctBlocks}</span></div>");
        AppendBlockCounts(builder, folderTree);
        builder.AppendLine("</div>");
        if (showUnique)
        {
            builder.AppendLine("<div id=\"unique-section\" class=\"card\">");
            builder.AppendLine("<div class=\"card-title-row\"><h2>Unique blocks</h2><button id=\"unique-clear\" type=\"button\" class=\"clear-btn\">Clear filters and sort</button></div>");
            var uniqueMods = uniqueRows
                .Select(row => row.Mod)
                .Where(mod => !string.IsNullOrWhiteSpace(mod))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            builder.AppendLine($"<div class=\"card-summary\"><span class=\"summary-pill\">Unique Blocks: {uniqueRows.Count}</span><span class=\"summary-pill\">Mods: {uniqueMods}</span></div>");
            AppendUniqueControls(builder, uniqueRows);
            AppendUniqueBlocks(builder, uniqueRows);
            builder.AppendLine("</div>");
        }

        builder.AppendLine("</div>");
        builder.AppendLine("</section>");
        builder.AppendLine("</main>");
        builder.AppendLine("<script>");
        builder.AppendLine(ScriptBlock());
        builder.AppendLine("</script>");
        builder.AppendLine("</body>");
        builder.AppendLine("</html>");
        return builder.ToString();
    }

    private static List<UniqueBlockMinimumLevelRow> BuildUniqueMinimumLevelRows(IReadOnlyCollection<BlueprintFileReport> files)
    {
        return files
            .SelectMany(file => file.Blocks.Select(block => new { file.Level, block.Name, block.Mod }))
            .GroupBy(item => $"{item.Mod}:{item.Name}", StringComparer.OrdinalIgnoreCase)
            .Select(group => new UniqueBlockMinimumLevelRow(
                group.First().Name,
                group.First().Mod,
                group.Where(item => item.Level.HasValue).Select(item => item.Level!.Value).DefaultIfEmpty(1).Min(),
                true))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AppendUniqueControls(StringBuilder builder, IReadOnlyCollection<UniqueBlockMinimumLevelRow> rows)
    {
        var modOptions = rows
            .Select(row => row.Mod)
            .Where(mod => !string.IsNullOrWhiteSpace(mod))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(mod => mod, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var levelOptions = rows
            .Where(row => row.HasLevel)
            .Select(row => row.MinimumLevel)
            .Distinct()
            .OrderBy(level => level)
            .ToList();

        builder.AppendLine("<div class=\"unique-controls\">");
        builder.AppendLine("<div class=\"unique-controls-left\">");

        builder.AppendLine("<label class=\"control\">Search Block<input id=\"unique-filter-block-query\" type=\"text\" placeholder=\"e.g. bri\" /></label>");

        builder.AppendLine("<div class=\"control-row\">");
        builder.AppendLine("<label class=\"control\">Filter Mod<select id=\"unique-filter-mod-select\"><option value=\"\">Select mod...</option>");
        foreach (var mod in modOptions)
        {
            builder.AppendLine($"<option value=\"{WebUtility.HtmlEncode(mod)}\">{WebUtility.HtmlEncode(mod)}</option>");
        }

        builder.AppendLine("</select></label>");
        builder.AppendLine("<button id=\"unique-add-mod\" type=\"button\" class=\"add-filter-btn\">Add</button>");
        builder.AppendLine("</div>");

        builder.AppendLine("<div class=\"control-row\">");
        builder.AppendLine("<label class=\"control\">Filter Minimum Level<select id=\"unique-filter-level-select\"><option value=\"\">Select level...</option>");
        foreach (var level in levelOptions)
        {
            builder.AppendLine($"<option value=\"{level}\">{level}</option>");
        }

        builder.AppendLine("</select></label>");
        builder.AppendLine("<button id=\"unique-add-level\" type=\"button\" class=\"add-filter-btn\">Add</button>");
        builder.AppendLine("</div>");
        builder.AppendLine("</div>");

        builder.AppendLine("<div class=\"unique-controls-right\">");
        builder.AppendLine("<div class=\"tag-group\"><div class=\"tag-group-title\">Selected Mods</div><div id=\"unique-selected-mod-tags\" class=\"selected-tags\"></div></div>");
        builder.AppendLine("<div class=\"tag-group\"><div class=\"tag-group-title\">Selected Levels</div><div id=\"unique-selected-level-tags\" class=\"selected-tags\"></div></div>");
        builder.AppendLine("</div>");

        builder.AppendLine("</div>");
    }

    private static void AppendUniqueBlocks(StringBuilder builder, IReadOnlyCollection<UniqueBlockMinimumLevelRow> rows)
    {
        builder.AppendLine("<table id=\"unique-blocks-table\"><thead><tr><th><button type=\"button\" class=\"sort-header\" data-sort-key=\"name\">Block <span class=\"sort-indicator\"></span></button></th><th><button type=\"button\" class=\"sort-header\" data-sort-key=\"mod\">Mod <span class=\"sort-indicator\"></span></button></th><th><button type=\"button\" class=\"sort-header\" data-sort-key=\"minLevel\">Minimum Level <span class=\"sort-indicator\"></span></button></th></tr></thead><tbody id=\"unique-blocks-body\">");
        var rowIndex = 0;
        foreach (var row in rows)
        {
            var minLevel = row.MinimumLevel.ToString(CultureInfo.InvariantCulture);
            var minLevelSort = minLevel;
            builder.AppendLine($"<tr data-name=\"{WebUtility.HtmlEncode(row.Name)}\" data-mod=\"{WebUtility.HtmlEncode(row.Mod)}\" data-min-level=\"{WebUtility.HtmlEncode(minLevel)}\" data-min-level-sort=\"{WebUtility.HtmlEncode(minLevelSort)}\" data-default-index=\"{rowIndex}\"><td>{WebUtility.HtmlEncode(row.Name)}</td><td>{WebUtility.HtmlEncode(row.Mod)}</td><td>{WebUtility.HtmlEncode(minLevel)}</td></tr>");
            rowIndex += 1;
        }

        builder.AppendLine("</tbody></table>");
    }

    private static void AppendBlockCounts(StringBuilder builder, FolderNode root)
    {
        foreach (var styleFolder in root.Children.Values.OrderBy(node => node.Name, StringComparer.OrdinalIgnoreCase))
        {
            AppendCountsFolder(builder, styleFolder, 0);
        }
    }

    private static void AppendCountsFolder(StringBuilder builder, FolderNode node, int depth)
    {
        var subtreeFiles = GetFilesInSubtree(node).ToList();
        var subtreeDistinctBlocks = subtreeFiles
            .SelectMany(file => file.Blocks)
            .Select(block => block.Key)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        var depthClass = $"depth-{Math.Min(depth, 4)}";
        builder.AppendLine($"<details open class=\"folder {depthClass}\" ontoggle=\"collapseFolderChildren(this)\">");
        builder.AppendLine($"<summary class=\"folder-title\"><span class=\"folder-name\">{WebUtility.HtmlEncode(node.Name)}</span><span class=\"summary-meta\">{subtreeFiles.Count} files · {subtreeDistinctBlocks} blocks</span></summary>");

        foreach (var child in node.Children.Values.OrderBy(child => child.Name, StringComparer.OrdinalIgnoreCase))
        {
            AppendCountsFolder(builder, child, depth + 1);
        }

        var groups = node.Files
            .GroupBy(file => file.BuildingType, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var buildingGroup in groups)
        {
            var buildingFiles = buildingGroup.ToList();
            var buildingDistinctBlocks = buildingFiles
                .SelectMany(file => file.Blocks)
                .Select(block => block.Key)
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            builder.AppendLine("<details open class=\"building-group\">");
            builder.AppendLine($"<summary class=\"building-title\"><span>{WebUtility.HtmlEncode(buildingGroup.Key)}</span><span class=\"summary-meta\">{buildingFiles.Count} files · {buildingDistinctBlocks} blocks</span></summary>");

            var levelGroups = buildingGroup
                .GroupBy(file => file.Level)
                .OrderBy(group => group.Key ?? int.MaxValue)
                .ToList();

            var hasAnyLevels = levelGroups.Any(group => group.Key.HasValue);

            foreach (var levelGroup in levelGroups)
            {
                if (hasAnyLevels && levelGroup.Key.HasValue)
                {
                    var levelFiles = levelGroup.ToList();
                    var levelDistinctBlocks = levelFiles
                        .SelectMany(file => file.Blocks)
                        .Select(block => block.Key)
                        .Where(key => !string.IsNullOrWhiteSpace(key))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count();

                    builder.AppendLine("<details open class=\"level-group\">");
                    builder.AppendLine($"<summary class=\"muted level-title\"><span>Level {WebUtility.HtmlEncode(levelGroup.Key.Value.ToString(CultureInfo.InvariantCulture))}</span><span class=\"summary-meta\">{levelFiles.Count} files · {levelDistinctBlocks} blocks</span></summary>");
                }

                foreach (var file in levelGroup.OrderBy(file => file.FileName, StringComparer.OrdinalIgnoreCase))
                {
                    builder.AppendLine($"<div style=\"margin: 10px 0 18px;\"><div class=\"muted\">{WebUtility.HtmlEncode(file.FileName)}{WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(file.Error) ? string.Empty : " - " + file.Error)}</div>");
                    AppendBlockTable(builder, file.Blocks);
                    builder.AppendLine("</div>");
                }

                if (hasAnyLevels && levelGroup.Key.HasValue)
                {
                    builder.AppendLine("</details>");
                }
            }

            builder.AppendLine("</details>");
        }

        builder.AppendLine("</details>");
    }

    private static IEnumerable<BlueprintFileReport> GetFilesInSubtree(FolderNode node)
    {
        foreach (var file in node.Files)
        {
            yield return file;
        }

        foreach (var child in node.Children.Values)
        {
            foreach (var file in GetFilesInSubtree(child))
            {
                yield return file;
            }
        }
    }

    private static FolderNode BuildFolderTree(IEnumerable<BlueprintFileReport> files)
    {
        var root = new FolderNode("root");

        foreach (var file in files)
        {
            var directory = Path.GetDirectoryName(file.RelativePath) ?? string.Empty;
            var parts = directory
                .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

            FolderNode current = root;
            foreach (var part in parts)
            {
                if (!current.Children.TryGetValue(part, out var child))
                {
                    child = new FolderNode(part);
                    current.Children[part] = child;
                }

                current = child;
            }

            current.Files.Add(file);
        }

        return root;
    }

    private static void AppendBlockTable(StringBuilder builder, List<BlockRecord> blocks)
    {
        builder.AppendLine("<table class=\"requirements-table\"><thead><tr><th><button type=\"button\" class=\"requirements-sort-header\" data-sort-key=\"name\" data-sort-type=\"text\">Block <span class=\"sort-indicator\"></span></button></th><th><button type=\"button\" class=\"requirements-sort-header\" data-sort-key=\"mod\" data-sort-type=\"text\">Mod <span class=\"sort-indicator\"></span></button></th><th><button type=\"button\" class=\"requirements-sort-header\" data-sort-key=\"count\" data-sort-type=\"number\">Count <span class=\"sort-indicator\"></span></button></th></tr></thead><tbody>");
        foreach (var block in blocks)
        {
            builder.AppendLine($"<tr><td>{WebUtility.HtmlEncode(block.Name)}</td><td>{WebUtility.HtmlEncode(block.Mod)}</td><td>{block.Count}</td></tr>");
        }
        builder.AppendLine("</tbody></table>");
    }

    private static string StyleBlock()
    {
        return """
:root {
  color-scheme: dark;
  --bg: #0b1020;
  --panel: rgba(15, 23, 42, 0.88);
  --panel-2: rgba(30, 41, 59, 0.92);
  --text: #e2e8f0;
  --muted: #94a3b8;
  --accent: #22c55e;
  --border: rgba(148, 163, 184, 0.25);
}
* { box-sizing: border-box; }
body {
  margin: 0;
  font-family: "Trebuchet MS", "Avenir Next", "Segoe UI", sans-serif;
  color: var(--text);
  background:
    radial-gradient(circle at top left, rgba(34, 197, 94, 0.18), transparent 28%),
    radial-gradient(circle at 80% 20%, rgba(56, 189, 248, 0.14), transparent 24%),
    linear-gradient(180deg, #08101c 0%, #0b1020 100%);
  min-height: 100vh;
}
header { padding: 32px 24px 12px; max-width: 1200px; margin: 0 auto; }
h1 { margin: 0; font-size: clamp(2rem, 4vw, 3.5rem); letter-spacing: -0.04em; }
.sub { color: var(--muted); margin-top: 10px; max-width: 72ch; line-height: 1.5; }
main { max-width: 1200px; margin: 0 auto; padding: 12px 24px 48px; display: grid; gap: 16px; }
.toolbar, .panel { background: var(--panel); border: 1px solid var(--border); border-radius: 20px; box-shadow: 0 24px 60px rgba(0,0,0,0.28); backdrop-filter: blur(16px); }
.toolbar { padding: 18px; display: grid; gap: 12px; grid-template-columns: repeat(3, minmax(0, 1fr)); align-items: end; }
label { display: grid; gap: 6px; font-size: 0.85rem; color: var(--muted); }
input, button { font: inherit; }
input { width: 100%; padding: 12px 14px; border-radius: 12px; border: 1px solid var(--border); background: rgba(15, 23, 42, 0.8); color: var(--text); }
button { padding: 12px 18px; border-radius: 12px; border: 0; cursor: pointer; color: #08101c; background: linear-gradient(135deg, var(--accent), #bef264); font-weight: 700; }
.toolbar-actions { display: flex; gap: 8px; align-items: center; }
.top-export-actions { display: flex; gap: 8px; justify-content: flex-end; align-items: center; }
.top-filter-controls { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 10px; grid-column: 1 / -1; }
.top-filter-tags { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 10px; grid-column: 1 / -1; padding: 10px; border: 1px solid var(--border); border-radius: 10px; background: rgba(15, 23, 42, 0.45); }
.secondary-btn { color: var(--text); background: rgba(148, 163, 184, 0.2); border: 1px solid rgba(148, 163, 184, 0.35); font-weight: 600; }
.panel { padding: 18px; overflow: hidden; }
.grid { display: grid; gap: 16px; grid-template-columns: 1fr 1fr; }
.grid.single { grid-template-columns: 1fr; }
.card { background: var(--panel-2); border: 1px solid var(--border); border-radius: 18px; padding: 16px; }
.card h2 { margin: 0; font-size: 1.05rem; }
.card h2 { margin-bottom: 10px; }
.section-toggle { cursor: pointer; user-select: none; }
.section-toggle:hover { color: #cbd5e1; }
.card-title-row { display: flex; align-items: center; justify-content: space-between; gap: 10px; margin-bottom: 10px; }
.card-title-row h2 { margin-bottom: 0; }
.card-summary { display: flex; flex-wrap: wrap; gap: 8px; margin: 0 0 12px; }
.summary-pill { padding: 4px 8px; border: 1px solid rgba(148, 163, 184, 0.28); border-radius: 999px; color: #cbd5e1; font-size: 0.8rem; background: rgba(148, 163, 184, 0.08); }
.summary { display: flex; flex-wrap: wrap; gap: 10px; margin-bottom: 14px; color: var(--muted); }
.pill { padding: 6px 10px; border: 1px solid var(--border); border-radius: 999px; background: rgba(255,255,255,0.03); }
.warning { margin: 0 0 12px; padding: 10px 12px; border: 1px solid rgba(251,191,36,0.4); background: rgba(251,191,36,0.12); border-radius: 12px; color: #fde68a; }
table { width: 100%; border-collapse: collapse; font-size: 0.92rem; }
th, td { text-align: left; padding: 9px 8px; border-bottom: 1px solid rgba(148, 163, 184, 0.16); vertical-align: top; }
th { color: var(--muted); font-weight: 600; }
details { margin-bottom: 14px; }
summary { cursor: pointer; }
.style-title { margin: 0 0 8px; font-size: 1.1rem; color: #dbeafe; }
.building-title { margin: 12px 0 8px; color: #cbd5e1; }
.muted { color: var(--muted); }
.folder summary { padding: 4px 0; }
.folder-name { font-size: 1.05rem; color: #dbeafe; }
.depth-1 > summary .folder-name { font-size: 0.98rem; color: #cbd5e1; }
.depth-2 > summary .folder-name { font-size: 0.92rem; color: #bfdbfe; }
.depth-3 > summary .folder-name, .depth-4 > summary .folder-name { font-size: 0.9rem; color: #93c5fd; }
.building-group { margin: 10px 0 12px 14px; }
.building-group > summary { color: #cbd5e1; }
.level-group { margin: 8px 0 10px 14px; }
.summary-meta { color: #94a3b8; font-size: 0.82rem; margin-left: 8px; }
.folder-title { display: flex; align-items: center; justify-content: space-between; gap: 10px; }
.building-title, .level-title { display: flex; align-items: center; justify-content: space-between; gap: 10px; }
.unique-controls { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 10px; margin: 0 0 12px; }
.unique-controls-left { display: grid; gap: 10px; align-content: start; }
.unique-controls-right { display: grid; gap: 10px; align-content: start; padding: 10px; border: 1px solid var(--border); border-radius: 10px; background: rgba(15, 23, 42, 0.45); }
.control { display: grid; gap: 6px; font-size: 0.82rem; color: #cbd5e1; }
.control select { width: 100%; min-height: 40px; padding: 8px 10px; border-radius: 10px; border: 1px solid var(--border); background: rgba(15, 23, 42, 0.8); color: var(--text); }
.control-row { display: grid; grid-template-columns: minmax(0, 1fr) auto; gap: 8px; align-items: end; }
.add-filter-btn { min-height: 40px; padding: 6px 12px; border-radius: 10px; border: 1px solid var(--border); background: rgba(148, 163, 184, 0.14); color: var(--text); cursor: pointer; }
.add-filter-btn:hover { background: rgba(148, 163, 184, 0.24); }
.tag-group { display: grid; gap: 6px; }
.tag-group-title { font-size: 0.78rem; color: #94a3b8; letter-spacing: 0.02em; text-transform: uppercase; }
.selected-tags { display: flex; flex-wrap: wrap; gap: 6px; min-height: 28px; }
.filter-tag { display: inline-flex; align-items: center; gap: 6px; padding: 4px 8px; border-radius: 999px; border: 1px solid rgba(148, 163, 184, 0.3); background: rgba(148, 163, 184, 0.14); color: #e2e8f0; font-size: 0.78rem; }
.filter-tag button { border: 0; background: transparent; color: #cbd5e1; cursor: pointer; font-size: 0.8rem; line-height: 1; padding: 0; }
.filter-tag button:hover { color: #ffffff; }
.tag-empty { color: #64748b; font-size: 0.8rem; }
.sort-header { width: 100%; display: inline-flex; align-items: center; justify-content: flex-start; gap: 6px; padding: 0; border: 0; background: transparent; color: var(--muted); font: inherit; font-weight: 600; cursor: pointer; text-align: left; }
.sort-header:hover { color: #cbd5e1; }
.sort-header.active { color: #e2e8f0; }
.sort-indicator { min-width: 10px; }
.requirements-sort-header { width: 100%; display: inline-flex; align-items: center; justify-content: flex-start; gap: 6px; padding: 0; border: 0; background: transparent; color: var(--muted); font: inherit; font-weight: 600; cursor: pointer; text-align: left; }
.requirements-sort-header:hover { color: #cbd5e1; }
.requirements-sort-header.active { color: #e2e8f0; }
.clear-btn { min-height: 36px; padding: 6px 10px; border-radius: 10px; border: 1px solid var(--border); background: rgba(148, 163, 184, 0.14); color: var(--text); cursor: pointer; }
.clear-btn:hover { background: rgba(148, 163, 184, 0.24); }
@media (max-width: 900px) {
  .toolbar, .grid { grid-template-columns: 1fr; }
    .unique-controls { grid-template-columns: 1fr; }
        .top-filter-controls, .top-filter-tags { grid-template-columns: 1fr; }
}
""";
    }

        private static string ScriptBlock()
        {
                return """
function collapseFolderChildren(folder) {
    if (!folder || folder.open) {
        return;
    }

    folder.querySelectorAll('details').forEach(child => {
        if (child !== folder) {
            child.open = false;
        }
    });
}

function splitCsvValues(text) {
    if (!text) {
        return [];
    }

    return String(text)
        .split(',')
        .map(value => value.trim())
        .filter(value => value.length > 0);
}

function getTopFilterState() {
    if (!window.topFilterState) {
        window.topFilterState = {
            styles: [],
            buildings: [],
            levels: []
        };
    }

    return window.topFilterState;
}

function queueTopFilterSubmit() {
    const form = document.querySelector('form.toolbar');
    if (!form) {
        return;
    }

    if (window.topFilterSubmitTimer) {
        clearTimeout(window.topFilterSubmitTimer);
    }

    window.topFilterSubmitTimer = setTimeout(() => {
        window.topFilterSubmitTimer = null;
        form.requestSubmit();
    }, 120);
}

function syncTopFilterHiddenInputs() {
    const state = getTopFilterState();
    const styleHidden = document.getElementById('style-hidden');
    const buildingHidden = document.getElementById('building-hidden');
    const levelHidden = document.getElementById('level-hidden');

    if (styleHidden) {
        styleHidden.value = state.styles.join(',');
    }

    if (buildingHidden) {
        buildingHidden.value = state.buildings.join(',');
    }

    if (levelHidden) {
        levelHidden.value = state.levels.join(',');
    }
}

function removeTopFilterValue(kind, value) {
    const state = getTopFilterState();
    const list = kind === 'styles'
        ? state.styles
        : kind === 'buildings'
            ? state.buildings
            : state.levels;
    const index = list.indexOf(value);
    if (index >= 0) {
        list.splice(index, 1);
    }

    renderTopFilterTags();
    syncTopFilterHiddenInputs();
    queueTopFilterSubmit();
}

function renderTopFilterTagGroup(containerId, values, kind) {
    const container = document.getElementById(containerId);
    if (!container) {
        return;
    }

    if (values.length === 0) {
        container.innerHTML = '<span class="tag-empty">None</span>';
        return;
    }

    container.innerHTML = values.map(value => {
        const encoded = String(value)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');
        return `<span class="filter-tag">${encoded}<button type="button" data-top-kind="${kind}" data-top-value="${encoded}" aria-label="Remove ${encoded}">x</button></span>`;
    }).join('');

    container.querySelectorAll('button[data-top-kind][data-top-value]').forEach(button => {
        button.addEventListener('click', () => {
            const topKind = button.getAttribute('data-top-kind') ?? 'styles';
            const topValue = button.getAttribute('data-top-value') ?? '';
            removeTopFilterValue(topKind, topValue);
        });
    });
}

function renderTopFilterTags() {
    const state = getTopFilterState();
    renderTopFilterTagGroup('top-selected-style-tags', state.styles, 'styles');
    renderTopFilterTagGroup('top-selected-building-tags', state.buildings, 'buildings');
    renderTopFilterTagGroup('top-selected-level-tags', state.levels, 'levels');
}

function addTopFilterValue(kind, value) {
    if (!value) {
        return;
    }

    const state = getTopFilterState();
    const list = kind === 'styles'
        ? state.styles
        : kind === 'buildings'
            ? state.buildings
            : state.levels;
    if (!list.includes(value)) {
        list.push(value);
    }

    renderTopFilterTags();
    syncTopFilterHiddenInputs();
    queueTopFilterSubmit();
}

function initializeTopToolbarControls() {
    const form = document.querySelector('form.toolbar');
    if (!form) {
        return;
    }

    const styleHidden = document.getElementById('style-hidden');
    const buildingHidden = document.getElementById('building-hidden');
    const levelHidden = document.getElementById('level-hidden');

    window.topFilterState = {
        styles: splitCsvValues(styleHidden?.value ?? ''),
        buildings: splitCsvValues(buildingHidden?.value ?? ''),
        levels: splitCsvValues(levelHidden?.value ?? '')
    };

    const addStyleButton = document.getElementById('top-add-style');
    const addBuildingButton = document.getElementById('top-add-building');
    const addLevelButton = document.getElementById('top-add-level');
    const styleSelect = document.getElementById('top-filter-style-select');
    const buildingSelect = document.getElementById('top-filter-building-select');
    const levelSelect = document.getElementById('top-filter-level-select');

    if (addStyleButton && styleSelect) {
        addStyleButton.addEventListener('click', () => addTopFilterValue('styles', styleSelect.value ?? ''));
    }

    if (addBuildingButton && buildingSelect) {
        addBuildingButton.addEventListener('click', () => addTopFilterValue('buildings', buildingSelect.value ?? ''));
    }

    if (addLevelButton && levelSelect) {
        addLevelButton.addEventListener('click', () => addTopFilterValue('levels', levelSelect.value ?? ''));
    }

    renderTopFilterTags();
    syncTopFilterHiddenInputs();

    const clearFiltersButton = document.getElementById('top-clear-filters');
    if (clearFiltersButton) {
        clearFiltersButton.addEventListener('click', () => {
            window.topFilterState = { styles: [], buildings: [], levels: [] };
            renderTopFilterTags();
            syncTopFilterHiddenInputs();

            form.requestSubmit();
        });
    }

    const runExport = (format) => {
        syncTopFilterHiddenInputs();
        const params = new URLSearchParams(new FormData(form));
        params.set('format', format === 'csv' ? 'csv' : 'json');
        window.location.href = `/export?${params.toString()}`;
    };

    const exportJsonButton = document.getElementById('export-json-btn');
    if (exportJsonButton) {
        exportJsonButton.addEventListener('click', () => runExport('json'));
    }

    const exportCsvButton = document.getElementById('export-csv-btn');
    if (exportCsvButton) {
        exportCsvButton.addEventListener('click', () => runExport('csv'));
    }

    form.addEventListener('submit', () => {
        syncTopFilterHiddenInputs();
    });
}

function getUniqueFilterState() {
    if (!window.uniqueFilterState) {
        window.uniqueFilterState = {
            mods: [],
            levels: []
        };
    }

    return window.uniqueFilterState;
}

function addUniqueFilterValue(kind, value) {
    if (!value) {
        return;
    }

    const state = getUniqueFilterState();
    const list = kind === 'mods' ? state.mods : state.levels;
    if (!list.includes(value)) {
        list.push(value);
    }

    renderUniqueFilterTags();
    applyUniqueFiltersAndSort();
}

function removeUniqueFilterValue(kind, value) {
    const state = getUniqueFilterState();
    const list = kind === 'mods' ? state.mods : state.levels;
    const index = list.indexOf(value);
    if (index >= 0) {
        list.splice(index, 1);
    }

    renderUniqueFilterTags();
    applyUniqueFiltersAndSort();
}

function renderTagGroup(containerId, values, kind) {
    const container = document.getElementById(containerId);
    if (!container) {
        return;
    }

    if (values.length === 0) {
        container.innerHTML = '<span class="tag-empty">None</span>';
        return;
    }

    container.innerHTML = values.map(value => {
        const encoded = String(value)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/\"/g, '&quot;')
            .replace(/'/g, '&#39;');
        return `<span class="filter-tag">${encoded}<button type="button" data-kind="${kind}" data-value="${encoded}" aria-label="Remove ${encoded}">x</button></span>`;
    }).join('');

    container.querySelectorAll('button[data-kind][data-value]').forEach(button => {
        button.addEventListener('click', () => {
            removeUniqueFilterValue(button.getAttribute('data-kind') === 'mods' ? 'mods' : 'levels', button.getAttribute('data-value') ?? '');
        });
    });
}

function renderUniqueFilterTags() {
    const state = getUniqueFilterState();
    renderTagGroup('unique-selected-mod-tags', state.mods, 'mods');
    renderTagGroup('unique-selected-level-tags', state.levels, 'levels');
}

function applyUniqueFiltersAndSort() {
    const body = document.getElementById('unique-blocks-body');
    if (!body) {
        return;
    }

    const rows = Array.from(body.querySelectorAll('tr'));
    const blockQuery = (document.getElementById('unique-filter-block-query')?.value ?? '').trim().toLowerCase();
    const state = getUniqueFilterState();
    const modFilter = state.mods;
    const levelFilter = state.levels;

    const sortState = window.uniqueSortState ?? { key: null, direction: 0 };

    for (const row of rows) {
        const name = row.getAttribute('data-name') ?? '';
        const mod = row.getAttribute('data-mod') ?? '';
        const minLevel = row.getAttribute('data-min-level') ?? '';

        const includeByBlock = blockQuery.length === 0 || name.toLowerCase().includes(blockQuery);
        const includeByMod = modFilter.length === 0 || modFilter.includes(mod);
        const includeByLevel = levelFilter.length === 0 || levelFilter.includes(minLevel);

        row.hidden = !(includeByBlock && includeByMod && includeByLevel);
    }

    rows.sort((a, b) => {
        if (!sortState.key || sortState.direction === 0) {
            const aDefault = Number.parseInt(a.getAttribute('data-default-index') ?? '0', 10);
            const bDefault = Number.parseInt(b.getAttribute('data-default-index') ?? '0', 10);
            return aDefault - bDefault;
        }

        const directionMultiplier = sortState.direction;
        const aName = a.getAttribute('data-name') ?? '';
        const bName = b.getAttribute('data-name') ?? '';
        const aMod = a.getAttribute('data-mod') ?? '';
        const bMod = b.getAttribute('data-mod') ?? '';
        const aLevelText = a.getAttribute('data-min-level-sort') ?? '';
        const bLevelText = b.getAttribute('data-min-level-sort') ?? '';

        if (sortState.key === 'mod') {
            return aMod.localeCompare(bMod) * directionMultiplier || aName.localeCompare(bName) * directionMultiplier;
        }

        if (sortState.key === 'minLevel') {
            const aLevel = aLevelText ? Number.parseInt(aLevelText, 10) : Number.MAX_SAFE_INTEGER;
            const bLevel = bLevelText ? Number.parseInt(bLevelText, 10) : Number.MAX_SAFE_INTEGER;
            if (aLevel !== bLevel) {
                return (aLevel - bLevel) * directionMultiplier;
            }

            return aName.localeCompare(bName) * directionMultiplier;
        }

        return aName.localeCompare(bName) * directionMultiplier || aMod.localeCompare(bMod) * directionMultiplier;
    });

    for (const row of rows) {
        body.appendChild(row);
    }

    updateSortHeaderIndicators();
}

function updateSortHeaderIndicators() {
    const sortState = window.uniqueSortState ?? { key: null, direction: 0 };
    document.querySelectorAll('.sort-header').forEach(button => {
        const key = button.getAttribute('data-sort-key');
        const indicator = button.querySelector('.sort-indicator');
        button.classList.remove('active');
        if (!indicator) {
            return;
        }

        if (sortState.key !== key || sortState.direction === 0) {
            indicator.textContent = '';
            return;
        }

        button.classList.add('active');
        indicator.textContent = sortState.direction === 1 ? '↑' : '↓';
    });
}

function cycleUniqueSort(sortKey) {
    const current = window.uniqueSortState ?? { key: null, direction: 0 };
    if (current.key !== sortKey) {
        window.uniqueSortState = { key: sortKey, direction: 1 };
        applyUniqueFiltersAndSort();
        return;
    }

    window.uniqueSortState = { key: sortKey, direction: current.direction === 1 ? -1 : 1 };
    applyUniqueFiltersAndSort();
}

function clearUniqueFiltersAndSort() {
    const blockSearch = document.getElementById('unique-filter-block-query');
    if (blockSearch) {
        blockSearch.value = '';
    }

    ['unique-filter-mod-select', 'unique-filter-level-select'].forEach(id => {
        const select = document.getElementById(id);
        if (!select) {
            return;
        }

        select.value = '';
    });

    window.uniqueFilterState = { mods: [], levels: [] };
    renderUniqueFilterTags();

    window.uniqueSortState = { key: null, direction: 0 };

    applyUniqueFiltersAndSort();
}

function initializeUniqueControls() {
    window.uniqueSortState = { key: null, direction: 0 };
    window.uniqueFilterState = { mods: [], levels: [] };

    const addModButton = document.getElementById('unique-add-mod');
    const addLevelButton = document.getElementById('unique-add-level');
    const modSelect = document.getElementById('unique-filter-mod-select');
    const levelSelect = document.getElementById('unique-filter-level-select');

    if (addModButton && modSelect) {
        addModButton.addEventListener('click', () => addUniqueFilterValue('mods', modSelect.value ?? ''));
    }

    if (addLevelButton && levelSelect) {
        addLevelButton.addEventListener('click', () => addUniqueFilterValue('levels', levelSelect.value ?? ''));
    }

    const blockSearch = document.getElementById('unique-filter-block-query');
    if (blockSearch) {
        blockSearch.addEventListener('input', applyUniqueFiltersAndSort);
    }

    const clearButton = document.getElementById('unique-clear');
    if (clearButton) {
        clearButton.addEventListener('click', clearUniqueFiltersAndSort);
    }

    document.querySelectorAll('.sort-header').forEach(button => {
        button.addEventListener('click', () => {
            const sortKey = button.getAttribute('data-sort-key');
            if (sortKey) {
                cycleUniqueSort(sortKey);
            }
        });
    });

    renderUniqueFilterTags();
    applyUniqueFiltersAndSort();
}

function initializeRequirementsTableSorting() {
    document.querySelectorAll('.requirements-table').forEach(table => {
        table.querySelectorAll('tbody tr').forEach((row, index) => {
            row.dataset.defaultIndex = index.toString();
        });

        table.querySelectorAll('.requirements-sort-header').forEach((button, buttonIndex) => {
            button.addEventListener('click', () => {
                const currentKey = table.dataset.sortKey ?? '';
                const clickedKey = button.getAttribute('data-sort-key') ?? '';
                const nextDirection = currentKey === clickedKey && table.dataset.sortDirection === 'asc' ? 'desc' : 'asc';

                table.dataset.sortKey = clickedKey;
                table.dataset.sortDirection = nextDirection;

                const tbody = table.querySelector('tbody');
                if (!tbody) {
                    return;
                }

                const rows = Array.from(tbody.querySelectorAll('tr'));
                rows.sort((a, b) => {
                    const aCell = a.children[buttonIndex]?.textContent?.trim() ?? '';
                    const bCell = b.children[buttonIndex]?.textContent?.trim() ?? '';

                    let compare = 0;
                    if (button.getAttribute('data-sort-type') === 'number') {
                        const aNum = Number.parseInt(aCell, 10);
                        const bNum = Number.parseInt(bCell, 10);
                        compare = (Number.isNaN(aNum) ? 0 : aNum) - (Number.isNaN(bNum) ? 0 : bNum);
                    } else {
                        compare = aCell.localeCompare(bCell);
                    }

                    if (compare === 0) {
                        const aDefault = Number.parseInt(a.dataset.defaultIndex ?? '0', 10);
                        const bDefault = Number.parseInt(b.dataset.defaultIndex ?? '0', 10);
                        compare = aDefault - bDefault;
                    }

                    return nextDirection === 'asc' ? compare : -compare;
                });

                rows.forEach(row => tbody.appendChild(row));

                table.querySelectorAll('.requirements-sort-header').forEach(header => {
                    header.classList.remove('active');
                    const indicator = header.querySelector('.sort-indicator');
                    if (indicator) {
                        indicator.textContent = '';
                    }
                });

                button.classList.add('active');
                const currentIndicator = button.querySelector('.sort-indicator');
                if (currentIndicator) {
                    currentIndicator.textContent = nextDirection === 'asc' ? '↑' : '↓';
                }
            });
        });
    });
}

function initializeBlockRequirementsCollapse() {
    const title = document.getElementById('counts-title');
    const section = document.getElementById('counts-section');
    if (!title || !section) {
        return;
    }

    const getGroups = () => Array.from(section.querySelectorAll('details'));
    const updateExpandedState = () => {
        const hasOpen = getGroups().some(group => group.open);
        title.setAttribute('aria-expanded', hasOpen ? 'true' : 'false');
    };

    const toggleAllGroups = () => {
        const groups = getGroups();
        if (groups.length === 0) {
            return;
        }

        const hasOpen = groups.some(group => group.open);
        groups.forEach(group => {
            group.open = !hasOpen;
        });

        updateExpandedState();
    };

    title.addEventListener('click', toggleAllGroups);
    title.addEventListener('keydown', event => {
        if (event.key === 'Enter' || event.key === ' ') {
            event.preventDefault();
            toggleAllGroups();
        }
    });

    section.querySelectorAll('details').forEach(group => {
        group.addEventListener('toggle', updateExpandedState);
    });

    updateExpandedState();
}

if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', () => {
        initializeTopToolbarControls();
        initializeUniqueControls();
        initializeRequirementsTableSorting();
        initializeBlockRequirementsCollapse();
    });
} else {
    initializeTopToolbarControls();
    initializeUniqueControls();
    initializeRequirementsTableSorting();
    initializeBlockRequirementsCollapse();
}
""";
        }

        private sealed record UniqueBlockMinimumLevelRow(string Name, string Mod, int MinimumLevel, bool HasLevel);
        private sealed record CachedScanReport(string Fingerprint, BlueprintScanReport Report, DateTime CachedAtUtc, DateTime NextValidationUtc);

        private sealed class FolderNode
        {
                public FolderNode(string name)
                {
                        Name = name;
                }

                public string Name { get; }
                public Dictionary<string, FolderNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
                public List<BlueprintFileReport> Files { get; } = [];
        }

    private static HashSet<string> ParseMultiValues(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return raw
            .Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryGetHotCachedScanReport(string sourceKey, out BlueprintScanReport report)
    {
        PruneInMemoryScanCache();

        if (ScanCache.TryGetValue(sourceKey, out var cached)
            && DateTime.UtcNow <= cached.NextValidationUtc
            && DateTime.UtcNow - cached.CachedAtUtc <= ScanCacheTtl)
        {
            report = cached.Report;
            return true;
        }

        report = null!;
        return false;
    }

    private static async Task HandleExportAsync(
        HttpListenerResponse response,
        string? format,
        IReadOnlyCollection<BlueprintFileReport> filteredFiles,
        string resolvedPath,
        HashSet<string> selectedStyles,
        HashSet<string> selectedBuildings,
        HashSet<int> selectedLevels)
    {
        var normalizedFormat = string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase) ? "csv" : "json";
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

        if (normalizedFormat == "csv")
        {
            var csv = BuildExportCsv(filteredFiles);
            response.AddHeader("Content-Disposition", $"attachment; filename=\"mc-nbt-export-{timestamp}.csv\"");
            await WriteStringAsync(response, csv, "text/csv; charset=utf-8");
            return;
        }

        var payload = new
        {
            exportedAtUtc = DateTime.UtcNow,
            resolvedPath,
            fileCount = filteredFiles.Count,
            filters = new
            {
                styles = selectedStyles.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
                buildings = selectedBuildings.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
                levels = selectedLevels.OrderBy(value => value).ToArray()
            },
            files = filteredFiles
        };

        response.AddHeader("Content-Disposition", $"attachment; filename=\"mc-nbt-export-{timestamp}.json\"");
        var json = JsonSerializer.Serialize(payload, JsonOptions.Default);
        await WriteStringAsync(response, json, "application/json; charset=utf-8");
    }

    private static string BuildExportCsv(IReadOnlyCollection<BlueprintFileReport> files)
    {
        var builder = new StringBuilder();
        builder.AppendLine("style,buildingType,level,fileName,relativePath,blockName,mod,count");

        foreach (var file in files)
        {
            foreach (var block in file.Blocks)
            {
                builder.AppendLine(string.Join(",", new[]
                {
                    EscapeCsv(file.Style),
                    EscapeCsv(file.BuildingType),
                    EscapeCsv(file.Level?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
                    EscapeCsv(file.FileName),
                    EscapeCsv(file.RelativePath),
                    EscapeCsv(block.Name),
                    EscapeCsv(block.Mod),
                    EscapeCsv(block.Count.ToString(CultureInfo.InvariantCulture))
                }));
            }
        }

        return builder.ToString();
    }

    private static string EscapeCsv(string value)
    {
        return value.Contains('"', StringComparison.Ordinal)
            || value.Contains(',', StringComparison.Ordinal)
            || value.Contains('\n', StringComparison.Ordinal)
            || value.Contains('\r', StringComparison.Ordinal)
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }

    private static bool TryGetCachedScanReport(string sourceKey, string sourceFingerprint, out BlueprintScanReport report, out string cacheLayer)
    {
        PruneInMemoryScanCache();

        if (ScanCache.TryGetValue(sourceKey, out var cached)
            && string.Equals(cached.Fingerprint, sourceFingerprint, StringComparison.Ordinal)
            && DateTime.UtcNow - cached.CachedAtUtc <= ScanCacheTtl)
        {
            var refreshed = cached with { NextValidationUtc = DateTime.UtcNow.Add(HotCacheValidationInterval) };
            ScanCache[sourceKey] = refreshed;
            report = cached.Report;
            cacheLayer = "Memory cache hit";
            return true;
        }

        if (TryLoadPersistentScanReport(sourceKey, sourceFingerprint, out report))
        {
            SetCachedScanReport(sourceKey, sourceFingerprint, report);
            cacheLayer = "Disk cache hit";
            return true;
        }

        report = null!;
        cacheLayer = string.Empty;
        return false;
    }

    private static void SetCachedScanReport(string sourceKey, string sourceFingerprint, BlueprintScanReport report)
    {
        var now = DateTime.UtcNow;
        ScanCache[sourceKey] = new CachedScanReport(sourceFingerprint, report, now, now.Add(HotCacheValidationInterval));
        TryPersistScanReport(sourceKey, sourceFingerprint, report);
        PruneInMemoryScanCache();
    }

    private static void PruneInMemoryScanCache()
    {
        var now = DateTime.UtcNow;

        foreach (var pair in ScanCache)
        {
            if (now - pair.Value.CachedAtUtc > ScanCacheTtl)
            {
                ScanCache.TryRemove(pair.Key, out _);
            }
        }

        var overflow = ScanCache.Count - MaxInMemoryScanCacheEntries;
        if (overflow <= 0)
        {
            return;
        }

        foreach (var toRemove in ScanCache
                     .OrderBy(pair => pair.Value.CachedAtUtc)
                     .Take(overflow)
                     .Select(pair => pair.Key)
                     .ToList())
        {
            ScanCache.TryRemove(toRemove, out _);
        }
    }

    private static string GetPersistentScanCacheDirectory()
    {
        return Path.Combine(ScanInputResolver.GetCacheRoot(), "report-cache");
    }

    private static string BuildPersistentScanCachePath(string sourceKey, string sourceFingerprint)
    {
        var sourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceKey)));
        return Path.Combine(GetPersistentScanCacheDirectory(), $"{sourceHash}_{sourceFingerprint}.json");
    }

    private static bool TryLoadPersistentScanReport(string sourceKey, string sourceFingerprint, out BlueprintScanReport report)
    {
        report = null!;
        PrunePersistentScanCache();
        var path = BuildPersistentScanCachePath(sourceKey, sourceFingerprint);
        if (!File.Exists(path))
        {
            return false;
        }

        var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
        if (age > ScanCacheTtl)
        {
            TryDeleteFile(path);
            return false;
        }

        try
        {
            var json = File.ReadAllText(path);
            var deserialized = JsonSerializer.Deserialize<BlueprintScanReport>(json, JsonOptions.Default);
            if (deserialized is null)
            {
                return false;
            }

            report = deserialized;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryPersistScanReport(string sourceKey, string sourceFingerprint, BlueprintScanReport report)
    {
        try
        {
            var directory = GetPersistentScanCacheDirectory();
            Directory.CreateDirectory(directory);
            PrunePersistentScanCache();

            var path = BuildPersistentScanCachePath(sourceKey, sourceFingerprint);
            var json = JsonSerializer.Serialize(report, JsonOptions.Default);
            File.WriteAllText(path, json);
        }
        catch
        {
            // Best effort cache write.
        }
    }

    private static void TryClearPersistentScanCache()
    {
        try
        {
            var directory = GetPersistentScanCacheDirectory();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // Best effort cache clear.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    private static void PrunePersistentScanCache()
    {
        try
        {
            var directory = GetPersistentScanCacheDirectory();
            if (!Directory.Exists(directory))
            {
                return;
            }

            var cutoff = DateTime.UtcNow - ScanCacheTtl;
            foreach (var filePath in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
            {
                if (File.GetLastWriteTimeUtc(filePath) < cutoff)
                {
                    TryDeleteFile(filePath);
                }
            }
        }
        catch
        {
            // Best effort cache pruning.
        }
    }

    private static string NormalizeSourceKey(string scanPath)
    {
        var parts = scanPath
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(path => Path.GetFullPath(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return parts.Count == 0
            ? scanPath
            : string.Join("|", parts);
    }

    private static string ComputeSourceFingerprint(string scanPath)
    {
        var entries = scanPath
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(path => Path.GetFullPath(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (entries.Count == 0)
        {
            return string.Empty;
        }

        var hash = new HashCode();

        foreach (var entry in entries)
        {
            hash.Add(entry, StringComparer.OrdinalIgnoreCase);

            if (File.Exists(entry))
            {
                var fileInfo = new FileInfo(entry);
                hash.Add(1);
                hash.Add(fileInfo.Length);
                hash.Add(fileInfo.LastWriteTimeUtc.Ticks);
                continue;
            }

            if (Directory.Exists(entry))
            {
                hash.Add(2);

                foreach (var filePath in Directory.EnumerateFiles(entry, "*.*", SearchOption.AllDirectories)
                             .Where(path => SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                             .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    var fileInfo = new FileInfo(filePath);
                    hash.Add(filePath, StringComparer.OrdinalIgnoreCase);
                    hash.Add(fileInfo.Length);
                    hash.Add(fileInfo.LastWriteTimeUtc.Ticks);
                }

                continue;
            }

            hash.Add(0);
        }

        return hash.ToHashCode().ToString("X8", CultureInfo.InvariantCulture);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(query))
        {
            return result;
        }

        var trimmed = query.StartsWith('?') ? query[1..] : query;
        foreach (var pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = WebUtility.UrlDecode(parts[0]);
            var value = parts.Length > 1 ? WebUtility.UrlDecode(parts[1]) : string.Empty;
            if (!string.IsNullOrWhiteSpace(key))
            {
                result[key] = value;
            }
        }

        return result;
    }

    private static string? JoinMessages(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first))
        {
            return string.IsNullOrWhiteSpace(second) ? null : second;
        }

        if (string.IsNullOrWhiteSpace(second))
        {
            return first;
        }

        return $"{first} {second}";
    }

    private static Task WriteStringAsync(HttpListenerResponse response, string text, string contentType, int statusCode = 200)
    {
        response.StatusCode = statusCode;
        response.ContentType = contentType;
        var bytes = Encoding.UTF8.GetBytes(text);
        response.ContentLength64 = bytes.Length;
        return response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
    }
}