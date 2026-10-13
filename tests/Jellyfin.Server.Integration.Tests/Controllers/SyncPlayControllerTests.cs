using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Jellyfin.Api.Models.SyncPlayDtos;
using Jellyfin.Extensions.Json;
using Xunit;

namespace Jellyfin.Server.Integration.Tests.Controllers;

public sealed class SyncPlayControllerTests : IClassFixture<JellyfinApplicationFactory>
{
    private readonly JellyfinApplicationFactory _factory;
    private static string? _accessToken;

    public SyncPlayControllerTests(JellyfinApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetGroups_Unauthorized_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/SyncPlay/List", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetGroups_InvalidToken_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader("invalid-token");

        var response = await client.GetAsync("/SyncPlay/List", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateGroup_ClientWithoutWebSocket_IsRefusedWithTheReason()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(_accessToken ??= await AuthHelper.CompleteStartupAsync(client));

        // Every SyncPlay command is pushed over the WebSocket, so a client without one could join a
        // group but would never hear from it again.
        using var response = await client.PostAsync(
            "SyncPlay/New",
            JsonContent.Create(new NewGroupRequestDto { GroupName = "NoWebSocket" }, options: JsonDefaults.Options),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("WebSocket", body, StringComparison.Ordinal);

        using var listResponse = await client.GetAsync("SyncPlay/List", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        Assert.Equal("[]", await listResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task JoinGroup_ClientWithoutWebSocket_IsRefusedWithTheReason()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(_accessToken ??= await AuthHelper.CompleteStartupAsync(client));

        using var response = await client.PostAsync(
            "SyncPlay/Join",
            JsonContent.Create(new JoinGroupRequestDto { GroupId = Guid.NewGuid() }, options: JsonDefaults.Options),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
