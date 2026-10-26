using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace NxHub.Pokemon.Tests;

/// <summary>Sign-in vouched for by a reverse proxy on this machine: loopback, the proxy key and the user header.</summary>
public sealed class ProxyTests : IDisposable
{
    private const string UserHeader = "X-Site-User";
    private const string KeyHeader = "X-Site-Proxy-Key";
    private static readonly string Key = new('k', 64);
    private readonly string _dir = Directory.CreateTempSubdirectory("nxhub-pokemon-proxy-").FullName;

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

    /// <summary>Makes every in-memory request look like it came from a proxy on this machine.</summary>
    private sealed class LoopbackStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Loopback;
                return nextMiddleware(context);
            });
            next(app);
        };
    }

    private WebApplicationFactory<Program> Factory(string? keyFile = "write")
    {
        if (keyFile == "write")
        {
            keyFile = Path.Combine(_dir, "proxy-key");
            File.WriteAllText(keyFile, Key + "\n");
        }

        return new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
        {
            web.UseSetting("NxHubPokemon:DataDirectory", _dir);
            web.UseSetting("NxHubPokemon:Auth:Token", "test-token-0123456789abcdef");
            web.UseSetting("NxHubPokemon:Auth:TrustedProxyUserHeader", UserHeader);
            web.UseSetting("NxHubPokemon:Auth:TrustedProxyKeyFile", keyFile ?? "/nonexistent/key");
            web.ConfigureServices(services => services.AddSingleton<IStartupFilter, LoopbackStartupFilter>());
        });
    }

    private static HttpRequestMessage Status(string? key)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        request.Headers.Add(UserHeader, "owner");
        if (key is not null)
        {
            request.Headers.Add(KeyHeader, key);
        }

        return request;
    }

    [Fact]
    public async Task Proxy_user_with_the_key_is_signed_in()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Status(Key))).StatusCode);
    }

    [Theory]
    [InlineData(null)]                                  // another process on this machine
    [InlineData("wrong-key-wrong-key-wrong-key-wrong-key")]
    public async Task Proxy_user_without_the_right_key_is_refused(string? key)
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Status(key))).StatusCode);
    }

    [Fact]
    public async Task Proxy_key_header_must_appear_once()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var request = Status(Key);
        request.Headers.Add(KeyHeader, Key);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Trusting_a_proxy_header_without_a_key_refuses_to_start()
    {
        using var factory = Factory(keyFile: null);
        var error = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using var client = factory.CreateClient();
            await client.GetAsync("/api/health");
        });
        Assert.Contains("proxy", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
