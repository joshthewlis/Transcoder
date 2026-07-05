using Microsoft.Extensions.Options;
using Transcoder.Server.Options;

namespace Transcoder.Server.Middleware;

public sealed class ApiKeyMiddleware(RequestDelegate next, IOptions<SecurityOptions> options, IWebHostEnvironment environment)
{
    private const string HeaderName = "X-Transcoder-Api-Key";

    public async Task Invoke(HttpContext context)
    {
        var security = options.Value;
        var hasConfiguredKey = !string.IsNullOrWhiteSpace(security.AdminApiKey) || !string.IsNullOrWhiteSpace(security.WorkerApiKey);

        if (!hasConfiguredKey || environment.IsDevelopment())
        {
            await next(context);
            return;
        }

        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await next(context);
            return;
        }

        var supplied = context.Request.Headers[HeaderName].FirstOrDefault();
        var isValid = !string.IsNullOrWhiteSpace(supplied)
                      && (string.Equals(supplied, security.AdminApiKey, StringComparison.Ordinal)
                          || string.Equals(supplied, security.WorkerApiKey, StringComparison.Ordinal));

        if (!isValid)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Missing or invalid Transcoder API key.");
            return;
        }

        await next(context);
    }
}
