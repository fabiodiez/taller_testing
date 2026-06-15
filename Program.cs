using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<ChargeStore>();
builder.Services.AddSingleton<PaymentProcessor>();
builder.Services.AddSingleton<AuditLog>();
var app = builder.Build();

// ============================================================
//  Charges API - small payments service
// ============================================================

app.MapPost("/charges", async (ChargeRequest req, ChargeStore store, PaymentProcessor processor, AuditLog audit) =>
{
    if (string.IsNullOrWhiteSpace(req.IdempotencyKey))
        return Results.BadRequest(new { error = "idempotencyKey is required" });

    var result = await store.GetOrCreateAsync(req, processor.ChargeAsync);
    if (!result.IsSameRequest)
    {
        return Results.Conflict(new { error = "idempotencyKey has already been used with a different request" });
    }

    if (!result.IsReplay)
    {
        _ = audit.LogChargeAsync(result.Charge, req.CustomerEmail);
    }

    return Results.Created($"/charges/{result.Charge.Id}", result.Charge);
});

app.MapGet("/charges/{id}", (string id, ChargeStore store) =>
{
    var charge = store.GetById(id);
    return charge is null ? Results.NotFound() : Results.Ok(charge);
});

app.MapGet("/customers/search", (string email, ChargeStore store) =>
{
    var results = store.FindByEmail(email);
    return Results.Ok(results);
});

app.Run();

// Expose Program to the test project (WebApplicationFactory<Program>)
public partial class Program { }

// ============================================================
//  Domain
// ============================================================

public record ChargeRequest(
    [property: JsonPropertyName("idempotencyKey")] string IdempotencyKey,
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("customerEmail")] string CustomerEmail,
    [property: JsonPropertyName("cardToken")] string CardToken
);

public record Charge(
    string Id,
    decimal Amount,
    string Currency,
    string CustomerEmail,
    string Status,
    DateTime CreatedAt
);

public record ChargeResult(Charge Charge, bool IsReplay, bool IsSameRequest);

public record IdempotentCharge(IdempotencyFingerprint Fingerprint, Charge Charge);

public record IdempotencyFingerprint(string Value)
{
    public static IdempotencyFingerprint From(ChargeRequest request)
    {
        var payload = $"{request.Amount:G29}|{request.Currency}|{request.CustomerEmail}|{request.CardToken}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return new IdempotencyFingerprint(Convert.ToHexString(bytes));
    }
}

// ============================================================
//  Store
// ============================================================

public class ChargeStore
{
    private readonly ConcurrentDictionary<string, IdempotentCharge> _byKey = new();
    private readonly ConcurrentDictionary<string, Charge> _byId = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _keyLocks = new();
    private readonly List<Charge> _all = new();
    private readonly object _allLock = new();

    public async Task<ChargeResult> GetOrCreateAsync(
        ChargeRequest request,
        Func<ChargeRequest, Task<Charge>> createCharge)
    {
        var key = NormalizeKey(request.IdempotencyKey);
        var fingerprint = IdempotencyFingerprint.From(request);
        var keyLock = _keyLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

        await keyLock.WaitAsync();
        try
        {
            if (_byKey.TryGetValue(key, out var existing))
            {
                return existing.Fingerprint == fingerprint
                    ? new ChargeResult(existing.Charge, IsReplay: true, IsSameRequest: true)
                    : new ChargeResult(existing.Charge, IsReplay: true, IsSameRequest: false);
            }

            var charge = await createCharge(request);
            var entry = new IdempotentCharge(fingerprint, charge);

            if (!_byKey.TryAdd(key, entry))
            {
                var saved = _byKey[key];
                return saved.Fingerprint == fingerprint
                    ? new ChargeResult(saved.Charge, IsReplay: true, IsSameRequest: true)
                    : new ChargeResult(saved.Charge, IsReplay: true, IsSameRequest: false);
            }

            _byId[charge.Id] = charge;
            lock (_allLock)
            {
                _all.Add(charge);
            }

            return new ChargeResult(charge, IsReplay: false, IsSameRequest: true);
        }
        finally
        {
            keyLock.Release();
        }
    }

    public Charge? GetById(string id) => _byId.TryGetValue(id, out var charge) ? charge : null;

    public List<Charge> FindByEmail(string email)
    {
        lock (_allLock)
        {
            return _all.Where(charge => charge.CustomerEmail == email).ToList();
        }
    }

    private static string NormalizeKey(string key) => key.Trim();
}

// ============================================================
//  Payment processor (calls a fake external service)
// ============================================================

public class PaymentProcessor
{
    public async Task<Charge> ChargeAsync(ChargeRequest req)
    {
        await Task.Delay(250);

        var id = "ch_" + Guid.NewGuid().ToString("N")[..16];
        return new Charge(
            Id: id,
            Amount: req.Amount,
            Currency: req.Currency,
            CustomerEmail: req.CustomerEmail,
            Status: "succeeded",
            CreatedAt: DateTime.UtcNow
        );
    }
}

// ============================================================
//  Audit log
// ============================================================

public class AuditLog
{
    public async Task LogChargeAsync(Charge charge, string customerEmail)
    {
        await Task.Delay(50);
        Console.WriteLine($"[audit] charge={charge.Id} amount={charge.Amount} {charge.Currency} email={customerEmail} cardToken=*** at={charge.CreatedAt:O}");
    }
}
