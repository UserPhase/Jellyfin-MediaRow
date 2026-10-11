using Jellyfin.Plugin.Mediarow.Models;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Mediarow.Services;

public static class WatchedItemCleanup
{
    public static bool CanRemoveMovie(bool enabled, IReadOnlyList<bool> watchedByUsers, bool inUse, bool favorite) =>
        enabled && !inUse && !favorite && watchedByUsers.Count > 0 && watchedByUsers.All(watched => watched);

    public static bool CanRemoveSeries(
        bool enabled, SeriesStatus? status, IReadOnlyList<bool> watchedEpisodes,
        bool inUse, bool favorite, bool hasFutureEpisodes) =>
        enabled && status == SeriesStatus.Ended && !inUse && !favorite && !hasFutureEpisodes &&
        watchedEpisodes.Count > 0 && watchedEpisodes.All(watched => watched);

    public static IReadOnlyList<string> GetOwnedFiles(
        string persistentRoot, string titlePath, bool isSeries, IReadOnlyList<string> indexedStreams)
    {
        if (!Path.IsPathFullyQualified(persistentRoot) || !Path.IsPathFullyQualified(titlePath) ||
            indexedStreams.Count == 0)
        {
            return [];
        }

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(persistentRoot));
        string title = Path.TrimEndingDirectorySeparator(Path.GetFullPath(titlePath));
        string? category = Path.GetDirectoryName(title);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        if (category == null || !string.Equals(Path.GetDirectoryName(category), root, comparison) ||
            !(isSeries ? Path.GetFileName(category) is "tv" or "anime" : Path.GetFileName(category) == "movies") ||
            !Directory.Exists(title))
        {
            return [];
        }

        // Never follow a junction/symlink, including one above the configured root.
        for (DirectoryInfo? directory = new(title); directory != null; directory = directory.Parent)
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return [];
            }
        }

        var directories = new List<string> { title };
        foreach (string directory in Directory.EnumerateDirectories(title))
        {
            if (!isSeries || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 ||
                !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(directory), @"^Season \d+$"))
            {
                return [];
            }
            if (Directory.EnumerateDirectories(directory).Any())
            {
                return [];
            }
            directories.Add(directory);
        }

        var streams = new List<string>();
        var filesToDelete = new List<string>();
        foreach (string directory in directories)
        {
            foreach (string file in Directory.EnumerateFiles(directory))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                {
                    return [];
                }

                string extension = Path.GetExtension(file).ToLowerInvariant();
                if (extension == ".strm")
                {
                    if ((!isSeries && !string.Equals(directory, title, comparison)) ||
                        (isSeries && string.Equals(directory, title, comparison)) ||
                        new FileInfo(file).Length > 4096 ||
                        !StrmMarker.IsMarker(File.ReadAllText(file).Trim()))
                    {
                        return [];
                    }
                    streams.Add(file);
                    filesToDelete.Add(file);
                    string nfo = Path.ChangeExtension(file, ".nfo");
                    if (File.Exists(nfo))
                    {
                        filesToDelete.Add(nfo);
                    }
                }
                else if (extension is not (".nfo" or ".jpg" or ".jpeg" or ".png" or ".webp" or ".srt" or ".vtt"))
                {
                    // A mixed folder may contain real media or user files: leave the entire title alone.
                    return [];
                }
            }

            if (isSeries)
            {
                string nfo = Path.Combine(directory, string.Equals(directory, title, comparison) ? "tvshow.nfo" : "season.nfo");
                if (File.Exists(nfo))
                {
                    filesToDelete.Add(nfo);
                }
            }
        }

        var indexed = new HashSet<string>(indexedStreams.Select(Path.GetFullPath), comparer);
        if (streams.Count == 0 || (!isSeries && streams.Count != 1) ||
            indexed.Count != indexedStreams.Count || !indexed.SetEquals(streams))
        {
            return [];
        }

        return filesToDelete.Distinct(comparer).ToArray();
    }

    public static void DeleteOwnedFiles(
        string persistentRoot, string titlePath, bool isSeries, IReadOnlyList<string> indexedStreams)
    {
        var files = GetOwnedFiles(persistentRoot, titlePath, isSeries, indexedStreams);
        if (files.Count == 0)
        {
            throw new InvalidOperationException("The title no longer consists solely of indexed Mediarow streams; cleanup was refused.");
        }
        foreach (string file in files)
        {
            File.Delete(file);
        }

        foreach (string directory in Directory.EnumerateDirectories(titlePath))
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        if (!Directory.EnumerateFileSystemEntries(titlePath).Any())
        {
            Directory.Delete(titlePath);
        }
    }
}
