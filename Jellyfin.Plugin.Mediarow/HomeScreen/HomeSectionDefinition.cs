namespace Jellyfin.Plugin.Mediarow.HomeScreen;

/// <summary>
/// Persistable definition for a configurable Home section.
/// </summary>
public sealed class HomeSectionDefinition
{
    public string Id { get; set; } = string.Empty;

    public string DisplayTitle { get; set; } = string.Empty;

    public int Order { get; set; }

    public int Limit { get; set; } = 20;

    public string? Route { get; set; }

    public string? AdditionalData { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets optional provider/query parameters. A list is used because
    /// Jellyfin's XML plugin configuration serializer does not support dictionaries.
    /// </summary>
    public List<HomeSectionParameter> Parameters { get; set; } = new();
}

/// <summary>
/// A configurable provider/query parameter for a Home section.
/// </summary>
public sealed class HomeSectionParameter
{
    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}
