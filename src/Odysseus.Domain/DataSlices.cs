namespace StockSharp.Odysseus.Domain;

/// <summary>
/// Parts a dataset is divided into, by time and never by sampling.
/// </summary>
public enum DataSlices
{
	/// <summary>Where hypotheses are formed and parameters are searched.</summary>
	Development,

	/// <summary>Where a candidate is checked against data it was not tuned on.</summary>
	Validation,

	/// <summary>
	/// Held back and used once, for the finalist only. No tool hands out its bars before then.
	/// </summary>
	Final,
}
