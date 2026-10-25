using Jellyfin.Plugin.Mediarow.Services;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediarow.HomeScreen;

/// <summary>
/// Owns Mediarow Home sections and executes their virtual catalog providers.
/// </summary>
public sealed class HomeSectionManager
{
    private readonly IMemoryCache _cache;
    private readonly ILogger<HomeSectionManager> _logger;
    private readonly UserPreferenceStore? _preferences;
    private readonly Dictionary<string, IHomeSection> _sections = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _syncRoot = new();
    private long _configurationVersion;

    public HomeSectionManager(
        IMemoryCache cache,
        ILogger<HomeSectionManager> logger,
        UserPreferenceStore? preferences = null)
    {
        _cache = cache;
        _logger = logger;
        _preferences = preferences;
    }

    public IReadOnlyList<IHomeSection> GetSections()
    {
        lock (_syncRoot)
        {
            return _sections.Values
                .Where(section => section.Enabled)
                .OrderBy(section => section.Order)
                .ThenBy(section => section.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public void Register(IHomeSection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        lock (_syncRoot)
        {
            _sections[section.Id] = section;
        }
    }

    public void ReplaceSections(IEnumerable<IHomeSection> sections)
    {
        var replacement = sections.ToArray();
        lock (_syncRoot)
        {
            _sections.Clear();
            foreach (var section in replacement)
            {
                _sections[section.Id] = section;
            }

            _configurationVersion++;
        }
    }

    public bool TryGet(string id, out IHomeSection? section)
    {
        lock (_syncRoot)
        {
            return _sections.TryGetValue(id, out section);
        }
    }

    public async Task<QueryResult<BaseItemDto>> ExecuteAsync(
        string id,
        HomeSectionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!TryGet(id, out IHomeSection? section) || section == null || !section.Enabled)
        {
            return EmptyResult();
        }

        if (_preferences != null && request.UserId != Guid.Empty && _preferences.Get(request.UserId).IsSectionHidden(section.Id))
        {
            return EmptyResult();
        }

        string cacheKey;
        lock (_syncRoot)
        {
            cacheKey = $"{_configurationVersion}:{BuildCacheKey(section, request)}";
        }
        if (_cache.TryGetValue<QueryResult<BaseItemDto>>(cacheKey, out QueryResult<BaseItemDto>? cached) && cached != null)
        {
            return cached;
        }

        try
        {
            QueryResult<BaseItemDto> result = await section.GetResultsAsync(request, cancellationToken);
            BaseItemDto[] limitedItems = (result.Items ?? Array.Empty<BaseItemDto>())
                .Where(item => item != null)
                .GroupBy(item => item.Id)
                .Select(group => group.First())
                .Take(Math.Clamp(section.Limit, 1, 100))
                .ToArray();
            QueryResult<BaseItemDto> limitedResult = new(limitedItems);
            _cache.Set(cacheKey, limitedResult, TimeSpan.FromMinutes(5));
            return limitedResult;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Mediarow] Home section {SectionId} failed for user {UserId}", id, request.UserId);
            return EmptyResult();
        }
    }

    private static QueryResult<BaseItemDto> EmptyResult()
    {
        return new QueryResult<BaseItemDto>(Array.Empty<BaseItemDto>());
    }

    private static string BuildCacheKey(IHomeSection section, HomeSectionRequest request)
    {
        string parameters = string.Join(";", request.Parameters.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}"));
        return $"mediarow-home-section:{request.UserId}:{section.Id}:{section.Limit}:{parameters}";
    }
}
