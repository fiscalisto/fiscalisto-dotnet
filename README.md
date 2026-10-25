# Fiscalisto para .NET

El SDK de la [API de facturación de Fiscalisto](https://fiscalisto.com/docs/api): timbre CFDI 4.0 (facturas, pagos, notas de crédito, nómina, retenciones y lotes) desde C#.

- Una clase para lo que manda y para lo que recibe, con la descripción de cada campo y su nombre en el XML del SAT; `required` en lo obligatorio, para que el compilador le avise.
- `Idempotency-Key` automática en cada `POST`: un reintento nunca timbra, crea ni manda nada dos veces.
- Reintentos solo de lo que es seguro repetir.
- Listas que se recorren solas con `await foreach`.
- Webhooks verificados en una línea.
- Sin dependencias: .NET 8 o más reciente.

```bash
dotnet add package Fiscalisto
```

```csharp
using Fiscalisto;

var fiscalisto = new FiscalistoClient(); // la llave viene de FISCALISTO_API_KEY

var invoice = await fiscalisto.Invoices.CreateAsync(new InvoiceCreate
{
    Customer = new InvoiceCreateCustomer { TaxId = "ICV060329BY0", LegalName = "INMOBILIARIA CVA", TaxSystem = "601", Zip = "33826" },
    PaymentForm = "03",
    Items = new() { new() { Description = "Mensualidad", ProductKey = "86121700", Quantity = 2, UnitPrice = 2500 } },
}, new RequestOptions { IdempotencyKey = "pedido-A-1042" });

Console.WriteLine($"{invoice.Uuid} {invoice.Total}");
```

La guía completa está en [fiscalisto.com/docs/api/sdk](https://fiscalisto.com/docs/api/sdk) y cada página de la [referencia](https://fiscalisto.com/docs/api) trae su ejemplo con el SDK.

## Desarrollo

`src/Fiscalisto/Generated/` se genera desde la especificación OpenAPI con `bun run openapi` en la raíz del repositorio; no lo edite. Lo demás se escribe a mano.

```bash
dotnet test tests/Fiscalisto.Tests
dotnet pack src/Fiscalisto -c Release
```
