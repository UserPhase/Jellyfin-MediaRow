namespace Jellyfin.Plugin.Mediarow.HomeScreen;

/// <summary>
/// User and request context supplied to a Home section.
/// </summary>
public sealed class HomeSectionRequest
{
    public Guid UserId { get; init; }

    public string? Language { get; init; }

    public IReadOnlyDictionary<string, string> Parameters { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
