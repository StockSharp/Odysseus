namespace StockSharp.Odysseus.Application;

/// <summary>
/// Thrown when a candidate that was asked for does not exist.
/// </summary>
public sealed class CandidateNotFoundException : Exception
{
	/// <summary>
	/// Creates the exception.
	/// </summary>
	/// <param name="id">Candidate that was not found.</param>
	public CandidateNotFoundException(CandidateId id)
		: base($"Candidate {id} does not exist in this project.")
	{
		Id = id;
	}

	/// <summary>Candidate that was not found.</summary>
	public CandidateId Id { get; }
}
