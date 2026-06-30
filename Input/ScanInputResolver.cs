using System.Diagnostics;
using minecraft_nbt_tool.Models;

namespace minecraft_nbt_tool.Input;

/// <summary>
/// Resolves local and remote inputs into normalized scan paths.
/// </summary>
internal static class ScanInputResolver
{
    /// <summary>
    /// Gets the root cache directory used by the scanner.
    /// </summary>
    public static string GetCacheRoot()
    {
        return Path.Combine(Path.GetTempPath(), "minecraft-nbt-tool-cache");
    }

    /// <summary>
    /// Clears all cached source and report data.
    /// </summary>
    public static bool TryClearCache(out string message)
    {
        var cacheRoot = GetCacheRoot();

        if (!Directory.Exists(cacheRoot))
        {
            message = $"Cache not found at {cacheRoot}.";
            return true;
        }

        try
        {
            ForceDeleteDirectory(cacheRoot);
            message = $"Cleared cache at {cacheRoot}.";
            return true;
        }
        catch (Exception ex)
        {
            message = $"Unable to clear cache at {cacheRoot}: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Deletes a directory tree with retries and attribute normalization.
    /// </summary>
    internal static void ForceDeleteDirectory(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            return;
        }

        const int maxAttempts = 4;
        var delayMs = 150;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                foreach (var filePath in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        File.SetAttributes(filePath, FileAttributes.Normal);
                    }
                    catch
                    {
                        // Best effort; continue attempting deletion.
                    }
                }

                var allDirectories = Directory
                    .EnumerateDirectories(directoryPath, "*", SearchOption.AllDirectories)
                    .OrderByDescending(path => path.Length)
                    .ToList();

                foreach (var filePath in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
                {
                    File.Delete(filePath);
                }

                foreach (var childDirectory in allDirectories)
                {
                    Directory.Delete(childDirectory, recursive: false);
                }

                Directory.Delete(directoryPath, recursive: false);
                return;
            }
            catch when (attempt < maxAttempts)
            {
                Thread.Sleep(delayMs);
                delayMs *= 2;
            }
        }

        Directory.Delete(directoryPath, recursive: true);
    }

    /// <summary>
    /// Resolves a local path or GitHub tree URL into a scan input.
    /// </summary>
    public static async Task<ResolvedScanInput> ResolveAsync(string defaultRootPath, string? localPath, string? repoUrl)
    {
        if (!string.IsNullOrWhiteSpace(repoUrl))
        {
            try
            {
                var resolved = await GitHubFolderMaterializer.MaterializeTreeUrlAsync(repoUrl.Trim());
                return new ResolvedScanInput(resolved, resolved, null);
            }
            catch (Exception ex)
            {
                var fallback = string.IsNullOrWhiteSpace(localPath) ? defaultRootPath : ToAbsoluteLocalPath(defaultRootPath, localPath);
                return new ResolvedScanInput(fallback, fallback, $"GitHub URL could not be resolved ({ex.Message}). Using local path instead.");
            }
        }

        if (!string.IsNullOrWhiteSpace(localPath))
        {
            var resolved = ToAbsoluteLocalPath(defaultRootPath, localPath);
            return new ResolvedScanInput(resolved, resolved, null);
        }

        return new ResolvedScanInput(defaultRootPath, defaultRootPath, null);
    }

    /// <summary>
    /// Converts a user-supplied local path list to absolute pipe-delimited paths.
    /// </summary>
    private static string ToAbsoluteLocalPath(string defaultRootPath, string localPath)
    {
        var parts = localPath
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(path => Path.IsPathRooted(path)
                ? path
                : Path.GetFullPath(Path.Combine(defaultRootPath, path)))
            .ToList();

        return string.Join('|', parts);
    }

}
