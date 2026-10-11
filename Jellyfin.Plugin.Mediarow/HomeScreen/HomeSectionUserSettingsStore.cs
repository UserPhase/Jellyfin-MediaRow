using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediarow.HomeScreen;

/// <summary>
/// Persists per-user enabled Home section IDs for Mediarow.
/// </summary>
public sealed class HomeSectionUserSettingsStore
{
    private readonly string _path;
    private readonly ILogger<HomeSectionUserSettingsStore> _logger;
    private readonly object _syncRoot = new();
    private Dictionary<Guid, HashSet<string>> _settings = new();

    public HomeSectionUserSettingsStore(
        IApplicationPaths applicationPaths,
        ILogger<HomeSectionUserSettingsStore> logger)
    {
        _logger = logger;
        string directory = Path.Combine(applicationPaths.PluginConfigurationsPath, "Jellyfin.Plugin.Mediarow");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "home-sections-users.json");
        Load();
    }

    public IReadOnlySet<string>? Get(Guid userId)
    {
        lock (_syncRoot)
        {
            return _settings.TryGetValue(userId, out HashSet<string>? sections)
                ? new HashSet<string>(sections, StringComparer.OrdinalIgnoreCase)
                : null;
        }
    }

    public void Set(Guid userId, IEnumerable<string> sectionIds)
    {
        lock (_syncRoot)
        {
            _settings[userId] = new HashSet<string>(sectionIds, StringComparer.OrdinalIgnoreCase);
            Save();
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                Dictionary<Guid, HashSet<string>>? loaded = JsonSerializer.Deserialize<Dictionary<Guid, HashSet<string>>>(File.ReadAllText(_path));
                if (loaded != null)
                {
                    _settings = loaded;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Mediarow] Could not load Home section user settings");
        }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Mediarow] Could not save Home section user settings");
        }
    }
}
