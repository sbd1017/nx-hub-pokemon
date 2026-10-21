using System.Text.Json;
using Microsoft.Extensions.Options;
using NxHub.Pokemon.Requests;
using SysBot.Base;
using SysBot.Pokemon;

namespace NxHub.Pokemon.Bots;

public sealed record SetupView(bool Configured, ProgramMode Mode, string? Game, string? Host, int Port, bool Discord, string? Problem);

/// <summary>
/// Owns the SysBot.NET configuration (<c>config.json</c>, the same file the SysBot.NET console and
/// Windows apps use) and the trade bot built from it. The bots never start on their own unless
/// <see cref="PokemonOptions.StartBotsOnLaunch"/> is set.
/// </summary>
public sealed class TradeBotHost : IHostedService, IDisposable
{
    public const string ConfigFileName = "config.json";

    private readonly PokemonOptions _options;
    private readonly DataDirectory _data;
    private readonly RequestRegistry _registry;
    private readonly BotLog _log;
    private readonly SwitchHandOver _handOver;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<TradeBotHost> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public TradeBotHost(
        IOptions<PokemonOptions> options,
        DataDirectory data,
        RequestRegistry registry,
        BotLog log,
        SwitchHandOver handOver,
        IHostApplicationLifetime lifetime,
        ILogger<TradeBotHost> logger)
    {
        _options = options.Value;
        _data = data;
        _registry = registry;
        _log = log;
        _handOver = handOver;
        _lifetime = lifetime;
        _logger = logger;
    }

    public string ConfigPath => Path.Combine(_data.Path, ConfigFileName);

    public ProgramConfig? Config { get; private set; }

    public ITradeBot? Bot { get; private set; }

    /// <summary>Why there is no bot (no config yet, or it could not be loaded).</summary>
    public string? Problem { get; private set; }

    public bool RestartPending { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        LogUtil.Forwarders.Add(_log);
        Load();
        if (Bot is not null && _options.StartBotsOnLaunch)
        {
            await StartBotAsync(cancellationToken);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Bot is not null && !_disposed)
        {
            await StopBotAsync(cancellationToken);
        }
    }

    public async Task StartBotAsync(CancellationToken cancellationToken)
    {
        var bot = Bot ?? throw new InvalidOperationException(Problem ?? "The trade bot is not set up.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (bot.IsRunning)
            {
                return;
            }

            // Ask NX Hub to let go of the Switch first, so nothing else presses buttons mid-trade.
            await _handOver.AcquireAsync(cancellationToken);
            bot.Start();
            _logger.LogInformation("Trade bot started ({Mode}).", bot.Mode);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopBotAsync(CancellationToken cancellationToken)
    {
        var bot = Bot;
        if (bot is null)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            bot.Stop();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            while (bot.IsRunning && !timeout.IsCancellationRequested)
            {
                await Task.Delay(200, CancellationToken.None);
            }

            await _handOver.ReleaseAsync(CancellationToken.None);
            _logger.LogInformation("Trade bot stopped.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _gate.Dispose();
    }

    public SetupView Setup()
    {
        var config = Config;
        var console = config?.Bots.FirstOrDefault()?.Connection;
        var game = config is null ? null : TradeBotFactory.Games.FirstOrDefault(g => g.Mode == config.Mode)?.Name;
        return new SetupView(
            Bot is not null,
            config?.Mode ?? ProgramMode.None,
            game,
            console?.IP,
            console?.Port ?? 6000,
            !string.IsNullOrWhiteSpace(config?.Hub.Discord.Token),
            Problem);
    }

    /// <summary>Saves the game and Switch address, keeping every other setting. Takes effect after a restart.</summary>
    public void SaveSetup(ProgramMode mode, string host, int port)
    {
        if (TradeBotFactory.Games.All(g => g.Mode != mode))
        {
            throw new ArgumentException("Choose one of the supported games.", nameof(mode));
        }

        var config = ReadConfig() ?? new ProgramConfig();
        var routine = config.Bots.FirstOrDefault()?.InitialRoutine is { } existing && existing != PokeRoutineType.Idle
            ? existing
            : PokeRoutineType.FlexTrade;
        config.Mode = mode;
        config.Bots =
        [
            new PokeBotState
            {
                Connection = new SwitchConnectionConfig { IP = host, Port = port, Protocol = SwitchProtocol.WiFi },
                InitialRoutine = routine,
            },
        ];
        WriteConfig(config);
    }

    /// <summary>Replaces config.json with one exported from another SysBot.NET install. Takes effect after a restart.</summary>
    public void ImportConfig(string json)
    {
        ProgramConfig? config;
        try
        {
            config = JsonSerializer.Deserialize(json, ProgramConfigContext.Default.ProgramConfig);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"That isn't a SysBot.NET config.json ({ex.Message}).", nameof(json), ex);
        }

        if (config is null || TradeBotFactory.Games.All(g => g.Mode != config.Mode))
        {
            throw new ArgumentException("That config.json doesn't name a supported game (Mode).", nameof(json));
        }

        if (config.Bots.Length == 0 || config.Bots.Any(b => !b.IsValid()))
        {
            throw new ArgumentException("That config.json needs at least one Switch with a valid IP address.", nameof(json));
        }

        WriteConfig(config);
    }

    /// <summary>
    /// SysBot.NET keeps per-game state in static fields, so a new game or console means a fresh
    /// process. systemd (Restart=always) brings the service straight back.
    /// </summary>
    public void RequestRestart()
    {
        RestartPending = true;
        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            _lifetime.StopApplication();
        });
    }

    private void Load()
    {
        var config = ReadConfig();
        if (config is null)
        {
            Problem = "Not set up yet: choose the game and your Switch's address.";
            return;
        }

        Config = config;
        if (config.Bots.Length == 0)
        {
            Problem = "config.json has no Switch in it.";
            return;
        }

        // Folders from another computer (for example a Windows path in an imported config) won't exist here.
        var folder = config.Hub.Folder;
        if (!Directory.Exists(folder.DistributeFolder) || (folder.Dump && !Directory.Exists(folder.DumpFolder)))
        {
            folder.CreateDefaults(_data.Path);
        }

        try
        {
            Bot = TradeBotFactory.Create(config, _registry);
            Problem = null;
        }
        catch (Exception ex)
        {
            Problem = $"The trade bot couldn't be created: {ex.Message}";
            _logger.LogError(ex, "Could not create the trade bot from {ConfigPath}.", ConfigPath);
        }
    }

    private ProgramConfig? ReadConfig()
    {
        if (!File.Exists(ConfigPath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(ConfigPath), ProgramConfigContext.Default.ProgramConfig);
        }
        catch (JsonException ex)
        {
            Problem = $"config.json couldn't be read: {ex.Message}";
            return null;
        }
    }

    private void WriteConfig(ProgramConfig config)
    {
        var temp = ConfigPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(config, ProgramConfigContext.Default.ProgramConfig));
        if (!OperatingSystem.IsWindows())
        {
            // It can hold a Discord token.
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temp, ConfigPath, overwrite: true);
        _logger.LogInformation("Saved {ConfigPath}.", ConfigPath);
    }
}
