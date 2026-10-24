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
    /// <summary>El id, si se dio un id.</summary>
    public string? Id { get; }

    /// <summary>El objeto, si se dio el objeto.</summary>
    public T? Value { get; }

    /// <summary>Un id.</summary>
    public StringOr(string id) { Id = id; Value = null; }

    /// <summary>El objeto completo.</summary>
    public StringOr(T value) { Id = null; Value = value; }

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
            : new StringOr<T>(JsonSerializer.Deserialize<T>(ref reader, options)!);

    public override void Write(Utf8JsonWriter writer, StringOr<T> value, JsonSerializerOptions options)
    {
        if (value.Id is not null) writer.WriteStringValue(value.Id);
        else if (value.Value is not null) JsonSerializer.Serialize(writer, value.Value, options);
        else writer.WriteNullValue();
    }
}
