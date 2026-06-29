using System.Diagnostics;
using System.Text.Json;

internal sealed record ResolvedScanInput(string ScanPath, string DisplayPath, string? Warning);

internal static class ScanInputResolver
{
    private static readonly HttpClient Http = CreateHttpClient();

    public static string GetCacheRoot()
    {
        return Path.Combine(Path.GetTempPath(), "mc-nbt-tool-cache");
    }

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

    private static void ForceDeleteDirectory(string directoryPath)
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

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("mc-nbt-tool/1.0");
        return client;
    }

    private static class GitHubFolderMaterializer
    {
        public static async Task<string> MaterializeTreeUrlAsync(string treeUrl)
        {
            var uri = new Uri(treeUrl);
            if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Only github.com tree URLs are supported.");
            }

            var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4 || !string.Equals(parts[2], "tree", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Expected URL format: https://github.com/<owner>/<repo>/tree/<branch>/<path>");
            }

            var owner = parts[0];
            var repo = parts[1];
            var remaining = parts.Skip(3).ToArray();
            var branches = await GetBranchesAsync(owner, repo);

            var branch = branches
                .OrderByDescending(value => value.Length)
                .FirstOrDefault(candidate => IsPrefixMatch(candidate, remaining));

            if (branch is null)
            {
                throw new InvalidOperationException("Could not match URL path to a repository branch.");
            }

            var branchSegments = branch.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var folderSegments = remaining.Skip(branchSegments.Length).ToArray();
            var folderPath = string.Join('/', folderSegments);

            var cacheRoot = Path.Combine(GetCacheRoot(), owner, repo, Sanitize(branch));
            var repoUrl = $"https://github.com/{owner}/{repo}.git";

            EnsureValidCacheRepository(cacheRoot);

            if (!Directory.Exists(cacheRoot) || !Directory.Exists(Path.Combine(cacheRoot, ".git")))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cacheRoot)!);
                if (Directory.Exists(cacheRoot))
                {
                    ForceDeleteDirectory(cacheRoot);
                }

                RunGit($"clone --depth 1 --filter=blob:none --sparse --branch \"{branch}\" \"{repoUrl}\" \"{cacheRoot}\"", Directory.GetCurrentDirectory());
            }

            if (!string.IsNullOrWhiteSpace(folderPath))
            {
                RunGit($"sparse-checkout set --cone \"{folderPath}\"", cacheRoot);
            }

            return string.IsNullOrWhiteSpace(folderPath)
                ? cacheRoot
                : Path.Combine(cacheRoot, folderPath.Replace('/', Path.DirectorySeparatorChar));
        }

        private static void EnsureValidCacheRepository(string cacheRoot)
        {
            if (!Directory.Exists(cacheRoot))
            {
                return;
            }

            if (!Directory.Exists(Path.Combine(cacheRoot, ".git")))
            {
                ForceDeleteDirectory(cacheRoot);
                return;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "rev-parse --is-inside-work-tree",
                WorkingDirectory = cacheRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                ForceDeleteDirectory(cacheRoot);
                return;
            }

            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                ForceDeleteDirectory(cacheRoot);
            }
        }

        private static async Task<List<string>> GetBranchesAsync(string owner, string repo)
        {
            var url = $"https://api.github.com/repos/{owner}/{repo}/branches?per_page=100";
            using var response = await Http.GetAsync(url);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var json = await JsonDocument.ParseAsync(stream);
            return json.RootElement
                .EnumerateArray()
                .Select(element => element.GetProperty("name").GetString())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .ToList();
        }

        private static bool IsPrefixMatch(string branch, string[] remaining)
        {
            var branchSegments = branch.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (branchSegments.Length > remaining.Length)
            {
                return false;
            }

            for (var index = 0; index < branchSegments.Length; index++)
            {
                if (!string.Equals(branchSegments[index], remaining[index], StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        private static string Sanitize(string value)
        {
            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(invalid, '_');
            }

            return value.Replace('/', '_');
        }

        private static void RunGit(string arguments, string workingDirectory)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to launch git.");
            var stdOut = process.StandardOutput.ReadToEnd();
            var stdErr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"git {arguments} failed: {stdErr}{stdOut}");
            }
        }
    }
}
