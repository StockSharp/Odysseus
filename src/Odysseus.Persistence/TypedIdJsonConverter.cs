namespace Odysseus.Persistence;

using System.Reflection;
using System.Text.Json.Serialization;

/// <summary>
/// Writes a typed identifier as the text it stands for, and reads it back.
/// </summary>
/// <remarks>
/// Without this the serializer sees a struct with no public state — the value is behind a property that
/// throws when unassigned — and writes an empty object. Nothing fails at that point; the identifier
/// simply comes back as the default, and the loss only surfaces much later as a lookup that finds
/// nothing. The type is the same one the contracts declare, so the text written here is also what the
/// published schemas accept.
/// </remarks>
public sealed class TypedIdJsonConverter : JsonConverterFactory
{
	private static readonly Dictionary<Type, (MethodInfo Parse, PropertyInfo Value)> _known = [];
	private static readonly Lock _sync = new();

	/// <inheritdoc />
	public override bool CanConvert(Type typeToConvert)
	{
		ArgumentNullException.ThrowIfNull(typeToConvert);

		if (!typeToConvert.IsValueType || typeToConvert.Namespace != "Odysseus.Domain")
			return false;

		using (_sync.EnterScope())
		{
			if (_known.ContainsKey(typeToConvert))
				return true;

			var parse = typeToConvert.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, [typeof(string)]);
			var value = typeToConvert.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);

			if (parse is null || value is null || value.PropertyType != typeof(string))
				return false;

			_known.Add(typeToConvert, (parse, value));

			return true;
		}
	}

	/// <inheritdoc />
	public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
		=> (JsonConverter)Activator.CreateInstance(typeof(Converter<>).MakeGenericType(typeToConvert));

	private sealed class Converter<T> : JsonConverter<T>
		where T : struct
	{
		public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
		{
			var text = reader.GetString();

			if (string.IsNullOrEmpty(text))
				return default;

			using (_sync.EnterScope())
				return (T)_known[typeof(T)].Parse.Invoke(null, [text]);
		}

		public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
		{
			ArgumentNullException.ThrowIfNull(writer);

			writer.WriteStringValue(value.ToString());
		}
	}
}
