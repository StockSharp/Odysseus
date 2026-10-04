namespace Odysseus.Domain;

using System;
using System.Text.RegularExpressions;

/// <summary>
/// Shared rules for the typed identifiers the domain issues.
/// </summary>
/// <remarks>
/// The format is the one every contract schema declares, so an identifier the domain issues is always
/// one the published schemas accept. The prefix is what makes a mixed-up identifier catchable at the
/// boundary, where the compiler can no longer help.
/// </remarks>
public static partial class TypedId
{
	/// <summary>Longest identifier any contract accepts.</summary>
	public const int MaxLength = 64;

	/// <summary>
	/// Issues a new identifier of the given kind.
	/// </summary>
	/// <param name="prefix">Kind prefix, for example <c>prj</c>.</param>
	/// <returns>The identifier text.</returns>
	public static string Issue(string prefix)
		=> $"{prefix}_{Guid.NewGuid():n}";

	/// <summary>
	/// Checks that the text is a well-formed identifier of the given kind.
	/// </summary>
	/// <param name="value">Text to check.</param>
	/// <param name="prefix">Expected kind prefix.</param>
	/// <returns><see langword="true"/> when the text is usable as an identifier of that kind.</returns>
	public static bool IsValid(string value, string prefix)
	{
		if (string.IsNullOrEmpty(value) || value.Length > MaxLength)
			return false;

		if (!value.StartsWith(prefix, StringComparison.Ordinal) ||
			value.Length <= prefix.Length + 1 ||
			value[prefix.Length] != '_')
			return false;

		return ContractPattern().IsMatch(value);
	}

	/// <summary>
	/// Checks the text and reports why it is unusable.
	/// </summary>
	/// <param name="value">Text to check.</param>
	/// <param name="prefix">Expected kind prefix.</param>
	/// <param name="kind">Name of the identifier type, used in the message.</param>
	/// <returns>The text itself.</returns>
	/// <exception cref="FormatException">The text is not an identifier of that kind.</exception>
	public static string Ensure(string value, string prefix, string kind)
	{
		if (!IsValid(value, prefix))
			throw new FormatException($"'{value}' is not a {kind}; expected the form {prefix}_ followed by 32 hexadecimal digits.");

		return value;
	}

	[GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$")]
	private static partial Regex ContractPattern();
}
