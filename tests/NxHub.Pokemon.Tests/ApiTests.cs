using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace NxHub.Pokemon.Tests;

public sealed class ApiTests : IDisposable
{
    private const string Token = "test-token-0123456789abcdef";
    private readonly string _dir = Directory.CreateTempSubdirectory("nxhub-pokemon-api-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private WebApplicationFactory<Program> Factory(string? config = null)
    {
        if (config is not null)
        {
            File.WriteAllText(Path.Combine(_dir, "config.json"), config);
        }

        return new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
        {
            web.UseSetting("NxHubPokemon:DataDirectory", _dir);
            web.UseSetting("NxHubPokemon:Auth:Token", Token);
        });
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory, bool signedIn = true)
    {
        var client = factory.CreateClient();
        if (signedIn)
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", Token);
        }

        return client;
    }

    [Fact]
    public async Task The_api_needs_the_token_but_health_does_not()
    {
        using var factory = Factory();
        using var client = Client(factory, signedIn: false);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health")).StatusCode);
        var response = await client.GetAsync("/api/status");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("unauthorized", (await Problem(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Without_a_config_the_bot_is_not_set_up_and_requests_are_refused()
    {
        using var factory = Factory();
        using var client = Client(factory);

        var status = await client.GetFromJsonAsync<JsonElement>("/api/status");
        Assert.False(status.GetProperty("configured").GetBoolean());
        Assert.False(status.GetProperty("running").GetBoolean());

        var response = await client.PostAsJsonAsync("/api/requests/trainer-info", new { });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("not_configured", (await Problem(response)).GetProperty("code").GetString());

        var setup = await client.GetFromJsonAsync<JsonElement>("/api/setup");
        Assert.Equal(5, setup.GetProperty("games").GetArrayLength());
    }

    [Fact]
    public async Task With_a_config_requests_are_queued_but_the_bot_stays_stopped()
    {
        using var factory = Factory("""
            { "Mode": 4, "Bots": [ { "Connection": { "IP": "192.0.2.10", "Port": 6000, "Protocol": 0 }, "InitialRoutine": 2 } ] }
            """);
        using var client = Client(factory);

        var status = await client.GetFromJsonAsync<JsonElement>("/api/status");
        Assert.True(status.GetProperty("configured").GetBoolean());
        Assert.False(status.GetProperty("running").GetBoolean());
        Assert.Equal("sv", status.GetProperty("mode").GetString());

        var created = await client.PostAsJsonAsync("/api/requests/trainer-info", new { code = "1234 5678" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var request = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("1234 5678", request.GetProperty("code").GetString());
        Assert.Equal("trainerInfo", request.GetProperty("kind").GetString());

        var badCode = await client.PostAsJsonAsync("/api/requests/trainer-info", new { code = "12ab" });
        Assert.Equal(HttpStatusCode.BadRequest, badCode.StatusCode);

        var refused = await client.PostAsJsonAsync("/api/requests/trade", new { set = "Notamon\n- Tackle" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);

        var id = request.GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/requests/{id}")).StatusCode);
        var list = await client.GetFromJsonAsync<JsonElement>("/api/requests");
        Assert.Equal("canceled", list[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_cross_site_change_through_a_proxy_login_is_blocked()
    {
        using var factory = Factory();
        using var client = Client(factory, signedIn: false);

        // The test server's connection is not loopback-vouched, so a proxy header alone is not enough either.
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/bot/start");
        request.Headers.Add("X-Site-User", "sam");
        request.Headers.Add("Sec-Fetch-Site", "cross-site");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Saving_setup_writes_config_json()
    {
        using var factory = Factory();
        using var client = Client(factory);

        var invalid = await client.PutAsJsonAsync("/api/setup", new { mode = "sv", host = "", port = 6000 });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var saved = await client.PutAsJsonAsync("/api/setup", new { mode = "sv", host = "192.168.1.50", port = 6000 });
        Assert.Equal(HttpStatusCode.Accepted, saved.StatusCode);

        using var config = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_dir, "config.json")));
        Assert.Equal(4, config.RootElement.GetProperty("Mode").GetInt32());
        Assert.Equal("192.168.1.50", config.RootElement.GetProperty("Bots")[0].GetProperty("Connection").GetProperty("IP").GetString());
    }

    private static async Task<JsonElement> Problem(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();
}

public sealed class LinkTests
{
    [Theory]
    [InlineData("/nxhub/", "/nxhub/")]
    [InlineData("https://hub.example/", "https://hub.example/")]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("//evil.example/", null)]
    [InlineData("", null)]
    public void Only_site_paths_and_web_urls_become_links(string url, string? expected) =>
        Assert.Equal(expected, NxHub.Pokemon.Api.Endpoints.SafeLink(url));
}
