using System.Diagnostics;
using System.Text.Json;

/// <summary>
/// Resolves a GitHub tree URL to a sparse local checkout directory.
/// </summary>
internal static class GitHubFolderMaterializer
{
    private static readonly HttpClient Http = CreateHttpClient();

    /// <summary>
    /// Materializes a GitHub tree URL into a local folder path in cache.
    /// </summary>
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

        var cacheRoot = Path.Combine(ScanInputResolver.GetCacheRoot(), owner, repo, Sanitize(branch));
        var repoUrl = $"https://github.com/{owner}/{repo}.git";

        EnsureValidCacheRepository(cacheRoot);

        if (!Directory.Exists(cacheRoot) || !Directory.Exists(Path.Combine(cacheRoot, ".git")))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cacheRoot)!);
            if (Directory.Exists(cacheRoot))
            {
                ScanInputResolver.ForceDeleteDirectory(cacheRoot);
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

    /// <summary>
    /// Ensure valid cache repository.
    /// </summary>
    private static void EnsureValidCacheRepository(string cacheRoot)
    {
        if (!Directory.Exists(cacheRoot))
        {
            return;
        }

        if (!Directory.Exists(Path.Combine(cacheRoot, ".git")))
        {
            ScanInputResolver.ForceDeleteDirectory(cacheRoot);
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
            ScanInputResolver.ForceDeleteDirectory(cacheRoot);
            return;
        }

        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            ScanInputResolver.ForceDeleteDirectory(cacheRoot);
        }
    }

    /// <summary>
    /// Get branches async.
    /// </summary>
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

    /// <summary>
    /// Is prefix match.
    /// </summary>
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

    /// <summary>
    /// Sanitize.
    /// </summary>
    private static string Sanitize(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalid, '_');
        }

        return value.Replace('/', '_');
    }

    /// <summary>
    /// Run git.
    /// </summary>
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

    /// <summary>
    /// Create http client.
    /// </summary>
    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("mc-nbt-tool/1.0");
        return client;
    }
}
