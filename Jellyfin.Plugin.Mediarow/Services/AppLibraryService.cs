using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Mediarow.Configuration;
using Jellyfin.Plugin.Mediarow.HomeScreen;
using MediaBrowser.Controller;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Mediarow.Services;

/// <summary>
/// Publishes Mediarow Home rows as virtual libraries for clients that do not run the injected web script.
/// </summary>
public sealed class AppLibraryService
{
    private readonly HomeSectionManager _manager;
    private readonly HomeSectionBootstrapper _bootstrapper;
    private readonly UserPreferenceFilter _preferences;
    private readonly IServerApplicationHost _serverApplicationHost;

    public AppLibraryService(
        HomeSectionManager manager,
        HomeSectionBootstrapper bootstrapper,
        UserPreferenceFilter preferences,
        IServerApplicationHost serverApplicationHost)
    {
        _manager = manager;
        _bootstrapper = bootstrapper;
        _preferences = preferences;
        _serverApplicationHost = serverApplicationHost;
    }

    private static PluginConfiguration Config => MediarowPlugin.Instance?.Configuration ?? new PluginConfiguration();

    public static bool IsEnabled => Config.EnableHomeRows && Config.EnableAppLibraries;

    public static bool ShowInWeb => Config.ShowAppLibrariesInWeb;

    public IReadOnlyList<AppLibraryDefinition> GetLibraries(Guid userId)
    {
        if (!IsEnabled)
        {
            return Array.Empty<AppLibraryDefinition>();
        }

        _bootstrapper.RegisterConfiguredSections();
        // Hidden rows are dropped and the user's row order becomes the library order.
        IReadOnlyList<IHomeSection> sections = _preferences.ArrangeSections(
            userId,
            _manager.GetSections().Where(section => section is VirtualCatalogHomeSection or DiscoveryCatalogHomeSection),
            section => section.Id);
        var categories = HomeRowContentFilter.GetCategories(Config);
        var builtIn = AppLibraryLayout.Project(sections.OfType<VirtualCatalogHomeSection>(), categories, preserveOrder: true)
            .ToLookup(library => library.SectionId, StringComparer.OrdinalIgnoreCase);
        var result = new List<AppLibraryDefinition>();
        foreach (var section in sections)
        {
            if (section is DiscoveryCatalogHomeSection catalog)
            {
                // Explicit catalogs do not consume one of the six rotating genre slots.
                foreach (var category in categories.Where(category => catalog.Selection.MediaType == Models.DiscoveryMediaType.All ||
                    category == "Anime" || (catalog.Selection.MediaType == Models.DiscoveryMediaType.Movie) == (category == "Movies")))
                {
                    result.Add(new AppLibraryDefinition(AppLibraryLayout.CreateId(section.Id, category),
                        section.DisplayTitle + " " + category, section.Id, category,
                        category == "Movies" ? CollectionType.movies : CollectionType.tvshows, result.Count));
                }
            }
            else
            {
                foreach (var library in builtIn[section.Id]) result.Add(library with { Order = result.Count });
            }
        }
        return result;
    }

    public AppLibraryDefinition? Find(Guid userId, Guid libraryId)
    {
        if (libraryId == Guid.Empty)
        {
            return null;
        }

        return GetLibraries(userId).FirstOrDefault(library => library.Id == libraryId);
    }

    public async Task<IReadOnlyList<BaseItemDto>> GetItemsAsync(
        Guid userId,
        AppLibraryDefinition library,
        CancellationToken cancellationToken)
    {
        var result = await _manager.ExecuteAsync(library.SectionId, new HomeSectionRequest
        {
            UserId = userId,
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["contentCategory"] = library.Category
            }
        }, cancellationToken).ConfigureAwait(false);
        return result.Items ?? Array.Empty<BaseItemDto>();
    }

    public BaseItemDto CreateView(AppLibraryDefinition library)
    {
        string id = library.Id.ToString("N");
        return new BaseItemDto
        {
            Id = library.Id,
            Name = library.Name,
            SortName = library.Order.ToString("D3", System.Globalization.CultureInfo.InvariantCulture) + " " + library.Name,
            ServerId = _serverApplicationHost.SystemId,
            Type = BaseItemKind.CollectionFolder,
            CollectionType = library.CollectionType,
            IsFolder = true,
            Etag = id,
            DisplayPreferencesId = id,
            PrimaryImageAspectRatio = 16d / 9d,
            ImageTags = new Dictionary<ImageType, string> { [ImageType.Primary] = "mediarow" },
            BackdropImageTags = Array.Empty<string>(),
            LocationType = LocationType.Virtual,
            UserData = new UserItemDataDto { Key = id, ItemId = library.Id }
        };
    }
}
