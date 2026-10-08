namespace StockSharp.Odysseus.Spec;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Reads and writes an expression tree, using a <c>kind</c> field to say what each node is.
/// </summary>
/// <remarks>
/// The names here are the vocabulary an agent writes in, so a wrong one is refused with the list of
/// what exists. Guessing — treating an unknown kind as something adjacent — would turn a clear refusal
/// into a strategy that quietly does something other than what was asked for.
/// </remarks>
public sealed class ExpressionJsonConverter : JsonConverter<Expression>
{
	private static readonly string[] _kinds =
	[
		"All", "Any", "Not", "Compare", "Add", "Subtract", "Multiply", "Divide", "Min", "Max",
		"Abs", "Field", "Parameter", "Constant", "Indicator", "Cross", "Position", "Session",
	];

	/// <inheritdoc />
	public override Expression Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
	{
		using var document = JsonDocument.ParseValue(ref reader);

		return Read(document.RootElement);
	}

	/// <inheritdoc />
	public override void Write(Utf8JsonWriter writer, Expression value, JsonSerializerOptions options)
	{
		ArgumentNullException.ThrowIfNull(writer);
		ArgumentNullException.ThrowIfNull(value);

		writer.WriteStartObject();

		switch (value)
		{
			case AllOf node:
				writer.WriteString("kind", "All");
				WriteList(writer, "conditions", node.Conditions, options);
				break;

			case AnyOf node:
				writer.WriteString("kind", "Any");
				WriteList(writer, "conditions", node.Conditions, options);
				break;

			case Not node:
				writer.WriteString("kind", "Not");
				writer.WritePropertyName("condition");
				Write(writer, node.Condition, options);
				break;

			case Compare node:
				writer.WriteString("kind", "Compare");
				writer.WritePropertyName("left");
				Write(writer, node.Left, options);
				writer.WriteString("operator", node.Operator.ToString());
				writer.WritePropertyName("right");
				Write(writer, node.Right, options);
				break;

			case Arithmetic node:
				writer.WriteString("kind", node.Operator.ToString());
				WriteList(writer, "operands", node.Operands, options);
				break;

			case AbsoluteValue node:
				writer.WriteString("kind", "Abs");
				writer.WritePropertyName("value");
				Write(writer, node.Value, options);
				break;

			case Field node:
				writer.WriteString("kind", "Field");
				writer.WriteString("field", node.Which.ToString());
				writer.WriteNumber("offset", node.Offset);
				break;

			case ParameterRef node:
				writer.WriteString("kind", "Parameter");
				writer.WriteString("name", node.Name);
				break;

			case Constant node:
				writer.WriteString("kind", "Constant");
				writer.WriteNumber("value", node.Value);
				break;

			case IndicatorRef node:
				writer.WriteString("kind", "Indicator");
				writer.WriteString("name", node.Name);
				writer.WritePropertyName("length");
				Write(writer, node.Length, options);

				if (node.Source is not null)
					writer.WriteString("source", node.Source.Value.ToString());

				writer.WriteNumber("offset", node.Offset);
				break;

			case Cross node:
				writer.WriteString("kind", "Cross");
				writer.WritePropertyName("left");
				Write(writer, node.Left, options);
				writer.WriteString("direction", node.Direction.ToString());
				writer.WritePropertyName("right");
				Write(writer, node.Right, options);
				break;

			case PositionIs node:
				writer.WriteString("kind", "Position");
				writer.WriteString("state", node.State.ToString());
				break;

			case SessionWindow node:
				writer.WriteString("kind", "Session");
				writer.WriteString("notBefore", node.NotBefore);
				writer.WriteString("notAfter", node.NotAfter);
				break;

			default:
				throw new JsonException($"There is no written form for {value.GetType().Name}.");
		}

		writer.WriteEndObject();
	}

	private static Expression Read(JsonElement element)
	{
		if (element.ValueKind != JsonValueKind.Object)
			throw new JsonException($"An expression is an object with a 'kind'; found {element.ValueKind}.");

		if (!element.TryGetProperty("kind", out var kindElement))
			throw new JsonException($"An expression needs a 'kind'. The kinds that exist are: {string.Join(", ", _kinds)}.");

		var kind = kindElement.GetString();

		return kind switch
		{
			"All" => new AllOf(ReadList(element, "conditions")),
			"Any" => new AnyOf(ReadList(element, "conditions")),
			"Not" => new Not(Read(Required(element, "condition", kind))),
			"Compare" => new Compare(
				Read(Required(element, "left", kind)),
				ReadEnum<ComparisonOperators>(element, "operator", kind),
				Read(Required(element, "right", kind))),
			"Add" or "Subtract" or "Multiply" or "Divide" or "Min" or "Max" => new Arithmetic(
				Enum.Parse<ArithmeticOperators>(kind),
				ReadList(element, "operands")),
			"Abs" => new AbsoluteValue(Read(Required(element, "value", kind))),
			"Field" => new Field(ReadEnum<CandleFields>(element, "field", kind), Offset(element)),
			"Parameter" => new ParameterRef(Text(element, "name", kind)),
			"Constant" => new Constant(Number(element, "value", kind)),
			"Indicator" => new IndicatorRef(
				Text(element, "name", kind),
				Read(Required(element, "length", kind)),
				element.TryGetProperty("source", out var source) && source.ValueKind != JsonValueKind.Null
					? ReadEnum<CandleFields>(element, "source", kind)
					: null,
				Offset(element)),
			"Cross" => new Cross(
				Read(Required(element, "left", kind)),
				ReadEnum<CrossDirections>(element, "direction", kind),
				Read(Required(element, "right", kind))),
			"Position" => new PositionIs(ReadEnum<PositionStates>(element, "state", kind)),
			"Session" => new SessionWindow(Text(element, "notBefore", kind), Text(element, "notAfter", kind)),
			_ => throw new JsonException(
				$"'{kind}' is not an expression kind. Use one of: {string.Join(", ", _kinds)}."),
		};
	}

	private static IReadOnlyList<Expression> ReadList(JsonElement element, string property)
	{
		if (!element.TryGetProperty(property, out var list) || list.ValueKind != JsonValueKind.Array)
			throw new JsonException($"'{element.GetProperty("kind").GetString()}' needs an array called '{property}'.");

		return [.. list.EnumerateArray().Select(Read)];
	}

	private static JsonElement Required(JsonElement element, string property, string kind)
		=> element.TryGetProperty(property, out var value)
			? value
			: throw new JsonException($"'{kind}' needs a '{property}'.");

	// Every one of these refuses in the shape the caller can act on rather than reading the value out of
	// the element directly. A JsonElement accessor thrown at the wrong kind raises InvalidOperationException
	// or FormatException, which travels past the one place that turns a bad document into a located problem
	// and reaches the agent as a failure of this server.
	private static string Text(JsonElement element, string property, string kind)
	{
		var value = Required(element, property, kind);

		return value.ValueKind == JsonValueKind.String
			? value.GetString()
			: throw new JsonException($"'{kind}' needs a '{property}' that is text; found {value.ValueKind}.");
	}

	private static decimal Number(JsonElement element, string property, string kind)
	{
		var value = Required(element, property, kind);

		if (value.ValueKind != JsonValueKind.Number)
			throw new JsonException($"'{kind}' needs a '{property}' that is a number; found {value.ValueKind}.");

		return value.TryGetDecimal(out var number)
			? number
			: throw new JsonException($"The '{property}' of '{kind}' is outside the range of numbers this server holds.");
	}

	private static int Offset(JsonElement element)
	{
		if (!element.TryGetProperty("offset", out var offset) || offset.ValueKind == JsonValueKind.Null)
			return 0;

		if (offset.ValueKind != JsonValueKind.Number || !offset.TryGetInt32(out var value))
			throw new JsonException($"An 'offset' is a whole number of candles counted back from the one being evaluated; found {offset}.");

		return value;
	}

	private static T ReadEnum<T>(JsonElement element, string property, string kind)
		where T : struct, Enum
	{
		var text = Text(element, property, kind);

		if (!Enum.TryParse<T>(text, ignoreCase: true, out var value) || !Enum.IsDefined(value))
		{
			throw new JsonException(
				$"'{text}' is not a valid '{property}' for '{kind}'. Use one of: {string.Join(", ", Enum.GetNames<T>())}.");
		}

		return value;
	}

	private static void WriteList(
		Utf8JsonWriter writer,
		string property,
		IReadOnlyList<Expression> items,
		JsonSerializerOptions options)
	{
		writer.WritePropertyName(property);
		writer.WriteStartArray();

		foreach (var item in items)
			new ExpressionJsonConverter().Write(writer, item, options);

		writer.WriteEndArray();
	}
}

/// <summary>
/// How a specification travels between an agent and the server.
/// </summary>
public static class SpecJson
{
	/// <summary>Settings every read and write of a specification uses.</summary>
	public static JsonSerializerOptions Options { get; } = Create();

	/// <summary>
	/// Reads a specification.
	/// </summary>
	/// <param name="json">Text the agent sent.</param>
	/// <returns>The specification.</returns>
	/// <exception cref="JsonException">The text is not a specification.</exception>
	public static StrategySpec Read(string json)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(json);

		return JsonSerializer.Deserialize<StrategySpec>(json, Options)
			?? throw new JsonException("The specification is empty.");
	}

	/// <summary>
	/// Writes a specification.
	/// </summary>
	/// <param name="spec">Specification to write.</param>
	/// <returns>The text.</returns>
	public static string Write(StrategySpec spec)
		=> JsonSerializer.Serialize(spec, Options);

	private static JsonSerializerOptions Create()
	{
		var options = new JsonSerializerOptions
		{
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			PropertyNameCaseInsensitive = true,
			WriteIndented = true,

			// An exit that is not written as a condition has no condition, and saying so with a null is
			// noise: it puts a member in the document that the published schema has no shape for, and
			// invites a reader to send it back.
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		};

		options.Converters.Add(new ExpressionJsonConverter());
		options.Converters.Add(new JsonStringEnumConverter());

		return options;
	}
}
