using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fiscalisto;

/// <summary>
/// Un campo que acepta un id o el objeto completo, como <c>customer</c> al facturar: el id de un
/// cliente guardado (<c>"cus_…"</c>) o sus datos. Asígnele cualquiera de los dos.
/// </summary>
/// <example><code>Customer = "cus_OufpKqnrvx9p1"</code> o <code>Customer = new InvoiceCreateCustomer { … }</code></example>
public readonly struct StringOr<T> where T : class
{
    /// <summary>El id: el que se dio o llegó solo, o el <c>id</c> del objeto que llegó expandido.</summary>
    public string? Id { get; }

    /// <summary>El objeto, si se dio el objeto o llegó expandido (expand[]); null si es solo el id.</summary>
    public T? Value { get; }

    /// <summary>true si trae el objeto completo, no solo su id.</summary>
    public bool IsExpanded => Value is not null;

    /// <summary>Un id.</summary>
    public StringOr(string id) { Id = id; Value = null; }

    /// <summary>El objeto completo.</summary>
    public StringOr(T value) { Id = null; Value = value; }

    internal StringOr(string? id, T value) { Id = id; Value = value; }

    /// <summary>Un id.</summary>
    public static implicit operator StringOr<T>(string id) => new(id);

    /// <summary>El objeto completo.</summary>
    public static implicit operator StringOr<T>(T value) => new(value);
}

internal sealed class StringOrConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(StringOr<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(StringOrConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;
}

internal sealed class StringOrConverter<T> : JsonConverter<StringOr<T>> where T : class
{
    public override StringOr<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String
            ? new StringOr<T>(reader.GetString()!)
            : Expanded(JsonSerializer.Deserialize<JsonElement>(ref reader, options), options);

    // An expanded object: its "id" read too, so Id answers the same with or without expand[].
    private static StringOr<T> Expanded(JsonElement element, JsonSerializerOptions options) =>
        new(
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null,
            element.Deserialize<T>(options)!);

    public override void Write(Utf8JsonWriter writer, StringOr<T> value, JsonSerializerOptions options)
    {
        if (value.Value is not null) JsonSerializer.Serialize(writer, value.Value, options);
        else if (value.Id is not null) writer.WriteStringValue(value.Id);
        else writer.WriteNullValue();
    }
}
