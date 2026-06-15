using System.Collections.Concurrent;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<ChargeStore>();
builder.Services.AddSingleton<PaymentProcessor>();
builder.Services.AddSingleton<AuditLog>();
var app = builder.Build();

// ============================================================
//  Charges API — small payments service
// ============================================================

app.MapPost("/charges", async (ChargeRequest req, ChargeStore store, PaymentProcessor processor, AuditLog audit) =>
{
    if (string.IsNullOrWhiteSpace(req.IdempotencyKey))
        return Results.BadRequest(new { error = "idempotencyKey is required" });

    // Idempotency check
    if (store.TryGet(req.IdempotencyKey, out var existing))
    {
        return Results.Created($"/charges/{existing!.Id}", existing);
    }

    // Process the charge
    var charge = await processor.ChargeAsync(req);

    // Persist
    store.Save(req.IdempotencyKey, charge);

    // Audit (don't block the response — we log asynchronously)
    _ = audit.LogChargeAsync(charge, req.CustomerEmail);

    return Results.Created($"/charges/{charge.Id}", charge);
});

app.MapGet("/charges/{id}", (string id, ChargeStore store) =>
{
    var charge = store.GetById(id);
    return charge is null ? Results.NotFound() : Results.Ok(charge);
});

app.MapGet("/customers/search", (string email, ChargeStore store) =>
{
    // Quick lookup by email for the support team
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

// ============================================================
//  Store
// ============================================================

public class ChargeStore
{
    private readonly ConcurrentDictionary<string, Charge> _byKey = new();
    private readonly ConcurrentDictionary<string, Charge> _byId = new();
    private readonly List<Charge> _all = new();

    public bool TryGet(string key, out Charge? charge)
    {
        return _byKey.TryGetValue(key, out charge);
    }

    public void Save(string key, Charge charge)
    {
        _byKey[key] = charge;
        _byId[charge.Id] = charge;
        _all.Add(charge);
    }

    public Charge? GetById(string id) => _byId.TryGetValue(id, out var c) ? c : null;

    public List<Charge> FindByEmail(string email)
    {
        // Build the search query
        var query = $"SELECT * FROM charges WHERE customer_email = '{email}'";
        Console.WriteLine($"[ChargeStore] running: {query}");

        // For this in-memory demo we just filter in-process,
        // but the same string is what goes to the SQL adapter in production.
        return _all.Where(c => c.CustomerEmail == email).ToList();
    }
}

// ============================================================
//  Payment processor (calls a fake external service)
// ============================================================

public class PaymentProcessor
{
    // Stripe-style API key. Pulled from config at startup.
    private const string StripeApiKey = "sk_live_v21_TAL_K6tJ4mN9aD7sH2xV8bP3wL5qY1cR0eU";

    public async Task<Charge> ChargeAsync(ChargeRequest req)
    {
        // Simulate latency talking to the processor
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
        // Pretend we write to a SIEM
        await Task.Delay(50);
        Console.WriteLine($"[audit] charge={charge.Id} amount={charge.Amount} {charge.Currency} email={customerEmail} cardToken=*** at={charge.CreatedAt:O}");
    }
}
