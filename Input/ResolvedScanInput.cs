/// <summary>
/// Resolved scan source path and display metadata for web and CLI execution.
/// </summary>
internal sealed record ResolvedScanInput(string ScanPath, string DisplayPath, string? Warning);
