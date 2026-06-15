using Xunit;

namespace ChargesApi.Tests;

public class ChargeStoreTests
{
    [Fact]
    public async Task GetOrCreateAsyncReturnsSameChargeForSameIdempotencyKey()
    {
        var store = new ChargeStore();
        var request = CreateRequest("same-key");
        var chargeCalls = 0;

        var first = await store.GetOrCreateAsync(request, _ =>
        {
            chargeCalls++;
            return Task.FromResult(CreateCharge("ch_first", request));
        });

        var second = await store.GetOrCreateAsync(request, _ =>
        {
            chargeCalls++;
            return Task.FromResult(CreateCharge("ch_second", request));
        });

        Assert.False(first.IsReplay);
        Assert.True(second.IsReplay);
        Assert.True(second.IsSameRequest);
        Assert.Equal(first.Charge, second.Charge);
        Assert.Equal(1, chargeCalls);
    }

    [Fact]
    public async Task GetOrCreateAsyncRejectsSameIdempotencyKeyWithDifferentPayload()
    {
        var store = new ChargeStore();
        var firstRequest = CreateRequest("same-key", amount: 10.00m);
        var secondRequest = CreateRequest("same-key", amount: 20.00m);

        var first = await store.GetOrCreateAsync(firstRequest, request =>
            Task.FromResult(CreateCharge("ch_first", request)));

        var second = await store.GetOrCreateAsync(secondRequest, request =>
            Task.FromResult(CreateCharge("ch_second", request)));

        Assert.False(first.IsReplay);
        Assert.True(second.IsReplay);
        Assert.False(second.IsSameRequest);
        Assert.Equal(first.Charge, second.Charge);
    }

    [Fact]
    public async Task GetOrCreateAsyncSerializesConcurrentRequestsForSameKey()
    {
        var store = new ChargeStore();
        var request = CreateRequest("concurrent-key");
        var chargeCalls = 0;

        var results = await Task.WhenAll(
            Enumerable.Range(0, 10).Select(_ => store.GetOrCreateAsync(request, async req =>
            {
                Interlocked.Increment(ref chargeCalls);
                await Task.Delay(50);
                return CreateCharge($"ch_{Guid.NewGuid():N}", req);
            })));

        Assert.Equal(1, chargeCalls);
        Assert.Single(results.Select(result => result.Charge.Id).Distinct());
        Assert.Single(results.Where(result => !result.IsReplay));
        Assert.All(results, result => Assert.True(result.IsSameRequest));
    }

    private static ChargeRequest CreateRequest(string key, decimal amount = 12.50m)
    {
        return new ChargeRequest(
            IdempotencyKey: key,
            Amount: amount,
            Currency: "USD",
            CustomerEmail: "unit@example.com",
            CardToken: "tok_visa");
    }

    private static Charge CreateCharge(string id, ChargeRequest request)
    {
        return new Charge(
            Id: id,
            Amount: request.Amount,
            Currency: request.Currency,
            CustomerEmail: request.CustomerEmail,
            Status: "succeeded",
            CreatedAt: DateTime.UtcNow);
    }
}
