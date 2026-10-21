namespace NxHub.Pokemon;

/// <summary>Service settings (section <c>NxHubPokemon</c>). The trade bot itself is configured by SysBot.NET's <c>config.json</c>.</summary>
public sealed class PokemonOptions
{
    public const string SectionName = "NxHubPokemon";

    /// <summary>Holds config.json, the admin token, dumps and the request history. Defaults to systemd's STATE_DIRECTORY, else ./data.</summary>
    public string? DataDirectory { get; set; }

    /// <summary>Sub-path when served behind a reverse proxy, for example <c>/nxhub/trade</c>.</summary>
    public string? PathBase { get; set; }

    /// <summary>
    /// Start the bots when the service starts. Off by default: starting a trade bot presses buttons
    /// and goes online, so it should be a deliberate choice.
    /// </summary>
    public bool StartBotsOnLaunch { get; set; }

    /// <summary>
    /// Where people using the bot can get its source code. AGPL-3.0 section 13 requires offering it
    /// to everyone who uses the bot over a network; change this if you run a modified copy.
    /// </summary>
    public string SourceUrl { get; set; } = "https://github.com/sbd1017/nx-hub-pokemon";

    public AuthOptions Auth { get; set; } = new();

    public HandOverOptions NxHub { get; set; } = new();

    public string ResolveDataDirectory(string contentRoot)
    {
        var configured = DataDirectory;
        if (string.IsNullOrWhiteSpace(configured))
        {
            var state = Environment.GetEnvironmentVariable("STATE_DIRECTORY");
            configured = string.IsNullOrWhiteSpace(state) ? Path.Combine(contentRoot, "data") : state.Split(':')[0];
        }

        return Path.GetFullPath(configured, contentRoot);
    }
}

public enum AuthMode
{
    Token,
    Disabled,
}

public sealed class AuthOptions
{
    public AuthMode Mode { get; set; } = AuthMode.Token;

    /// <summary>Fixed admin token. When empty, one is generated into <c>&lt;data dir&gt;/admin-token</c>.</summary>
    public string? Token { get; set; }

    /// <summary>
    /// Header a reverse proxy on this machine sets to the signed-in user's name (for example
    /// <c>X-Site-User</c>). Trusted from loopback only. Empty disables proxy sign-in.
    /// </summary>
    public string? TrustedProxyUserHeader { get; set; }

    /// <summary>
    /// Proxy users who may start and stop the bot, change setup and see everyone's requests.
    /// Empty means every proxy user is an owner (a single-person site login).
    /// </summary>
    public List<string> Owners { get; set; } = [];
}

/// <summary>
/// How to ask NX Hub to hand the Switch over while the trade bot runs, so nothing else presses
/// buttons in the middle of a trade. Leave <see cref="BaseUrl"/> empty if NX Hub is not used.
/// </summary>
public sealed class HandOverOptions
{
    /// <summary>NX Hub's address as seen from this machine, for example <c>http://127.0.0.1:8810</c>.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>NX Hub admin token. Alternatively <see cref="TokenFile"/>.</summary>
    public string? Token { get; set; }

    /// <summary>File holding the NX Hub admin token, for example <c>/var/lib/nxhub/admin-token</c>.</summary>
    public string? TokenFile { get; set; }

    /// <summary>NX Hub's id for the Switch the trade bot uses (<c>sw_…</c>).</summary>
    public string? SwitchId { get; set; }

    /// <summary>The hand-over expires this long after the last renewal, so a crashed bot can't hold the Switch forever.</summary>
    public int LeaseSeconds { get; set; } = 60;

    public bool Enabled => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(SwitchId);
}

/// <summary>Resolved data directory.</summary>
public sealed record DataDirectory(string Path);
