namespace Odysseus.Domain;

/// <summary>
/// One strategy built from one specification.
/// </summary>
/// <param name="Id">Identity of the candidate.</param>
/// <param name="Spec">Specification revision it was built from.</param>
/// <param name="Status">Where it has reached in its lifecycle.</param>
/// <param name="ClassName">Name of the generated strategy class.</param>
/// <param name="SourceHash">
/// Hash of the generated source without the name and the thesis, which is what makes two candidates the
/// same: they do the same thing, however differently they are worded.
/// </param>
/// <param name="AssemblyHash">Hash of the compiled assembly.</param>
/// <param name="Source">Artifact holding the generated C#.</param>
/// <param name="Assembly">Artifact holding the compiled assembly.</param>
/// <param name="TranslatorVersion">Version of the translator that produced the source.</param>
/// <param name="CreatedAt">When it was built, in UTC.</param>
/// <param name="UpdatedAt">When its status last changed, in UTC.</param>
/// <remarks>
/// The candidate is the thing every later result points at, so it carries the whole chain back to the
/// words it came from: which specification, which translator, which source, which assembly. A metric
/// whose candidate cannot be traced back that far is a number without a claim attached.
/// </remarks>
public sealed record Candidate(
	CandidateId Id,
	SpecId Spec,
	CandidateStatuses Status,
	string ClassName,
	string SourceHash,
	string AssemblyHash,
	ArtifactId Source,
	ArtifactId Assembly,
	string TranslatorVersion,
	DateTime CreatedAt,
	DateTime UpdatedAt)
{
	/// <summary>
	/// Moves the candidate to another status.
	/// </summary>
	/// <param name="status">Status to move to.</param>
	/// <param name="now">Moment of the move, in UTC.</param>
	/// <returns>The moved candidate.</returns>
	/// <exception cref="InvalidOperationException">The move is not part of the declared lifecycle.</exception>
	public Candidate WithStatus(CandidateStatuses status, DateTime now)
	{
		if (!CandidateLifecycle.CanTransition(Status, status))
			throw new InvalidOperationException($"A candidate cannot move from {Status} to {status}.");

		return this with { Status = status, UpdatedAt = now };
	}
}
