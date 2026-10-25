using System.Linq.Expressions;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.Mediarow.Services;

/// <summary>
/// Builds per-user response copies of shared, cached catalog DTOs. Cached instances are treated as immutable:
/// every response that carries user state (UserData) or request-specific fields gets its own top-level copy.
/// </summary>
public static class VirtualItemProjection
{
    private static readonly Func<BaseItemDto, BaseItemDto> ShallowClone = CreateCloner();

    public static BaseItemDto Clone(BaseItemDto source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return ShallowClone(source);
    }

    /// <summary>
    /// Returns a copy carrying only <paramref name="userData"/>; another user's data is never inherited.
    /// </summary>
    public static BaseItemDto ForUser(BaseItemDto source, UserItemDataDto? userData)
    {
        BaseItemDto copy = Clone(source);
        copy.UserData = userData == null ? null : CloneUserData(userData);
        return copy;
    }

    public static BaseItemDto WithoutUserData(BaseItemDto source) => ForUser(source, null);

    public static UserItemDataDto CloneUserData(UserItemDataDto source) => new()
    {
        Rating = source.Rating,
        PlayedPercentage = source.PlayedPercentage,
        UnplayedItemCount = source.UnplayedItemCount,
        PlaybackPositionTicks = source.PlaybackPositionTicks,
        PlayCount = source.PlayCount,
        IsFavorite = source.IsFavorite,
        Likes = source.Likes,
        LastPlayedDate = source.LastPlayedDate,
        Played = source.Played,
        Key = source.Key,
        ItemId = source.ItemId
    };

    private static Func<BaseItemDto, BaseItemDto> CreateCloner()
    {
        var method = typeof(object).GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var parameter = Expression.Parameter(typeof(BaseItemDto), "source");
        var body = Expression.Convert(Expression.Call(parameter, method), typeof(BaseItemDto));
        return Expression.Lambda<Func<BaseItemDto, BaseItemDto>>(body, parameter).Compile();
    }
}
