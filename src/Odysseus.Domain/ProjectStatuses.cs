namespace StockSharp.Odysseus.Domain;

/// <summary>
/// States a research project passes through.
/// </summary>
public enum ProjectStatuses
{
	/// <summary>Created; no dataset has been imported yet, so nothing can be measured.</summary>
	Draft,

	/// <summary>A dataset is imported and research can start.</summary>
	Ready,

	/// <summary>Research is under way.</summary>
	Researching,

	/// <summary>Research finished; what it found is on the record either way.</summary>
	Completed,

	/// <summary>Kept for the record and read-only.</summary>
	Archived,
}
