using System.Text.RegularExpressions;
using PKHeX.Core;
using SysBot.Pokemon;

namespace NxHub.Pokemon.Requests;

/// <summary>
/// Receives SysBot.NET's progress callbacks for one request and records them in the
/// <see cref="RequestRegistry"/>, where the web page picks them up. The web counterpart of the
/// Discord bot's direct messages.
/// </summary>
public sealed partial class WebTradeNotifier<T>(RequestRegistry registry, string requestId, RequestKind kind) : IPokeTradeNotifier<T>
    where T : PKM, new()
{
    private Action<PokeRoutineExecutor<T>>? _onFinish;

    public string RequestId => requestId;

    public Action<PokeRoutineExecutor<T>>? OnFinish
    {
        set => _onFinish = value;
    }

    public Task TradeInitialize(PokeRoutineExecutor<T> routine, PokeTradeDetail<T> info)
    {
        registry.SetStatus(requestId, RequestStatus.Starting, $"Your turn is next. Get ready: your link code is {RequestRegistry.FormatCode(info.Code)}.");
        return Task.CompletedTask;
    }

    public Task TradeSearching(PokeRoutineExecutor<T> routine, PokeTradeDetail<T> info)
    {
        var what = kind == RequestKind.TrainerInfo
            ? " When the trade opens, show any Pokémon you caught yourself. Nothing will be traded."
            : "";
        registry.SetStatus(
            requestId,
            RequestStatus.Searching,
            $"Searching now. Enter link code {RequestRegistry.FormatCode(info.Code)} in the game and look for {routine.InGameName}.{what}");
        return Task.CompletedTask;
    }

    public Task TradeCanceled(PokeRoutineExecutor<T> routine, PokeTradeDetail<T> info, PokeTradeResult msg)
    {
        _onFinish?.Invoke(routine);

        // A trainer-info request has done its job once it has read a trainer, however the trade ended.
        var gotTrainer = kind == RequestKind.TrainerInfo && registry.Get(requestId)?.Trainers.Count > 0;
        if (gotTrainer)
        {
            registry.Finish(requestId, RequestStatus.Finished, "Trainer details read. Nothing was traded.");
        }
        else
        {
            registry.Finish(requestId, RequestStatus.Canceled, Describe(msg));
        }

        return Task.CompletedTask;
    }

    public Task TradeFinished(PokeRoutineExecutor<T> routine, PokeTradeDetail<T> info, T result)
    {
        _onFinish?.Invoke(routine);
        if (kind == RequestKind.TrainerInfo)
        {
            registry.Finish(requestId, RequestStatus.Finished, "Trainer details read. Nothing was traded.");
            return Task.CompletedTask;
        }

        var sent = info.TradeData.Species == 0 ? "the Pokémon" : SpeciesName(info.TradeData.Species);
        string? received = null;
        if (result.Species != 0)
        {
            received = SpeciesName(result.Species);
            registry.AddTrainer(requestId, Read(result));
        }

        registry.Finish(requestId, RequestStatus.Finished, $"Trade finished. Enjoy your {sent}!", received);
        return Task.CompletedTask;
    }

    public Task SendNotification(PokeRoutineExecutor<T> routine, PokeTradeDetail<T> info, string message)
    {
        var partner = PartnerPattern().Match(message);
        if (partner.Success)
        {
            registry.SetPartner(requestId, partner.Groups["name"].Value.Trim());
        }

        registry.AddMessage(requestId, Clean(message));
        return Task.CompletedTask;
    }

    public Task SendNotification(PokeRoutineExecutor<T> routine, PokeTradeDetail<T> info, PokeTradeSummary trade)
    {
        var text = trade.Summary;
        if (trade.Details.Count > 0)
        {
            text += ": " + string.Join(", ", trade.Details.Select(d => $"{d.Heading}: {d.Detail}"));
        }

        registry.AddMessage(requestId, Clean(text));
        return Task.CompletedTask;
    }

    /// <summary>Called for each Pokémon the partner shows in a dump (trainer-info) or clone trade.</summary>
    public Task SendNotification(PokeRoutineExecutor<T> routine, PokeTradeDetail<T> info, T result, string message)
    {
        if (result.Species == 0)
        {
            return Task.CompletedTask;
        }

        var trainer = Read(result);
        registry.AddTrainer(requestId, trainer);
        registry.AddMessage(requestId, $"Read {trainer.Pokemon}: trainer {trainer.Name}, TID {trainer.Tid}, SID {trainer.Sid}.");
        return Task.CompletedTask;
    }

    /// <summary>Trainer details carried by a Pokémon (its original trainer).</summary>
    public static TrainerDetails Read(PKM pk)
    {
        var format = pk.GetTrainerIDFormat();
        return new TrainerDetails(
            Name: pk.OriginalTrainerName,
            Gender: pk.OriginalTrainerGender == 0 ? "Male" : "Female",
            Tid: pk.GetDisplayTID().ToString(format.GetTrainerIDFormatStringTID(), System.Globalization.CultureInfo.InvariantCulture),
            Sid: pk.GetDisplaySID().ToString(format.GetTrainerIDFormatStringSID(), System.Globalization.CultureInfo.InvariantCulture),
            Game: GameName(pk.Version),
            Language: ((LanguageID)pk.Language).ToString(),
            Pokemon: SpeciesName(pk.Species),
            Shiny: pk.IsShiny,
            Legal: new LegalityAnalysis(pk).Valid);
    }

    public static string SpeciesName(ushort species)
    {
        var names = GameInfo.GetStrings("en").Species;
        return species < names.Count ? names[species] : $"#{species}";
    }

    private static string GameName(GameVersion version)
    {
        var names = GameInfo.GetStrings("en").gamelist;
        var index = (int)version;
        return index > 0 && index < names.Length && !string.IsNullOrWhiteSpace(names[index]) ? names[index] : version.ToString();
    }

    public static string Describe(PokeTradeResult result) => result switch
    {
        PokeTradeResult.NoTrainerFound => "Nobody joined with the link code in time.",
        PokeTradeResult.TrainerTooSlow => "The trade timed out waiting for you.",
        PokeTradeResult.TrainerLeft => "You left the trade.",
        PokeTradeResult.TrainerOfferCanceledQuick => "The offer was cancelled too quickly.",
        PokeTradeResult.TrainerRequestBad => "The trade was refused (the partner's offer wasn't allowed).",
        PokeTradeResult.IllegalTrade => "The trade was refused as illegal.",
        PokeTradeResult.SuspiciousActivity => "The trade was refused (suspicious activity).",
        PokeTradeResult.TradeEvolveNotAllowed => "Pokémon that evolve when traded aren't allowed.",
        PokeTradeResult.RoutineCancel => "The bot was stopped.",
        PokeTradeResult.ExceptionConnection => "The bot lost its connection to the Switch.",
        _ => $"The bot couldn't finish this trade ({result}).",
    };

    // SysBot.NET formats its messages for Discord; drop the markdown.
    private static string Clean(string message) => message.Replace("```", "", StringComparison.Ordinal).Replace("**", "", StringComparison.Ordinal).Trim();

    [GeneratedRegex(@"Found Link Trade partner: (?<name>.+?)\.(\s|$)")]
    private static partial Regex PartnerPattern();
}
