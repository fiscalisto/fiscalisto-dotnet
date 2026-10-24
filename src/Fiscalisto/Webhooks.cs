using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Fiscalisto;

/// <summary>El webhook no es de Fiscalisto: falta un encabezado, la hora o la firma no cuadran.</summary>
public sealed class FiscalistoWebhookException : Exception
{
    internal FiscalistoWebhookException(string message) : base(message) { }
}

/// <summary>
/// Verificar que un webhook lo mandó Fiscalisto (Standard Webhooks): la firma HMAC-SHA256 de
/// <c>webhook-id.webhook-timestamp.cuerpo</c> con el secreto whsec_ del endpoint.
/// </summary>
public sealed class FiscalistoWebhooks
{
    /// <summary>
    /// Revisa la firma y la hora de un webhook y le devuelve el evento. Pase el cuerpo exactamente
    /// como llegó, no el JSON ya interpretado: un espacio de diferencia cambia la firma.
    /// </summary>
    /// <param name="payload">El cuerpo tal como llegó.</param>
    /// <param name="header">Cómo leer un encabezado; en ASP.NET Core: <c>name =&gt; Request.Headers[name]</c>.</param>
    /// <param name="secret">El secreto whsec_ del endpoint.</param>
    /// <param name="tolerance">Cuánto puede diferir la hora del webhook (por omisión 5 minutos).</param>
    /// <param name="now">La hora actual (para pruebas).</param>
    /// <exception cref="FiscalistoWebhookException">No es de Fiscalisto.</exception>
    public Event Verify(byte[] payload, Func<string, string?> header, string secret, TimeSpan? tolerance = null, DateTimeOffset? now = null)
    {
        var id = header("webhook-id");
        var timestamp = header("webhook-timestamp");
        var signatures = header("webhook-signature");
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(timestamp) || string.IsNullOrEmpty(signatures))
            throw new FiscalistoWebhookException("Faltan los encabezados webhook-id, webhook-timestamp o webhook-signature.");
        var current = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        if (!long.TryParse(timestamp, out var sent) || Math.Abs(current - sent) > (tolerance ?? TimeSpan.FromMinutes(5)).TotalSeconds)
            throw new FiscalistoWebhookException("La hora del webhook está fuera de tolerancia: puede ser una repetición.");
        if (!secret.StartsWith("whsec_", StringComparison.Ordinal))
            throw new FiscalistoWebhookException("El secreto del endpoint empieza con whsec_.");
        byte[] key;
        try
        {
            key = Convert.FromBase64String(secret["whsec_".Length..]);
        }
        catch (FormatException)
        {
            throw new FiscalistoWebhookException("El secreto del endpoint no es válido.");
        }
        var signed = new List<byte>(Encoding.UTF8.GetBytes($"{id}.{timestamp}."));
        signed.AddRange(payload);
        var expected = HMACSHA256.HashData(key, signed.ToArray());
        // Several during a secret change, separated by spaces: any one valid is enough.
        foreach (var part in signatures.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var comma = part.IndexOf(',');
            if (comma < 0 || part[..comma] != "v1") continue;
            byte[] given;
            try
            {
                given = Convert.FromBase64String(part[(comma + 1)..]);
            }
            catch (FormatException)
            {
                continue;
            }
            if (CryptographicOperations.FixedTimeEquals(given, expected))
                return JsonSerializer.Deserialize<Event>(payload, FiscalistoClient.JsonOptions)!;
        }
        throw new FiscalistoWebhookException("La firma del webhook no coincide con el secreto.");
    }

    /// <summary>Igual, con el cuerpo como texto (UTF-8).</summary>
    public Event Verify(string payload, Func<string, string?> header, string secret, TimeSpan? tolerance = null, DateTimeOffset? now = null)
        => Verify(Encoding.UTF8.GetBytes(payload), header, secret, tolerance, now);

    /// <summary>Igual, con los encabezados en un diccionario (sin importar mayúsculas).</summary>
    public Event Verify(string payload, IReadOnlyDictionary<string, string> headers, string secret, TimeSpan? tolerance = null, DateTimeOffset? now = null)
        => Verify(Encoding.UTF8.GetBytes(payload), name =>
        {
            foreach (var (k, v) in headers)
                if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) return v;
            return null;
        }, secret, tolerance, now);
}
