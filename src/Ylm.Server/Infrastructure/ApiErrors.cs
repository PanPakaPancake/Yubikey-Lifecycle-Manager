using Ylm.Core;

namespace Ylm.Server.Infrastructure;

public sealed record ApiError(string Code, string Message, object? Details = null);

public sealed class ApiException(int status, string code, string message, object? details = null) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public object? Details { get; } = details;

    public static ApiException NotFound(string what = "Eintrag") => new(404, "not_found", $"{what} nicht gefunden.");
    public static ApiException BadRequest(string code, string message, object? details = null) => new(400, code, message, details);
    public static ApiException Conflict(string code, string message) => new(409, code, message);
    public static ApiException Forbidden(string message = "Keine Berechtigung für diese Aktion.") => new(403, "forbidden", message);
}

public sealed class ApiExceptionMiddleware(RequestDelegate next, ILogger<ApiExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (ApiException ex) when (!context.Response.HasStarted)
        {
            context.Response.StatusCode = ex.Status;
            await context.Response.WriteAsJsonAsync(new ApiError(ex.Code, ex.Message, ex.Details));
        }
        catch (DomainException ex) when (!context.Response.HasStarted)
        {
            context.Response.StatusCode = 409;
            await context.Response.WriteAsJsonAsync(new ApiError(ex.Code, ex.Message));
        }
        catch (Graph.GraphException ex) when (!context.Response.HasStarted)
        {
            logger.LogWarning(ex, "Graph call failed");
            context.Response.StatusCode = 502;
            await context.Response.WriteAsJsonAsync(new ApiError("graph_error", ex.Message, new { ex.StatusCode, ex.GraphCode }));
        }
    }
}
