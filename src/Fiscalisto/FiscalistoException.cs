using System;
using System.Text.Json;

namespace Fiscalisto;

/// <summary>
/// Un error de la API (o de la conexión, con <see cref="Type"/> connection_error).
/// <see cref="Code"/> es estable: decida con él; <see cref="Exception.Message"/> es para mostrarlo a una persona.
/// </summary>
public sealed class FiscalistoException : Exception
{
    /// <summary>invalid_request_error, authentication_error, invoice_error, rate_limit_error, connection_error…</summary>
    public string Type { get; }

    /// <summary>El código del error (parameter_invalid, sat_rejected…); la lista está en la guía de errores.</summary>
    public string Code { get; }

    /// <summary>El campo que hay que corregir (items.0.unit_price).</summary>
    public string? Param { get; }

    /// <summary>El código del SAT o del PAC cuando ellos lo rechazaron (CFDI40145).</summary>
    public string? SatCode { get; }

    /// <summary>El comprobante rechazado (inv_…), cuando lo hay.</summary>
    public string? Invoice { get; }

    /// <summary>La página de la documentación de este error.</summary>
    public string? DocUrl { get; }

    /// <summary>Búsquelo en Desarrolladores › Solicitudes o mándelo a soporte.</summary>
    public string? RequestId { get; }

    /// <summary>El código HTTP (sin él, la solicitud no llegó).</summary>
    public int? StatusCode { get; }

    /// <summary>La Idempotency-Key con que se mandó, para repetir sin timbrar dos veces.</summary>
    public string? IdempotencyKey { get; }

    /// <summary>El objeto error tal como llegó.</summary>
    public JsonElement? Raw { get; }

    internal FiscalistoException(
        string type, string code, string message, int? statusCode, string? requestId, JsonElement? raw,
        string? idempotencyKey = null, string? param = null, string? satCode = null, string? invoice = null,
        string? docUrl = null, Exception? inner = null)
        : base(message, inner)
    {
        Type = type;
        Code = code;
        StatusCode = statusCode;
        RequestId = requestId;
        Raw = raw;
        IdempotencyKey = idempotencyKey;
        Param = param;
        SatCode = satCode;
        Invoice = invoice;
        DocUrl = docUrl;
    }

    internal static FiscalistoException FromError(JsonElement? error, int status, string? requestId)
    {
        string? Text(string name) =>
            error is { } e && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return new FiscalistoException(
            Text("type") ?? "api_error",
            Text("code") ?? "unknown",
            Text("message") ?? "Error de la API de Fiscalisto",
            status,
            Text("request_id") ?? requestId,
            error,
            param: Text("param"),
            satCode: Text("sat_code"),
            invoice: Text("invoice"),
            docUrl: Text("doc_url"));
    }

    /// <inheritdoc/>
    public override string ToString() => $"FiscalistoException(Code={Code}, StatusCode={StatusCode}): {Message}";
}
