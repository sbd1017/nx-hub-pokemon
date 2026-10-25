using NxHub.Pokemon.Auth;
using NxHub.Pokemon.Requests;
using PKHeX.Core;
using SysBot.Base;
using SysBot.Pokemon;
using SysBot.Pokemon.Discord;

namespace NxHub.Pokemon.Bots;

public sealed record BotView(string Name, string Console, string Routine, bool Running, bool Paused);

public sealed record QueueEntryView(int Position, string RequestId, string User, string Type, string? Pokemon, bool Processing);

/// <summary>The outcome of asking for a trade: the new request, or why it was refused.</summary>
public sealed record RequestOutcome(TradeRequestView? Request, string? Error, string? ErrorCode)
{
    public static RequestOutcome Ok(TradeRequestView request) => new(request, null, null);

    public static RequestOutcome Refused(string code, string error) => new(null, error, code);
}

/// <summary>The game-independent face of a SysBot.NET trade hub for one game.</summary>
public interface ITradeBot
{
    ProgramMode Mode { get; }

    bool IsRunning { get; }

    bool CanQueue { get; }

    IReadOnlyList<BotView> Bots { get; }

    IReadOnlyList<QueueEntryView> Queue { get; }

    void Start();

    void Stop();

    /// <summary>A Showdown set, or a Pokémon file as Base64 text.</summary>
    RequestOutcome RequestTrade(WebUser user, string content, int? code);

    RequestOutcome RequestTradeFile(WebUser user, byte[] file, int? code);

    RequestOutcome RequestTrainerInfo(WebUser user, int? code);

    /// <summary>Removes a queued request. A request already being traded can't be removed.</summary>
    bool Cancel(string requestId);
}

/// <summary>
/// Runs SysBot.NET's trade engine for one game (<typeparamref name="T"/> is that game's Pokémon format)
/// and puts web requests into its queue the same way the Discord commands do.
/// </summary>
internal sealed class TradeBot<T> : ITradeBot
    where T : PKM, new()
{
    private readonly WebBotRunner<T> _runner;
    private readonly RequestRegistry _registry;

    public TradeBot(ProgramConfig config, BotFactory<T> factory, RequestRegistry registry)
    {
        Mode = config.Mode;
        _registry = registry;
        _runner = new WebBotRunner<T>(config.Hub, factory);
        foreach (var bot in config.Bots)
        {
            bot.Initialize();
            _runner.Add(_runner.CreateBotFromConfig(bot));
        }

        // Loads legality data, the Discord bot (if configured) and the queue monitor. Starts no bots.
        _runner.InitializeStart();
    }

    public ProgramMode Mode { get; }

    private PokeTradeHub<T> Hub => _runner.Hub;

    public bool IsRunning => _runner.IsRunning;

    public bool CanQueue => Hub.Config.Queues.CanQueue && Hub.Queues.Info.Count < Hub.Config.Queues.MaxQueueCount;

    public IReadOnlyList<BotView> Bots =>
    [
        .. _runner.Bots.Select(b => new BotView(
            ((PokeRoutineExecutorBase)b.Bot).InGameName,
            b.Bot.Config.Connection.ToString(),
            b.Bot.Config.CurrentRoutineType.ToString(),
            b.IsRunning,
            b.IsPaused)),
    ];

    public IReadOnlyList<QueueEntryView> Queue
    {
        get
        {
            var entries = Hub.Queues.Info.GetIsUserQueued(_ => true);
            return
            [
                .. entries.Select((e, i) => new QueueEntryView(
                    i + 1,
                    e.Trade.Notifier is WebTradeNotifier<T> web ? web.RequestId : "",
                    e.Username,
                    e.Type == PokeRoutineType.Dump ? "Trainer info" : "Trade",
                    e.Trade.TradeData.Species == 0 ? null : WebTradeNotifier<T>.SpeciesName(e.Trade.TradeData.Species),
                    e.Trade.IsProcessing)),
            ];
        }
    }

    public void Start() => _runner.StartAll();

    public void Stop() => _runner.StopAll();

    public RequestOutcome RequestTrade(WebUser user, string content, int? code)
    {
        content = content.Trim().Trim('`').Trim();
        if (content.Length == 0)
        {
            return RequestOutcome.Refused("invalid_request", "Paste a Showdown set or a Pokémon file.");
        }

        if (!ShowdownParsing.TryParseAnyLanguage(content, out var set))
        {
            try
            {
                return RequestTradeFile(user, Convert.FromBase64String(content), code);
            }
            catch (FormatException)
            {
                return RequestOutcome.Refused("invalid_set", "That isn't a Showdown set or a Pokémon file.");
            }
        }

        if (set.InvalidLines.Count != 0 || set.Species == 0)
        {
            var problems = set.Species == 0 ? "The species wasn't recognised. Check the spelling." : "Some lines weren't understood.";
            if (set.InvalidLines.Count != 0)
            {
                var localization = BattleTemplateParseErrorLocalization.Get();
                problems += " " + string.Join(" ", set.InvalidLines.Select(l => l.Humanize(localization)));
            }

            return RequestOutcome.Refused("invalid_set", problems);
        }

        var template = AutoLegalityWrapper.GetTemplate(set);
        var trainer = AutoLegalityWrapper.GetTrainerInfo<T>();
        var created = trainer.GetLegal(template, out var result);
        var species = WebTradeNotifier<T>.SpeciesName(template.Species);
        if (EntityConverter.ConvertToType(created, typeof(T), out _) is not T pk || !new LegalityAnalysis(pk).Valid)
        {
            var reason = result switch
            {
                "Timeout" => $"That {species} set took too long to make.",
                "VersionMismatch" => "PKHeX and Auto-Legality Mod versions don't match. The bot needs updating.",
                _ => $"A legal {species} couldn't be made from that set.",
            };
            if (result == "Failed")
            {
                reason += " " + AutoLegalityWrapper.GetLegalizationHint(template, trainer, created);
            }

            return RequestOutcome.Refused("not_legal", reason);
        }

        pk.ResetPartyStats();
        return Enqueue(user, pk, code, PokeRoutineType.LinkTrade, PokeTradeType.Specific);
    }

    public RequestOutcome RequestTradeFile(WebUser user, byte[] file, int? code)
    {
        var entity = EntityFormat.GetFromBytes(file, new T().Context);
        if (entity is null || EntityConverter.ConvertToType(entity, typeof(T), out _) is not T pk)
        {
            return RequestOutcome.Refused("invalid_file", $"That file isn't a Pokémon this game ({Mode}) can receive.");
        }

        return Enqueue(user, pk, code, PokeRoutineType.LinkTrade, PokeTradeType.Specific);
    }

    public RequestOutcome RequestTrainerInfo(WebUser user, int? code) =>
        Enqueue(user, new T(), code, PokeRoutineType.Dump, PokeTradeType.Dump);

    public bool Cancel(string requestId)
    {
        var info = Hub.Queues.Info;
        var match = info.GetIsUserQueued(e => e.Trade.Notifier is WebTradeNotifier<T> web && web.RequestId == requestId);
        if (match.Count == 0 || info.ClearTrade(match, Hub) == 0)
        {
            return false;
        }

        _registry.Finish(requestId, RequestStatus.Canceled, "You cancelled this request.");
        return true;
    }

    private RequestOutcome Enqueue(WebUser user, T pk, int? code, PokeRoutineType routine, PokeTradeType type)
    {
        // Same checks as the Discord trade command.
        if (type == PokeTradeType.Specific)
        {
            var la = new LegalityAnalysis(pk);
            if (!la.Valid)
            {
                return RequestOutcome.Refused("not_legal", "That Pokémon isn't legal, so it can't be traded.");
            }

            var encounter = la.EncounterOriginal;
            if (!pk.CanBeTraded(encounter))
            {
                return RequestOutcome.Refused("blocked", "That Pokémon can't be traded (its name, held item or species is blocked).");
            }

            var trade = Hub.Config.Trade;
            if (trade.DisallowNonNatives && (encounter.Context != pk.Context || pk.GO))
            {
                return RequestOutcome.Refused("not_native", "That Pokémon isn't native to this game, so it can't be traded.");
            }

            if (trade.DisallowTracked && pk is IHomeTrack { HasTracker: true })
            {
                return RequestOutcome.Refused("home_tracked", "That Pokémon has a HOME tracker, so it can't be traded.");
            }
        }

        if (!Hub.Config.Queues.CanQueue)
        {
            return RequestOutcome.Refused("queue_closed", "The queue is closed right now.");
        }

        if (code is { } chosen && (chosen < 0 || chosen > 99_999_999))
        {
            return RequestOutcome.Refused("invalid_code", "A link code is 8 digits.");
        }

        var tradeCode = code ?? Hub.Config.Trade.GetRandomTradeCode();
        var kind = type == PokeTradeType.Dump ? RequestKind.TrainerInfo : RequestKind.Trade;
        var species = pk.Species == 0 ? null : WebTradeNotifier<T>.SpeciesName(pk.Species);
        var request = _registry.Create(kind, user.Name, user.Id, tradeCode, species);

        var trainer = new PokeTradeTrainerInfo(user.Name, user.Id);
        var detail = new PokeTradeDetail<T>
        {
            Type = type,
            Code = tradeCode,
            TradeData = pk,
            Trainer = trainer,
            Notifier = new WebTradeNotifier<T>(_registry, request.Id, kind),
        };
        var entry = new TradeEntry<T>(detail, user.Id, routine, user.Name);

        // Owners may queue several requests at once; everyone else one at a time.
        var added = Hub.Queues.Info.AddToTradeQueue(entry, user.Id, sudo: user.IsOwner);
        if (added == QueueResultAdd.AlreadyInQueue)
        {
            _registry.Finish(request.Id, RequestStatus.Canceled, "You already have a request in the queue.");
            return RequestOutcome.Refused("already_queued", "You already have a request in the queue.");
        }

        detail.IsReady = true;
        _registry.AddMessage(request.Id, $"Added to the queue at position {PositionOf(entry)}. Your link code is {RequestRegistry.FormatCode(tradeCode)}.");
        return RequestOutcome.Ok(_registry.Get(request.Id)!);
    }

    // The entry's place in the whole queue, counted the same way as Queue. SysBot.NET's
    // CheckPosition finds a user's first entry only and counts each routine type separately.
    private int PositionOf(TradeEntry<T> entry)
    {
        var entries = Hub.Queues.Info.GetIsUserQueued(_ => true);
        for (var i = 0; i < entries.Count; i++)
        {
            if (ReferenceEquals(entries[i], entry))
            {
                return i + 1;
            }
        }

        return entries.Count;
    }
}

/// <summary>SysBot.NET's bot runner with the Discord integration and nothing else.</summary>
internal sealed class WebBotRunner<T>(PokeTradeHubConfig config, BotFactory<T> factory) : PokeBotRunner<T>(config, factory)
    where T : PKM, new()
{
    protected override void AddIntegrations()
    {
        var token = Hub.Config.Discord.Token;
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        var discord = new SysCord<T>(this);
        Task.Run(() => discord.MainAsync(token, CancellationToken.None), CancellationToken.None);
    }
}

/// <summary>Creates the trade bot for the game named in <see cref="ProgramConfig.Mode"/>.</summary>
public static class TradeBotFactory
{
    public static ITradeBot Create(ProgramConfig config, RequestRegistry registry) => config.Mode switch
    {
        ProgramMode.SWSH => new TradeBot<PK8>(config, new BotFactory8SWSH(), registry),
        ProgramMode.BDSP => new TradeBot<PB8>(config, new BotFactory8BS(), registry),
        ProgramMode.LA => new TradeBot<PA8>(config, new BotFactory8LA(), registry),
        ProgramMode.SV => new TradeBot<PK9>(config, new BotFactory9SV(), registry),
        ProgramMode.LZA => new TradeBot<PA9>(config, new BotFactory9LZA(), registry),
        _ => throw new ArgumentOutOfRangeException(nameof(config), config.Mode, "Choose a game."),
    };

    public static IReadOnlyList<GameChoice> Games { get; } =
    [
        new(ProgramMode.SV, "Pokémon Scarlet and Violet"),
        new(ProgramMode.LZA, "Pokémon Legends: Z-A"),
        new(ProgramMode.SWSH, "Pokémon Sword and Shield"),
        new(ProgramMode.BDSP, "Pokémon Brilliant Diamond and Shining Pearl"),
        new(ProgramMode.LA, "Pokémon Legends: Arceus"),
    ];
}

public sealed record GameChoice(ProgramMode Mode, string Name);

/// <summary>Passes the bot's log to this service's log and keeps the most recent lines for the web page.</summary>
public sealed class BotLog : ILogForwarder
{
    private const int Capacity = 300;
    private readonly Lock _sync = new();
    private readonly Queue<LogLine> _lines = new();
    private readonly TimeProvider _time;
    private readonly ILogger<BotLog> _logger;

    public BotLog(TimeProvider time, ILogger<BotLog> logger)
    {
        _time = time;
        _logger = logger;
    }

    public void Forward(string message, string identity)
    {
        _logger.LogInformation("{Source}: {Message}", identity, message);
        lock (_sync)
        {
            _lines.Enqueue(new LogLine(_time.GetUtcNow(), identity, message));
            while (_lines.Count > Capacity)
            {
                _lines.Dequeue();
            }
        }
    }

    public IReadOnlyList<LogLine> Recent(int count)
    {
        lock (_sync)
        {
            return [.. _lines.Skip(Math.Max(0, _lines.Count - count))];
        }
    }
}

public sealed record LogLine(DateTimeOffset At, string Source, string Message);
