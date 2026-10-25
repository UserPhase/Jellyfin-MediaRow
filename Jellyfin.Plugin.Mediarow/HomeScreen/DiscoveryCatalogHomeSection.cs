using Jellyfin.Plugin.Mediarow.Models;
using Jellyfin.Plugin.Mediarow.Services;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.Mediarow.HomeScreen;

/// <summary>
/// Home row backed by an admin-selected catalog (explicit Stremio catalog or structured TMDB discovery).
/// </summary>
public sealed class DiscoveryCatalogHomeSection : IHomeSection
{
    private readonly VirtualCatalogService _catalogService;
    private readonly HomeSectionDefinition _definition;

    public DiscoveryCatalogHomeSection(
        VirtualCatalogService catalogService,
        HomeCatalogSelection selection,
        HomeSectionDefinition definition)
    {
        _catalogService = catalogService;
        Selection = selection;
        _definition = definition;
    }

    public HomeCatalogSelection Selection { get; }

    public string Id => _definition.Id;

    public string DisplayTitle => _definition.DisplayTitle;

    public int Order => _definition.Order;

    public int Limit => _definition.Limit;

    public string? Route => _definition.Route;

    public string? AdditionalData => _definition.AdditionalData;

    public bool Enabled => _definition.Enabled;

    public async Task<QueryResult<BaseItemDto>> GetResultsAsync(
        HomeSectionRequest request,
        CancellationToken cancellationToken = default)
    {
        request.Parameters.TryGetValue("contentCategory", out var contentCategory);
        IReadOnlyList<BaseItemDto> items = await _catalogService.GetCatalogRowAsync(Selection, Limit, cancellationToken, contentCategory)
            .ConfigureAwait(false);
        return new QueryResult<BaseItemDto>(items.Take(Limit).ToArray());
    }
}
