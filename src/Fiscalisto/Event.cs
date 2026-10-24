using System.Text.Json;

namespace Fiscalisto;

public sealed partial class Event
{
    /// <summary>
    /// <c>data.object</c> como el objeto del evento: <c>Invoice</c> en invoice.*, <c>Retention</c>
    /// en retention.*, <c>InvoiceBatch</c> en invoice_batch.*, <c>CustomerDataRequest</c> o <c>Issuer</c>.
    /// </summary>
    /// <example><code>if (evt.Type == "invoice.stamped") { var invoice = evt.DataObject&lt;Invoice&gt;(); }</code></example>
    public T DataObject<T>() =>
        Data?.Object is { } obj
            ? JsonSerializer.SerializeToElement(obj, FiscalistoClient.JsonOptions).Deserialize<T>(FiscalistoClient.JsonOptions)!
            : throw new JsonException("El evento no trae data.object.");
}
