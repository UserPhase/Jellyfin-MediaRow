using Jellyfin.Plugin.Mediarow.Services;
using Jellyfin.Plugin.Mediarow.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.Mediarow.HomeScreen;

/// <summary>
/// Displays partially watched virtual Mediarow items for the requesting user.
/// </summary>
public sealed class VirtualContinueWatchingSection : IHomeSection
{
    private readonly DynamicItemCache _itemCache;
    private readonly VirtualResumeTracker _resumeTracker;
    private readonly HomeSectionDefinition _definition;

    public VirtualContinueWatchingSection(
        DynamicItemCache itemCache,
        VirtualResumeTracker resumeTracker,
        HomeSectionDefinition definition)
    {
        _itemCache = itemCache;
        _resumeTracker = resumeTracker;
        _definition = definition;
    }

    public string Id => _definition.Id;
    public string DisplayTitle => _definition.DisplayTitle;
    public int Order => _definition.Order;
    public int Limit => _definition.Limit;
    public string? Route => _definition.Route;
    public string? AdditionalData => _definition.AdditionalData;
    public bool Enabled => _definition.Enabled;

    public Task<QueryResult<BaseItemDto>> GetResultsAsync(HomeSectionRequest request, CancellationToken cancellationToken = default)
    {
        var config = MediarowPlugin.Instance?.Configuration ?? new PluginConfiguration();
        request.Parameters.TryGetValue("contentCategory", out var contentCategory);
        BaseItemDto[] items = _resumeTracker.GetTrackedItemSnapshots(request.UserId)
            .Select(snapshot => VirtualItemProjection.ForUser(
                _itemCache.GetItem(snapshot.Item.Id) ?? snapshot.Item,
                new UserItemDataDto
                {
                    Key = snapshot.Item.Id.ToString("N"),
                    PlaybackPositionTicks = snapshot.PlaybackPositionTicks,
                    LastPlayedDate = snapshot.LastPlayedDate
                }))
            .Where(item => HomeRowContentFilter.Matches(item, config, contentCategory))
            .OrderByDescending(item => item.UserData?.LastPlayedDate)
            .Take(Limit)
            .ToArray();
        return Task.FromResult(new QueryResult<BaseItemDto>(items));
    }
}
