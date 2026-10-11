using Jellyfin.Plugin.Mediarow.Configuration;
using Jellyfin.Plugin.Mediarow.Services;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Jellyfin.Plugin.Mediarow.HomeScreen;

/// <summary>
/// Registers configured virtual catalog sections with Mediarow's internal manager.
/// </summary>
public sealed class HomeSectionBootstrapper
{
    private readonly HomeSectionManager _manager;
    private readonly VirtualCatalogService _catalogService;
    private readonly DynamicItemCache _itemCache;
    private readonly VirtualResumeTracker _resumeTracker;
    private readonly ILogger<HomeSectionBootstrapper> _logger;
    private readonly object _syncRoot = new();
    private string? _registeredConfiguration;

    public HomeSectionBootstrapper(
        HomeSectionManager manager,
        VirtualCatalogService catalogService,
        DynamicItemCache itemCache,
        VirtualResumeTracker resumeTracker,
        ILogger<HomeSectionBootstrapper> logger)
    {
        _manager = manager;
        _catalogService = catalogService;
        _itemCache = itemCache;
        _resumeTracker = resumeTracker;
        _logger = logger;
    }

    public void RegisterConfiguredSections(PluginConfiguration? configuration = null)
    {
        lock (_syncRoot)
        {
            PluginConfiguration config = configuration ?? MediarowPlugin.Instance?.Configuration ?? new PluginConfiguration();
            string snapshot = JsonSerializer.Serialize(config);
            if (snapshot == _registeredConfiguration)
            {
                return;
            }

            RegisterSections(config);
            _registeredConfiguration = snapshot;
        }
    }

    /// <summary>
    /// Builds every enabled catalog row once so the first Home visit after a restart is served from cache.
    /// </summary>
    public async Task WarmCatalogRowsAsync(CancellationToken cancellationToken)
    {
        PluginConfiguration config = MediarowPlugin.Instance?.Configuration ?? new PluginConfiguration();
        IReadOnlyList<string> categories = HomeRowContentFilter.GetCategories(config);
        var work = _manager.GetSections()
            .OfType<VirtualCatalogHomeSection>()
            .Where(section => section.Enabled)
            .SelectMany(section => categories.Select(category => (Section: section, Category: category)))
            .ToList();

        await Parallel.ForEachAsync(work, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, async (entry, token) =>
        {
            try
            {
                await entry.Section.GetResultsAsync(new HomeSectionRequest
                {
                    Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["contentCategory"] = entry.Category }
                }, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "[Mediarow] Could not prepare Home row {SectionId} for {Category}", entry.Section.Id, entry.Category);
            }
        }).ConfigureAwait(false);

        _logger.LogInformation("[Mediarow] Prepared {Count} Home rows in the background", work.Count);
    }

    private void RegisterSections(PluginConfiguration config)
    {
        var sections = new List<IHomeSection>();
        if (config.HomeSections.Count > 0)
        {
            foreach (HomeSectionDefinition definition in config.HomeSections.OrderBy(section => section.Order))
            {
                if (definition.Id.Equals("Mediarow_ContinueWatching", StringComparison.OrdinalIgnoreCase))
                {
                    sections.Add(CreateContinueWatching(config, definition));
                }
                else
                {
                    sections.Add(CreateCatalogSection(definition, config.EnableHomeRows));
                }
            }

            if (!config.HomeSections.Any(section =>
                section.Id.Equals("Mediarow_ContinueWatching", StringComparison.OrdinalIgnoreCase)))
            {
                sections.Add(CreateContinueWatching(config));
            }

            _manager.ReplaceSections(sections);
            _logger.LogInformation("[Mediarow] Registered {Count} configured internal Home sections", sections.Count);
            return;
        }

        sections.Add(CreateContinueWatching(config));
        int order = 0;

        if (config.EnablePopularHomeRow)
        {
            sections.Add(CreateCatalogSection(new HomeSectionDefinition
            {
                Id = "Mediarow_Popular",
                DisplayTitle = "Popular",
                Order = order++,
                Limit = config.HomeRowItemCount,
                Enabled = config.EnableHomeRows
            }));
        }

        foreach (string genre in config.HomeRowGenres.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(genre))
            {
                continue;
            }

            sections.Add(CreateCatalogSection(new HomeSectionDefinition
            {
                Id = $"Mediarow_{NormalizeId(genre)}",
                DisplayTitle = genre,
                AdditionalData = genre,
                Order = order++,
                Limit = config.HomeRowItemCount,
                Enabled = config.EnableHomeRows
            }));
        }

        _manager.ReplaceSections(sections);
        _logger.LogInformation("[Mediarow] Registered {Count} internal virtual Home sections", _manager.GetSections().Count);
    }

    private IHomeSection CreateCatalogSection(HomeSectionDefinition definition, bool homeRowsEnabled = true)
    {
        var effectiveDefinition = new HomeSectionDefinition
        {
            Id = definition.Id,
            DisplayTitle = definition.DisplayTitle,
            Order = definition.Order,
            Limit = definition.Limit,
            Route = definition.Route,
            AdditionalData = definition.AdditionalData,
            Enabled = definition.Enabled && homeRowsEnabled,
            Parameters = definition.Parameters
        };
        return new VirtualCatalogHomeSection(_catalogService, effectiveDefinition);
    }

    private IHomeSection CreateContinueWatching(PluginConfiguration config)
    {
        return CreateContinueWatching(config, new HomeSectionDefinition
        {
            Id = "Mediarow_ContinueWatching",
            DisplayTitle = "Continue Watching",
            Order = -1,
            Limit = config.HomeRowItemCount,
            Enabled = config.EnableHomeRows
        });
    }

    private IHomeSection CreateContinueWatching(PluginConfiguration config, HomeSectionDefinition definition)
    {
        var effectiveDefinition = new HomeSectionDefinition
        {
            Id = definition.Id,
            DisplayTitle = definition.DisplayTitle,
            Order = definition.Order,
            Limit = definition.Limit,
            Route = definition.Route,
            AdditionalData = definition.AdditionalData,
            Enabled = definition.Enabled && config.EnableHomeRows,
            Parameters = definition.Parameters
        };
        return new VirtualContinueWatchingSection(_itemCache, _resumeTracker, effectiveDefinition);
    }

    private static string NormalizeId(string value)
    {
        return new string(value.Where(char.IsLetterOrDigit).ToArray());
    }
}
