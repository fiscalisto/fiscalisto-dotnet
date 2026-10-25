using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fiscalisto;
using Xunit;

namespace Fiscalisto.Tests;

/// <summary>Answers each request with the next queued response and keeps what it was sent.</summary>
internal sealed class FakeHandler : HttpMessageHandler
{
    public readonly List<(HttpRequestMessage Request, string? Body)> Sent = new();
    private readonly Queue<Func<HttpResponseMessage>> _answers = new();

    public FakeHandler Answer(int status, string json, Action<HttpResponseMessage>? edit = null)
    {
        _answers.Enqueue(() =>
        {
            var r = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            edit?.Invoke(r);
            return r;
        });
        return this;
    }

    public FakeHandler Fail()
    {
        _answers.Enqueue(() => throw new HttpRequestException("reset"));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Sent.Add((request, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
        return _answers.Dequeue()();
    }
}

public class ClientTests
{
    private static (FiscalistoClient, FakeHandler, List<TimeSpan>) Client(FakeHandler handler)
    {
        var waits = new List<TimeSpan>();
        var client = new FiscalistoClient("fk_test_abc", new FiscalistoClientOptions
        {
            BaseUrl = new Uri("https://api.example.test"),
            HttpClient = new HttpClient(handler),
        })
        {
            Delay = (t, _) =>
            {
                waits.Add(t);
                return Task.CompletedTask;
            },
        };
        return (client, handler, waits);
    }

    private static InvoiceCreate Invoice() => new()
    {
        Customer = new InvoiceCreateCustomer { TaxId = "ICV060329BY0", LegalName = "INMOBILIARIA CVA", TaxSystem = "601", Zip = "33826" },
        Items = new() { new() { Description = "Mensualidad", ProductKey = "86121700", Quantity = 2, UnitPrice = 2500 } },
    };

    [Fact]
    public void RefusesMissingOrWrongKeys()
    {
        Assert.Throws<ArgumentException>(() => new FiscalistoClient("whsec_abc"));
        Assert.True(new FiscalistoClient("fk_live_x").Livemode);
        Assert.False(new FiscalistoClient("fk_test_x").Livemode);
    }

    [Fact]
    public async Task StampsWithSnakeCaseBodyAndKeepsTheIdempotencyKeyAcrossRetries()
    {
        var (client, handler, waits) = Client(new FakeHandler()
            .Fail()
            .Answer(500, """{"error":{"type":"api_error","code":"internal"}}""")
            .Answer(201, """{"id":"inv_1","object":"invoice","uuid":"A-B","total":5800}"""));

        var invoice = await client.Invoices.CreateAsync(Invoice());

        Assert.Equal("inv_1", invoice.Id);
        Assert.Equal(5800m, invoice.Total);
        Assert.Equal(3, handler.Sent.Count);
        Assert.Equal(2, waits.Count);
        var keys = handler.Sent.Select(s => s.Request.Headers.GetValues("Idempotency-Key").Single()).Distinct().ToList();
        Assert.Single(keys);
        var (request, body) = handler.Sent[0];
        Assert.Equal("Bearer fk_test_abc", request.Headers.Authorization!.ToString());
        Assert.Equal("https://api.example.test/v1/invoices", request.RequestUri!.ToString());
        using var json = JsonDocument.Parse(body!);
        Assert.Equal("ICV060329BY0", json.RootElement.GetProperty("customer").GetProperty("tax_id").GetString());
        Assert.Equal(2500, json.RootElement.GetProperty("items")[0].GetProperty("unit_price").GetDecimal());
        Assert.False(json.RootElement.GetProperty("customer").TryGetProperty("email", out _));
    }

    [Fact]
    public async Task UsesTheGivenIdempotencyKey()
    {
        var (client, handler, _) = Client(new FakeHandler().Answer(201, """{"id":"inv_1"}"""));
        await client.Invoices.CreateAsync(Invoice(), new RequestOptions { IdempotencyKey = "pedido-A-1042" });
        Assert.Equal("pedido-A-1042", handler.Sent[0].Request.Headers.GetValues("Idempotency-Key").Single());
    }

    [Fact]
    public async Task RepeatsACreateWithTheSameKey()
    {
        var (client, handler, _) = Client(new FakeHandler()
            .Answer(500, """{"error":{"type":"api_error","code":"internal","message":"Falló."}}""")
            .Answer(201, """{"id":"cus_1"}"""));
        await client.Customers.CreateAsync(new CreateCustomerParams { TaxId = "XAXX010101000", LegalName = "PUBLICO EN GENERAL", TaxSystem = "616", Zip = "06000" });
        Assert.Equal(2, handler.Sent.Count);
        Assert.Single(handler.Sent.Select(s => s.Request.Headers.GetValues("Idempotency-Key").Single()).Distinct());
    }

    [Fact]
    public async Task DoesNotRepeatAPostWithoutIdempotencyKeyAfterA500()
    {
        // Your own call to a route, without idempotent: it may have worked, so it isn't repeated.
        var (client, handler, _) = Client(new FakeHandler()
            .Answer(500, """{"error":{"type":"api_error","code":"internal","message":"Falló."}}"""));
        var error = await Assert.ThrowsAsync<FiscalistoException>(() =>
            client.RequestAsync<System.Text.Json.JsonElement>("POST", "/v1/customers", new { tax_id = "XAXX010101000" }));
        Assert.Equal(500, error.StatusCode);
        Assert.Single(handler.Sent);
        Assert.False(handler.Sent[0].Request.Headers.Contains("Idempotency-Key"));
    }

    [Fact]
    public async Task HonorsRetryAfterOn429()
    {
        var (client, handler, waits) = Client(new FakeHandler()
            .Answer(429, """{"error":{"type":"rate_limit_error","code":"rate_limited"}}""", r => r.Headers.Add("Retry-After", "3"))
            .Answer(200, """{"data":[],"has_more":false,"url":"/v1/customers"}"""));
        await client.Customers.ListAsync();
        Assert.Equal(2, handler.Sent.Count);
        Assert.Equal(TimeSpan.FromSeconds(3), waits.Single());
    }

    [Fact]
    public async Task ErrorsCarryTheirFields()
    {
        var (client, _, _) = Client(new FakeHandler().Answer(402,
            """{"error":{"type":"invalid_request_error","code":"sat_rejected","message":"El SAT lo rechazó.","param":"customer.zip","sat_code":"CFDI40147","doc_url":"https://fiscalisto.com/docs/api/errores#sat_rejected"}}""",
            r => r.Headers.Add("Request-Id", "req_9")));
        var error = await Assert.ThrowsAsync<FiscalistoException>(() => client.Invoices.CreateAsync(Invoice(), new RequestOptions { MaxRetries = 0 }));
        Assert.Equal("sat_rejected", error.Code);
        Assert.Equal("customer.zip", error.Param);
        Assert.Equal("CFDI40147", error.SatCode);
        Assert.Equal("req_9", error.RequestId);
        Assert.Equal("El SAT lo rechazó.", error.Message);
    }

    [Fact]
    public async Task ConnectionFailuresBecomeFiscalistoExceptions()
    {
        var (client, _, _) = Client(new FakeHandler().Fail());
        var error = await Assert.ThrowsAsync<FiscalistoException>(() => client.Invoices.CreateAsync(Invoice(), new RequestOptions { MaxRetries = 0 }));
        Assert.Equal("connection_error", error.Type);
        Assert.Equal("connection_failed", error.Code);
        Assert.NotNull(error.IdempotencyKey);
    }

    [Fact]
    public async Task AutoPagingWalksEveryPage()
    {
        var (client, handler, _) = Client(new FakeHandler()
            .Answer(200, """{"data":[{"id":"cus_1"},{"id":"cus_2"}],"has_more":true,"url":"/v1/customers"}""")
            .Answer(200, """{"data":[{"id":"cus_3"}],"has_more":false,"url":"/v1/customers"}"""));
        var ids = new List<string>();
        await foreach (var customer in client.Customers.ListAutoPagingAsync(new ListCustomersParams { Limit = 2 }))
            ids.Add(customer.Id);
        Assert.Equal(new[] { "cus_1", "cus_2", "cus_3" }, ids);
        Assert.Equal("?limit=2", handler.Sent[0].Request.RequestUri!.Query);
        Assert.Equal("?limit=2&starting_after=cus_2", handler.Sent[1].Request.RequestUri!.Query);
    }
}

public class WebhookTests
{
    private const string Secret = "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw";
    private const string Payload = """{"id":"evt_1","object":"event","type":"invoice.stamped","api_version":"2026-10-01","livemode":false,"created_at":"2026-10-24T12:00:00Z","data":{"object":{"id":"inv_1","object":"invoice","total":5800}}}""";

    private static Dictionary<string, string> Headers(string payload, long timestamp, string secret = Secret)
    {
        var key = Convert.FromBase64String(secret["whsec_".Length..]);
        var signature = Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"msg_1.{timestamp}.{payload}")));
        return new() { ["Webhook-Id"] = "msg_1", ["Webhook-Timestamp"] = timestamp.ToString(), ["Webhook-Signature"] = $"v1,bad v1,{signature}" };
    }

    [Fact]
    public void VerifiesAndReadsTheObject()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var evt = FiscalistoClient.Webhooks.Verify(Payload, Headers(Payload, 1_800_000_000), Secret, now: now);
        Assert.Equal("invoice.stamped", evt.Type);
        var invoice = evt.DataObject<Invoice>();
        Assert.Equal("inv_1", invoice.Id);
        Assert.Equal(5800m, invoice.Total);
    }

    [Fact]
    public void RefusesTamperedOldOrUnsigned()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var headers = Headers(Payload, 1_800_000_000);
        Assert.Throws<FiscalistoWebhookException>(() => FiscalistoClient.Webhooks.Verify(Payload.Replace("5800", "1"), headers, Secret, now: now));
        Assert.Throws<FiscalistoWebhookException>(() => FiscalistoClient.Webhooks.Verify(Payload, headers, Secret, now: now.AddMinutes(10)));
        Assert.Throws<FiscalistoWebhookException>(() => FiscalistoClient.Webhooks.Verify(Payload, new Dictionary<string, string>(), Secret, now: now));
    }
}
