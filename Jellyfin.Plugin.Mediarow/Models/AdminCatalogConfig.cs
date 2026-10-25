using Jellyfin.Plugin.Mediarow.Providers;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.Mediarow.Models;

/// <summary>
/// Where a discovery row gets its titles from.
/// </summary>
public enum DiscoverySource
{
    /// <summary>The configured catalog provider: the Stremio addon in Stremio mode, otherwise TMDB discovery.</summary>
    Auto = 0,

    /// <summary>A catalog declared in the Stremio addon manifest.</summary>
    Stremio = 1,

    /// <summary>TMDB <c>/discover</c> with structured genre, sort and year filters.</summary>
    Tmdb = 2
}

/// <summary>
/// Sort order for structured discovery. Stremio catalogs keep their own order.
/// </summary>
public enum DiscoverySort
{
    Popular = 0,
    TopRated = 1,
    Newest = 2
}

/// <summary>
/// Content type a configured catalog row covers.
/// </summary>
public enum DiscoveryMediaType
{
    /// <summary>Every content category enabled for Home.</summary>
    All = 0,
    Movie = 1,
    Series = 2
}

/// <summary>
/// An admin-configured Home row backed by an explicit catalog. Stored in the plugin XML configuration,
/// so it keeps public setters and a parameterless constructor.
/// </summary>
public sealed class HomeCatalogSelection
{
    /// <summary>Home section ids of configured catalog rows start with this prefix.</summary>
    public const string SectionPrefix = "MediarowCatalog_";

    /// <summary>Gets or sets the stable id (letters and digits) used to build the Home section id.</summary>
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public int Order { get; set; }

    public DiscoverySource Source { get; set; } = DiscoverySource.Auto;

    /// <summary>Gets or sets the Stremio manifest catalog id. Empty uses the addon's top/popular catalog.</summary>
    public string? CatalogId { get; set; }

    public DiscoveryMediaType MediaType { get; set; } = DiscoveryMediaType.All;

    /// <summary>Gets or sets a genre name, e.g. "Science Fiction" or "Anime". Empty means no genre filter.</summary>
    public string? Genre { get; set; }

    public DiscoverySort Sort { get; set; } = DiscoverySort.Popular;

    /// <summary>Gets or sets a release year filter. Null or zero means any year.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets the minimum community rating (0–10). Zero disables the filter.</summary>
    public double MinimumRating { get; set; }

    public string SectionId => SectionPrefix + Id;
}

/// <summary>
/// One structured upstream query: a single content type against a single catalog.
/// </summary>
public sealed record CatalogDiscoveryRequest
{
    public DiscoverySource Source { get; init; } = DiscoverySource.Auto;

    public CatalogContentType ContentType { get; init; } = CatalogContentType.Movie;

    public string? CatalogId { get; init; }

    public string? Genre { get; init; }

    public DiscoverySort Sort { get; init; } = DiscoverySort.Popular;

    public int? Year { get; init; }

    public double MinimumRating { get; init; }

    /// <summary>Gets a value indicating whether only anime is wanted (Home's Anime category).</summary>
    public bool AnimeOnly { get; init; }

    /// <summary>Gets the key for this query's cached upstream pages.</summary>
    public string CacheKey =>
        $"{Source}|{ContentType}|{CatalogId}|{Genre?.ToLowerInvariant()}|{Sort}|{Year}|{MinimumRating:0.#}|{AnimeOnly}";
}

/// <summary>
/// A page of a catalog for "See All". Items, StartIndex and TotalRecordCount keep the standard
/// Jellyfin <see cref="QueryResult{T}"/> shape so existing clients can read it unchanged.
/// </summary>
public sealed class CatalogPageResult : QueryResult<BaseItemDto>
{
    public CatalogPageResult(IReadOnlyList<BaseItemDto> items, int pageIndex, int pageSize, bool hasMore)
        : base(items)
    {
        PageIndex = pageIndex;
        PageSize = pageSize;
        HasMore = hasMore;
        StartIndex = pageIndex * pageSize;
        // Upstream catalogs rarely report totals: when more pages exist this is a lower bound.
        TotalRecordCount = StartIndex + items.Count + (hasMore ? pageSize : 0);
    }

    public string CatalogId { get; init; } = string.Empty;

    public string DisplayTitle { get; init; } = string.Empty;

    public int PageIndex { get; }

    public int PageSize { get; }

    public bool HasMore { get; }
}

/// <summary>
/// A catalog the admin can add to Home.
/// </summary>
public sealed record CatalogDescriptor(
    DiscoverySource Source,
    string CatalogId,
    string Name,
    string Type,
    IReadOnlyList<string> Genres,
    bool SupportsPaging,
    bool SupportsGenre);

/// <summary>
/// Catalogs and genres available for admin catalog selection.
/// </summary>
public sealed record CatalogOptions(
    IReadOnlyList<CatalogDescriptor> Catalogs,
    IReadOnlyList<string> MovieGenres,
    IReadOnlyList<string> SeriesGenres,
    bool StremioConfigured,
    bool TmdbConfigured);
