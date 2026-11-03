using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace BusinessCard.Api.Common;

/// <summary>Business error returned to clients as <c>{ "code": "...", "message": "..." }</c>.</summary>
public class ApiException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;

    public static ApiException BadRequest(string code, string message) => new(400, code, message);
    public static ApiException Unauthorized(string message = "Authentication required") => new(401, "UNAUTHORIZED", message);
    public static ApiException Forbidden(string message = "You do not have permission for this operation") => new(403, "FORBIDDEN", message);
    public static ApiException NotFound(string message = "Not found") => new(404, "NOT_FOUND", message);
    public static ApiException Conflict(string code, string message) => new(409, code, message);
}

public record ErrorResponse(string Code, string Message);

public class ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
{
    public async Task Invoke(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (ApiException ex)
        {
            await Write(context, ex.Status, ex.Code, ex.Message);
        }
        catch (DbUpdateConcurrencyException)
        {
            await Write(context, 409, "CONFLICT", "The record was changed by another request. Please retry.");
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Client went away; nothing to write.
        }
        catch (BadHttpRequestException ex)
        {
            await Write(context, ex.StatusCode, "BAD_REQUEST", ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled error for {Method} {Path}", context.Request.Method, context.Request.Path);
            await Write(context, 500, "SERVER_ERROR", "An unexpected server error occurred.");
        }
    }

    private static async Task Write(HttpContext context, int status, string code, string message)
    {
        if (context.Response.HasStarted) return;
        context.Response.Clear();
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(JsonSerializer.Serialize(
            new ErrorResponse(code, message), JsonSerializerOptions.Web));
    }
}
