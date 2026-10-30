using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Mediarow.HomeScreen;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediarow.Services;

/// <summary>Owns per-user resume positions and snapshots in a single atomically replaced document.</summary>
public sealed class VirtualResumeTracker
{
    private readonly ILogger<VirtualResumeTracker> _logger;
    private readonly string _directory;
    private readonly object _lock = new();
    private readonly Dictionary<string, Dictionary<string, Entry>> _state;
    private string StoragePath => Path.Combine(_directory, "virtual-resume-state.json");

    private sealed class Entry
    {
        public BaseItemDto? Item { get; set; }
        public long PositionTicks { get; set; }
        public DateTime LastPlayedDate { get; set; }
    }

    public VirtualResumeTracker(IApplicationPaths applicationPaths, ILogger<VirtualResumeTracker> logger)
    {
        _logger = logger;
        _directory = Path.Combine(applicationPaths.PluginConfigurationsPath, "Jellyfin.Plugin.Mediarow");
        _state = Load();
    }

    public void SaveResumeTicks(Guid userId, Guid itemId, long playbackPositionTicks)
    {
        if (userId == Guid.Empty || itemId == Guid.Empty) return;
        lock (_lock)
        {
            if (playbackPositionTicks <= 0) { RemoveCore(userId, itemId); return; }
            var items = GetOrCreate(userId);
            var key = itemId.ToString("N");
            items.TryGetValue(key, out var existing);
            var entry = new Entry { Item = existing?.Item, PositionTicks = playbackPositionTicks, LastPlayedDate = DateTime.UtcNow };
            if (entry.Item != null) entry.Item = ForUser(entry.Item, UserData(itemId, entry));
            items[key] = entry;
            Save();
        }
    }

    public void SaveResumeItem(Guid userId, BaseItemDto item, long playbackPositionTicks)
    {
        if (userId == Guid.Empty || item.Id == Guid.Empty) return;
        lock (_lock)
        {
            if (playbackPositionTicks <= 0) { RemoveCore(userId, item.Id); return; }
            var entry = new Entry { PositionTicks = playbackPositionTicks, LastPlayedDate = DateTime.UtcNow };
            entry.Item = ForUser(item, UserData(item.Id, entry));
            GetOrCreate(userId)[item.Id.ToString("N")] = entry;
            Save();
        }
    }

    public void RemoveResumeItem(Guid userId, Guid itemId)
    {
        lock (_lock) RemoveCore(userId, itemId);
    }

    /// <summary>Finds this account's copies of a title, including its former virtual identity.</summary>
    public IReadOnlyList<Guid> GetMatchingItemIds(Guid userId, BaseItemDto identity)
    {
        lock (_lock)
        {
            if (!_state.TryGetValue(userId.ToString("N"), out var items)) return Array.Empty<Guid>();
            return items.Where(pair => Guid.TryParse(pair.Key, out var id) &&
                    (id == identity.Id || pair.Value.Item is { } saved && SameTitle(saved, identity)))
                .Select(pair => Guid.Parse(pair.Key)).ToArray();
        }
    }

    public void RemoveResumeItems(Guid userId, IEnumerable<Guid> itemIds)
    {
        lock (_lock)
        {
            if (!_state.TryGetValue(userId.ToString("N"), out var items)) return;
            bool changed = false;
            foreach (Guid id in itemIds) changed |= items.Remove(id.ToString("N"));
            if (!changed) return;
            if (items.Count == 0) _state.Remove(userId.ToString("N"));
            Save();
        }
    }

    public void UpdateResumeItems(Guid userId, IEnumerable<Guid> itemIds, long playbackPositionTicks)
    {
        if (playbackPositionTicks <= 0) { RemoveResumeItems(userId, itemIds); return; }
        lock (_lock)
        {
            bool changed = false;
            foreach (Guid id in itemIds)
            {
                var entry = Find(userId, id);
                if (entry == null) continue;
                entry.PositionTicks = playbackPositionTicks;
                entry.LastPlayedDate = DateTime.UtcNow;
                if (entry.Item != null) entry.Item = ForUser(entry.Item, UserData(id, entry));
                changed = true;
            }
            if (changed) Save();
        }
    }

    private static bool SameTitle(BaseItemDto left, BaseItemDto right)
    {
        if (left.Type != right.Type) return false;
        bool ProviderMatches(params string[] names) => names.Any(name =>
            left.ProviderIds?.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value is string a &&
            right.ProviderIds?.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value is string b &&
            !string.IsNullOrWhiteSpace(a) && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase));

        if (left.Type is BaseItemKind.Movie or BaseItemKind.Series)
            return ProviderMatches("Imdb", "Tmdb", "Tvdb", "AniList");
        if (left.Type != BaseItemKind.Episode) return false;
        // Episode-specific IDs are safe across imports. An IMDb series ID alone must never
        // cause completion of one episode to clear a sibling episode's progress.
        if (ProviderMatches("Tvdb", "Tmdb", "Stremio")) return true;
        return left.ParentIndexNumber is >= 0 && left.IndexNumber is > 0 &&
            left.ParentIndexNumber == right.ParentIndexNumber && left.IndexNumber == right.IndexNumber &&
            (left.SeriesId is { } series && series != Guid.Empty && left.SeriesId == right.SeriesId ||
                ProviderMatches("SeriesImdb", "SeriesTmdb", "SeriesTvdb", "SeriesAniList"));
    }

    private void RemoveCore(Guid userId, Guid itemId)
    {
        if (!_state.TryGetValue(userId.ToString("N"), out var items) || !items.Remove(itemId.ToString("N"))) return;
        if (items.Count == 0) _state.Remove(userId.ToString("N"));
        Save(); // Position and snapshot disappear in the same durable replacement.
    }

    public long GetResumeTicks(Guid userId, Guid itemId)
    {
        lock (_lock) return Find(userId, itemId)?.PositionTicks ?? 0;
    }

    public BaseItemDto? GetTrackedItem(Guid userId, Guid itemId)
    {
        lock (_lock)
        {
            var entry = Find(userId, itemId);
            return entry?.Item == null ? null : ForUser(entry.Item, UserData(itemId, entry));
        }
    }

    public IReadOnlyDictionary<Guid, long> GetResumeTicksForUser(Guid userId)
    {
        lock (_lock) return _state.TryGetValue(userId.ToString("N"), out var items)
            ? items.Where(pair => pair.Value.PositionTicks > 0 && Guid.TryParse(pair.Key, out _))
                .ToDictionary(pair => Guid.Parse(pair.Key), pair => pair.Value.PositionTicks)
            : new Dictionary<Guid, long>();
    }

    public UserItemDataDto? GetUserData(Guid userId, Guid itemId)
    {
        lock (_lock)
        {
            var entry = Find(userId, itemId);
            return entry?.PositionTicks > 0 ? UserData(itemId, entry) : null;
        }
    }

    public IReadOnlyList<VirtualResumeItem> GetTrackedItemSnapshots(Guid userId)
    {
        lock (_lock) return _state.TryGetValue(userId.ToString("N"), out var items)
            ? items.Values.Where(entry => entry.Item != null && entry.PositionTicks > 0)
                .OrderByDescending(entry => entry.LastPlayedDate).Select(entry => new VirtualResumeItem
                {
                    Item = ForUser(entry.Item!, UserData(entry.Item!.Id, entry)),
                    PlaybackPositionTicks = entry.PositionTicks, LastPlayedDate = entry.LastPlayedDate
                }).ToArray()
            : Array.Empty<VirtualResumeItem>();
    }

    private Entry? Find(Guid userId, Guid itemId) =>
        _state.TryGetValue(userId.ToString("N"), out var items) && items.TryGetValue(itemId.ToString("N"), out var entry) ? entry : null;

    private Dictionary<string, Entry> GetOrCreate(Guid userId)
    {
        var key = userId.ToString("N");
        if (!_state.TryGetValue(key, out var items)) _state[key] = items = new(StringComparer.OrdinalIgnoreCase);
        return items;
    }

    private static UserItemDataDto UserData(Guid itemId, Entry entry) => new()
    {
        Key = itemId.ToString("N"), PlaybackPositionTicks = entry.PositionTicks,
        Played = false, PlayCount = 0, IsFavorite = false, LastPlayedDate = entry.LastPlayedDate
    };

    private static BaseItemDto ForUser(BaseItemDto item, UserItemDataDto userData)
    {
        // Snapshots outlive the catalog cache. Detach nested sources/provider dictionaries as well
        // as UserData so callers cannot mutate either another response or the persisted snapshot.
        var copy = JsonSerializer.Deserialize<BaseItemDto>(JsonSerializer.Serialize(item))!;
        copy.UserData = VirtualItemProjection.CloneUserData(userData);
        return copy;
    }

    private Dictionary<string, Dictionary<string, Entry>> Load()
    {
        try
        {
            if (File.Exists(StoragePath))
                return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, Entry>>>(File.ReadAllText(StoragePath)) ?? new();
            // Import the previous two-file format once. An empty canonical document prevents resurrection.
            var positionsPath = Path.Combine(_directory, "virtual-resume.json");
            var snapshotsPath = Path.Combine(_directory, "virtual-resume-items.json");
            var positions = File.Exists(positionsPath)
                ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, long>>>(File.ReadAllText(positionsPath)) : null;
            var snapshots = File.Exists(snapshotsPath)
                ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, VirtualResumeItem>>>(File.ReadAllText(snapshotsPath)) : null;
            var result = new Dictionary<string, Dictionary<string, Entry>>(StringComparer.OrdinalIgnoreCase);
            foreach (var user in positions ?? [])
            {
                var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in user.Value.Where(item => item.Value > 0))
                {
                    VirtualResumeItem? snapshot = null;
                    if (snapshots?.TryGetValue(user.Key, out var saved) == true) saved.TryGetValue(item.Key, out snapshot);
                    entries[item.Key] = new Entry { Item = snapshot?.Item, PositionTicks = item.Value, LastPlayedDate = snapshot?.LastPlayedDate ?? DateTime.MinValue };
                }
                result[user.Key] = entries;
            }
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning("[Mediarow] Could not load virtual resume state ({ErrorType})", ex.GetType().Name);
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Save()
    {
        var temporary = StoragePath + ".tmp";
        try
        {
            Directory.CreateDirectory(_directory);
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, _state);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, StoragePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _logger.LogWarning("[Mediarow] Could not save virtual resume state ({ErrorType})", ex.GetType().Name); }
    }
}
