using Microsoft.AspNetCore.Diagnostics;

public static class ApiExceptionHandler
{
    // Store methods signal business errors with BadHttpRequestException; return them as { detail }
    // with their status code, and never leak stack traces for anything else.
    public static async Task HandleAsync(HttpContext context)
    {
        var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
        var (status, detail) = exception switch
        {
            BadHttpRequestException bad => (bad.StatusCode, bad.Message),
            OperationCanceledException when context.RequestAborted.IsCancellationRequested => (499, "Request cancelled."),
            _ => (StatusCodes.Status500InternalServerError, "服务器内部错误，请稍后重试。")
        };

        if (status >= 500 && exception is not null)
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("MaskFlow.Errors")
                .LogError(exception, "Unhandled exception for {Method} {Path}", context.Request.Method, context.Request.Path);
        }

        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { detail });
    }
}
