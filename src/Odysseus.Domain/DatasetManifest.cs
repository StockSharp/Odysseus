namespace Odysseus.Domain;

using System.Collections.Generic;
using System.Linq;

/// <summary>
/// What a project's research runs against: which symbols, at which candle length, over which range, and
/// how that range is divided.
/// </summary>
/// <remarks>
/// The bars themselves are not part of it. They live in the market-data storage every project shares, and
/// a dataset is the range read out of it.
/// </remarks>
public sealed record DatasetManifest
{
	/// <summary>Identity of the dataset.</summary>
	public required DatasetId Id { get; init; }

	/// <summary>Symbols the data covers.</summary>
	public required IReadOnlyList<string> Symbols { get; init; }

	/// <summary>Length of one candle.</summary>
	public required TimeSpan TimeFrame { get; init; }

	/// <summary>Identity of the source the bars came through, for example a connector or the bundled demo set.</summary>
	public required string Source { get; init; }

	/// <summary>
	/// Whether the data was generated rather than observed.
	/// </summary>
	/// <remarks>
	/// Generated data is for proving that the machinery runs, never for judging a strategy, and every
	/// report over such a dataset has to say so. Recording it here rather than in a comment is what makes
	/// that possible.
	/// </remarks>
	public required bool IsSynthetic { get; init; }

	/// <summary>What was found in the bars of each symbol, over the whole range.</summary>
	public required IReadOnlyList<SymbolQuality> Quality { get; init; }

	/// <summary>How the range is divided in time.</summary>
	public required DatasetSplit Split { get; init; }

	/// <summary>Total bars, across every symbol.</summary>
	public int Records => Quality.Sum(q => q.Records);
}
