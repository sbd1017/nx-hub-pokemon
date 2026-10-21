using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using NxHub.Pokemon;
using NxHub.Pokemon.Api;
using NxHub.Pokemon.Auth;
using NxHub.Pokemon.Bots;
using NxHub.Pokemon.Requests;

// SysBot.NET would otherwise write its own log files next to the program (read-only once installed).
// Its log goes to this service's log and the web page instead (BotLog).
SysBot.Base.LogConfig.LoggingEnabled = false;

// A published install is self-contained in its own directory; resolve config and the web page from there.
var installDirectory = AppContext.BaseDirectory;
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = File.Exists(Path.Combine(installDirectory, "appsettings.json")) && Directory.Exists(Path.Combine(installDirectory, "wwwroot"))
        ? installDirectory
        : null,
});

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.AddServerHeader = false;
    // Large enough for an imported config.json or a Pokémon file, small enough to be safe.
    kestrel.Limits.MaxRequestBodySize = 512 * 1024;
});

builder.Services.Configure<PokemonOptions>(builder.Configuration.GetSection(PokemonOptions.SectionName));
builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    json.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<IOptions<PokemonOptions>>().Value;
    var path = options.ResolveDataDirectory(builder.Environment.ContentRootPath);
    Directory.CreateDirectory(path);
    return new DataDirectory(path);
});
builder.Services.AddSingleton<WebAuth>();
builder.Services.AddSingleton(sp => new RequestRegistry(
    sp.GetRequiredService<TimeProvider>(),
    Path.Combine(sp.GetRequiredService<DataDirectory>().Path, "requests.json")));
builder.Services.AddSingleton<BotLog>();
builder.Services.AddSingleton(sp => new SwitchHandOver(
    sp.GetRequiredService<IOptions<PokemonOptions>>(),
    sp.GetRequiredService<ILogger<SwitchHandOver>>()));
builder.Services.AddSingleton<TradeBotHost>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TradeBotHost>());
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedProto });
var pathBase = app.Configuration[$"{PokemonOptions.SectionName}:PathBase"];
if (!string.IsNullOrWhiteSpace(pathBase))
{
    app.UsePathBase("/" + pathBase.Trim().Trim('/'));
}

app.UseExceptionHandler();
app.UseRouting();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseMiddleware<AuthMiddleware>();
app.MapTradeApi();
app.UseDefaultFiles();
app.UseStaticFiles();

var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("NxHub.Pokemon");
var auth = app.Services.GetRequiredService<WebAuth>();
logger.LogInformation(
    "NX Hub trade bot {Version} starting. Data directory: {DataDirectory}. Auth: {AuthMode}",
    Endpoints.Version,
    app.Services.GetRequiredService<DataDirectory>().Path,
    auth.Enabled ? "token" : "disabled");

app.Run();

public partial class Program;
