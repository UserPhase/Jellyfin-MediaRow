using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Mediarow.Models;

[JsonConverter(typeof(JsonStringEnumConverter<DiagnosticStatus>))]
public enum DiagnosticStatus
{
    Pass,
    Warning,
    Fail,
    Skipped
}

/// <summary>One diagnostic probe. Never contains URLs, API keys or tokens; hosts are reported without paths.</summary>
public sealed record DiagnosticCheckResult(
    string Id,
    string Name,
    string Category,
    DiagnosticStatus Status,
    string Message,
    long DurationMs,
    IReadOnlyDictionary<string, string>? Details = null,
    string? Hint = null);

public sealed record DiagnosticsReport(
    DiagnosticStatus Status,
    DateTimeOffset StartedAt,
    long DurationMs,
    string PluginVersion,
    int Passed,
    int Warnings,
    int Failed,
    int Skipped,
    IReadOnlyList<DiagnosticCheckResult> Checks);

public sealed record DiagnosticsOptions
{
    /// <summary>Runs ffprobe against a real stream (slowest check, typically 3-15 seconds).</summary>
    public bool ProbeStream { get; init; } = true;

    /// <summary>Title used for the stream checks. The Shawshank Redemption is on every debrid service.</summary>
    public string ImdbId { get; init; } = "tt0111161";
}
