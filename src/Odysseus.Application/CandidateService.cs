namespace Odysseus.Application;

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Odysseus.Domain;
using Odysseus.Spec;

/// <summary>
/// Building a specification into a strategy that can be run.
/// </summary>
/// <remarks>
/// This is where a project starts spending: the specification was words, and a candidate is machine
/// time. The allowance is claimed before the build, and a specification that translates to a strategy
/// the project already has costs nothing at all — the same strategy written twice is one candidate, and
/// charging twice for it would let a search burn its budget going in circles.
/// </remarks>
public sealed class CandidateService
{
	private readonly IProjectStore _projects;
	private readonly ISpecStore _specs;
	private readonly ICandidateStore _candidates;
	private readonly IArtifactStore _artifacts;
	private readonly IStrategyBuilder _builder;
	private readonly IAuditLog _audit;
	private readonly IOperationLog _operations;
	private readonly IClock _clock;

	/// <summary>
	/// Creates the service.
	/// </summary>
	/// <param name="projects">Where projects are kept.</param>
	/// <param name="specs">Where specifications are kept.</param>
	/// <param name="candidates">Where candidates are kept.</param>
	/// <param name="artifacts">Where sources and assemblies are kept.</param>
	/// <param name="builder">What turns a specification into a compiled strategy.</param>
	/// <param name="audit">Where the permanent record is kept.</param>
	/// <param name="operations">Where operation keys are remembered.</param>
	/// <param name="clock">Source of the current moment.</param>
	public CandidateService(
		IProjectStore projects,
		ISpecStore specs,
		ICandidateStore candidates,
		IArtifactStore artifacts,
		IStrategyBuilder builder,
		IAuditLog audit,
		IOperationLog operations,
		IClock clock)
	{
		_projects = projects ?? throw new ArgumentNullException(nameof(projects));
		_specs = specs ?? throw new ArgumentNullException(nameof(specs));
		_candidates = candidates ?? throw new ArgumentNullException(nameof(candidates));
		_artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
		_builder = builder ?? throw new ArgumentNullException(nameof(builder));
		_audit = audit ?? throw new ArgumentNullException(nameof(audit));
		_operations = operations ?? throw new ArgumentNullException(nameof(operations));
		_clock = clock ?? throw new ArgumentNullException(nameof(clock));
	}

	/// <summary>
	/// Builds a specification into a candidate.
	/// </summary>
	/// <param name="project">Project the candidate belongs to.</param>
	/// <param name="spec">Specification to build.</param>
	/// <param name="operationKey">Key that makes a repeated call return the first result.</param>
	/// <param name="actor">Who is asking.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The candidate.</returns>
	public async ValueTask<Candidate> BuildAsync(
		ProjectId project,
		SpecId spec,
		string operationKey,
		Actors actor,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);

		var recorded = await _operations.TryGetAsync(project.Value, operationKey, cancellationToken);

		if (recorded is not null)
			return await _candidates.GetAsync(project, CandidateId.Parse(recorded), cancellationToken);

		var existing = await _projects.OpenAsync(project, cancellationToken);
		var revision = await _specs.GetAsync(project, spec, cancellationToken);
		var built = _builder.Build(SpecJson.Read(revision.Json));

		// Recognised before the allowance is touched: a specification reworded without changing what it
		// does keeps the identity it had, and that is one strategy, not two.
		var duplicate = await _candidates.FindBySourceAsync(project, built.SourceHash, cancellationToken);

		if (duplicate is not null)
		{
			await _operations.RecordAsync(project.Value, operationKey, duplicate.Id.Value, cancellationToken);

			return duplicate;
		}

		if (!await _projects.TryClaimAsync(project, backtests: 0, candidates: 1, cancellationToken))
		{
			throw new ResearchBudgetExhaustedException(
				$"This project has already built its {existing.Budget.MaxCandidates} candidates, or spent " +
				$"the {existing.Budget.MaxWallClock.TotalMinutes:0} minutes of machine time it was granted. " +
				"Read the results it has rather than adding another; a search that keeps generating past " +
				"its allowance is looking for a result rather than testing a hypothesis.");
		}

		await using var claim = new BudgetClaim(_projects, project, backtests: 0, candidates: 1);

		var now = _clock.UtcNow;
		var source = await _artifacts.PutAsync(project, Encoding.UTF8.GetBytes(built.Source), cancellationToken);
		var assembly = await _artifacts.PutAsync(project, built.Assembly, cancellationToken);

		var candidate = new Candidate(
			CandidateId.New(),
			spec,
			CandidateStatuses.Compiled,
			built.ClassName,
			built.SourceHash,
			built.AssemblyHash,
			source.Id,
			assembly.Id,
			built.TranslatorVersion,
			now,
			now);

		var claimed = await _operations.RecordAsync(project.Value, operationKey, candidate.Id.Value, cancellationToken);

		if (claimed != candidate.Id.Value)
			return await _candidates.GetAsync(project, CandidateId.Parse(claimed), cancellationToken);

		await _candidates.AddAsync(project, candidate, cancellationToken);

		claim.Keep();

		await _projects.UpdateAsync(existing with { UpdatedAt = now }, cancellationToken);

		await _audit.AppendAsync(
			project,
			AuditEventTypes.CandidateBuilt,
			actor,
			$"Built {built.ClassName} from specification revision {revision.Revision} with translator {built.TranslatorVersion}.",
			built.SourceHash,
			cancellationToken);

		return candidate;
	}

	/// <summary>
	/// Reads the generated source of a candidate.
	/// </summary>
	/// <param name="project">Project the candidate belongs to.</param>
	/// <param name="candidate">Candidate to read.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The source.</returns>
	public async ValueTask<string> ReadSourceAsync(
		ProjectId project,
		CandidateId candidate,
		CancellationToken cancellationToken)
	{
		var existing = await _candidates.GetAsync(project, candidate, cancellationToken);

		return Encoding.UTF8.GetString(await _artifacts.ReadAsync(project, existing.Source, cancellationToken));
	}

	/// <summary>
	/// Reads one candidate.
	/// </summary>
	/// <param name="project">Project the candidate belongs to.</param>
	/// <param name="candidate">Candidate to read.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The candidate.</returns>
	public ValueTask<Candidate> GetAsync(ProjectId project, CandidateId candidate, CancellationToken cancellationToken)
		=> _candidates.GetAsync(project, candidate, cancellationToken);

	/// <summary>
	/// Lists the candidates of a project, oldest first.
	/// </summary>
	/// <param name="project">Project to list.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The candidates.</returns>
	public ValueTask<IReadOnlyList<Candidate>> ListAsync(ProjectId project, CancellationToken cancellationToken)
		=> _candidates.ListAsync(project, cancellationToken);
}

/// <summary>
/// Thrown when a project has spent the allowance it was granted.
/// </summary>
public sealed class ResearchBudgetExhaustedException : Exception
{
	/// <summary>
	/// Creates the exception.
	/// </summary>
	/// <param name="message">What ran out, and what to do instead.</param>
	public ResearchBudgetExhaustedException(string message)
		: base(message)
	{
	}
}
