using BusinessCard.Api.Common;
using BusinessCard.Api.Domain;
using BusinessCard.Api.Dtos;
using BusinessCard.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BusinessCard.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
[Produces("application/json")]
public class SuperAdminController(CurrentUser current, CompanyService companies) : ControllerBase
{
    [HttpGet("dashboard")]
    [ProducesResponseType<DashboardDto>(200)]
    public async Task<DashboardDto> Dashboard()
    {
        await current.RequireAsync(Role.SuperAdmin);
        return await companies.DashboardAsync();
    }

    /// <summary>Paged companies: <c>{ items, totalCount, page, pageSize }</c>.</summary>
    [HttpGet("companies")]
    [ProducesResponseType<PageResult<CompanyDto>>(200)]
    public async Task<PageResult<CompanyDto>> GetCompanies([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        await current.RequireAsync(Role.SuperAdmin);
        return await companies.ListAsync(page, pageSize);
    }

    /// <summary>
    /// Creates a company and its company admin. The admin login is <c>email</c> + the company VÖEN; the one-time
    /// temporary password is returned only in this response and must be changed at first login.
    /// </summary>
    /// <response code="409">VOEN_TAKEN</response>
    [HttpPost("companies")]
    [ProducesResponseType<CreateCompanyResultDto>(200)]
    [ProducesResponseType<ErrorResponse>(400)]
    [ProducesResponseType<ErrorResponse>(409)]
    public async Task<CreateCompanyResultDto> CreateCompany([FromBody] CreateCompanyDto dto)
    {
        await current.RequireAsync(Role.SuperAdmin);
        return await companies.CreateAsync(dto);
    }

    /// <summary>Sets the user limit. Body is a bare integer, e.g. <c>50</c>. Cannot go below current usage.</summary>
    [HttpPut("companies/{id:guid}/limit")]
    [ProducesResponseType<CompanyDto>(200)]
    public async Task<CompanyDto> SetLimit(Guid id, [FromBody] int limit)
    {
        await current.RequireAsync(Role.SuperAdmin);
        return await companies.SetLimitAsync(id, limit);
    }

    [HttpGet("companies/{id:guid}")]
    [ProducesResponseType<CompanyDto>(200)]
    public async Task<CompanyDto> GetCompany(Guid id)
    {
        await current.RequireAsync(Role.SuperAdmin);
        return await companies.GetAsync(id);
    }

    /// <summary>Updates any company field, including <c>voen</c>, <c>userLimit</c> and <c>brandColor</c> (same omitted/null rules).</summary>
    [HttpPut("companies/{id:guid}")]
    [ProducesResponseType<CompanyDto>(200)]
    public async Task<CompanyDto> UpdateCompany(Guid id, [FromBody] UpdateCompanyDto dto)
    {
        await current.RequireAsync(Role.SuperAdmin);
        return await companies.UpdateAsync(id, dto, isSuperAdmin: true);
    }

    [HttpPut("companies/{id:guid}/active")]
    [ProducesResponseType<CompanyDto>(200)]
    public async Task<CompanyDto> SetActive(Guid id, [FromQuery] bool isActive)
    {
        await current.RequireAsync(Role.SuperAdmin);
        return await companies.SetActiveAsync(id, isActive);
    }
}
