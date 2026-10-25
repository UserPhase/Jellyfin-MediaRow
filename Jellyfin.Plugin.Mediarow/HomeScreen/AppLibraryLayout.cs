using System.Security.Cryptography;
using System.Text;
using Jellyfin.Data.Enums;

namespace Jellyfin.Plugin.Mediarow.HomeScreen;

/// <summary>
/// One Mediarow catalog row exposed to native apps as a virtual Jellyfin library.
/// </summary>
public sealed record AppLibraryDefinition(
    Guid Id,
    string Name,
    string SectionId,
    string Category,
    CollectionType CollectionType,
    int Order);

/// <summary>
/// Mirrors the web client's "Home" projection (mediarow-home.js projectHomeSections) so apps that
/// cannot run the injected script get the same rows as "Latest in ..." libraries.
/// </summary>
public static class AppLibraryLayout
{
    public const int MaxGenreRows = 6;
    public const string ContinueWatchingId = "Mediarow_ContinueWatching";
    public const string PopularId = "Mediarow_Popular";

    public static IReadOnlyList<AppLibraryDefinition> Project(
        IEnumerable<IHomeSection> sections,
        IReadOnlyList<string> categories,
        bool preserveOrder = false)
    {
        var libraries = new List<AppLibraryDefinition>();
        if (categories.Count == 0)
        {
            return libraries;
        }

        int genreIndex = 0;
        foreach (IHomeSection section in preserveOrder ? sections : sections.OrderBy(section => section.Order))
        {
            if (section.Id.Equals(ContinueWatchingId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bool isAnime = string.Equals(section.AdditionalData ?? section.DisplayTitle, "Anime", StringComparison.OrdinalIgnoreCase);
            IEnumerable<string> rowCategories;
            if (section.Id.Equals(PopularId, StringComparison.OrdinalIgnoreCase))
            {
                rowCategories = categories;
            }
            else
            {
                if (genreIndex >= MaxGenreRows || (isAnime && !categories.Contains("Anime")))
                {
                    continue;
                }

                rowCategories = new[] { isAnime ? "Anime" : categories[genreIndex % categories.Count] };
                genreIndex++;
            }

            foreach (string category in rowCategories)
            {
                libraries.Add(new AppLibraryDefinition(
                    CreateId(section.Id, category),
                    isAnime ? section.DisplayTitle : section.DisplayTitle + " " + category,
                    section.Id,
                    category,
                    category == "Movies" ? CollectionType.movies : CollectionType.tvshows,
                    libraries.Count));
            }
        }

        return libraries;
    }

    public static Guid CreateId(string sectionId, string category)
    {
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes("mediarow-library:" + sectionId.ToLowerInvariant() + ":" + category.ToLowerInvariant()));
        return new Guid(hash);
    }
}
