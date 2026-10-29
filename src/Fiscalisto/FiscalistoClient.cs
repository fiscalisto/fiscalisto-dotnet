using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Fiscalisto;

/// <summary>
/// Opciones del cliente. Todas tienen un valor usual.
/// </summary>
public sealed class FiscalistoClientOptions
{
    /// <summary>La dirección de la API. Por omisión https://api.fiscalisto.com.</summary>
    public Uri BaseUrl { get; set; } = new("https://api.fiscalisto.com");

    /// <summary>La versión de la API (Fiscalisto-Version); con null, la fijada en la cuenta. Por omisión <see cref="FiscalistoClient.ApiVersion"/>.</summary>
    public string? ApiVersion { get; set; } = FiscalistoClient.ApiVersion;

    /// <summary>"en": los mensajes de error en inglés (Accept-Language). Por omisión en español.</summary>
    public string? Language { get; set; }

    /// <summary>Cuántas veces repetir lo que es seguro repetir (por omisión 2).</summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>Cuánto esperar cada respuesta (por omisión 80 s: el PAC puede tardar).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(80);

    /// <summary>Su propio HttpClient (por ejemplo de IHttpClientFactory). El SDK no lo desecha.</summary>
    public HttpClient? HttpClient { get; set; }
}

/// <summary>
/// Opciones de una sola llamada.
/// </summary>
public sealed class RequestOptions
{
    /// <summary>
    /// La Idempotency-Key de lo que timbra (por ejemplo el id de su pedido). Sin ella el SDK
    /// pone una nueva en cada llamada y la repite en sus reintentos.
    /// </summary>
    public string? IdempotencyKey { get; set; }

    /// <summary>Otro tiempo de espera para esta llamada.</summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>Otro número de reintentos para esta llamada.</summary>
    public int? MaxRetries { get; set; }
}

/// <summary>
/// Una página de una lista: <see cref="Data"/> y <see cref="HasMore"/>. Para recorrer todas use
/// el método <c>…AutoPagingAsync</c> con <c>await foreach</c>.
/// </summary>
public sealed class Page<T>
{
    /// <summary>Los objetos de esta página.</summary>
    [JsonPropertyName("data")]
    public List<T> Data { get; set; } = new();

    /// <summary>true si hay más después del último.</summary>
    [JsonPropertyName("has_more")]
    public bool HasMore { get; set; }

    /// <summary>La ruta de la lista.</summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

/// <summary>
/// El cliente de la API de facturación de Fiscalisto. Uno por aplicación: es seguro usarlo
/// desde varios hilos.
/// </summary>
/// <example>
/// <code>
/// var fiscalisto = new FiscalistoClient(); // la llave viene de FISCALISTO_API_KEY
/// var invoice = await fiscalisto.Invoices.CreateAsync(new InvoiceCreate { … });
/// </code>
/// </example>
public sealed partial class FiscalistoClient : IDisposable
{
    /// <summary>La versión del SDK.</summary>
    public const string Version = "0.1.17";

    /// <summary>La versión de la API para la que se hizo este SDK (encabezado Fiscalisto-Version).</summary>
    public const string ApiVersion = "2026-10-24";

    /// <summary>
    /// Cómo se escribe y lee el JSON de la API: snake_case, sin los campos null. Úselas para
    /// leer <c>Event.Data.Object</c> o un JSON de la API que guardó.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new StringOrConverterFactory() },
    };

    private readonly string _key;
    private readonly FiscalistoClientOptions _options;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    /// <summary>Para las pruebas del SDK: cómo espera entre reintentos.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    /// <param name="apiKey">Su llave fk_test_… o fk_live_…; sin ella, la variable FISCALISTO_API_KEY.</param>
    /// <param name="options">Dirección, reintentos, tiempo de espera o su propio HttpClient.</param>
    /// <exception cref="ArgumentException">Falta la llave o no es una llave de la API.</exception>
    public FiscalistoClient(string? apiKey = null, FiscalistoClientOptions? options = null)
    {
        var key = apiKey ?? Environment.GetEnvironmentVariable("FISCALISTO_API_KEY");
        if (string.IsNullOrEmpty(key))
            throw new ArgumentException(
                "Falta la llave de la API: new FiscalistoClient(\"fk_test_…\") o la variable FISCALISTO_API_KEY. Créela en Desarrolladores › Llaves.",
                nameof(apiKey));
        if (!key.StartsWith("fk_live_", StringComparison.Ordinal) && !key.StartsWith("fk_test_", StringComparison.Ordinal))
            throw new ArgumentException(
                "La llave de la API empieza con fk_live_ o fk_test_. (Un secreto whsec_ es para verificar webhooks.)",
                nameof(apiKey));
        _key = key;
        _options = options ?? new FiscalistoClientOptions();
        _ownsHttp = _options.HttpClient is null;
        _http = _options.HttpClient ?? new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    }

    /// <summary>true con una llave fk_live_ (producción), false con fk_test_ (pruebas).</summary>
    public bool Livemode => _key.StartsWith("fk_live_", StringComparison.Ordinal);

    /// <summary>Verificar los webhooks que llegan a su endpoint.</summary>
    public static FiscalistoWebhooks Webhooks { get; } = new();

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    /// <summary>
    /// Cualquier ruta de la API, por si el SDK aún no tiene su método. <paramref name="body"/> es
    /// cualquier objeto (también uno anónimo o un diccionario) y se manda como JSON.
    /// </summary>
    public async Task<T> RequestAsync<T>(
        string method,
        string path,
        object? body = null,
        object? query = null,
        RequestOptions? options = null,
        bool idempotent = false,
        CancellationToken cancellationToken = default)
    {
        var bytes = await SendAsync(method, path, body, query, options, idempotent, cancellationToken, expectJson: true).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(bytes, JsonOptions)!;
    }

    internal Task<byte[]> RequestBytesAsync(
        string method, string path, object? body, object? query, RequestOptions? options, bool idempotent, CancellationToken cancellationToken)
        => SendAsync(method, path, body, query, options, idempotent, cancellationToken);

    internal async Task<string> RequestTextAsync(
        string method, string path, object? body, object? query, RequestOptions? options, bool idempotent, CancellationToken cancellationToken)
        => Encoding.UTF8.GetString(await SendAsync(method, path, body, query, options, idempotent, cancellationToken).ConfigureAwait(false));

    internal async Task<Page<T>> ListAsync<T>(
        string path, object? query, string? startingAfter, RequestOptions? options, CancellationToken cancellationToken)
    {
        var (page, _) = await PageAsync<T>(path, query, startingAfter, options, cancellationToken).ConfigureAwait(false);
        return page;
    }

    internal async IAsyncEnumerable<T> ListAllAsync<T>(
        string path, object? query, RequestOptions? options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? after = null;
        while (true)
        {
            var (page, lastId) = await PageAsync<T>(path, query, after, options, cancellationToken).ConfigureAwait(false);
            foreach (var item in page.Data) yield return item;
            if (!page.HasMore || page.Data.Count == 0 || lastId is null) yield break;
            after = lastId;
        }
    }

    private async Task<(Page<T>, string?)> PageAsync<T>(
        string path, object? query, string? startingAfter, RequestOptions? options, CancellationToken cancellationToken)
    {
        var pairs = QueryPairs(query);
        if (startingAfter is not null)
        {
            pairs.RemoveAll(p => p.Key == "starting_after");
            pairs.Add(new("starting_after", startingAfter));
        }
        var bytes = await SendAsync("GET", path, null, pairs, options, false, cancellationToken, expectJson: true).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(bytes);
        string? lastId = null;
        if (doc.RootElement.TryGetProperty("data", out var data) && data.GetArrayLength() > 0
            && data[data.GetArrayLength() - 1].TryGetProperty("id", out var id))
            lastId = id.GetString();
        return (JsonSerializer.Deserialize<Page<T>>(bytes, JsonOptions)!, lastId);
    }

    /// <summary>A query object's fields as name=value (an anonymous object, a dictionary or a Params class).</summary>
    private static List<KeyValuePair<string, string>> QueryPairs(object? query)
    {
        var pairs = new List<KeyValuePair<string, string>>();
        if (query is null) return pairs;
        if (query is List<KeyValuePair<string, string>> ready) return new(ready);
        var element = JsonSerializer.SerializeToElement(query, JsonOptions);
        if (element.ValueKind != JsonValueKind.Object) return pairs;
        foreach (var p in element.EnumerateObject())
        {
            var value = p.Value.ValueKind switch
            {
                JsonValueKind.String => p.Value.GetString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Number => p.Value.GetRawText(),
                JsonValueKind.Array => string.Join(",", p.Value.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText())),
                _ => null,
            };
            if (value is not null) pairs.Add(new(p.Name, value));
        }
        return pairs;
    }

    private static readonly Random Jitter = new();

    /// <summary>0.5 s, 1 s, 2 s… hasta 8 s, con algo de azar para no llegar todos juntos.</summary>
    private static TimeSpan Backoff(int attempt)
    {
        double factor;
        lock (Jitter) factor = 0.75 + Jitter.NextDouble() / 2;
        return TimeSpan.FromSeconds(Math.Min(8.0, 0.5 * Math.Pow(2, attempt)) * factor);
    }

    private async Task<byte[]> SendAsync(
        string method, string path, object? body, object? query, RequestOptions? options, bool idempotent, CancellationToken cancellationToken,
        bool expectJson = false)
    {
        // A proxy's path stays: https://proxy.example.com/fiscalisto + /v1/invoices.
        var url = new Uri(_options.BaseUrl.ToString().TrimEnd('/') + path);
        var pairs = QueryPairs(query);
        if (pairs.Count > 0)
            url = new Uri($"{url}?{string.Join("&", pairs.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"))}");
        var idempotencyKey = idempotent ? options?.IdempotencyKey ?? Guid.NewGuid().ToString() : null;
        var json = method == "POST" ? JsonSerializer.SerializeToUtf8Bytes(body ?? new { }, JsonOptions) : null;
        // Only what can't do something twice is repeated after a failure we can't see through:
        // reads, deletes and requests with an Idempotency-Key. A 429 was never processed.
        var safe = method is "GET" or "DELETE" || idempotencyKey is not null;
        var retries = options?.MaxRetries ?? _options.MaxRetries;
        var wait = options?.Timeout ?? _options.Timeout;

        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
            request.Headers.UserAgent.ParseAdd($"fiscalisto-dotnet/{Version}");
            request.Headers.Accept.ParseAdd("application/json");
            if (_options.ApiVersion is not null) request.Headers.Add("Fiscalisto-Version", _options.ApiVersion);
            if (_options.Language is not null) request.Headers.Add("Accept-Language", _options.Language);
            if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
            if (json is not null)
            {
                request.Content = new ByteArrayContent(json);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(wait);
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested && e is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                if (safe && attempt < retries)
                {
                    await Delay(Backoff(attempt), cancellationToken).ConfigureAwait(false);
                    continue;
                }
                var timedOut = e is TaskCanceledException or OperationCanceledException;
                var hint = idempotencyKey is not null
                    ? $" Repita con la misma Idempotency-Key ({idempotencyKey}) para saber si se timbró."
                    : "";
                throw new FiscalistoException(
                    "connection_error",
                    timedOut ? "timeout" : "connection_failed",
                    timedOut
                        ? $"La API no contestó en {wait.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s.{hint}"
                        : $"No se pudo conectar con la API: {e.Message}",
                    statusCode: null,
                    requestId: null,
                    raw: null,
                    idempotencyKey: idempotencyKey,
                    inner: e);
            }

            using (response)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                var requestId = response.Headers.TryGetValues("Request-Id", out var ids) ? ids.FirstOrDefault() : null;
                if (status is >= 200 and < 300)
                {
                    if (expectJson && !IsJson(bytes)) throw InvalidResponse(bytes, status, requestId);
                    return bytes;
                }
                JsonElement? error = null;
                try
                {
                    using var doc = JsonDocument.Parse(bytes);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var e)
                        && e.ValueKind == JsonValueKind.Object)
                        error = e.Clone();
                }
                catch (JsonException)
                {
                }
                var code = error is { } err && err.TryGetProperty("code", out var c) ? c.GetString() : null;
                var retryAfter = response.Headers.RetryAfter?.Delta
                    ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
                var retryable = status == 429 || code == "request_in_progress" || (safe && status >= 500);
                if (retryable && attempt < retries && !(retryAfter > TimeSpan.FromSeconds(60)))
                {
                    await Delay(retryAfter is { } ra && ra > TimeSpan.Zero ? ra : Backoff(attempt), cancellationToken).ConfigureAwait(false);
                    continue;
                }
                throw FiscalistoException.FromError(error, status, requestId);
            }
        }
    }

    private static bool IsJson(byte[] bytes)
    {
        var reader = new Utf8JsonReader(bytes);
        try
        {
            while (reader.Read())
            {
            }
            return reader.BytesConsumed > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>A success that isn't the API's JSON: a proxy's or a network portal's page answered instead.</summary>
    private static FiscalistoException InvalidResponse(byte[] bytes, int status, string? requestId)
    {
        var text = string.Join(" ", Encoding.UTF8.GetString(bytes).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length > 80) text = text[..80];
        return new FiscalistoException(
            "api_error",
            "invalid_response",
            $"La respuesta ({status}) no es de la API: {(text.Length > 0 ? text : "vacía")}. ¿Hay un proxy o un portal de por medio?",
            statusCode: status,
            requestId: requestId,
            raw: null,
            idempotencyKey: null,
            inner: null);
    }
}
