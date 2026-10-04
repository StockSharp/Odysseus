namespace Odysseus.Domain;

/// <summary>
/// One stretch of closed history that has been spent.
/// </summary>
/// <param name="Project">Project that spent it.</param>
/// <param name="Candidate">Candidate it was spent on.</param>
/// <param name="Symbol">Symbol it was measured on.</param>
/// <param name="TimeFrame">Length of one candle.</param>
/// <param name="From">Start of the closed stretch, in UTC.</param>
/// <param name="To">End of the closed stretch, exclusive, in UTC.</param>
/// <param name="SpentAt">When it was spent, in UTC.</param>
/// <remarks>
/// This is kept for the whole installation rather than for one project, and that is the entire point of
/// it. A closed slice belonging to a project is closed only until a second project imports the same
/// stretch of the same instrument: the answer is already known by then, and the second measurement is a
/// re-sit of an exam whose paper has been read.
/// </remarks>
public sealed record SpentWindow(
	ProjectId Project,
	CandidateId Candidate,
	string Symbol,
	TimeSpan TimeFrame,
	DateTime From,
	DateTime To,
	DateTime SpentAt)
{
	/// <summary>
	/// Whether this stretch shares any time with another on the same instrument.
	/// </summary>
	/// <param name="symbol">Symbol of the other stretch.</param>
	/// <param name="from">Start of the other stretch, in UTC.</param>
	/// <param name="to">End of the other stretch, exclusive, in UTC.</param>
	/// <returns>Whether the two overlap.</returns>
	/// <remarks>
	/// Overlap rather than equality. Importing one day more or one day less produces a different stretch
	/// of the same market, and treating those as two separate exams is the whole of the loophole.
	/// </remarks>
	public bool Overlaps(string symbol, DateTime from, DateTime to)
		=> string.Equals(Symbol, symbol, StringComparison.OrdinalIgnoreCase) && from < To && From < to;

	/// <summary>
	/// Whether this entry records exactly the stretch given, rather than one that merely touches it.
	/// </summary>
	/// <param name="symbol">Symbol of the other stretch.</param>
	/// <param name="from">Start of the other stretch, in UTC.</param>
	/// <param name="to">End of the other stretch, exclusive, in UTC.</param>
	/// <returns>Whether the two are the same stretch.</returns>
	/// <remarks>
	/// The strict counterpart of <see cref="Overlaps"/>, and the only test under which an entry may
	/// stand for the stretch being asked about rather than block it. Importing the data over another range
	/// moves the closed slice; an entry written before that describes a stretch that no longer exists, and
	/// reading it as this one would let a slice pass as already accounted for when nothing has.
	/// </remarks>
	public bool IsSameStretch(string symbol, DateTime from, DateTime to)
		=> string.Equals(Symbol, symbol, StringComparison.OrdinalIgnoreCase) && From == from && To == to;
}
