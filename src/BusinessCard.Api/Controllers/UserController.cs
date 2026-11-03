using BusinessCard.Api.Common;
using BusinessCard.Api.Domain;
using BusinessCard.Api.Dtos;
using BusinessCard.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace BusinessCard.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
[Produces("application/json")]
public class UserController(CurrentUser current, UserService users, NicknameService nicknames, ImageService images) : ControllerBase
{
    /// <summary>User detail (self, admins of the same company, or super admin). Always includes <c>nickname</c> (null when unset).</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<UserDto>(200)]
    [ProducesResponseType<ErrorResponse>(403)]
    [ProducesResponseType<ErrorResponse>(404)]
    public async Task<UserDto> Get(Guid id)
    {
        var me = await current.RequireAsync();
        var user = await users.GetDetailedAsync(id);
        UserService.EnsureCanView(me, user);
        return user.ToDto();
    }

    /// <summary>Updates the caller's own profile. Employees need <c>canEdit = true</c>.</summary>
    [HttpPut("profile")]
    [ProducesResponseType<UserDto>(200)]
    [ProducesResponseType<ErrorResponse>(403)]
    public async Task<UserDto> UpdateProfile([FromBody] UpdateUserProfileDto dto)
    {
        var me = await current.RequireAsync();
        if (!UserService.CanEditOwnProfile(me))
            throw ApiException.Forbidden("Profile editing is disabled for your account");
        var user = await users.GetDetailedAsync(me.Id);
        return await users.UpdateProfileAsync(user, dto);
    }

    /// <summary>
    /// Sets or removes the caller's own nickname. Not blocked by <c>canEdit = false</c>. Body must contain
    /// <c>nickname</c>; <c>null</c> removes it. Only the nickname is changed.
    /// </summary>
    /// <response code="400">INVALID_NICKNAME</response>
    /// <response code="409">NICKNAME_TAKEN</response>
    [HttpPut("profile/nickname")]
    [ProducesResponseType<NicknameResponse>(200)]
    [ProducesResponseType<ErrorResponse>(400)]
    [ProducesResponseType<ErrorResponse>(409)]
    public async Task<NicknameResponse> SetNickname([FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] NicknameRequest? request)
    {
        var me = await current.RequireAsync();
        if (me.Role == Role.SuperAdmin) throw ApiException.Forbidden("Super admins have no public card");
        return await nicknames.SetAsync(me, request);
    }

    /// <summary>Uploads (and optionally crops) the caller's profile photo.</summary>
    [HttpPost("profile/photo")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(12 * 1024 * 1024)]
    [ProducesResponseType<ImageResultDto>(200)]
    public async Task<ImageResultDto> UploadPhoto(IFormFile? file, [FromForm] CropRequest crop)
    {
        var me = await current.RequireAsync(Role.CompanyAdmin, Role.Employee);
        return await images.UploadForUserAsync(me, me, ImageKind.ProfilePhoto, file, crop);
    }

    /// <summary>Uploads (and optionally crops) the caller's card background.</summary>
    [HttpPost("profile/card-background")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(12 * 1024 * 1024)]
    [ProducesResponseType<ImageResultDto>(200)]
    public async Task<ImageResultDto> UploadBackground(IFormFile? file, [FromForm] CropRequest crop)
    {
        var me = await current.RequireAsync(Role.CompanyAdmin, Role.Employee);
        return await images.UploadForUserAsync(me, me, ImageKind.CardBackground, file, crop);
    }
}

[ApiController]
[Route("api/images")]
[Authorize]
[Produces("application/json")]
public class ImagesController(CurrentUser current, ImageService images) : ControllerBase
{
    /// <summary>Metadata of a stored image (result URL, original, dimensions, current crop).</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<ImageResultDto>(200)]
    public async Task<ImageResultDto> Get(Guid id)
    {
        var me = await current.RequireAsync();
        return await images.GetAsync(me, id);
    }

    /// <summary>
    /// Re-crops an existing image from its untouched ORIGINAL (never from the previous result).
    /// Coordinates are pixels of the EXIF-oriented original, origin top-left. The owning record is updated.
    /// </summary>
    [HttpPut("{id:guid}/crop")]
    [Consumes("application/json")]
    [ProducesResponseType<ImageResultDto>(200)]
    [ProducesResponseType<ErrorResponse>(400)]
    public async Task<ImageResultDto> Recrop(Guid id, [FromBody] CropRequest crop)
    {
        var me = await current.RequireAsync();
        return await images.RecropAsync(me, id, crop);
    }

    /// <summary>Downloads the original image (authorized users only; originals are not public).</summary>
    [HttpGet("{id:guid}/original")]
    [Produces("image/jpeg", "image/png", "image/webp", "image/gif", "image/bmp")]
    public async Task<IActionResult> Original(Guid id)
    {
        var me = await current.RequireAsync();
        var (path, contentType) = await images.GetOriginalAsync(me, id);
        Response.Headers.CacheControl = "private, no-store";
        return PhysicalFile(path, contentType);
    }
}
