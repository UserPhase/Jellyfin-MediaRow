using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Mediarow.Configuration;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.Mediarow.Services;

public static class HomeRowContentFilter
{
    public static IReadOnlyList<string> GetCategories(PluginConfiguration config, string? category = null)
    {
        var enabled = config.HomeRowContentCategories ?? (config.HomeRowContentType switch
        {
            HomeRowContentType.Movies => new[] { "Movies" },
            HomeRowContentType.Series => new[] { "Series" },
            _ => new[] { "Movies", "Series" }
        });
        return category == null ? enabled :
            enabled.Contains(category) ? new[] { category } : Array.Empty<string>();
    }

    public static HashSet<BaseItemKind> GetContentTypes(PluginConfiguration config, string? category = null)
    {
        var categories = GetCategories(config, category);
        var types = new HashSet<BaseItemKind>();
        if (categories.Contains("Movies") || categories.Contains("Anime"))
        {
            types.Add(BaseItemKind.Movie);
        }

        if (categories.Contains("Series") || categories.Contains("Anime"))
        {
            types.Add(BaseItemKind.Series);
        }

        return types;
    }

    public static bool Matches(BaseItemDto item, PluginConfiguration config, string? category = null)
    {
        var categories = GetCategories(config, category);
        if (MediarowService.IsAnime(item))
        {
            return categories.Contains("Anime");
        }

        return item.Type switch
        {
            BaseItemKind.Movie => categories.Contains("Movies"),
            BaseItemKind.Series or BaseItemKind.Episode => categories.Contains("Series"),
            _ => false
        };
    }
}
