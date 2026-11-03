using BusinessCard.Api.Common;
using BusinessCard.Api.Data;
using BusinessCard.Api.Domain;
using BusinessCard.Api.Dtos;
using Microsoft.EntityFrameworkCore;

namespace BusinessCard.Api.Services;

/// <summary>Shared application service behind both nickname endpoints. Only the nickname column is modified.</summary>
public class NicknameService(AppDbContext db)
{
    public async Task<NicknameResponse> SetAsync(User target, NicknameRequest? request)
    {
        if (request is null || !request.NicknameSpecified)
            throw ApiException.BadRequest("INVALID_NICKNAME", "İstifadəçi adının formatı düzgün deyil.");

        var normalized = Validation.NormalizeNickname(request.Nickname);
        if (target.Nickname == normalized) return new NicknameResponse(target.Id, target.Nickname);

        target.Nickname = normalized;
        try
        {
            // The unique index is the source of truth; concurrent identical requests: exactly one wins.
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex, AppDbContext.NicknameIndexName))
        {
            throw ApiException.Conflict("NICKNAME_TAKEN", "Bu istifadəçi adı artıq istifadə olunur.");
        }
        return new NicknameResponse(target.Id, target.Nickname);
    }
}
