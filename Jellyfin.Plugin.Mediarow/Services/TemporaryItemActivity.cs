using System.Collections.Concurrent;
using Jellyfin.Plugin.Mediarow.Configuration;

namespace Jellyfin.Plugin.Mediarow.Services;

/// <summary>Tracks title use in memory and serializes cleanup with request/import leases.</summary>
public sealed class TemporaryItemActivity(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, Entry> _entries = new(OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private bool _enabled;
    private DateTime _enabledAt;

    private sealed class Entry(DateTime lastUsed)
    {
        public readonly object Gate = new();
        public DateTime LastUsed = lastUsed;
        public int Users;
        public bool Removed;
    }

    public void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            // Enabling cleanup or restarting grants existing titles a full grace period.
            if (enabled && !_enabled) _enabledAt = _clock.GetUtcNow().UtcDateTime;
            _enabled = enabled;
        }
    }

    public IDisposable BeginUse(string titlePath)
    {
        SetEnabled(true);
        while (true)
        {
            var entry = GetEntry(titlePath);
            lock (entry.Gate)
            {
                if (entry.Removed) continue;
                entry.LastUsed = _clock.GetUtcNow().UtcDateTime;
                entry.Users++;
                return new Use(this, entry);
            }
        }
    }

    public void Touch(string titlePath)
    {
        while (true)
        {
            var entry = GetEntry(titlePath);
            lock (entry.Gate)
            {
                if (entry.Removed) continue;
                entry.LastUsed = _clock.GetUtcNow().UtcDateTime;
                return;
            }
        }
    }

    /// <summary>The callback must synchronously recheck retention rules and remove only owned files.</summary>
    public bool TryCleanup(string titlePath, DateTime lastFileChange, TimeSpan grace, Func<bool> remove)
    {
        var entry = GetEntry(titlePath);
        lock (entry.Gate)
        {
            if (entry.Removed || entry.Users > 0) return false;
            DateTime enabledAt;
            lock (_gate)
            {
                if (!_enabled) return false;
                enabledAt = _enabledAt;
            }
            var lastUse = new[] { enabledAt, lastFileChange, entry.LastUsed }.Max();
            if (_clock.GetUtcNow().UtcDateTime - lastUse < grace) return false;
            // BeginUse/Touch cannot race this final check and the deletion callback.
            if (!remove()) return false;
            entry.Removed = true;
            _entries.TryRemove(titlePath, out _);
            return true;
        }
    }

    private Entry GetEntry(string titlePath) => _entries.GetOrAdd(titlePath, _ => new Entry(DateTime.MinValue));

    private sealed class Use(TemporaryItemActivity owner, Entry entry) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (entry.Gate)
            {
                entry.Users--;
                entry.LastUsed = owner._clock.GetUtcNow().UtcDateTime;
            }
        }
    }

    /// <summary>Maps a movie, series, season or episode path to its generated title directory.</summary>
    public static string? GetTitlePath(PluginConfiguration config, string? itemPath)
    {
        if (string.IsNullOrWhiteSpace(itemPath) || !Path.IsPathFullyQualified(itemPath)) return null;
        foreach (var root in new[] { "movies", "tv", "anime" }.Select(config.GetPersistencePath)
            .Where(Path.IsPathFullyQualified).Select(Path.GetFullPath).OrderByDescending(path => path.Length))
        {
            var relative = Path.GetRelativePath(root, Path.GetFullPath(itemPath));
            var parts = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts[0] is "." or ".." || Path.IsPathRooted(relative)) continue;
            return Path.GetFullPath(Path.Combine(root, parts[0]));
        }
        return null;
    }

    public static string? GetLibraryRoot(PluginConfiguration config, string titlePath, bool series)
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(titlePath)));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return (series ? new[] { "tv", "anime" } : new[] { "movies" }).Select(config.GetPersistencePath)
            .Where(Path.IsPathFullyQualified).Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
            .FirstOrDefault(path => string.Equals(path, parent, comparison));
    }

    public static string? GetTitlePath(string root, string? itemPath)
    {
        if (!Path.IsPathFullyQualified(root) || string.IsNullOrWhiteSpace(itemPath) || !Path.IsPathFullyQualified(itemPath)) return null;
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(itemPath));
        var parts = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[1] is "." or ".." || parts[0] is not ("movies" or "tv" or "anime")) return null;
        return Path.GetFullPath(Path.Combine(root, parts[0], parts[1]));
    }
}
