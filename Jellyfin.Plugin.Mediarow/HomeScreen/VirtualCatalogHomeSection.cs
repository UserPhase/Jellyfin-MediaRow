using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Jellyfin.Plugin.Mediarow.Services;

namespace Jellyfin.Plugin.Mediarow.HomeScreen;

/// <summary>
/// Generic Home section backed by Mediarow virtual catalog results.
/// </summary>
public sealed class VirtualCatalogHomeSection : IHomeSection
{
    private readonly VirtualCatalogService _catalogService;
    private readonly HomeSectionDefinition _definition;

    public VirtualCatalogHomeSection(
        VirtualCatalogService catalogService,
        HomeSectionDefinition definition)
    {
        _catalogService = catalogService;
        _definition = definition;
    }

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
        IReadOnlyList<BaseItemDto> items = _definition.Id.Equals("Mediarow_Popular", StringComparison.OrdinalIgnoreCase)
            ? await _catalogService.GetPopularVirtualItemsAsync(cancellationToken, contentCategory)
            : await _catalogService.GetVirtualItemsByGenreAsync(_definition.AdditionalData ?? _definition.DisplayTitle, cancellationToken, contentCategory);

        return new QueryResult<BaseItemDto>(items.Take(Limit).ToArray());
    }
}
