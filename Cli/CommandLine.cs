using System.Text.Json;
using minecraft_nbt_tool.Diagnostics;
using minecraft_nbt_tool.Export;
using minecraft_nbt_tool.Models;
using minecraft_nbt_tool.Scanning;
using minecraft_nbt_tool.Web;

namespace minecraft_nbt_tool.Cli;

/// <summary>
/// Handles command dispatch and CLI execution flow.
/// </summary>
internal static class CommandLine
{
    /// <summary>
    /// Parses arguments and executes the requested command.
    /// </summary>
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

    /// <summary>
    /// Prints command usage instructions.
    /// </summary>
    private static void PrintUsage()
    {
        Console.WriteLine("Minecraft NBT blueprint scanner");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  minecraft-nbt-tool scan <root> [--output <file>] [--style <style>] [--building <name>] [--level <n>]");
        Console.WriteLine("  minecraft-nbt-tool scan <root> [--csv <prefix>]");
        Console.WriteLine("  minecraft-nbt-tool inspect <file>");
        Console.WriteLine("  minecraft-nbt-tool serve <root> [--port <port>]");
        Console.WriteLine();
        Console.WriteLine("The scan command returns JSON grouped by style, building type, and level.");
    }
}
