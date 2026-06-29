/// <summary>
/// Represents a cached scan report entry with fingerprint and validation timing.
/// </summary>
internal sealed record CachedScanReport(string Fingerprint, BlueprintScanReport Report, DateTime CachedAtUtc, DateTime NextValidationUtc);
