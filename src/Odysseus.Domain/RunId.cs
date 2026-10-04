namespace Odysseus.Domain;

using System;

/// <summary>
/// Identifies one execution of a candidate on one slice.
/// </summary>
public readonly record struct RunId
{
	/// <summary>Prefix every value of this kind carries.</summary>
	public const string Prefix = "run";

	private readonly string _value;

	private RunId(string value)
	{
		_value = value;
	}

	/// <summary>Whether this is the default value rather than an identifier.</summary>
	public bool IsEmpty => _value is null;

	/// <summary>
	/// The identifier text.
	/// </summary>
	/// <exception cref="InvalidOperationException">The value was never assigned.</exception>
	public string Value
		=> _value ?? throw new InvalidOperationException($"A {nameof(RunId)} was read before it was assigned.");

	/// <summary>
	/// Issues a new identifier.
	/// </summary>
	/// <returns>The new identifier.</returns>
	public static RunId New()
		=> new(TypedId.Issue(Prefix));

	/// <summary>
	/// Parses identifier text.
	/// </summary>
	/// <param name="value">Text to parse.</param>
	/// <returns>The identifier.</returns>
	/// <exception cref="FormatException">The text is not an identifier of this kind.</exception>
	public static RunId Parse(string value)
		=> new(TypedId.Ensure(value, Prefix, nameof(RunId)));

	/// <summary>
	/// Tries to parse identifier text.
	/// </summary>
	/// <param name="value">Text to parse.</param>
	/// <param name="id">The identifier when the text is well formed.</param>
	/// <returns><see langword="true"/> when the text was parsed.</returns>
	public static bool TryParse(string value, out RunId id)
	{
		if (!TypedId.IsValid(value, Prefix))
		{
			id = default;
			return false;
		}

		id = new(value);
		return true;
	}

	/// <inheritdoc />
	public override string ToString()
		=> _value ?? string.Empty;
}
