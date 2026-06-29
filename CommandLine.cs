using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using SharpNBT.SNBT;

internal static class CommandLine
{
    public static async Task<int> RunAsync(string[] args)
    {
        var options = CliOptions.Parse(args);

        if (options.ShowHelp)
        {
            PrintUsage();
            return 0;
        }

        if (options.Command == "inspect")
        {
            await BlueprintInspector.RunAsync(options.RootPath);
            return 0;
        }

        if (options.Command == "serve")
        {
            await BlueprintWebServer.RunAsync(options.RootPath, options.Port);
            return 0;
        }

        if (!Directory.Exists(options.RootPath) && !File.Exists(options.RootPath))
        {
            Console.Error.WriteLine($"Input path does not exist: {options.RootPath}");
            return 2;
        }

        var scanner = new BlueprintScanner();
        var report = scanner.Scan(options.RootPath, options);
        var json = JsonSerializer.Serialize(report, JsonOptions.Default);

        if (!string.IsNullOrWhiteSpace(options.CsvPath))
        {
            CsvExporter.WriteReports(report, options.CsvPath);
        }

        if (string.IsNullOrWhiteSpace(options.OutputPath))
        {
            Console.WriteLine(json);
        }
        else
        {
            var fullPath = Path.GetFullPath(options.OutputPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(fullPath, json);
        }

        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Minecraft NBT blueprint scanner");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  mc-nbt-tool scan <root> [--output <file>] [--style <style>] [--building <name>] [--level <n>]");
        Console.WriteLine("  mc-nbt-tool scan <root> [--csv <prefix>]");
        Console.WriteLine("  mc-nbt-tool inspect <file>");
        Console.WriteLine("  mc-nbt-tool serve <root> [--port <port>]");
        Console.WriteLine();
        Console.WriteLine("The scan command returns JSON grouped by style, building type, and level.");
    }
}

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

internal static class BlueprintInspector
{
    public static async Task RunAsync(string path)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"File does not exist: {path}");
            return;
        }

        object root = Path.GetExtension(path).Equals(".snbt", StringComparison.OrdinalIgnoreCase)
            ? StringNbt.Parse(await File.ReadAllTextAsync(path))
            : ReadBinary(path);

        PrintNode(root, 0, 2);
    }

    private static object ReadBinary(string path)
    {
        foreach (var options in new[] { SharpNBT.FormatOptions.Java, SharpNBT.FormatOptions.BedrockFile, SharpNBT.FormatOptions.BedrockNetwork })
        {
            try
            {
                return SharpNBT.NbtFile.Read(path, options, SharpNBT.CompressionType.AutoDetect);
            }
            catch
            {
            }
        }

        throw new InvalidDataException($"Unable to parse binary NBT file: {path}");
    }

    private static void PrintNode(object? node, int depth, int maxDepth)
    {
        if (node is null || depth > maxDepth)
        {
            return;
        }

        var indent = new string(' ', depth * 2);
        Console.WriteLine($"{indent}{node.GetType().FullName}");

        if (node.GetType().Namespace == "SharpNBT"
            && node.GetType().Name is not "CompoundTag"
            && node.GetType().Name is not "ListTag")
        {
            return;
        }

        if (depth == 0)
        {
            foreach (var iface in node.GetType().GetInterfaces())
            {
                Console.WriteLine($"{indent}  iface: {iface.FullName}");
            }
        }

        if (node is IEnumerable enumerable)
        {
            var previewIndex = 0;
            foreach (var item in enumerable)
            {
                if (previewIndex++ >= 32)
                {
                    break;
                }

                var keyProperty = item?.GetType().GetProperty("Key", BindingFlags.Public | BindingFlags.Instance);
                var valueProperty = item?.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                if (keyProperty is not null && valueProperty is not null)
                {
                    var key = keyProperty.GetValue(item);
                    var value = valueProperty.GetValue(item);
                    Console.WriteLine($"{indent}  enum: {key} => {value?.GetType().FullName ?? "null"}");
                }
                else
                {
                    Console.WriteLine($"{indent}  enum: {item?.GetType().FullName ?? "null"}");
                }
            }
        }

        foreach (var child in EnumerateChildren(node).Take(12))
        {
            Console.WriteLine($"{indent}- {child.Name}: {child.Value?.GetType().FullName ?? "null"}");
            if (depth == 0 && string.Equals(child.Name, "palette", StringComparison.OrdinalIgnoreCase) && child.Value is IEnumerable paletteEnumerable)
            {
                var paletteIndex = 0;
                foreach (var paletteItem in paletteEnumerable)
                {
                    if (paletteIndex++ >= 4)
                    {
                        break;
                    }

                    Console.WriteLine($"{indent}  palette-item: {paletteItem?.GetType().FullName ?? "null"}");
                    if (paletteItem is not null)
                    {
                        foreach (var subChild in EnumerateChildren(paletteItem).Take(8))
                        {
                            Console.WriteLine($"{indent}    {subChild.Name}: {subChild.Value?.GetType().FullName ?? "null"}");
                        }
                    }
                }
            }
            if (depth > 0 && depth < maxDepth && child.Value is not null && !string.Equals(child.Name, "palette", StringComparison.OrdinalIgnoreCase))
            {
                PrintNode(child.Value, depth + 1, maxDepth);
            }
        }
    }

    private static IEnumerable<(string Name, object? Value)> EnumerateChildren(object node)
    {
        if (node is System.Collections.IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Key is not null)
                {
                    yield return (entry.Key.ToString()!, entry.Value);
                }
            }

            yield break;
        }

        var properties = node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetIndexParameters().Length == 0);

        foreach (var property in properties)
        {
            object? value;
            try
            {
                value = property.GetValue(node);
            }
            catch
            {
                continue;
            }

            yield return (property.Name, value);
        }
    }
}