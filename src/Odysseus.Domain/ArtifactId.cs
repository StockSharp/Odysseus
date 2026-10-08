namespace StockSharp.Odysseus.Domain;

using System.Buffers.Text;
using System.Security.Cryptography;

/// <summary>
/// Identifies one stored artifact by its content.
/// </summary>
/// <remarks>
/// Unlike every other identifier in the domain this one is never issued: it is derived, so identical
/// content is always the same artifact and a changed byte is always a different one. The value carries
/// the whole SHA-256 rather than a shortening of it, encoded so that it fits the sixty-four characters
/// the contracts allow for an identifier and uses only the characters they permit.
/// </remarks>
public readonly record struct ArtifactId
{
	/// <summary>Prefix every value of this kind carries.</summary>
	public const string Prefix = "art";

	private const int HashSize = 32;

	private readonly string _value;

	private ArtifactId(string value)
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
		=> _value ?? throw new InvalidOperationException($"An {nameof(ArtifactId)} was read before it was assigned.");

	/// <summary>
	/// The content hash this identifier stands for, in lowercase hexadecimal, as the reports quote it.
	/// </summary>
	public string Sha256 => Convert.ToHexStringLower(Hash);

	/// <summary>The raw content hash.</summary>
	public byte[] Hash
	{
		get
		{
			var encoded = Value[(Prefix.Length + 1)..];

			return Base64Url.DecodeFromChars(encoded);
		}
	}

	/// <summary>
	/// Derives the identifier of the given content.
	/// </summary>
	/// <param name="content">Content to identify.</param>
	/// <returns>The identifier.</returns>
	public static ArtifactId FromContent(ReadOnlySpan<byte> content)
		=> FromHash(SHA256.HashData(content));

	/// <summary>
	/// Builds the identifier from an already computed content hash.
	/// </summary>
	/// <param name="hash">A SHA-256 hash.</param>
	/// <returns>The identifier.</returns>
	/// <exception cref="ArgumentException">The hash is not the size of a SHA-256.</exception>
	public static ArtifactId FromHash(ReadOnlySpan<byte> hash)
	{
		if (hash.Length != HashSize)
			throw new ArgumentException($"A content hash is {HashSize} bytes, not {hash.Length}.", nameof(hash));

		return new($"{Prefix}_{Base64Url.EncodeToString(hash)}");
	}

	/// <summary>
	/// Parses identifier text.
	/// </summary>
	/// <param name="value">Text to parse.</param>
	/// <returns>The identifier.</returns>
	/// <exception cref="FormatException">The text is not an artifact identifier.</exception>
	public static ArtifactId Parse(string value)
	{
		if (!TryParse(value, out var id))
			throw new FormatException($"'{value}' is not an {nameof(ArtifactId)}; expected {Prefix}_ followed by an encoded SHA-256.");

		return id;
	}

	/// <summary>
	/// Tries to parse identifier text.
	/// </summary>
	/// <param name="value">Text to parse.</param>
	/// <param name="id">The identifier when the text is well formed.</param>
	/// <returns><see langword="true"/> when the text was parsed.</returns>
	public static bool TryParse(string value, out ArtifactId id)
	{
		id = default;

		if (!TypedId.IsValid(value, Prefix))
			return false;

		Span<byte> hash = stackalloc byte[HashSize];

		if (!Base64Url.TryDecodeFromChars(value.AsSpan(Prefix.Length + 1), hash, out var written) || written != HashSize)
			return false;

		id = new(value);
		return true;
	}

	/// <inheritdoc />
	public override string ToString()
		=> _value ?? string.Empty;
}
