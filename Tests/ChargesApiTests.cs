using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ChargesApi.Tests;

// Happy-path smoke tests for the charges service.
// These tests currently pass. That doesn't mean the service is correct.
public class ChargesApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ChargesApiTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task CreateChargeReturns201ForAFreshKey()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/charges", new {
            idempotencyKey = "test_fresh_key",
            amount = 12.50m,
            currency = "USD",
            customerEmail = "happy@example.com",
            cardToken = "tok_visa"
        });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"id\":\"ch_", body);
    }

    [Fact]
    public async Task MissingIdempotencyKeyReturns400()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/charges", new {
            idempotencyKey = "",
            amount = 1.00m,
            currency = "USD",
            customerEmail = "x@y.com",
            cardToken = "tok_visa"
        });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task ConcurrentRequestsWithSameIdempotencyKeyReturnSameCharge()
    {
        var client = _factory.CreateClient();
        var key = $"test_concurrent_{Guid.NewGuid():N}";
        var request = new {
            idempotencyKey = key,
            amount = 49.99m,
            currency = "USD",
            customerEmail = "concurrent@example.com",
            cardToken = "tok_visa"
        };

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => client.PostAsJsonAsync("/charges", request)));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));

        var ids = await Task.WhenAll(responses.Select(ReadChargeIdAsync));
        Assert.Single(ids.Distinct());
    }

    [Fact]
    public async Task ReusingIdempotencyKeyWithDifferentPayloadReturnsConflict()
    {
        var client = _factory.CreateClient();
        var key = $"test_conflict_{Guid.NewGuid():N}";

        var first = await client.PostAsJsonAsync("/charges", new {
            idempotencyKey = key,
            amount = 10.00m,
            currency = "USD",
            customerEmail = "first@example.com",
            cardToken = "tok_visa"
        });

        var second = await client.PostAsJsonAsync("/charges", new {
            idempotencyKey = key,
            amount = 20.00m,
            currency = "USD",
            customerEmail = "second@example.com",
            cardToken = "tok_visa"
        });

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task CustomerSearchDoesNotTreatInputAsSql()
    {
        var client = _factory.CreateClient();
        var key = $"test_search_{Guid.NewGuid():N}";

        await client.PostAsJsonAsync("/charges", new {
            idempotencyKey = key,
            amount = 15.00m,
            currency = "USD",
            customerEmail = "search-target@example.com",
            cardToken = "tok_visa"
        });

        var response = await client.GetAsync("/customers/search?email=%27%20OR%20%271%27%3D%271");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("[]", body);
    }

    private static async Task<string> ReadChargeIdAsync(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var json = await JsonDocument.ParseAsync(stream);
        return json.RootElement.GetProperty("id").GetString()!;
    }
}
