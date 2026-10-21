using System.Globalization;
using NxHub.Pokemon.Auth;
using NxHub.Pokemon.Bots;
using NxHub.Pokemon.Requests;
using SysBot.Pokemon;

namespace NxHub.Pokemon.Api;

public sealed record TradeRequestBody(string? Set, string? File, string? Code);

public sealed record TrainerInfoBody(string? Code);

public sealed record SetupBody(ProgramMode Mode, string? Host, int? Port);

public sealed record ImportBody(string? Json);

public static class Endpoints
{
    public const string Version = "0.1.0";

    public static void MapTradeApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        api.MapGet("/me", (HttpContext context) =>
        {
            var user = context.User();
            return Results.Ok(new { name = user.Name, isOwner = user.IsOwner });
        });

        api.MapGet("/status", (HttpContext context, TradeBotHost host, SwitchHandOver handOver, Microsoft.Extensions.Options.IOptions<PokemonOptions> options) =>
        {
            var bot = host.Bot;
            var setup = host.Setup();
            return Results.Ok(new
            {
                version = Version,
                configured = bot is not null,
                game = setup.Game,
                mode = setup.Mode,
                running = bot?.IsRunning ?? false,
                restartPending = host.RestartPending,
                problem = host.Problem,
                canQueue = bot?.CanQueue ?? false,
                queueLength = bot?.Queue.Count ?? 0,
                bots = bot?.Bots ?? [],
                handOver = handOver.View,
                discord = setup.Discord,
                isOwner = context.User().IsOwner,
                sourceUrl = options.Value.SourceUrl,
            });
        });

        api.MapPost("/bot/start", async (HttpContext context, TradeBotHost host, CancellationToken cancellationToken) =>
        {
            if (Forbidden(context) is { } forbidden)
            {
                return forbidden;
            }

            if (host.Bot is null)
            {
                return Problem(StatusCodes.Status409Conflict, "not_configured", "Not set up", host.Problem ?? "Set up the trade bot first.");
            }

            try
            {
                await host.StartBotAsync(cancellationToken);
            }
            catch (HandOverException ex)
            {
                return Problem(StatusCodes.Status502BadGateway, "hand_over_failed", "NX Hub kept the Switch", ex.Message);
            }

            return Results.Accepted();
        });

        api.MapPost("/bot/stop", async (HttpContext context, TradeBotHost host, CancellationToken cancellationToken) =>
        {
            if (Forbidden(context) is { } forbidden)
            {
                return forbidden;
            }

            await host.StopBotAsync(cancellationToken);
            return Results.Accepted();
        });

        api.MapGet("/setup", (HttpContext context, TradeBotHost host) =>
            Forbidden(context) ?? Results.Ok(new { setup = host.Setup(), games = TradeBotFactory.Games }));

        api.MapPut("/setup", async (HttpContext context, SetupBody body, TradeBotHost host, CancellationToken cancellationToken) =>
        {
            if (Forbidden(context) is { } forbidden)
            {
                return forbidden;
            }

            var hostName = body.Host?.Trim() ?? "";
            if (hostName.Length is 0 or > 253 || hostName.Any(char.IsWhiteSpace))
            {
                return Problem(StatusCodes.Status400BadRequest, "validation_failed", "Check the address", "Enter your Switch's IP address, for example 192.168.1.50.");
            }

            var port = body.Port ?? 6000;
            if (port is < 1 or > 65535)
            {
                return Problem(StatusCodes.Status400BadRequest, "validation_failed", "Check the port", "The port is a number from 1 to 65535 (sys-botbase uses 6000).");
            }

            try
            {
                host.SaveSetup(body.Mode, hostName, port);
            }
            catch (ArgumentException ex)
            {
                return Problem(StatusCodes.Status400BadRequest, "validation_failed", "Check the setup", ex.Message);
            }

            await host.StopBotAsync(cancellationToken);
            host.RequestRestart();
            return Results.Accepted(value: new { restarting = true });
        });

        api.MapPost("/setup/import", async (HttpContext context, ImportBody body, TradeBotHost host, CancellationToken cancellationToken) =>
        {
            if (Forbidden(context) is { } forbidden)
            {
                return forbidden;
            }

            try
            {
                host.ImportConfig(body.Json ?? "");
            }
            catch (ArgumentException ex)
            {
                return Problem(StatusCodes.Status400BadRequest, "invalid_config", "That config can't be used", ex.Message);
            }

            await host.StopBotAsync(cancellationToken);
            host.RequestRestart();
            return Results.Accepted(value: new { restarting = true });
        });

        api.MapGet("/requests", (HttpContext context, RequestRegistry registry, bool? all) =>
        {
            var user = context.User();
            return Results.Ok(registry.List(user.IsOwner && all == true ? null : user.Id));
        });

        api.MapPost("/requests/trade", (HttpContext context, TradeRequestBody body, TradeBotHost host) =>
        {
            if (host.Bot is not { } bot)
            {
                return NotReady(host);
            }

            if (!TryParseCode(body.Code, out var code))
            {
                return Problem(StatusCodes.Status400BadRequest, "invalid_code", "Check the link code", "A link code is 8 digits, or leave it empty for a random one.");
            }

            RequestOutcome outcome;
            if (!string.IsNullOrWhiteSpace(body.File))
            {
                byte[] file;
                try
                {
                    file = Convert.FromBase64String(body.File);
                }
                catch (FormatException)
                {
                    return Problem(StatusCodes.Status400BadRequest, "invalid_file", "That file can't be read", "The file wasn't sent correctly. Try again.");
                }

                outcome = bot.RequestTradeFile(context.User(), file, code);
            }
            else
            {
                outcome = bot.RequestTrade(context.User(), body.Set ?? "", code);
            }

            return Created(outcome);
        });

        api.MapPost("/requests/trainer-info", (HttpContext context, TrainerInfoBody body, TradeBotHost host) =>
        {
            if (host.Bot is not { } bot)
            {
                return NotReady(host);
            }

            if (!TryParseCode(body.Code, out var code))
            {
                return Problem(StatusCodes.Status400BadRequest, "invalid_code", "Check the link code", "A link code is 8 digits, or leave it empty for a random one.");
            }

            return Created(bot.RequestTrainerInfo(context.User(), code));
        });

        api.MapDelete("/requests/{id}", (HttpContext context, string id, RequestRegistry registry, TradeBotHost host) =>
        {
            var user = context.User();
            var request = registry.Get(id);
            if (request is null || (!user.IsOwner && !registry.OwnedBy(id, user.Id)))
            {
                return Problem(StatusCodes.Status404NotFound, "request_not_found", "No such request", "That request doesn't exist.");
            }

            if (!request.IsActive)
            {
                return Results.NoContent();
            }

            return host.Bot?.Cancel(id) == true
                ? Results.NoContent()
                : Problem(StatusCodes.Status409Conflict, "request_in_progress", "Already trading", "That request is already being traded, so it can't be cancelled now.");
        });

        api.MapGet("/queue", (HttpContext context, TradeBotHost host) =>
            Forbidden(context) ?? Results.Ok(host.Bot?.Queue ?? []));

        api.MapGet("/log", (HttpContext context, BotLog log, int? count) =>
            Forbidden(context) ?? Results.Ok(log.Recent(Math.Clamp(count ?? 100, 1, 300))));

        api.MapFallback(() => Problem(StatusCodes.Status404NotFound, "not_found", "Not found", "No such API endpoint."));
    }

    internal static WebUser User(this HttpContext context) => (WebUser)context.Items[typeof(WebUser)]!;

    internal static bool TryParseCode(string? text, out int? code)
    {
        code = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var digits = new string([.. text.Where(c => !char.IsWhiteSpace(c) && c != '-')]);
        if (digits.Length is 0 or > 8 || !digits.All(char.IsAsciiDigit))
        {
            return false;
        }

        code = int.Parse(digits, CultureInfo.InvariantCulture);
        return true;
    }

    internal static IResult Problem(int status, string code, string title, string detail) =>
        Results.Problem(statusCode: status, title: title, detail: detail, extensions: new Dictionary<string, object?> { ["code"] = code });

    private static IResult? Forbidden(HttpContext context) => context.User().IsOwner
        ? null
        : Problem(StatusCodes.Status403Forbidden, "owner_only", "Owner only", "Only the bot's owner can do that.");

    private static IResult NotReady(TradeBotHost host) =>
        Problem(StatusCodes.Status409Conflict, "not_configured", "Not set up", host.Problem ?? "The trade bot isn't set up yet.");

    private static IResult Created(RequestOutcome outcome)
    {
        if (outcome.Request is { } request)
        {
            return Results.Created($"/api/requests/{request.Id}", request);
        }

        var status = outcome.ErrorCode is "queue_closed" or "already_queued" ? StatusCodes.Status409Conflict : StatusCodes.Status422UnprocessableEntity;
        return Problem(status, outcome.ErrorCode ?? "refused", "Request refused", outcome.Error ?? "The request was refused.");
    }
}
