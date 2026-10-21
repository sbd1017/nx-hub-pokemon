using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace NxHub.Pokemon.Bots;

public sealed class HandOverException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record HandOverView(bool Enabled, bool Held, string? SwitchId, string? LastError);

/// <summary>
/// Asks NX Hub to hand the Switch over while the trade bot runs (an NX Hub "lease"). NX Hub then
/// releases its inputs, disconnects and refuses controller and macro commands until the lease is
/// released or expires. The lease is renewed while the bot runs.
/// </summary>
public sealed class SwitchHandOver : IAsyncDisposable
{
    public const string Holder = "Trade bot";

    private readonly HandOverOptions _options;
    private readonly ILogger<SwitchHandOver> _logger;
    private readonly HttpClient? _http;
    private CancellationTokenSource? _renewal;

    public SwitchHandOver(IOptions<PokemonOptions> options, ILogger<SwitchHandOver> logger, HttpMessageHandler? handler = null)
    {
        _options = options.Value.NxHub;
        _logger = logger;
        if (!_options.Enabled)
        {
            return;
        }

        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri(_options.BaseUrl!.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(15);
        var token = ResolveToken(_options);
        if (token is not null)
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    public bool Held { get; private set; }

    public string? LastError { get; private set; }

    public HandOverView View => new(_options.Enabled, Held, _options.SwitchId, LastError);

    public async Task AcquireAsync(CancellationToken cancellationToken)
    {
        if (_http is null)
        {
            return;
        }

        await RenewAsync(cancellationToken);
        _renewal?.Cancel();
        _renewal = new CancellationTokenSource();
        _ = RenewLoopAsync(_renewal.Token);
    }

    public async Task ReleaseAsync(CancellationToken cancellationToken)
    {
        if (_http is null)
        {
            return;
        }

        _renewal?.Cancel();
        _renewal = null;
        try
        {
            using var response = await _http.DeleteAsync(LeasePath, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("NX Hub answered {Status} when the Switch was handed back.", (int)response.StatusCode);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // The lease expires on its own; NX Hub takes the Switch back then.
            _logger.LogWarning(ex, "Could not hand the Switch back to NX Hub; the hand-over will expire by itself.");
        }

        Held = false;
    }

    public async ValueTask DisposeAsync()
    {
        await ReleaseAsync(CancellationToken.None);
        _http?.Dispose();
    }

    private string LeasePath => $"api/switches/{Uri.EscapeDataString(_options.SwitchId!)}/lease";

    private async Task RenewAsync(CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http!.PostAsJsonAsync(LeasePath, new { holder = Holder, ttlSeconds = _options.LeaseSeconds }, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            LastError = $"NX Hub couldn't be reached at {_options.BaseUrl}.";
            throw new HandOverException(LastError, ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                LastError = $"NX Hub wouldn't hand the Switch over: {await DetailAsync(response, cancellationToken)}";
                throw new HandOverException(LastError);
            }
        }

        Held = true;
        LastError = null;
    }

    private async Task RenewLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, _options.LeaseSeconds / 3)));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    await RenewAsync(cancellationToken);
                }
                catch (HandOverException ex)
                {
                    _logger.LogWarning("Renewing the Switch hand-over failed: {Error}", ex.Message);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task<string> DetailAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (problem.RootElement.TryGetProperty("detail", out var detail) && detail.GetString() is { Length: > 0 } text)
            {
                return text;
            }
        }
        catch (JsonException)
        {
        }

        return $"HTTP {(int)response.StatusCode}";
    }

    private static string? ResolveToken(HandOverOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.Token))
        {
            return options.Token.Trim();
        }

        return !string.IsNullOrWhiteSpace(options.TokenFile) && File.Exists(options.TokenFile)
            ? File.ReadAllText(options.TokenFile).Trim()
            : null;
    }
}
