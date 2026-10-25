using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediarow.Services;

public interface IMovieWatchHistory
{
    Task<MovieWatchSnapshot> GetSnapshotAsync(User user, CancellationToken cancellationToken);
}

/// <summary>An immutable per-user index; catalog membership checks do not query Jellyfin per title.</summary>
public sealed class MovieWatchSnapshot(ImmutableHashSet<string> keys)
{
    public bool IsWatched(BaseItemDto item) => item.Type == BaseItemKind.Movie &&
        MovieWatchHistory.IdentityKeys(item.Id, item.ProviderIds).Any(keys.Contains);
}

/// <summary>Keeps provider identities across temporary-item deletion, including explicit mark-unwatched changes.</summary>
public sealed class MovieWatchHistory : BackgroundService, IMovieWatchHistory
{
    private sealed class State
    {
        public readonly object Gate = new();
        public readonly SemaphoreSlim Initialization = new(1, 1);
        public readonly Dictionary<string, bool> Values = new(StringComparer.OrdinalIgnoreCase);
        public ImmutableHashSet<string> Watched = ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase);
        public bool Initialized;
    }

    private readonly ConcurrentDictionary<Guid, State> _states = new();
    private readonly ILibraryManager _library;
    private readonly IUserDataManager _userData;
    private readonly ILogger<MovieWatchHistory> _logger;
    private readonly IDbContextFactory<JellyfinDbContext>? _database;
    private readonly string _path;
    private readonly SemaphoreSlim _fileGate = new(1, 1);
    private int _dirty;

    public MovieWatchHistory(ILibraryManager library, IUserDataManager userData, IApplicationPaths paths,
        ILogger<MovieWatchHistory> logger, IDbContextFactory<JellyfinDbContext>? database = null)
    {
        _library = library; _userData = userData; _logger = logger; _database = database;
        _path = Path.Combine(paths.PluginConfigurationsPath, "Jellyfin.Plugin.Mediarow", "movie-watch-history.json");
        Load();
        _userData.UserDataSaved += OnUserDataSaved;
    }

    public async Task<MovieWatchSnapshot> GetSnapshotAsync(User user, CancellationToken cancellationToken)
    {
        var state = _states.GetOrAdd(user.Id, _ => new State());
        await state.Initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!state.Initialized)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // One bulk library query per user after startup, never one query per poster or row.
                var existing = _library.GetItemsResult(new InternalItemsQuery(user)
                {
                    Recursive = true, IncludeItemTypes = [BaseItemKind.Movie], IsPlayed = true,
                    EnableTotalRecordCount = false
                }).Items.OfType<Movie>().SelectMany(item => IdentityKeys(item.Id, item.ProviderIds)).ToList();
                if (_database != null)
                {
                    await using var db = await _database.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                    // Deleted entries retain custom keys in Jellyfin. IMDb keys are globally unambiguous;
                    // bare numeric keys can mean a movie or a series and must not be guessed.
                    var retained = await db.UserData.AsNoTracking().Where(data => data.UserId == user.Id && data.Played &&
                            data.CustomDataKey.StartsWith("tt") && data.CustomDataKey.Length >= 9 && data.CustomDataKey.Length <= 12)
                        .Select(data => data.CustomDataKey).Distinct().ToArrayAsync(cancellationToken).ConfigureAwait(false);
                    existing.AddRange(retained.Where(key => key.StartsWith("tt", StringComparison.OrdinalIgnoreCase) &&
                        key.Skip(2).All(char.IsAsciiDigit)).Select(key => "imdb:" + key));
                }
                lock (state.Gate)
                {
                    var builder = state.Watched.ToBuilder();
                    foreach (var key in existing)
                    {
                        // Save events/tombstones are newer than this bootstrap query.
                        if (state.Values.TryAdd(key, true)) { builder.Add(key); Interlocked.Exchange(ref _dirty, 1); }
                    }
                    state.Watched = builder.ToImmutable();
                    state.Initialized = true;
                }
            }
            lock (state.Gate) return new MovieWatchSnapshot(state.Watched);
        }
        finally { state.Initialization.Release(); }
    }

    internal static IEnumerable<string> IdentityKeys(Guid id, IReadOnlyDictionary<string, string>? providers)
    {
        yield return "item:" + id.ToString("N");
        if (providers == null) yield break;
        foreach (var provider in new[] { "Imdb", "Tmdb", "Tvdb", "AniList" })
        {
            var value = providers.FirstOrDefault(pair => pair.Key.Equals(provider, StringComparison.OrdinalIgnoreCase)).Value;
            if (!string.IsNullOrWhiteSpace(value)) yield return provider.ToLowerInvariant() + ":" + value.Trim();
        }
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs args)
    {
        if (args.Item is not Movie movie || args.UserId == Guid.Empty || args.UserData == null) return;
        var state = _states.GetOrAdd(args.UserId, _ => new State());
        lock (state.Gate)
        {
            var keys = IdentityKeys(movie.Id, movie.ProviderIds).ToArray();
            if (!args.UserData.Played && args.SaveReason is not (UserDataSaveReason.TogglePlayed or UserDataSaveReason.UpdateUserData or UserDataSaveReason.Import) &&
                !keys.Any(state.Values.ContainsKey)) return;
            foreach (var key in keys)
            {
                bool played = args.UserData.Played;
                if (state.Values.TryGetValue(key, out var previous) && previous == played) continue;
                state.Values[key] = played;
                state.Watched = played ? state.Watched.Add(key) : state.Watched.Remove(key);
                Interlocked.Exchange(ref _dirty, 1);
            }
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var stored = JsonSerializer.Deserialize<Dictionary<Guid, Dictionary<string, bool>>>(File.ReadAllText(_path));
            foreach (var user in stored ?? [])
            {
                if (user.Key == Guid.Empty || user.Value == null) continue;
                var state = _states.GetOrAdd(user.Key, _ => new State());
                foreach (var pair in user.Value) state.Values[pair.Key] = pair.Value;
                state.Watched = state.Values.Where(pair => pair.Value).Select(pair => pair.Key)
                    .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { _logger.LogWarning("[Mediarow] Could not load movie watch index ({ErrorType})", ex.GetType().Name); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            await FlushAsync(stoppingToken).ConfigureAwait(false);
    }

    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        await _fileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Interlocked.Exchange(ref _dirty, 0) == 0) return;
            var snapshot = new Dictionary<Guid, Dictionary<string, bool>>();
            foreach (var entry in _states)
                lock (entry.Value.Gate) snapshot[entry.Key] = new(entry.Value.Values, StringComparer.OrdinalIgnoreCase);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(snapshot), cancellationToken).ConfigureAwait(false);
                File.Move(temporary, _path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            Interlocked.Exchange(ref _dirty, 1);
            if (ex is OperationCanceledException) throw;
            _logger.LogWarning("[Mediarow] Could not save movie watch index ({ErrorType})", ex.GetType().Name);
        }
        finally { _fileGate.Release(); }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _userData.UserDataSaved -= OnUserDataSaved;
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public override void Dispose()
    {
        _userData.UserDataSaved -= OnUserDataSaved;
        base.Dispose();
    }
}
