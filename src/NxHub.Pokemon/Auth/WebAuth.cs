using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace NxHub.Pokemon.Auth;

/// <summary>Who is making a request. <see cref="Id"/> is the stable id SysBot.NET's queue uses per user.</summary>
public sealed record WebUser(string Name, ulong Id, bool IsOwner)
{
    public static readonly WebUser Owner = new("owner", IdFor("owner"), true);

    /// <summary>A stable 64-bit id for a web user. The top bit is set so it never collides with a Discord user id.</summary>
    public static ulong IdFor(string name)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("web:" + name.ToUpperInvariant()));
        return BitConverter.ToUInt64(hash, 0) | (1UL << 63);
    }
}

/// <summary>
/// The admin bearer token, plus sign-in vouched for by a reverse proxy on this machine.
/// Same model as NX Hub: the token is generated on first start and never logged.
/// </summary>
public sealed class WebAuth
{
    public const string TokenFileName = "admin-token";

    private readonly byte[]? _tokenHash;
    private readonly string? _proxyHeader;
    private readonly HashSet<string> _owners;

    public WebAuth(IOptions<PokemonOptions> options, DataDirectory data, ILogger<WebAuth> logger)
    {
        var auth = options.Value.Auth;
        Enabled = auth.Mode == AuthMode.Token;
        _proxyHeader = string.IsNullOrWhiteSpace(auth.TrustedProxyUserHeader) ? null : auth.TrustedProxyUserHeader.Trim();
        _owners = new HashSet<string>(auth.Owners.Where(o => !string.IsNullOrWhiteSpace(o)).Select(o => o.Trim()), StringComparer.OrdinalIgnoreCase);
        TokenFilePath = Path.Combine(data.Path, TokenFileName);
        if (!Enabled)
        {
            logger.LogWarning("Authentication is DISABLED (NxHubPokemon:Auth:Mode=Disabled). Anyone who can reach this server controls the trade bot.");
            return;
        }

        var token = string.IsNullOrWhiteSpace(auth.Token) ? LoadOrCreate(TokenFilePath, logger) : auth.Token.Trim();
        if (token.Length < 16)
        {
            throw new InvalidOperationException("NxHubPokemon:Auth:Token must be at least 16 characters.");
        }

        _tokenHash = Hash(token);
    }

    public bool Enabled { get; }

    public string TokenFilePath { get; }

    /// <summary>The signed-in user, or null when the request is not authenticated.</summary>
    public WebUser? Identify(HttpContext context)
    {
        if (!Enabled)
        {
            return WebUser.Owner;
        }

        var header = context.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && ValidToken(header["Bearer ".Length..].Trim()))
        {
            return WebUser.Owner;
        }

        if (_proxyHeader is null || context.Connection.RemoteIpAddress is not { } remote || !System.Net.IPAddress.IsLoopback(remote))
        {
            return null;
        }

        var name = context.Request.Headers[_proxyHeader].ToString().Trim();
        if (name.Length is 0 or > 256)
        {
            return null;
        }

        var owner = _owners.Count == 0 || _owners.Contains(name);
        return new WebUser(name, WebUser.IdFor(name), owner);
    }

    /// <summary>True for requests authenticated by a proxy cookie, which other websites could otherwise trigger.</summary>
    public bool IsCookieBacked(HttpContext context) =>
        Enabled && !context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);

    /// <summary>Browser same-origin check. Non-browser clients send neither header and are allowed.</summary>
    public static bool IsSameOrigin(HttpRequest request)
    {
        var site = request.Headers["Sec-Fetch-Site"].ToString();
        if (site.Length > 0)
        {
            return site is "same-origin" or "none";
        }

        var origin = request.Headers.Origin.ToString();
        return origin.Length == 0 || (Uri.TryCreate(origin, UriKind.Absolute, out var uri) && string.Equals(uri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase));
    }

    private bool ValidToken(string presented) =>
        presented.Length is > 0 and <= 512 && CryptographicOperations.FixedTimeEquals(Hash(presented), _tokenHash);

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private static string LoadOrCreate(string path, ILogger logger)
    {
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length >= 16)
            {
                return existing;
            }
        }

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        File.WriteAllText(path, token + "\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        logger.LogInformation("Generated a new admin token in {TokenFile}.", path);
        return token;
    }
}
