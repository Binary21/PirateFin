using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Extensions;
using Jellyfin.Api.Helpers;
using Jellyfin.Api.ModelBinders;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// User library controller.
/// </summary>
[Route("")]
[Authorize]
[Tags("Library")]
public class UserLibraryController : BaseJellyfinApiController
{
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataRepository;
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IDtoService _dtoService;
    private readonly IUserViewManager _userViewManager;
    private readonly IFileSystem _fileSystem;
    private readonly IDownloadManager _downloadManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserLibraryController"/> class.
    /// </summary>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="userDataRepository">Instance of the <see cref="IUserDataManager"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="dtoService">Instance of the <see cref="IDtoService"/> interface.</param>
    /// <param name="userViewManager">Instance of the <see cref="IUserViewManager"/> interface.</param>
    /// <param name="fileSystem">Instance of the <see cref="IFileSystem"/> interface.</param>
    /// <param name="downloadManager">Instance of the <see cref="IDownloadManager"/> interface.</param>
    /// /// <param name="providerManager">Instance of the <see cref="IProviderManager"/> interface.</param>
    public UserLibraryController(
        IUserManager userManager,
        IUserDataManager userDataRepository,
        ILibraryManager libraryManager,
        IDtoService dtoService,
        IUserViewManager userViewManager,
        IFileSystem fileSystem,
        IDownloadManager downloadManager,
        IProviderManager providerManager)
    {
        _userManager = userManager;
        _userDataRepository = userDataRepository;
        _libraryManager = libraryManager;
        _dtoService = dtoService;
        _userViewManager = userViewManager;
        _fileSystem = fileSystem;
        _downloadManager = downloadManager;
        _providerManager = providerManager;
    }

    /// <summary>
    /// Gets an item from a user's library.
    /// </summary>
    /// <param name="userId">User id.</param>
    /// <param name="itemId">Item id.</param>
    /// <response code="200">Item returned.</response>
    /// <returns>An <see cref="OkResult"/> containing the item.</returns>
    [HttpGet("Items/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<BaseItemDto>> GetItem(
        [FromQuery] Guid? userId,
        [FromRoute, Required] Guid itemId)
    {
        userId = RequestHelpers.GetUserId(User, userId);
        var user = _userManager.GetUserById(userId.Value);
        if (user is null)
        {
            return NotFound();
        }

        var item = itemId.IsEmpty()
            ? _libraryManager.GetUserRootFolder()
            : _libraryManager.GetItemById<BaseItem>(itemId, user);
        if (item is null)
        {
            return NotFound();
        }

        if (item is Episode episode
            && episode.Scheduled
            && !episode.IsDownloaded
            && !string.IsNullOrWhiteSpace(episode.DownloadUrl))
        {
            await _downloadManager.QueueDownload(episode).ConfigureAwait(false);
        }

        await RefreshItemOnDemandIfNeeded(item).ConfigureAwait(false);

        var dtoOptions = new DtoOptions();

        return _dtoService.GetBaseItemDto(item, dtoOptions, user);
    }

    /// <summary>
    /// Gets an items play state from a user's library.
    /// </summary>
    /// <param name="userId">User id.</param>
    /// <param name="itemId">Item id.</param>
    /// <response code="200">Item state.</response>
    /// <returns>An <see cref="OkResult"/> containing the item.</returns>
    [HttpGet("Items/{itemId}/Playable")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<bool> GetPlayable(
        [FromQuery] Guid? userId,
        [FromRoute, Required] Guid itemId)
    {
        userId = RequestHelpers.GetUserId(User, userId);
        var user = _userManager.GetUserById(userId.Value);
        if (user is null)
        {
            return Task.FromResult(false);
        }

        var item = itemId.IsEmpty()
            ? _libraryManager.GetUserRootFolder()
            : _libraryManager.GetItemById<BaseItem>(itemId, user);
        if (item is null)
        {
            return Task.FromResult(false);
        }

        if (item is Video video)
        {
            if (item is Episode scheduledEpisode
                && scheduledEpisode.Scheduled
                && !scheduledEpisode.IsDownloaded)
            {
                if (!IsVideoFileReady(scheduledEpisode))
                {
                    return Task.FromResult(false);
                }

                return Task.FromResult(scheduledEpisode.IsPlayable || (scheduledEpisode.RunTimeTicks != null && scheduledEpisode.RunTimeTicks > 0));
            }

            if (video.IsPlaceHolder || video.IsInternet)
            {
                return Task.FromResult(video.IsPlayable || (video.RunTimeTicks != null && video.RunTimeTicks > 0));
            }

            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    private bool IsVideoFileReady(Video video)
    {
        if (!video.IsFileProtocol || string.IsNullOrWhiteSpace(video.Path))
        {
            return false;
        }

        var fileMeta = _fileSystem.GetFileSystemInfo(video.Path);
        return fileMeta is not null && fileMeta.Exists && !fileMeta.IsDirectory && fileMeta.Length > 0;
    }

    /// <summary>
    /// Gets an item from a user's library.
    /// </summary>
    /// <param name="userId">User id.</param>
    /// <param name="itemId">Item id.</param>
    /// <response code="200">Item returned.</response>
    /// <returns>An <see cref="OkResult"/> containing the item.</returns>
    [HttpGet("Users/{userId}/Items/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [Obsolete("Kept for backwards compatibility")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public Task<ActionResult<BaseItemDto>> GetItemLegacy(
        [FromRoute, Required] Guid userId,
        [FromRoute, Required] Guid itemId)
        => GetItem(userId, itemId);

    /// <summary>
    /// Gets the root folder from a user's library.
    /// </summary>
    /// <param name="userId">User id.</param>
    /// <response code="200">Root folder returned.</response>
    /// <returns>An <see cref="OkResult"/> containing the user's root folder.</returns>
    [HttpGet("Items/Root")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<BaseItemDto> GetRootFolder([FromQuery] Guid? userId)
    {
        userId = RequestHelpers.GetUserId(User, userId);
        var user = _userManager.GetUserById(userId.Value);
        if (user is null)
        {
            return NotFound();
        }

        var item = _libraryManager.GetUserRootFolder();
        var dtoOptions = new DtoOptions();
        return _dtoService.GetBaseItemDto(item, dtoOptions, user);
    }

    /// <summary>
    /// Gets an items play state from a user's library.
    /// </summary>
    /// <param name="userId">User id.</param>
    /// <param name="itemId">Item id.</param>
    /// <response code="200">Item state.</response>
    /// <returns>An <see cref="OkResult"/> containing the item.</returns>
    [HttpGet("Users/{userId}/Items/{itemId}/Playable")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [Obsolete("Kept for backwards compatibility")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public Task<bool> GetPlayableLegacy(
        [FromRoute, Required] Guid userId,
        [FromRoute, Required] Guid itemId)
        => GetPlayable(userId, itemId);

    /// <summary>
    /// Gets the root folder from a user's library.
    /// </summary>
    /// <param name="userId">User id.</param>
    /// <response code="200">Root folder returned.</response>
    /// <returns>An <see cref="OkResult"/> containing the user's root folder.</returns>
    [HttpGet("Users/{userId}/Items/Root")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [Obsolete("Kept for backwards compatibility")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public ActionResult<BaseItemDto> GetRootFolderLegacy(
        [FromRoute, Required] Guid userId)
        => GetRootFolder(userId);

    /// <summary>
    /// Gets intros to play before the main media item plays.
    /// </summary>
    /// <param name="userId">User id.</param>
    /// <param name="itemId">Item id.</param>
    /// <response code="200">Intros returned.</response>
    /// <returns>An <see cref="OkResult"/> containing the intros to play.</returns>
    [HttpGet("Items/{itemId}/Intros")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<QueryResult<BaseItemDto>>> GetIntros(
        [FromQuery] Guid? userId,
        [FromRoute, Required] Guid itemId)
    {
        userId = RequestHelpers.GetUserId(User, userId);
        var user = _userManager.GetUserById(userId.Value);
        if (user is null)
        {
            return NotFound();
        }

        var item = itemId.IsEmpty()
            ? _libraryManager.GetUserRootFolder()
            : _libraryManager.GetItemById<BaseItem>(itemId, user);
        if (item is null)
        {
            return NotFound();
        }

        var items = await _libraryManager.GetIntros(item, user).ConfigureAwait(false);
        var dtoOptions = new DtoOptions();
        var dtos = items.Select(i => _dtoService.GetBaseItemDto(i, dtoOptions, user)).ToArray();

        return new QueryResult<BaseItemDto>(dtos);
    }

    /// <summary>
    /// Gets intros to play before the main media item plays.
    /// </summary>
    /// <param name="userId">User id.</param>
    /// <param name="itemId">Item id.</param>
    /// <response code="200">Intros returned.</response>
    /// <returns>An <see cref="OkResult"/> containing the intros to play.</returns>
    [HttpGet("Users/{userId}/Items/{itemId}/Intros")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [Obsolete("Kept for backwards compatibility")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public Task<ActionResult<QueryResult<BaseItemDto>>> GetIntrosLegacy(
        [FromRoute, Required] Guid userId,
        [FromRoute, Required] Guid itemId)
        => GetIntros(userId, itemId);

    /// <summary>
    /// Marks an item as a favorite.
    /// </summary>
    /// <param name="userId">User id.</param>
    /// <param name="itemId">Item id.</param>
    /// <response code="200">Item marked as favorite.</response>
    /// <returns>An <see cref="OkResult"/> containing the <see cref="UserItemDataDto"/>.</returns>
    [HttpPost("UserFavoriteItems/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [Tags("UserData")]
    public ActionResult<UserItemDataDto> MarkFavoriteItem(
        [FromQuery] Guid? userId,
        [FromRoute, Required] Guid itemId)
    {
        userId = RequestHelpers.GetUserId(User, userId);
        var user = _userManager.GetUserById(userId.Value);
        if (user is null)
        {
            return NotFound();
        }

        var item = itemId.IsEmpty()
            ? _libraryManager.GetUserRootFolder()
            : _libraryManager.GetItemById<BaseItem>(itemId, user);
        if (item is null)
        {
            return NotFound();
        }

        return MarkFavorite(user, item, true);
    }

    /// <summary>
    /// Marks an item as a favorite.
    /// </summary>
    /// <param name="userId">User id.</param>
    /// <param name="itemId">Item id.</param>
    /// <response code="200">Item marked as favorite.</response>
    /// <returns>An <see cref="OkResult"/> containing the <see cref="UserItemDataDto"/>.</returns>
    [HttpPost("Users/{userId}/FavoriteItems/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [Obsolete("Kept for backwards compatibility")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public ActionResult<UserItemDataDto> MarkFavoriteItemLegacy(
        [FromRoute, Required] Guid userId,
        [FromRoute, Required] Guid itemId)
        => MarkFavoriteItem(userId, itemId);

    /// <summary>
    /// Unmarks item as a favorite.
    /// </summary>
    /// <param name="userId">User id.</param>
    /// <param name="itemId">Item id.</param>
    /// <response code="200">Item unmarked as favorite.</response>
    /// <returns>An <see cref="OkResult"/> containing the <see cref="UserItemDataDto"/>.</returns>
    [HttpDelete("UserFavoriteItems/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [Tags("UserData")]
    public ActionResult<UserItemDataDto> UnmarkFavoriteItem(
        [FromQuery] Guid? userId,
        [FromRoute, Required] Guid itemId)
    {
        userId = RequestHelpers.GetUserId(User, userId);
        var user = _userManager.GetUserById(userId.Value);
        if (user is null)
        {
            return NotFound();
        }

        var item = itemId.IsEmpty()
            ? _libraryManager.GetUserRootFolder()
            : _libraryManager.GetItemById<BaseItem>(itemId, user);
        if (item is null)
        {
            return NotFound();
        }

        return MarkFavorite(user, item, false);
    }

    /// <summary>
    /// Unmarks item as a favorite.
    /// </summary>
    /// <param name="userId">User id.</param>
    /// <param name="itemId">Item id.</param>
    /// <response code="200">Item unmarked as favorite.</response>
    /// <returns>An <see cref="OkResult"/> containing the <see cref="UserItemDataDto"/>.</returns>
    [HttpDelete("Users/{userId}/FavoriteItems/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [Obsolete("Kept for backwards compatibility")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public ActionResult<UserItemDataDto> UnmarkFavoriteItemLegacy(
        [FromRoute, Required] Guid userId,
        [FromRoute, Required] Guid itemId)
        => UnmarkFavoriteItem(userId, itemId);

    /// <summary>
    /// Deletes a user's saved personal rating for an item.
    /// </summary>
    /// <param name="userId">User id.</param>
    /// <param name="itemId">Item id.</param>
    /// <response code="200">Personal rating removed.</response>
    /// <returns>An <see cref="OkResult"/> containing the <see cref="UserItemDataDto"/>.</returns>
    [HttpDelete("UserItems/{itemId}/Rating")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [Tags("UserData")]
    public ActionResult<UserItemDataDto?> DeleteUserItemRating(
        [FromQuery] Guid? userId,
        [FromRoute, Required] Guid itemId)
    {
        userId = RequestHelpers.GetUserId(User, userId);
        var user = _userManager.GetUserById(userId.Value);
        if (user is null)
        {
            return NotFound();
        }

        var item = itemId.IsEmpty()
            ? _libraryManager.GetUserRootFolder()
            : _libraryManager.GetItemById<BaseItem>(itemId, user);
        if (item is null)
        {
            return NotFound();
        }

        return UpdateUserItemRatingInternal(user, item, null);
    }

    /// <summary>
    /// Deletes a user's saved personal rating for an item.
    /// </summary>
    /// <param name="userId">User id.</param>
    /// <param name="itemId">Item id.</param>
    /// <response code="200">Personal rating removed.</response>
    /// <returns>An <see cref="OkResult"/> containing the <see cref="UserItemDataDto"/>.</returns>
    [HttpDelete("Users/{userId}/Items/{itemId}/Rating")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [Obsolete("Kept for backwards compatibility")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public ActionResult<UserItemDataDto?> DeleteUserItemRatingLegacy(
        [FromRoute, Required] Guid userId,
        [FromRoute, Required] Guid itemId)
        => DeleteUserItemRating(userId, itemId);

    /// <summary>
    /// Updates a user's rating for an item.
    /// </summary>
    /// <param name="userId">User id.</param>
    /// <param name="itemId">Item id.</param>
    /// <param name="likes">Whether this <see cref="UpdateUserItemRating" /> is likes.</param>
    /// <response code="200">Item rating updated.</response>
    /// <returns>An <see cref="OkResult"/> containing the <see cref="UserItemDataDto"/>.</returns>
    [HttpPost("UserItems/{itemId}/Rating")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [Tags("UserData")]
    public ActionResult<UserItemDataDto?> UpdateUserItemRating(
        [FromQuery] Guid? userId,
        [FromRoute, Required] Guid itemId,
        [FromQuery] bool? likes)
    {
        userId = RequestHelpers.GetUserId(User, userId);
        var user = _userManager.GetUserById(userId.Value);
        if (user is null)
        {
            return NotFound();
        }

        var item = itemId.IsEmpty()
            ? _libraryManager.GetUserRootFolder()
            : _libraryManager.GetItemById<BaseItem>(itemId, user);
        if (item is null)
        {
            return NotFound();
        }

        return UpdateUserItemRatingInternal(user, item, likes);
    }

    /// <summary>
    /// Updates a user's rating for an item.
    /// </summary>
    /// <param name="userId">User id.</param>
    /// <param name="itemId">Item id.</param>
    /// <param name="likes">Whether this <see cref="UpdateUserItemRating" /> is likes.</param>
    /// <response code="200">Item rating updated.</response>
    /// <returns>An <see cref="OkResult"/> containing the <see cref="UserItemDataDto"/>.</returns>
    [HttpPost("Users/{userId}/Items/{itemId}/Rating")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [Obsolete("Kept for backwards compatibility")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public ActionResult<UserItemDataDto?> UpdateUserItemRatingLegacy(
        [FromRoute, Required] Guid userId,
        [FromRoute, Required] Guid itemId,
        [FromQuery] bool? likes)
        => UpdateUserItemRating(userId, itemId, likes);

    /// <summary>
    /// Gets local trailers for an item.
    /// </summary>
    /// <param name="userId">User id.</param>
    /// <param name="itemId">Item id.</param>
    /// <response code="200">An <see cref="OkResult"/> containing the item's local trailers.</response>
    /// <returns>The items local trailers.</returns>
    [HttpGet("Items/{itemId}/LocalTrailers")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<BaseItemDto>> GetLocalTrailers(
        [FromQuery] Guid? userId,
        [FromRoute, Required] Guid itemId)
    {
        userId = RequestHelpers.GetUserId(User, userId);
        var user = _userManager.GetUserById(userId.Value);
        if (user is null)
        {
            return NotFound();
        }

        var item = itemId.IsEmpty()
            ? _libraryManager.GetUserRootFolder()
            : _libraryManager.GetItemById<BaseItem>(itemId, user);
        if (item is null)
        {
            return NotFound();
        }

        var dtoOptions = new DtoOptions();

        return Ok(item.GetExtras([ExtraType.Trailer], user)
            .Select(i => _dtoService.GetBaseItemDto(i, dtoOptions, user, item)));
    }

    /// <summary>
    /// Gets local trailers for an item.
    /// </summary>
    /// <param name="userId">User id.</param>
    /// <param name="itemId">Item id.</param>
    /// <response code="200">An <see cref="OkResult"/> containing the item's local trailers.</response>
    /// <returns>The items local trailers.</returns>
    [HttpGet("Users/{userId}/Items/{itemId}/LocalTrailers")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [Obsolete("Kept for backwards compatibility")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public ActionResult<IEnumerable<BaseItemDto>> GetLocalTrailersLegacy(
        [FromRoute, Required] Guid userId,
        [FromRoute, Required] Guid itemId)
        => GetLocalTrailers(userId, itemId);

    /// <summary>
    /// Gets special features for an item.
    /// </summary>
    /// <param name="userId">User id.</param>
    /// <param name="itemId">Item id.</param>
    /// <response code="200">Special features returned.</response>
    /// <returns>An <see cref="OkResult"/> containing the special features.</returns>
    [HttpGet("Items/{itemId}/SpecialFeatures")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<BaseItemDto>> GetSpecialFeatures(
        [FromQuery] Guid? userId,
        [FromRoute, Required] Guid itemId)
    {
        userId = RequestHelpers.GetUserId(User, userId);
        var user = _userManager.GetUserById(userId.Value);
        if (user is null)
        {
            return NotFound();
        }

        var item = itemId.IsEmpty()
            ? _libraryManager.GetUserRootFolder()
            : _libraryManager.GetItemById<BaseItem>(itemId, user);
        if (item is null)
        {
            return NotFound();
        }

        var dtoOptions = new DtoOptions();

        return Ok(item
            .GetExtras(user)
            .Where(i => i.ExtraType.HasValue && BaseItem.DisplayExtraTypes.Contains(i.ExtraType.Value))
            .Select(i => _dtoService.GetBaseItemDto(i, dtoOptions, user, item)));
    }

    /// <summary>
    /// Gets special features for an item.
    /// </summary>
    /// <param name="userId">User id.</param>
    /// <param name="itemId">Item id.</param>
    /// <response code="200">Special features returned.</response>
    /// <returns>An <see cref="OkResult"/> containing the special features.</returns>
    [HttpGet("Users/{userId}/Items/{itemId}/SpecialFeatures")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [Obsolete("Kept for backwards compatibility")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public ActionResult<IEnumerable<BaseItemDto>> GetSpecialFeaturesLegacy(
        [FromRoute, Required] Guid userId,
        [FromRoute, Required] Guid itemId)
        => GetSpecialFeatures(userId, itemId);

    /// <summary>
    /// Gets latest media.
    /// </summary>
    /// <param name="userId">User id.</param>
    /// <param name="parentId">Specify this to localize the search to a specific item or folder. Omit to use the root.</param>
    /// <param name="fields">Optional. Specify additional fields of information to return in the output.</param>
    /// <param name="includeItemTypes">Optional. If specified, results will be filtered based on item type. This allows multiple, comma delimited.</param>
    /// <param name="isPlayed">Filter by items that are played, or not.</param>
    /// <param name="enableImages">Optional. include image information in output.</param>
    /// <param name="imageTypeLimit">Optional. the max number of images to return, per image type.</param>
    /// <param name="enableImageTypes">Optional. The image types to include in the output.</param>
    /// <param name="enableUserData">Optional. include user data.</param>
    /// <param name="limit">Return item limit.</param>
    /// <param name="groupItems">Whether or not to group items into a parent container.</param>
    /// <response code="200">Latest media returned.</response>
    /// <returns>An <see cref="OkResult"/> containing the latest media.</returns>
    [HttpGet("Items/Latest")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<BaseItemDto>> GetLatestMedia(
        [FromQuery] Guid? userId,
        [FromQuery] Guid? parentId,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] ItemFields[] fields,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] BaseItemKind[] includeItemTypes,
        [FromQuery] bool? isPlayed,
        [FromQuery] bool? enableImages,
        [FromQuery] int? imageTypeLimit,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] ImageType[] enableImageTypes,
        [FromQuery] bool? enableUserData,
        [FromQuery] int limit = 20,
        [FromQuery] bool groupItems = true)
    {
        var requestUserId = RequestHelpers.GetUserId(User, userId);
        var user = _userManager.GetUserById(requestUserId);
        if (user is null)
        {
            return NotFound();
        }

        if (!isPlayed.HasValue)
        {
            if (user.HidePlayedInLatest)
            {
                isPlayed = false;
            }
        }

        var dtoOptions = new DtoOptions { Fields = fields }
            .AddAdditionalDtoOptions(enableImages, enableUserData, imageTypeLimit, enableImageTypes);

        dtoOptions.PreferEpisodeParentPoster = true;

        var list = _userViewManager.GetLatestItems(
            new LatestItemsQuery
            {
                GroupItems = groupItems,
                IncludeItemTypes = includeItemTypes,
                IsPlayed = isPlayed,
                Limit = limit,
                ParentId = parentId ?? Guid.Empty,
                User = user,
            },
            dtoOptions);

        var resolvedItems = new BaseItem[list.Count];
        var childCounts = new int[list.Count];
        for (int i = 0; i < list.Count; i++)
        {
            var tuple = list[i];
            var item = tuple.Item2[0];
            var childCount = 0;

            if (tuple.Item1 is not null && (tuple.Item2.Count > 1 || tuple.Item1 is MusicAlbum))
            {
                item = tuple.Item1;
                childCount = tuple.Item2.Count;
            }

            resolvedItems[i] = item;
            childCounts[i] = childCount;
        }

        // Fetch DTOs without visibility check since we've already done that in GetLatestItems and restore child counts afterwards
        var dtos = _dtoService.GetBaseItemDtos(resolvedItems, dtoOptions, user, skipVisibilityCheck: true);
        for (int i = 0; i < dtos.Count; i++)
        {
            if (childCounts[i] > 0)
            {
                dtos[i].ChildCount = childCounts[i];
            }
        }

        return Ok(dtos.AsEnumerable());
    }

    /// <summary>
    /// Subscribe to a season's episodes for a user.
    /// </summary>
    /// <param name="itemId">The episode id to subscribe to its parent season.</param>
    /// <response code="200">Successfully subscribed to season.</response>
    /// <response code="404">Item not found.</response>
    [HttpGet("Subscribe/")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async void SubscribeToSeason(
        [FromQuery, Required] string itemId)
    {
        var itemToSubscribe = _libraryManager.GetItemById(itemId);
        if (itemToSubscribe is null)
        {
            return;
        }

        if (itemToSubscribe is Episode episodeToSubscribe)
        {
            var seasonToSubscribe = episodeToSubscribe.Season;
            if (seasonToSubscribe is null)
            {
                return;
            }

            seasonToSubscribe.Subscribed = true;
            await seasonToSubscribe.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);

            var refreshMetadata = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllImages = true,
                ReplaceAllMetadata = true,
                ForceSave = true,
                IsAutomated = false,
                RemoveOldMetadata = true,
                EnableRemoteContentProbe = false
            };

            await _providerManager.RefreshFullItem(episodeToSubscribe, refreshMetadata, CancellationToken.None).ConfigureAwait(false);
        }

        return;
    }

    /// <summary>
    /// Get Latest Non-Downloaded Movies.
    /// </summary>
    ///
    /// <param name="userId">The user id.</param>
    /// <param name="library"> The Libarary name.</param>
    /// <param name="fields">Optional. Specify additional fields of information to return in the output.</param>
    /// <param name="imageTypeLimit">Optional. the max number of images to return, per image type.</param>
    /// <param name="enableImageTypes">Optional. The image types to include in the output.</param>
    /// <param name="limit">Return item limit.</param>
    /// <response code="200">Latest media returned.</response>
    /// <returns>An <see cref="OkResult"/> containing the latest media.</returns>
    [HttpGet("Newest/Movies/")]
    public ActionResult<List<BaseItemDto>> GetNewestMovies(
        [FromRoute, Required] Guid userId,
        [FromQuery] string library,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] ItemFields[] fields,
        [FromQuery] int? imageTypeLimit,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] ImageType[] enableImageTypes,
        [FromQuery] int limit = 20)
    {
        var requestUserId = RequestHelpers.GetUserId(User, userId);
        var user = _userManager.GetUserById(requestUserId);
        if (user is null)
        {
            return NotFound();
        }

        var resolvedLibrary = _libraryManager.GetVirtualFolders().FirstOrDefault(vf => vf.Name.Equals(library, StringComparison.OrdinalIgnoreCase));

        if (resolvedLibrary == null)
        {
            return NotFound();
        }

        string? libraryPath = resolvedLibrary.Locations.FirstOrDefault();
        if (string.IsNullOrEmpty(libraryPath))
        {
            return NotFound();
        }

        var parentItems = _libraryManager.GetItemList(new InternalItemsQuery
        {
            Path = libraryPath,
            IsFolder = true,
        });
        var parentItem = parentItems.Count > 0 ? parentItems[0] : null;

        if (parentItem == null)
        {
            return NotFound();
        }

        // Query library directly for all episodes without user filtering to see placeholder/virtual items
        var movies = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie },
            Recursive = true,
            Limit = 2000,
        })
        .OfType<Movie>()
        .Where(m => m.IsPlaceHolder || m.IsInternet || m.IsDownloadable) // placeholder changes to false when we start downloading and is removed from new movies list
        .ToList();

        var currentTime = DateTime.Now.Date;
        foreach (Movie movie in movies)
        {
            if (movie != null)
            {
                var airedDateNormalized = movie.FirstAiring?.Date;
                var isToday = airedDateNormalized == currentTime;
            }
        }

        // Some shows release multiple episodes the same day. For each series and premiere date,
        // return only one episode: if episode 1 is part of that day's release return episode 1,
        // otherwise return the latest episode (highest IndexNumber). Finally order by PremiereDate desc.
        var filteredEpisodes = movies
            .Where(e => e.FirstAiring.HasValue && e.FirstAiring.Value.ToLocalTime().Date == DateTime.Now.ToLocalTime().Date)
            // TODO: make order by popularity score.
            .OrderByDescending(e => e.FirstAiring)
            .ToList();

        var dtoOptions = new DtoOptions { Fields = fields };

        var moviesDto = filteredEpisodes
            .Select(movie =>
            {
                var dto = _dtoService.GetBaseItemDto(movie, dtoOptions, user);
                dto.IsPlaceHolder = false;
                return dto;
            })
            .ToList();

        return Ok(moviesDto);
    }

    /// <summary>
    /// Get Latest Non-Downloaded Data.
    /// </summary>
    ///
    /// <param name="userId">The user id.</param>
    /// <param name="library"> The Libarary name.</param>
    /// <param name="fields">Optional. Specify additional fields of information to return in the output.</param>
    /// <param name="imageTypeLimit">Optional. the max number of images to return, per image type.</param>
    /// <param name="enableImageTypes">Optional. The image types to include in the output.</param>
    /// <param name="limit">Return item limit.</param>
    /// <response code="200">Latest media returned.</response>
    /// <returns>An <see cref="OkResult"/> containing the latest media.</returns>
    [HttpGet("Newest/Shows/")]
    public ActionResult<List<BaseItemDto>> GetNewestShows(
        [FromRoute, Required] Guid userId,
        [FromQuery] string library,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] ItemFields[] fields,
        [FromQuery] int? imageTypeLimit,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] ImageType[] enableImageTypes,
        [FromQuery] int limit = 20)
    {
        var requestUserId = RequestHelpers.GetUserId(User, userId);
        var user = _userManager.GetUserById(requestUserId);
        if (user is null)
        {
            return NotFound();
        }

        var resolvedLibrary = _libraryManager.GetVirtualFolders().FirstOrDefault(vf => vf.Name.Equals(library, StringComparison.OrdinalIgnoreCase));

        if (resolvedLibrary == null)
        {
            return NotFound();
        }

        string? libraryPath = resolvedLibrary.Locations.FirstOrDefault();
        if (string.IsNullOrEmpty(libraryPath))
        {
            return NotFound();
        }

        var parentItems = _libraryManager.GetItemList(new InternalItemsQuery
        {
            Path = libraryPath,
            IsFolder = true,
        });
        var parentItem = parentItems.Count > 0 ? parentItems[0] : null;

        if (parentItem == null)
        {
            return NotFound();
        }

        // Query library directly for all episodes without user filtering to see placeholder/virtual items
        var episodes = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode, BaseItemKind.Movie },
            Recursive = true,
            Limit = 2000,
        })
        .OfType<Episode>()
        .Where(m => (m.IsPlaceHolder || m.IsInternet || m.IsDownloadable) && !m.Series.Genres.Contains("Hentai") && !m.Series.Tags.Contains("Nudity") && parentItem.Id.Equals(m.Season.Series.ParentId)) // placeholder changes to false when we start downloading and is removed from new movies list
        .ToList();

        var currentTime = DateTime.Now.Date;
        foreach (Episode episode in episodes)
        {
            if (episode != null)
            {
                var airedDateNormalized = episode.FirstAiring?.Date;
                var isToday = airedDateNormalized == currentTime;
            }
        }

        // Some shows release multiple episodes the same day. For each series and premiere date,
        // return only one episode: if episode 1 is part of that day's release return episode 1,
        // otherwise return the latest episode (highest IndexNumber). Finally order by PremiereDate desc.
        var filteredEpisodes = episodes
            .Where(e => e.FirstAiring.HasValue && e.FirstAiring.Value.ToLocalTime().Date == DateTime.Now.ToLocalTime().Date)
            .GroupBy(e => e.SeriesId)
            .Select(g => g
                .OrderBy(e => e.FirstAiring)
                .ThenBy(e => e.IndexNumber)
                .First())
            .OrderBy(e => e.FirstAiring)
            .ToList();

        var dtoOptions = new DtoOptions { Fields = fields };

        var moviesDto = filteredEpisodes
            .Select(movie =>
            {
                var dto = _dtoService.GetBaseItemDto(movie, dtoOptions, user);
                dto.IsPlaceHolder = false;
                return dto;
            })
            .ToList();

        return Ok(moviesDto);
    }

    /// <summary>
    /// Gets latest media.
    /// </summary>
    /// <param name="userId">User id.</param>
    /// <param name="parentId">Specify this to localize the search to a specific item or folder. Omit to use the root.</param>
    /// <param name="fields">Optional. Specify additional fields of information to return in the output.</param>
    /// <param name="includeItemTypes">Optional. If specified, results will be filtered based on item type. This allows multiple, comma delimited.</param>
    /// <param name="isPlayed">Filter by items that are played, or not.</param>
    /// <param name="enableImages">Optional. include image information in output.</param>
    /// <param name="imageTypeLimit">Optional. the max number of images to return, per image type.</param>
    /// <param name="enableImageTypes">Optional. The image types to include in the output.</param>
    /// <param name="enableUserData">Optional. include user data.</param>
    /// <param name="limit">Return item limit.</param>
    /// <param name="groupItems">Whether or not to group items into a parent container.</param>
    /// <response code="200">Latest media returned.</response>
    /// <returns>An <see cref="OkResult"/> containing the latest media.</returns>
    [HttpGet("Users/{userId}/Items/Latest")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [Obsolete("Kept for backwards compatibility")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public ActionResult<IEnumerable<BaseItemDto>> GetLatestMediaLegacy(
        [FromRoute, Required] Guid userId,
        [FromQuery] Guid? parentId,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] ItemFields[] fields,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] BaseItemKind[] includeItemTypes,
        [FromQuery] bool? isPlayed,
        [FromQuery] bool? enableImages,
        [FromQuery] int? imageTypeLimit,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] ImageType[] enableImageTypes,
        [FromQuery] bool? enableUserData,
        [FromQuery] int limit = 20,
        [FromQuery] bool groupItems = true)
        => GetLatestMedia(
            userId,
            parentId,
            fields,
            includeItemTypes,
            isPlayed,
            enableImages,
            imageTypeLimit,
            enableImageTypes,
            enableUserData,
            limit,
            groupItems);

    /// <summary>
    /// Get Latest Non-Downloaded Data.
    /// </summary>
    ///
    /// <param name="userId">The user id.</param>
    /// <param name="libraryId"> The Libarary name.</param>
    /// <param name="fields">Optional. Specify additional fields of information to return in the output.</param>
    /// <param name="imageTypeLimit">Optional. the max number of images to return, per image type.</param>
    /// <param name="enableImageTypes">Optional. The image types to include in the output.</param>
    /// <param name="limit">Return item limit.</param>
    /// <response code="200">Latest media returned.</response>
    /// <returns>An <see cref="OkResult"/> containing the latest media.</returns>
    [HttpGet("Users/{userId}/Newest/Items/")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [Obsolete("Kept for backwards compatibility")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public ActionResult<List<BaseItemDto>> GetNewestMediaLegacy(
        [FromRoute, Required] Guid userId,
        [FromQuery] string libraryId,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] ItemFields[] fields,
        [FromQuery] int? imageTypeLimit,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] ImageType[] enableImageTypes,
        [FromQuery] int limit = 20)
    {
        if (libraryId == "Movies")
        {
            return GetNewestMovies(
                userId,
                libraryId,
                fields,
                imageTypeLimit,
                enableImageTypes,
                limit);
        }

        return GetNewestShows(
            userId,
            libraryId,
            fields,
            imageTypeLimit,
            enableImageTypes,
            limit);
    }

    /// <summary>
    /// Subscribe to a season's episodes for a user.
    /// </summary>
    /// <param name="itemId">The episode id to subscribe to its parent season.</param>
    /// <response code="200">Successfully subscribed to season.</response>
    /// <response code="404">Item not found.</response>
    [HttpGet("Users/{userId}/Subscribe/")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [Obsolete("Kept for backwards compatibility")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async void SubscribeToSeasonLegacy(
        [FromQuery, Required] string itemId)
        => SubscribeToSeason(
            itemId);

    private async Task RefreshItemOnDemandIfNeeded(BaseItem item)
    {
        if (item is Person)
        {
            var hasMetadata = !string.IsNullOrWhiteSpace(item.Overview) && item.HasImage(ImageType.Primary);
            var performFullRefresh = !hasMetadata && (DateTime.UtcNow - item.DateLastRefreshed).TotalDays >= 3;

            if (performFullRefresh)
            {
                var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                {
                    MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                    ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                    ForceSave = true
                };

                await item.RefreshMetadata(options, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Marks the favorite.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="item">The item.</param>
    /// <param name="isFavorite">if set to <c>true</c> [is favorite].</param>
    private UserItemDataDto MarkFavorite(User user, BaseItem item, bool isFavorite)
    {
        // Get the user data for this item
        var data = _userDataRepository.GetUserData(user, item);

        if (data is not null)
        {
            // Set favorite status
            data.IsFavorite = isFavorite;

            _userDataRepository.SaveUserData(user, item, data, UserDataSaveReason.UpdateUserRating, CancellationToken.None);
        }

        return _userDataRepository.GetUserDataDto(item, user)!;
    }

    /// <summary>
    /// Updates the user item rating.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="item">The item.</param>
    /// <param name="likes">if set to <c>true</c> [likes].</param>
    private UserItemDataDto? UpdateUserItemRatingInternal(User user, BaseItem item, bool? likes)
    {
        // Get the user data for this item
        var data = _userDataRepository.GetUserData(user, item);

        if (data is not null)
        {
            data.Likes = likes;

            _userDataRepository.SaveUserData(user, item, data, UserDataSaveReason.UpdateUserRating, CancellationToken.None);
        }

        return _userDataRepository.GetUserDataDto(item, user);
    }
}
