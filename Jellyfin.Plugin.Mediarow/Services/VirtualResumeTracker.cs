using System.Text.Json;
using Jellyfin.Plugin.Mediarow.HomeScreen;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediarow.Services;

/// <summary>
/// Persists resume positions for virtual/dynamic items so they can surface in Jellyfin's
/// continue-watching/home sections even when the item is not stored as a real database item.
/// </summary>
public sealed class VirtualResumeTracker
{
    private readonly ILogger<VirtualResumeTracker> _logger;
    private readonly IApplicationPaths _applicationPaths;
    private readonly object _lock = new();

    public VirtualResumeTracker(
        IApplicationPaths applicationPaths,
        ILogger<VirtualResumeTracker> logger)
    {
        _applicationPaths = applicationPaths;
        _logger = logger;
    }

    private string StorageDirectory => Path.Combine(
        _applicationPaths.PluginConfigurationsPath,
        "Jellyfin.Plugin.Mediarow");

    private string StoragePath => Path.Combine(StorageDirectory, "virtual-resume.json");

    private string SnapshotStoragePath => Path.Combine(StorageDirectory, "virtual-resume-items.json");

    private Dictionary<string, Dictionary<string, long>> LoadState()
    {
        try
        {
            var path = StoragePath;
            Directory.CreateDirectory(StorageDirectory);

            if (!File.Exists(path))
            {
                return new Dictionary<string, Dictionary<string, long>>(StringComparer.OrdinalIgnoreCase);
            }

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new Dictionary<string, Dictionary<string, long>>(StringComparer.OrdinalIgnoreCase);
            }

            var state = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, long>>>(json);
            return state ?? new Dictionary<string, Dictionary<string, long>>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load virtual resume state from {Path}", StoragePath);
            return new Dictionary<string, Dictionary<string, long>>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveState(Dictionary<string, Dictionary<string, long>> state)
    {
        try
        {
            var path = StoragePath;
            Directory.CreateDirectory(StorageDirectory);

            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save virtual resume state to {Path}", StoragePath);
        }
    }

    public void SaveResumeTicks(Guid userId, Guid itemId, long playbackPositionTicks)
    {
        if (playbackPositionTicks <= 0)
        {
            return;
        }

        lock (_lock)
        {
            var state = LoadState();
            var userKey = userId.ToString("N");
            var itemKey = itemId.ToString("N");

            if (!state.TryGetValue(userKey, out var itemMap))
            {
                itemMap = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                state[userKey] = itemMap;
            }

            itemMap[itemKey] = playbackPositionTicks;
            SaveState(state);
        }
    }

    public void SaveResumeItem(Guid userId, BaseItemDto item, long playbackPositionTicks)
    {
        SaveResumeTicks(userId, item.Id, playbackPositionTicks);

        lock (_lock)
        {
            var snapshots = LoadSnapshots();
            var userKey = userId.ToString("N");
            if (!snapshots.TryGetValue(userKey, out var itemMap))
            {
                itemMap = new Dictionary<string, VirtualResumeItem>(StringComparer.OrdinalIgnoreCase);
                snapshots[userKey] = itemMap;
            }

            // Snapshot a private copy: the caller's item may be a shared cache entry.
            var snapshot = VirtualItemProjection.ForUser(item, GetUserData(userId, item.Id));
            itemMap[item.Id.ToString("N")] = new VirtualResumeItem
            {
                Item = snapshot,
                PlaybackPositionTicks = playbackPositionTicks,
                LastPlayedDate = DateTime.UtcNow
            };
            SaveSnapshots(snapshots);
        }
    }

    public long GetResumeTicks(Guid userId, Guid itemId)
    {
        lock (_lock)
        {
            var state = LoadState();
            var userKey = userId.ToString("N");
            var itemKey = itemId.ToString("N");

            if (!state.TryGetValue(userKey, out var itemMap) || !itemMap.TryGetValue(itemKey, out var ticks))
            {
                return 0;
            }

            return ticks;
        }
    }

    public IReadOnlyDictionary<Guid, long> GetResumeTicksForUser(Guid userId)
    {
        lock (_lock)
        {
            var state = LoadState();
            if (!state.TryGetValue(userId.ToString("N"), out var itemMap))
            {
                return new Dictionary<Guid, long>();
            }

            var result = new Dictionary<Guid, long>();
            foreach (var (key, ticks) in itemMap)
            {
                if (ticks > 0 && Guid.TryParse(key, out var itemId))
                {
                    result[itemId] = ticks;
                }
            }

            return result;
        }
    }

    public UserItemDataDto? GetUserData(Guid userId, Guid itemId)
    {
        var ticks = GetResumeTicks(userId, itemId);
        if (ticks <= 0)
        {
            return null;
        }

        return new UserItemDataDto
        {
            Key = itemId.ToString("N"),
            PlaybackPositionTicks = ticks,
            PlayCount = 0,
            IsFavorite = false,
            Played = false,
            LastPlayedDate = DateTime.UtcNow
        };
    }


    public IReadOnlyList<VirtualResumeItem> GetTrackedItemSnapshots(Guid userId)
    {
        lock (_lock)
        {
            var snapshots = LoadSnapshots();
            return snapshots.TryGetValue(userId.ToString("N"), out var itemMap)
                ? itemMap.Values.Where(item => item.PlaybackPositionTicks > 0).OrderByDescending(item => item.LastPlayedDate).ToArray()
                : Array.Empty<VirtualResumeItem>();
        }
    }

    private Dictionary<string, Dictionary<string, VirtualResumeItem>> LoadSnapshots()
    {
        try
        {
            Directory.CreateDirectory(StorageDirectory);
            if (!File.Exists(SnapshotStoragePath))
            {
                return new Dictionary<string, Dictionary<string, VirtualResumeItem>>(StringComparer.OrdinalIgnoreCase);
            }

            return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, VirtualResumeItem>>>(File.ReadAllText(SnapshotStoragePath))
                ?? new Dictionary<string, Dictionary<string, VirtualResumeItem>>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load virtual resume item snapshots from {Path}", SnapshotStoragePath);
            return new Dictionary<string, Dictionary<string, VirtualResumeItem>>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveSnapshots(Dictionary<string, Dictionary<string, VirtualResumeItem>> snapshots)
    {
        try
        {
            Directory.CreateDirectory(StorageDirectory);
            File.WriteAllText(SnapshotStoragePath, JsonSerializer.Serialize(snapshots, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save virtual resume item snapshots to {Path}", SnapshotStoragePath);
        }
    }
}
