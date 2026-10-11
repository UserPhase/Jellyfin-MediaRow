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
    private readonly HomeSectionUserSettingsStore _userSettings;
    private readonly IServerApplicationHost _serverApplicationHost;

    public AppLibraryService(
        HomeSectionManager manager,
        HomeSectionBootstrapper bootstrapper,
        HomeSectionUserSettingsStore userSettings,
        IServerApplicationHost serverApplicationHost)
    {
        _manager = manager;
        _bootstrapper = bootstrapper;
        _userSettings = userSettings;
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
        IReadOnlySet<string>? enabled = userId == Guid.Empty ? null : _userSettings.Get(userId);
        IEnumerable<IHomeSection> sections = _manager.GetSections()
            .OfType<VirtualCatalogHomeSection>()
            .Where(section => enabled == null || enabled.Contains(section.Id));
        return AppLibraryLayout.Project(sections, HomeRowContentFilter.GetCategories(Config));
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
