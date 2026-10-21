using NxHub.Pokemon.Api;
using NxHub.Pokemon.Auth;

namespace NxHub.Pokemon;

/// <summary>
/// Identifies the user on every <c>/api</c> route except health. Changes made through a proxy
/// login (a cookie) must come from this site's own page.
/// </summary>
public sealed class AuthMiddleware(RequestDelegate next, WebAuth auth)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        if (!path.StartsWithSegments("/api") || path.StartsWithSegments("/api/health"))
        {
            await next(context);
            return;
        }

        var user = auth.Identify(context);
        if (user is null)
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            await Endpoints.Problem(
                StatusCodes.Status401Unauthorized,
                "unauthorized",
                "Sign in required",
                "Send 'Authorization: Bearer <token>'. The token is in the data directory (admin-token).").ExecuteAsync(context);
            return;
        }

        var safe = HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method) || HttpMethods.IsOptions(context.Request.Method);
        if (!safe && auth.IsCookieBacked(context) && !WebAuth.IsSameOrigin(context.Request))
        {
            await Endpoints.Problem(
                StatusCodes.Status403Forbidden,
                "cross_site_request",
                "Cross-site request blocked",
                "Changes must come from the trade bot page itself.").ExecuteAsync(context);
            return;
        }

        context.Items[typeof(WebUser)] = user;
        await next(context);
    }
}

/// <summary>Conservative browser security headers. The page is same-origin only.</summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    private const string ContentSecurityPolicy =
        "default-src 'self'; connect-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; " +
        "object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";

    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers.XFrameOptions = "DENY";
        headers.ContentSecurityPolicy = ContentSecurityPolicy;
        return next(context);
    }
}
