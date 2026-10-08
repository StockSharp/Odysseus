namespace StockSharp.Odysseus.Application;

using System.Linq;
using System.Text.Json;

using StockSharp.Odysseus.Spec;

/// <summary>
/// What checking a specification produced.
/// </summary>
/// <param name="IsValid">Whether it may be turned into code.</param>
/// <param name="Problems">Everything found wrong.</param>
/// <param name="RequiredWarmup">Candles the rules need before any of them can act.</param>
public sealed record SpecCheck(bool IsValid, IReadOnlyList<SpecProblem> Problems, int RequiredWarmup);

/// <summary>
/// Reading, checking and recording strategy specifications.
/// </summary>
public sealed class SpecService
{
	private readonly IProjectStore _projects;
	private readonly ISpecStore _specs;
	private readonly IAuditLog _audit;
	private readonly IOperationLog _operations;
	private readonly IClock _clock;

	/// <summary>
	/// Creates the service.
	/// </summary>
	/// <param name="projects">Where projects are kept.</param>
	/// <param name="specs">Where specifications are kept.</param>
	/// <param name="audit">Where the permanent record is kept.</param>
	/// <param name="operations">Where operation keys are remembered.</param>
	/// <param name="clock">Source of the current moment.</param>
	public SpecService(
		IProjectStore projects,
		ISpecStore specs,
		IAuditLog audit,
		IOperationLog operations,
		IClock clock)
	{
		_projects = projects ?? throw new ArgumentNullException(nameof(projects));
		_specs = specs ?? throw new ArgumentNullException(nameof(specs));
		_audit = audit ?? throw new ArgumentNullException(nameof(audit));
		_operations = operations ?? throw new ArgumentNullException(nameof(operations));
		_clock = clock ?? throw new ArgumentNullException(nameof(clock));
	}

	/// <summary>
	/// Checks a specification without recording it.
	/// </summary>
	/// <param name="json">The specification as text.</param>
	/// <returns>Everything found wrong.</returns>
	/// <remarks>
	/// Reading and checking are reported the same way on purpose. To the caller, a misspelled field and
	/// a missing exit are the same kind of problem — something to correct before proposing — and
	/// splitting them across an exception and a result list would only make that harder to act on.
	/// </remarks>
	public SpecCheck Check(string json)
	{
		StrategySpec spec;

		// Answered here rather than left to the reader, whose guard against a blank argument is the
		// ordinary one every public method has. An agent that sent nothing is drafting, not calling
		// wrongly, and it is owed the same problem list as an agent that sent something wrong.
		if (string.IsNullOrWhiteSpace(json))
		{
			return new(false,
				[new SpecProblem("(document)", "There is no specification here.", "Send the specification as a JSON document.")],
				0);
		}

		try
		{
			spec = SpecJson.Read(json);
		}
		catch (JsonException error)
		{
			return new(false,
				[new SpecProblem("(document)", error.Message, "Correct the document and send it again.")],
				0);
		}

		var validation = SpecValidator.Validate(spec);

		return new(validation.IsValid, validation.Problems, SpecValidator.RequiredWarmup(spec));
	}

	/// <summary>
	/// Records a specification as the project's next revision.
	/// </summary>
	/// <param name="project">Project the specification belongs to.</param>
	/// <param name="json">The specification as text.</param>
	/// <param name="operationKey">Key that makes a repeated call return the first result.</param>
	/// <param name="author">Who wrote it.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The recorded revision.</returns>
	/// <exception cref="InvalidSpecException">The specification would not survive translation.</exception>
	public async ValueTask<SpecRevision> ProposeAsync(
		ProjectId project,
		string json,
		string operationKey,
		Actors author,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);

		var recorded = await _operations.TryGetAsync(project.Value, operationKey, cancellationToken);

		if (recorded is not null)
			return await _specs.GetAsync(project, SpecId.Parse(recorded), cancellationToken);

		// Nothing invalid is ever recorded. A rejected specification is not part of the history of what
		// was tried, because nothing was tried: it could not have produced a candidate.
		var check = Check(json);

		if (!check.IsValid)
			throw new InvalidSpecException(check.Problems);

		var existing = await _projects.OpenAsync(project, cancellationToken);

		if (existing.Dataset.IsEmpty)
		{
			throw new InvalidOperationException(
				"This project has no data yet, so nothing proposed against it could be measured. " +
				"Import a dataset first, for example with import_demo_dataset.");
		}

		var revision = await _specs.AddAsync(project, json, author, _clock.UtcNow, cancellationToken);
		var claimed = await _operations.RecordAsync(project.Value, operationKey, revision.Id.Value, cancellationToken);

		if (claimed != revision.Id.Value)
			return await _specs.GetAsync(project, SpecId.Parse(claimed), cancellationToken);

		await _audit.AppendAsync(
			project,
			AuditEventTypes.SpecRevised,
			author,
			$"Recorded specification revision {revision.Revision}.",
			revision.Hash,
			cancellationToken);

		return revision;
	}

	/// <summary>
	/// Reads one recorded specification.
	/// </summary>
	/// <param name="project">Project the specification belongs to.</param>
	/// <param name="spec">Specification to read.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The revision.</returns>
	public ValueTask<SpecRevision> GetAsync(ProjectId project, SpecId spec, CancellationToken cancellationToken)
		=> _specs.GetAsync(project, spec, cancellationToken);

	/// <summary>
	/// Lists the specifications recorded for a project.
	/// </summary>
	/// <param name="project">Project to list.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The revisions, oldest first.</returns>
	public ValueTask<IReadOnlyList<SpecRevision>> ListAsync(ProjectId project, CancellationToken cancellationToken)
		=> _specs.ListAsync(project, cancellationToken);
}

/// <summary>
/// Thrown when a specification cannot be turned into code.
/// </summary>
public sealed class InvalidSpecException : Exception
{
	/// <summary>
	/// Creates the exception.
	/// </summary>
	/// <param name="problems">Everything found wrong.</param>
	public InvalidSpecException(IReadOnlyList<SpecProblem> problems)
		: base(Describe(problems))
	{
		Problems = problems;
	}

	/// <summary>Everything found wrong.</summary>
	public IReadOnlyList<SpecProblem> Problems { get; }

	private static string Describe(IReadOnlyList<SpecProblem> problems)
	{
		ArgumentNullException.ThrowIfNull(problems);

		// Every problem is listed, with its place and its remedy. A caller that has to make several
		// corrections should be able to make them all in one attempt rather than one per round.
		return $"The specification has {problems.Count} problem(s): " +
			string.Join("; ", problems.Select(p => $"{p.Path}: {p.Message} {p.Remedy}"));
	}
}
