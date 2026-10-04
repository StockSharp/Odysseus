namespace Odysseus.Domain;

/// <summary>
/// How a dataset is divided in time.
/// </summary>
/// <remarks>
/// The division is by time and never by sampling: a strategy is judged on what came after what it was
/// built on, which is the only order that exists when it runs for real.
/// </remarks>
public sealed record DatasetSplit
{
	/// <summary>Share of the range every dataset gives to development.</summary>
	public const double DevelopmentShare = 0.6;

	/// <summary>Share of the range every dataset gives to validation. The rest is closed.</summary>
	public const double ValidationShare = 0.2;

	/// <summary>Start of the whole dataset, in UTC.</summary>
	public required DateTime From { get; init; }

	/// <summary>End of the development slice, exclusive, in UTC.</summary>
	public required DateTime DevelopmentTo { get; init; }

	/// <summary>End of the validation slice, exclusive, in UTC.</summary>
	public required DateTime ValidationTo { get; init; }

	/// <summary>End of the whole dataset, exclusive, in UTC. Also the end of the closed slice.</summary>
	public required DateTime To { get; init; }

	/// <summary>
	/// Divides a range by the shares every dataset is divided by.
	/// </summary>
	/// <param name="from">Start of the data, in UTC.</param>
	/// <param name="to">End of the data, exclusive, in UTC.</param>
	/// <returns>The division.</returns>
	/// <exception cref="ArgumentException">The range is empty.</exception>
	public static DatasetSplit Divide(DateTime from, DateTime to)
		=> Create(from, to, DevelopmentShare, ValidationShare);

	/// <summary>
	/// Divides a range by time.
	/// </summary>
	/// <param name="from">Start of the data, in UTC.</param>
	/// <param name="to">End of the data, exclusive, in UTC.</param>
	/// <param name="developmentFraction">Share of the range used for development.</param>
	/// <param name="validationFraction">Share of the range used for validation.</param>
	/// <returns>The division.</returns>
	/// <exception cref="ArgumentException">The range is empty or the fractions leave no closed slice.</exception>
	public static DatasetSplit Create(DateTime from, DateTime to, double developmentFraction, double validationFraction)
	{
		if (from.Kind != DateTimeKind.Utc || to.Kind != DateTimeKind.Utc)
			throw new ArgumentException("A split is defined over UTC moments.", nameof(from));

		if (to <= from)
			throw new ArgumentException("A split needs a range that contains something.", nameof(to));

		if (developmentFraction <= 0 || validationFraction <= 0)
			throw new ArgumentOutOfRangeException(nameof(developmentFraction), "Every slice must contain something.");

		if (developmentFraction + validationFraction >= 1)
			throw new ArgumentOutOfRangeException(nameof(validationFraction),
				"The fractions leave nothing closed, and a split without a closed slice cannot answer the only question that matters.");

		var span = to - from;

		return new()
		{
			From = from,
			DevelopmentTo = from + span * developmentFraction,
			ValidationTo = from + span * (developmentFraction + validationFraction),
			To = to,
		};
	}

	/// <summary>
	/// Which slice a moment belongs to.
	/// </summary>
	/// <param name="moment">Moment to place, in UTC.</param>
	/// <returns>The slice.</returns>
	/// <exception cref="ArgumentOutOfRangeException">The moment lies outside the dataset.</exception>
	public DataSlices SliceOf(DateTime moment)
	{
		if (moment < From || moment >= To)
			throw new ArgumentOutOfRangeException(nameof(moment), moment, "The moment lies outside the dataset.");

		if (moment < DevelopmentTo)
			return DataSlices.Development;

		return moment < ValidationTo ? DataSlices.Validation : DataSlices.Final;
	}

	/// <summary>
	/// The bounds of a slice.
	/// </summary>
	/// <param name="slice">Slice to bound.</param>
	/// <returns>Start, and end exclusive, in UTC.</returns>
	public (DateTime From, DateTime To) BoundsOf(DataSlices slice)
		=> slice switch
		{
			DataSlices.Development => (From, DevelopmentTo),
			DataSlices.Validation => (DevelopmentTo, ValidationTo),
			DataSlices.Final => (ValidationTo, To),
			_ => throw new ArgumentOutOfRangeException(nameof(slice), slice, "Unknown slice."),
		};
}
