using Jellyfin.Plugin.Mediarow.Api;
using Jellyfin.Plugin.Mediarow.Filters;
using Jellyfin.Plugin.Mediarow.HomeScreen;
using Jellyfin.Plugin.Mediarow.Web;
using Jellyfin.Plugin.Mediarow.Providers;
using Jellyfin.Plugin.Mediarow.ScheduledTasks;
using Jellyfin.Plugin.Mediarow.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Mediarow;

public class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost host)
    {
        // Register memory cache if not already registered
        services.AddMemoryCache();

        // Register HttpClient for image proxying
        services.AddHttpClient();

        // Register HttpContextAccessor for accessing request context
        services.AddHttpContextAccessor();

        // Register API clients
        services.AddSingleton<ITvdbClient, TvdbClient>();
        services.AddSingleton<ITmdbClient, TmdbClient>();
        services.AddSingleton<IEmbedarrClient, EmbedarrClient>();
        services.AddSingleton<IAIOStreamsClient, AIOStreamsClient>();
        services.AddSingleton<AniListClient>();
        services.AddSingleton<IOpenSubtitlesClient, OpenSubtitlesClient>();
        services.AddSingleton<IHlsProbeService, HlsProbeService>();

        // Register catalog providers
        services.AddSingleton<DirectCatalogProvider>();
        services.AddSingleton<StremioCatalogProvider>();

        // Register caches and services
        services.AddSingleton<DynamicItemCache>();
        services.AddSingleton<SearchResultFactory>();
        services.AddSingleton<MediarowService>();
        services.AddSingleton<VirtualCatalogService>();
        services.AddSingleton<HomeSectionManager>();
        services.AddSingleton<HomeSectionBootstrapper>();
        services.AddSingleton<HomeSectionUserSettingsStore>();
        services.AddSingleton<MediarowWebIntegrationStartupFilter>();
        services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter>(provider =>
            provider.GetRequiredService<MediarowWebIntegrationStartupFilter>());
        services.AddSingleton<SubtitleService>();
        services.AddSingleton<PersistenceService>();
        services.AddSingleton<SeriesResumeResolver>();
        services.AddSingleton<VirtualResumeTracker>();
        services.AddSingleton<DynamicAioStreamsMediaSourceProvider>();
        services.AddSingleton<IMediaSourceProvider>(provider => provider.GetRequiredService<DynamicAioStreamsMediaSourceProvider>());
        services.AddSingleton<PlaybackItemResolver>();
        services.AddSingleton<AioStreamsHttpRelay>();
        services.AddSingleton<AioStreamsPlaybackFilter>();
        services.AddSingleton<LanguagePreferenceFilter>();
        services.AddSingleton<SkipSegmentService>();
        services.AddSingleton<SkipSegmentFilter>();
        services.AddScoped<MediaBrowser.Controller.Events.IEventConsumer<PlaybackStartEventArgs>, LanguagePlaybackStartConsumer>();
        services.AddScoped<MediaBrowser.Controller.Events.IEventConsumer<PlaybackProgressEventArgs>, LanguageTrackChangeConsumer>();
        services.AddScoped<MediaBrowser.Controller.Events.IEventConsumer<PlaybackStopEventArgs>, LanguageTrackChangeConsumer>();
        services.AddSingleton<AppLibraryService>();

        // Register filters
        services.AddSingleton<AppLibraryFilter>();
        services.AddSingleton<SearchActionFilter>();
        services.AddSingleton<ItemLookupFilter>();
        services.AddSingleton<ImageFilter>();
        services.AddSingleton<DynamicItemEndpointsFilter>();
        services.AddSingleton<SeasonEpisodeFilter>();
        services.AddSingleton<SubtitleFilter>();
        services.AddSingleton<VirtualPlaybackProgressFilter>();

        // Register filters with MVC
        services.PostConfigure<Microsoft.AspNetCore.Mvc.MvcOptions>(options =>
        {
            options.Filters.AddService<AppLibraryFilter>(order: -1);           // App libraries for native/TV clients
            options.Filters.AddService<ImageFilter>(order: 0);                  // Run first for images
            options.Filters.AddService<ItemLookupFilter>(order: 0);             // Run first for item lookups
            options.Filters.AddService<DynamicItemEndpointsFilter>(order: 0);   // Handle secondary endpoints
            options.Filters.AddService<SeasonEpisodeFilter>(order: 0);          // Handle seasons/episodes
            options.Filters.AddService<AioStreamsPlaybackFilter>(order: 0);
            options.Filters.AddService<LanguagePreferenceFilter>(order: 1);    // After AIOStreams source selection
            options.Filters.AddService<SkipSegmentFilter>(order: 1);            // Online intro/credits markers
            options.Filters.AddService<SubtitleFilter>(order: 0);               // Handle subtitles for dynamic items
            options.Filters.AddService<VirtualPlaybackProgressFilter>(order: 1);
            options.Filters.AddService<SearchActionFilter>(order: 1);
        });

        // Register scheduled tasks
        services.AddSingleton<IScheduledTask, CacheCleanupTask>();
        services.AddSingleton<IScheduledTask, WatchedItemCleanupTask>();
        services.AddSingleton<IScheduledTask, ApiHealthCheckTask>();
        services.AddSingleton<IScheduledTask, HomeSectionStartupTask>();
    }
}
