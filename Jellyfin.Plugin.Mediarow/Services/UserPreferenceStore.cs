using System.Text.Json;
using Jellyfin.Plugin.Mediarow.Models;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediarow.Services;

/// <summary>
/// Per-user Home preferences, stored as one JSON document keyed by Jellyfin user id.
/// Readers always get a deep copy, so callers can never mutate the shared state.
/// </summary>
public sealed class UserPreferenceStore
{
    public const string FileName = "user-preferences.json";
    public const string LegacyFileName = "home-sections-users.json";
    public const int MaxNotInterested = 2000;
    public const int MaxSectionIds = 200;
    public const int MaxSectionIdLength = 200;

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly ILogger<UserPreferenceStore> _logger;
    private readonly object _syncRoot = new();
    private readonly object _fileLock = new();
    private Dictionary<Guid, UserHomePreferences> _preferences = new();
    private long _version;
    private long _savedVersion;

    public UserPreferenceStore(IApplicationPaths applicationPaths, ILogger<UserPreferenceStore> logger)
        : this(Path.Combine(applicationPaths.PluginConfigurationsPath, "Jellyfin.Plugin.Mediarow"), logger)
    {
    }

    public UserPreferenceStore(string directory, ILogger<UserPreferenceStore> logger)
    {
        _logger = logger;
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, FileName);
        Load(Path.Combine(directory, LegacyFileName));
    }

    public UserHomePreferences Get(Guid userId)
    {
        lock (_syncRoot)
        {
            return _preferences.TryGetValue(userId, out UserHomePreferences? preferences)
                ? preferences.Clone()
                : new UserHomePreferences();
        }
    }

    public UserHomePreferences Update(Guid userId, UserPreferencesUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        return Mutate(userId, preferences =>
        {
            if (update.SectionOrder != null)
            {
                preferences.SectionOrder = CleanSectionIds(update.SectionOrder);
            }

            if (update.HiddenSections != null)
            {
                preferences.HiddenSections = CleanSectionIds(update.HiddenSections);
                preferences.LegacyEnabledSections = null;
            }

            if (update.HideWatched.HasValue)
            {
                preferences.HideWatched = update.HideWatched.Value;
            }

            if (update.RemoveNotInterested is { Count: > 0 } removed)
            {
                var ids = removed.ToHashSet();
                preferences.NotInterested.RemoveAll(entry => ids.Contains(entry.ItemId));
            }
        });
    }

    /// <summary>
    /// Adds (or refreshes) a dismissed title. The oldest entries are dropped beyond <see cref="MaxNotInterested"/>.
    /// </summary>
    public UserHomePreferences AddNotInterested(Guid userId, NotInterestedEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.ItemId == Guid.Empty)
        {
            throw new ArgumentException("Item id is required.", nameof(entry));
        }

        NotInterestedEntry stored = entry.Clone();
        stored.AddedUtc = DateTime.UtcNow;
        return Mutate(userId, preferences =>
        {
            preferences.NotInterested.RemoveAll(existing => existing.ItemId == stored.ItemId);
            preferences.NotInterested.Add(stored);
            if (preferences.NotInterested.Count > MaxNotInterested)
            {
                preferences.NotInterested.RemoveRange(0, preferences.NotInterested.Count - MaxNotInterested);
            }
        });
    }

    public UserHomePreferences RemoveNotInterested(Guid userId, Guid itemId) =>
        Update(userId, new UserPreferencesUpdate { RemoveNotInterested = new List<Guid> { itemId } });

    /// <summary>
    /// Legacy enabled-sections API: everything not in <paramref name="enabledSectionIds"/> becomes hidden.
    /// </summary>
    public UserHomePreferences SetEnabledSections(Guid userId, IEnumerable<string> enabledSectionIds, IEnumerable<string> allSectionIds)
    {
        var enabled = enabledSectionIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Update(userId, new UserPreferencesUpdate
        {
            HiddenSections = allSectionIds.Where(id => !enabled.Contains(id)).ToList()
        });
    }

    private UserHomePreferences Mutate(Guid userId, Action<UserHomePreferences> change)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User id is required.", nameof(userId));
        }

        string json;
        long version;
        UserHomePreferences snapshot;
        lock (_syncRoot)
        {
            UserHomePreferences current = _preferences.TryGetValue(userId, out UserHomePreferences? existing)
                ? existing.Clone()
                : new UserHomePreferences();
            change(current);
            current.UpdatedUtc = DateTime.UtcNow;
            _preferences[userId] = current;
            snapshot = current.Clone();
            json = JsonSerializer.Serialize(_preferences, WriteOptions);
            version = ++_version;
        }

        Save(json, version);
        return snapshot;
    }

    private static List<string> CleanSectionIds(IEnumerable<string> ids) => ids
        .Where(id => !string.IsNullOrWhiteSpace(id) && id.Length <= MaxSectionIdLength)
        .Select(id => id.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(MaxSectionIds)
        .ToList();

    private void Load(string legacyPath)
    {
        try
        {
            if (File.Exists(_path))
            {
                Dictionary<Guid, UserHomePreferences>? loaded =
                    JsonSerializer.Deserialize<Dictionary<Guid, UserHomePreferences>>(File.ReadAllText(_path));
                if (loaded != null)
                {
                    _preferences = loaded;
                }

                return;
            }

            if (File.Exists(legacyPath))
            {
                Dictionary<Guid, List<string>>? legacy =
                    JsonSerializer.Deserialize<Dictionary<Guid, List<string>>>(File.ReadAllText(legacyPath));
                if (legacy is { Count: > 0 })
                {
                    _preferences = legacy.ToDictionary(
                        pair => pair.Key,
                        pair => new UserHomePreferences
                        {
                            LegacyEnabledSections = CleanSectionIds(pair.Value),
                            UpdatedUtc = DateTime.UtcNow
                        });
                    Save(JsonSerializer.Serialize(_preferences, WriteOptions), ++_version);
                    _logger.LogInformation("[Mediarow] Migrated Home row settings for {Count} users to {File}", legacy.Count, FileName);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Mediarow] Could not load user Home preferences");
        }
    }

    private void Save(string json, long version)
    {
        lock (_fileLock)
        {
            // Writes happen outside the state lock, so a slower older snapshot must not overwrite a newer one.
            if (version <= _savedVersion)
            {
                return;
            }

            _savedVersion = version;
            try
            {
                string temporary = _path + ".tmp";
                File.WriteAllText(temporary, json);
                File.Move(temporary, _path, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Mediarow] Could not save user Home preferences");
            }
        }
    }
}
