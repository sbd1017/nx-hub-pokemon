using NxHub.Pokemon.Auth;
using NxHub.Pokemon.Bots;
using NxHub.Pokemon.Requests;
using PKHeX.Core;
using SysBot.Base;
using SysBot.Pokemon;

namespace NxHub.Pokemon.Tests;

/// <summary>
/// Drives SysBot.NET's real queue and legality code for Scarlet/Violet. No bot is started, so
/// nothing connects to a Switch.
/// </summary>
public sealed class TradeBotTests
{
    private static readonly WebUser Friend = new("alex", WebUser.IdFor("alex"), false);

    private static (ITradeBot Bot, RequestRegistry Registry) Create()
    {
        var config = new ProgramConfig
        {
            Mode = ProgramMode.SV,
            Bots =
            [
                new PokeBotState
                {
                    Connection = new SwitchConnectionConfig { IP = "192.0.2.10", Port = 6000, Protocol = SwitchProtocol.WiFi },
                    InitialRoutine = PokeRoutineType.FlexTrade,
                },
            ],
        };
        var registry = new RequestRegistry(TimeProvider.System, null);
        return (TradeBotFactory.Create(config, registry), registry);
    }

    [Fact]
    public void A_showdown_set_becomes_a_legal_queued_trade()
    {
        var (bot, registry) = Create();

        var outcome = bot.RequestTrade(WebUser.Owner, "Pikachu @ Light Ball\nAbility: Static\nLevel: 50\n- Thunderbolt", 1234_5678);

        Assert.Null(outcome.Error);
        var request = outcome.Request!;
        Assert.Equal(RequestKind.Trade, request.Kind);
        Assert.Equal("Pikachu", request.Pokemon);
        Assert.Equal("1234 5678", request.Code);
        var queued = Assert.Single(bot.Queue);
        Assert.Equal(request.Id, queued.RequestId);
        Assert.Equal("Trade", queued.Type);
        Assert.False(bot.IsRunning);
        Assert.Contains("position 1", registry.Get(request.Id)!.Messages[^1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_set_that_cannot_be_read_is_refused()
    {
        var (bot, _) = Create();

        var outcome = bot.RequestTrade(WebUser.Owner, "Pikachoo\n- Thunderbolt", null);

        Assert.Null(outcome.Request);
        Assert.Equal("invalid_set", outcome.ErrorCode);
        Assert.Empty(bot.Queue);
    }

    [Fact]
    public void Trainer_info_is_queued_as_a_dump_with_nothing_to_send()
    {
        var (bot, _) = Create();

        var outcome = bot.RequestTrainerInfo(WebUser.Owner, null);

        Assert.Equal(RequestKind.TrainerInfo, outcome.Request!.Kind);
        Assert.Null(outcome.Request.Pokemon);
        Assert.Equal("Trainer info", Assert.Single(bot.Queue).Type);
    }

    [Fact]
    public void Friends_get_one_request_at_a_time_and_owners_more()
    {
        var (bot, registry) = Create();

        Assert.NotNull(bot.RequestTrainerInfo(Friend, null).Request);
        var second = bot.RequestTrainerInfo(Friend, null);
        Assert.Equal("already_queued", second.ErrorCode);
        Assert.Single(bot.Queue);
        Assert.Equal(RequestStatus.Canceled, registry.List(Friend.Id)[0].Status);

        Assert.NotNull(bot.RequestTrainerInfo(WebUser.Owner, null).Request);
        Assert.NotNull(bot.RequestTrainerInfo(WebUser.Owner, null).Request);
        Assert.Equal(3, bot.Queue.Count);
    }

    [Fact]
    public void A_queued_request_can_be_cancelled()
    {
        var (bot, registry) = Create();
        var id = bot.RequestTrainerInfo(WebUser.Owner, null).Request!.Id;

        Assert.True(bot.Cancel(id));

        Assert.Empty(bot.Queue);
        Assert.Equal(RequestStatus.Canceled, registry.Get(id)!.Status);
        Assert.False(bot.Cancel(id));
    }

    [Fact]
    public void A_file_for_another_game_is_refused()
    {
        var (bot, _) = Create();

        var outcome = bot.RequestTradeFile(WebUser.Owner, [1, 2, 3], null);

        Assert.Equal("invalid_file", outcome.ErrorCode);
    }

    [Fact]
    public void A_legal_pokemon_file_is_queued()
    {
        var (bot, _) = Create();
        var trainer = AutoLegalityWrapper.GetTrainerInfo<PK9>();
        var set = new ShowdownSet("Eevee\nLevel: 20");
        var pk = (PK9)trainer.GetLegal(AutoLegalityWrapper.GetTemplate(set), out _);

        var file = new byte[pk.SIZE_PARTY];
        pk.WriteDecryptedDataParty(file);

        var outcome = bot.RequestTradeFile(WebUser.Owner, file, null);

        Assert.Null(outcome.Error);
        Assert.Equal("Eevee", outcome.Request!.Pokemon);
    }
}
