using NxHub.Pokemon.Requests;
using PKHeX.Core;
using SysBot.Pokemon;

namespace NxHub.Pokemon.Tests;

public sealed class NotifierTests
{
    private static PK9 Shown()
    {
        var pk = new PK9
        {
            Species = (ushort)Species.Sprigatito,
            OriginalTrainerName = "Sam",
            OriginalTrainerGender = 1,
            Version = GameVersion.SL,
            Language = (int)LanguageID.English,
        };
        pk.SetDisplayID(123_456, 1234);
        return pk;
    }

    private static PokeTradeDetail<PK9> Detail(WebTradeNotifier<PK9> notifier, PokeTradeType type) => new()
    {
        Code = 8180,
        TradeData = new PK9(),
        Trainer = new PokeTradeTrainerInfo("sam", 1),
        Notifier = notifier,
        Type = type,
    };

    [Fact]
    public async Task Trainer_info_is_read_from_the_shown_pokemon_and_nothing_is_traded()
    {
        var registry = new RequestRegistry(TimeProvider.System, null);
        var id = registry.Create(RequestKind.TrainerInfo, "sam", 1, 8180, null).Id;
        var notifier = new WebTradeNotifier<PK9>(registry, id, RequestKind.TrainerInfo);
        var detail = Detail(notifier, PokeTradeType.Dump);

        await notifier.SendNotification(null!, detail, "Found Link Trade partner: Sam. Waiting for a Pokémon...");
        await notifier.SendNotification(null!, detail, Shown(), "```Legality report```");
        // The dump routine ends with a cancel when the partner leaves early; the details are already read.
        await notifier.TradeCanceled(null!, detail, PokeTradeResult.TrainerLeft);

        var request = registry.Get(id)!;
        Assert.Equal(RequestStatus.Finished, request.Status);
        Assert.Equal("Sam", request.Partner);
        var trainer = Assert.Single(request.Trainers);
        Assert.Equal("Sam", trainer.Name);
        Assert.Equal("Female", trainer.Gender);
        Assert.Equal("123456", trainer.Tid);
        Assert.Equal("1234", trainer.Sid);
        Assert.Equal("Scarlet", trainer.Game);
        Assert.Equal("English", trainer.Language);
        Assert.Equal("Sprigatito", trainer.Pokemon);
        Assert.DoesNotContain(request.Messages, m => m.Text.Contains("```", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_trainer_info_request_with_nothing_shown_is_cancelled_with_a_reason()
    {
        var registry = new RequestRegistry(TimeProvider.System, null);
        var id = registry.Create(RequestKind.TrainerInfo, "sam", 1, 8180, null).Id;
        var notifier = new WebTradeNotifier<PK9>(registry, id, RequestKind.TrainerInfo);

        await notifier.TradeCanceled(null!, Detail(notifier, PokeTradeType.Dump), PokeTradeResult.NoTrainerFound);

        var request = registry.Get(id)!;
        Assert.Equal(RequestStatus.Canceled, request.Status);
        Assert.Equal("Nobody joined with the link code in time.", request.Outcome);
    }

    [Fact]
    public async Task Finishing_a_trade_runs_the_queue_callback_and_records_what_was_received()
    {
        var registry = new RequestRegistry(TimeProvider.System, null);
        var id = registry.Create(RequestKind.Trade, "sam", 1, 8180, "Pikachu").Id;
        var notifier = new WebTradeNotifier<PK9>(registry, id, RequestKind.Trade);
        var finished = false;
        notifier.OnFinish = _ => finished = true;
        var detail = Detail(notifier, PokeTradeType.Specific);
        detail.TradeData.Species = (ushort)Species.Pikachu;

        await notifier.TradeFinished(null!, detail, Shown());

        var request = registry.Get(id)!;
        Assert.True(finished);
        Assert.Equal(RequestStatus.Finished, request.Status);
        Assert.Equal("Sprigatito", request.Received);
        Assert.Equal("Trade finished. Enjoy your Pikachu!", request.Outcome);
    }
}
