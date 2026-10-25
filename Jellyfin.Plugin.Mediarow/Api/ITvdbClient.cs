using Jellyfin.Plugin.Mediarow.Models;

namespace Jellyfin.Plugin.Mediarow.Api;

public interface ITvdbClient
{
    /// <summary>
    /// Search for series by name.
    /// </summary>
    Task<IReadOnlyList<TvdbSearchResult>> SearchSeriesAsync(string query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get detailed series information including seasons and episodes.
    /// </summary>
    Task<TvdbSeriesExtended?> GetSeriesExtendedAsync(int seriesId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops cached series details so the next lookup fetches newly announced episodes.
    /// </summary>
    void InvalidateSeries(int seriesId)
    {
    }

    /// <summary>
    /// Get series information by remote ID (e.g. IMDB).
    /// </summary>
    Task<TvdbSeriesExtended?> GetSeriesByRemoteIdAsync(string remoteId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get translation for a series in a specific language.
    /// </summary>
    Task<TvdbTranslationData?> GetSeriesTranslationAsync(int seriesId, string language, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get translation for an episode in a specific language.
    /// </summary>
    Task<TvdbTranslationData?> GetEpisodeTranslationAsync(int episodeId, string language, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if the client is configured with a valid API key.
    /// </summary>
    bool IsConfigured { get; }
}
