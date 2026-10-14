using System.Security.Cryptography;
using System.Text;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Mediarow.Configuration;
using Jellyfin.Plugin.Mediarow.Services;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.MediaSegments;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.Mediarow.Filters;

/// <summary>
/// Adds intro, recap and credits markers from online databases to episodes that have none, on the standard
/// media-segment endpoint every Jellyfin client uses for "Skip Intro" and the next-episode prompt.
/// Markers from other providers (e.g. Intro Skipper) always win.
/// </summary>
public sealed class SkipSegmentFilter(ILibraryManager libraryManager, SkipSegmentService segments, DynamicItemCache itemCache)
    : IAsyncActionFilter, IOrderedFilter
{
    public int Order => 1;

    public PluginConfiguration? Config { get; init; }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var config = Config ?? MediarowPlugin.Instance?.Configuration;
        var action = (context.ActionDescriptor as ControllerActionDescriptor)?.ActionName;
        if (config is not { EnableOnlineSkipSegments: true } ||
            action is not ("GetItemSegments" or "GetPlaybackInfo" or "GetPostedPlaybackInfo") ||
            !context.ActionArguments.TryGetValue("itemId", out var idValue) || idValue is not Guid itemId)
        {
            await next().ConfigureAwait(false);
            return;
        }

        // Jellyfin Web only asks for segments when the playing source says it has some, and it asks with the
        // media-source id. Online markers are not stored, so flag episode sources and map stream ids to the episode.
        if (action == "GetItemSegments" && libraryManager.GetItemById(itemId) == null && ResolveEpisode(itemId) is { } mapped)
        {
            itemId = mapped.Id;
            context.ActionArguments["itemId"] = itemId;
        }

        var executed = await next().ConfigureAwait(false);
        if (executed.Exception != null)
        {
            return;
        }

        if (action is "GetPlaybackInfo" or "GetPostedPlaybackInfo")
        {
            if (executed.Result is ObjectResult { Value: PlaybackInfoResponse playback } &&
                libraryManager.GetItemById(itemId) is Episode)
            {
                foreach (var source in playback.MediaSources ?? [])
                {
                    source.HasSegments = true;
                }
            }

            return;
        }

        if (action != "GetItemSegments" ||
            executed.Result is not ObjectResult { Value: QueryResult<MediaSegmentDto> found } ||
            libraryManager.GetItemById(itemId) is not Episode episode)
        {
            return;
        }

        var requested = context.ActionArguments.TryGetValue("includeSegmentTypes", out var typesValue) &&
            typesValue is IEnumerable<MediaSegmentType> types && types.Any()
                ? types.ToHashSet()
                : null;
        var merged = await AddSegmentsAsync(episode, found.Items ?? [], requested, context.HttpContext.RequestAborted)
            .ConfigureAwait(false);
        if (merged != null)
        {
            executed.Result = new OkObjectResult(new QueryResult<MediaSegmentDto>(merged));
        }
    }

    /// <summary>Returns the merged list, or null when nothing was added.</summary>
    public async Task<MediaSegmentDto[]?> AddSegmentsAsync(
        Episode episode, IReadOnlyList<MediaSegmentDto> existing, ISet<MediaSegmentType>? requested, CancellationToken cancellationToken)
    {
        var wanted = new[] { MediaSegmentType.Intro, MediaSegmentType.Recap, MediaSegmentType.Outro }
            .Where(type => (requested == null || requested.Contains(type)) && existing.All(segment => segment.Type != type))
            .ToHashSet();
        if (wanted.Count == 0 || episode.IndexNumber is not { } number)
        {
            return null;
        }

        var series = episode.Series;
        string? Id(string name) => series?.GetProviderId(name) is { Length: > 0 } value ? value : null;
        var request = new SkipSegmentRequest(
            Id("Imdb"), Id("Tmdb"), Id("AniList"), Id("Mal") ?? Id("MyAnimeList"),
            episode.ParentIndexNumber, number, episode.RunTimeTicks);
        var added = (await segments.GetSegmentsAsync(request, cancellationToken).ConfigureAwait(false))
            .Where(segment => wanted.Contains(segment.Type))
            .Select(segment => new MediaSegmentDto
            {
                Id = SegmentId(episode.Id, segment.Type),
                ItemId = episode.Id,
                Type = segment.Type,
                StartTicks = segment.StartTicks,
                EndTicks = segment.EndTicks
            })
            .ToList();
        if (added.Count == 0)
        {
            return null;
        }

        return existing.Concat(added).OrderBy(segment => segment.StartTicks).ToArray();
    }

    private Episode? ResolveEpisode(Guid id) =>
        libraryManager.GetItemById(id) as Episode ??
        (itemCache.GetAIOStreamsMapping(id.ToString("N")) is { } mapping ? libraryManager.GetItemById(mapping.ItemId) as Episode : null);

    private static Guid SegmentId(Guid itemId, MediaSegmentType type) =>
        new(MD5.HashData(Encoding.UTF8.GetBytes($"mediarow-segment:{itemId:N}:{type}")));
}
