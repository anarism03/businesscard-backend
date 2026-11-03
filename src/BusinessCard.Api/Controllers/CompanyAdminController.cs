using BusinessCard.Api.Common;
using BusinessCard.Api.Data;
using BusinessCard.Api.Domain;
using BusinessCard.Api.Dtos;
using BusinessCard.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;

namespace BusinessCard.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
[Produces("application/json")]
public class CompanyAdminController(
    AppDbContext db,
    CurrentUser current,
    CompanyService companies,
    UserService users,
    NicknameService nicknames,
    ImageService images) : ControllerBase
{
    /// <summary>The caller's company (always includes the effective <c>brandColor</c>).</summary>
    [HttpGet("company")]
    [ProducesResponseType<CompanyDto>(200)]
    public async Task<CompanyDto> GetCompany()
    {
        var me = await current.RequireAsync(Role.CompanyAdmin);
        return await companies.GetAsync(me.CompanyId!.Value);
    }

    /// <summary>
    /// Updates the caller's own company. <c>brandColor</c>: omitted = unchanged, <c>null</c> = reset to <c>#1b4a75</c>,
    /// otherwise strict <c>#RRGGBB</c>. The color applies to every employee card of the company immediately.
    /// </summary>
    /// <response code="400">INVALID_BRAND_COLOR or other validation error.</response>
    [HttpPut("company")]
    [ProducesResponseType<CompanyDto>(200)]
    [ProducesResponseType<ErrorResponse>(400)]
    public async Task<CompanyDto> UpdateCompany([FromBody] UpdateCompanyDto dto)
    {
        var me = await current.RequireAsync(Role.CompanyAdmin);
        return await companies.UpdateAsync(me.CompanyId!.Value, dto, isSuperAdmin: false);
    }

    /// <summary>Uploads (and optionally crops) the company logo. See <c>CropRequest</c> for the coordinate rules.</summary>
    [HttpPost("company/logo")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(12 * 1024 * 1024)]
    [ProducesResponseType<ImageResultDto>(200)]
    [ProducesResponseType<ErrorResponse>(400)]
    public async Task<ImageResultDto> UploadLogo(IFormFile? file, [FromForm] CropRequest crop)
    {
        var me = await current.RequireAsync(Role.CompanyAdmin);
        var company = await db.Companies.FirstAsync(c => c.Id == me.CompanyId);
        return await images.UploadCompanyLogoAsync(me, company, file, crop);
    }

    /// <summary>Creates a user in the caller's company. New users start with <c>canEdit = false</c>.</summary>
    /// <response code="400">USER_LIMIT_EXCEEDED / validation error.</response>
    /// <response code="409">EMAIL_TAKEN.</response>
    [HttpPost("users")]
    [ProducesResponseType<CreateUserResultDto>(200)]
    [ProducesResponseType<ErrorResponse>(400)]
    [ProducesResponseType<ErrorResponse>(409)]
    public async Task<CreateUserResultDto> CreateUser([FromBody] CreateUserDto dto)
    {
        var me = await current.RequireAsync(Role.CompanyAdmin, Role.SuperAdmin);
        return await users.CreateAsync(me, dto);
    }

    /// <summary>Paged users of a company: <c>{ items, totalCount, page, pageSize }</c>.</summary>
    [HttpGet("users/company/{companyId:guid}")]
    [ProducesResponseType<PageResult<UserDto>>(200)]
    public async Task<PageResult<UserDto>> GetUsers(Guid companyId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var me = await current.RequireAsync(Role.CompanyAdmin, Role.SuperAdmin);
        CurrentUser.EnsureCompanyAccess(me, companyId);
        return await users.ListByCompanyAsync(companyId, page, pageSize);
    }

    [HttpPut("users/{id:guid}/active")]
    [ProducesResponseType<UserDto>(200)]
    public async Task<UserDto> SetActive(Guid id, [FromQuery] bool isActive)
    {
        var me = await current.RequireAsync(Role.CompanyAdmin, Role.SuperAdmin);
        return await users.SetActiveAsync(me, id, isActive);
    }

    [HttpPut("users/{id:guid}/canedit")]
    [ProducesResponseType<UserDto>(200)]
    public async Task<UserDto> SetCanEdit(Guid id, [FromQuery] bool canEdit)
    {
        var me = await current.RequireAsync(Role.CompanyAdmin, Role.SuperAdmin);
        return await users.SetCanEditAsync(me, id, canEdit);
    }

    /// <summary>Updates another user's profile in the caller's company.</summary>
    [HttpPut("users/{id:guid}")]
    [ProducesResponseType<UserDto>(200)]
    public async Task<UserDto> UpdateUser(Guid id, [FromBody] UpdateUserProfileDto dto)
    {
        var me = await current.RequireAsync(Role.CompanyAdmin, Role.SuperAdmin);
        var target = await users.GetDetailedAsync(id);
        UserService.EnsureCanManage(me, target);
        return await users.UpdateProfileAsync(target, dto);
    }

    /// <summary>
    /// Sets or removes a user's nickname (only within the caller's company). Body must contain <c>nickname</c>;
    /// <c>null</c> removes it. Only the nickname is changed.
    /// </summary>
    /// <response code="400">INVALID_NICKNAME</response>
    /// <response code="403">User belongs to another company.</response>
    /// <response code="404">User not found.</response>
    /// <response code="409">NICKNAME_TAKEN</response>
    [HttpPut("users/{id:guid}/nickname")]
    [ProducesResponseType<NicknameResponse>(200)]
    [ProducesResponseType<ErrorResponse>(400)]
    [ProducesResponseType<ErrorResponse>(403)]
    [ProducesResponseType<ErrorResponse>(404)]
    [ProducesResponseType<ErrorResponse>(409)]
    public async Task<NicknameResponse> SetNickname(Guid id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] NicknameRequest? request)
    {
        var me = await current.RequireAsync(Role.CompanyAdmin);
        var target = await db.Users.FirstOrDefaultAsync(u => u.Id == id) ?? throw ApiException.NotFound("User not found");
        UserService.EnsureCanManage(me, target);
        return await nicknames.SetAsync(target, request);
    }

    /// <summary>Uploads (and optionally crops) another user's profile photo.</summary>
    [HttpPost("users/{id:guid}/photo")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(12 * 1024 * 1024)]
    [ProducesResponseType<ImageResultDto>(200)]
    public async Task<ImageResultDto> UploadUserPhoto(Guid id, IFormFile? file, [FromForm] CropRequest crop)
    {
        var me = await current.RequireAsync(Role.CompanyAdmin, Role.SuperAdmin);
        var target = await db.Users.FirstOrDefaultAsync(u => u.Id == id) ?? throw ApiException.NotFound("User not found");
        return await images.UploadForUserAsync(me, target, ImageKind.ProfilePhoto, file, crop);
    }

    /// <summary>Uploads (and optionally crops) another user's card background (same company only).</summary>
    [HttpPost("users/{id:guid}/card-background")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(12 * 1024 * 1024)]
    [ProducesResponseType<ImageResultDto>(200)]
    public async Task<ImageResultDto> UploadUserBackground(Guid id, IFormFile? file, [FromForm] CropRequest crop)
    {
        var me = await current.RequireAsync(Role.CompanyAdmin, Role.SuperAdmin);
        var target = await db.Users.FirstOrDefaultAsync(u => u.Id == id) ?? throw ApiException.NotFound("User not found");
        return await images.UploadForUserAsync(me, target, ImageKind.CardBackground, file, crop);
    }

    /// <summary>Resets a user's password. Returns the new temporary password once; the user must change it at next login.</summary>
    [HttpPost("users/{id:guid}/reset-password")]
    [ProducesResponseType<ResetPasswordResultDto>(200)]
    public async Task<ResetPasswordResultDto> ResetPassword(Guid id, [FromBody] ResetPasswordDto dto)
    {
        var me = await current.RequireAsync(Role.CompanyAdmin, Role.SuperAdmin);
        return await users.ResetPasswordAsync(me, id, dto);
    }
}
