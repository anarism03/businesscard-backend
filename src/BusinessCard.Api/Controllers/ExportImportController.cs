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
public class ExportImportController(CurrentUser current, ExportImportService service) : ControllerBase
{
    private FileContentResult Send(FileResultData f)
    {
        Response.Headers.CacheControl = "no-store";
        return File(f.Content, f.ContentType, f.FileName);
    }

    /// <summary>vCard 3.0 of a user (photo embedded).</summary>
    [HttpGet("vcf/{userId:guid}")]
    [Produces("text/vcard")]
    public async Task<IActionResult> Vcf(Guid userId) => Send(await service.VcfAsync(await current.RequireAsync(), userId));

    /// <summary>All users of a company as .xlsx (passwords are never exported).</summary>
    [HttpGet("excel/{companyId:guid}")]
    [Produces("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    public async Task<IActionResult> Excel(Guid companyId) =>
        Send(await service.ExcelCompanyAsync(await current.RequireAsync(Role.CompanyAdmin, Role.SuperAdmin), companyId));

    /// <summary>Selected users as .xlsx. Body: array of user UUIDs.</summary>
    [HttpPost("excel/export-selected")]
    [Consumes("application/json")]
    [Produces("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    public async Task<IActionResult> ExcelSelected([FromBody] List<Guid> userIds) =>
        Send(await service.ExcelSelectedAsync(await current.RequireAsync(Role.CompanyAdmin, Role.SuperAdmin), userIds));

    /// <summary>Import template (.xlsx) with the expected column headers.</summary>
    [HttpGet("excel/template")]
    [Produces("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    public async Task<IActionResult> Template()
    {
        await current.RequireAsync(Role.CompanyAdmin, Role.SuperAdmin);
        return Send(service.Template());
    }

    /// <summary>
    /// Imports users from .xlsx/.xls. Rows are processed independently: <c>{ created, failed, errors[], generatedCredentials[] }</c>.
    /// The company user limit is enforced under a lock.
    /// </summary>
    [HttpPost("excel/import/{companyId:guid}")]
    [Consumes("multipart/form-data")]
    [Produces("application/json")]
    [RequestSizeLimit(12 * 1024 * 1024)]
    [ProducesResponseType<ImportResultDto>(200)]
    public async Task<ImportResultDto> Import(Guid companyId, IFormFile? file) =>
        await service.ImportAsync(await current.RequireAsync(Role.CompanyAdmin, Role.SuperAdmin), companyId, file);

    /// <summary>A single self-contained offline HTML card.</summary>
    [HttpGet("html/user/{userId:guid}")]
    [Produces("text/html")]
    public async Task<IActionResult> HtmlUser(Guid userId) => Send(await service.HtmlUserAsync(await current.RequireAsync(), userId));

    /// <summary>ZIP of offline HTML cards for the whole company (plus index.html).</summary>
    [HttpGet("html/{companyId:guid}")]
    [Produces("application/zip")]
    public async Task<IActionResult> HtmlCompany(Guid companyId) =>
        Send(await service.HtmlCompanyAsync(await current.RequireAsync(Role.CompanyAdmin, Role.SuperAdmin), companyId));

    /// <summary>ZIP of offline HTML cards for the selected users. Body: array of user UUIDs.</summary>
    [HttpPost("html/export-selected")]
    [Consumes("application/json")]
    [Produces("application/zip")]
    public async Task<IActionResult> HtmlSelected([FromBody] List<Guid> userIds) =>
        Send(await service.HtmlSelectedAsync(await current.RequireAsync(Role.CompanyAdmin, Role.SuperAdmin), userIds));
}
