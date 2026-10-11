using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.Mediarow.HomeScreen;

/// <summary>
/// A self-contained Mediarow Home section.
/// </summary>
public interface IHomeSection
{
    string Id { get; }

    string DisplayTitle { get; }

    int Order { get; }

    int Limit { get; }

    string? Route { get; }

    string? AdditionalData { get; }

    bool Enabled { get; }

    Task<QueryResult<BaseItemDto>> GetResultsAsync(
        HomeSectionRequest request,
        CancellationToken cancellationToken = default);
}
