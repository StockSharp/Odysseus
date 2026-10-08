namespace StockSharp.Odysseus.Server;

/// <summary>
/// The tools that turn a recorded specification into something that can be run.
/// </summary>
[McpServerToolType]
public static class CandidateTools
{
	/// <summary>
	/// Builds a specification into a strategy.
	/// </summary>
	/// <param name="guard">Turns a failure into the published error shape.</param>
	/// <param name="service">Candidate use cases.</param>
	/// <param name="projectId">Project the candidate belongs to.</param>
	/// <param name="specId">Specification to build.</param>
	/// <param name="operationKey">Key that makes a repeated call return the first result.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The candidate.</returns>
	[McpServerTool(Name = "build_candidate")]
	[Description("Turn a recorded specification into a real StockSharp strategy and compile it. This is " +
		"where a project starts spending its allowance, so it is worth reading analyze_market first: a " +
		"hypothesis the data already contradicts costs a candidate to disprove. A specification reworded " +
		"without changing what it does builds to the same code and costs nothing — the same candidate is " +
		"returned. Nothing is run here; use run_backtest for that.")]
	public static Task<object> BuildCandidate(
		ToolGuard guard,
		CandidateService service,
		[Description("Identifier of the project.")] string projectId,
		[Description("Identifier of the specification to build, as returned by propose_spec.")] string specId,
		[Description("Caller-generated key identifying this request, reused on retries of the same intent.")] string operationKey,
		CancellationToken cancellationToken)
		=> guard.RunAsync(nameof(BuildCandidate), async () =>
		{
			var candidate = await service.BuildAsync(
				Ids.Project(projectId),
				Ids.Spec(specId),
				operationKey,
				Actors.Agent,
				cancellationToken);

			return Describe(candidate);
		});

	/// <summary>
	/// Reads the code a candidate compiled from.
	/// </summary>
	/// <param name="guard">Turns a failure into the published error shape.</param>
	/// <param name="service">Candidate use cases.</param>
	/// <param name="projectId">Project the candidate belongs to.</param>
	/// <param name="candidateId">Candidate to read.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The generated source.</returns>
	[McpServerTool(Name = "get_candidate_source")]
	[Description("Read the C# a candidate was compiled from. It is an ordinary StockSharp strategy: the " +
		"parameters it declares are the ones an optimiser may vary, and every rule carries the identifier " +
		"it has in the specification, so the two can be read side by side. This is also the file to hand " +
		"to anyone who asks what the strategy actually does.")]
	public static Task<object> GetCandidateSource(
		ToolGuard guard,
		CandidateService service,
		[Description("Identifier of the project.")] string projectId,
		[Description("Identifier of the candidate.")] string candidateId,
		CancellationToken cancellationToken)
		=> guard.RunAsync(nameof(GetCandidateSource), async () =>
		{
			var project = Ids.Project(projectId);
			var id = Ids.Candidate(candidateId);

			var candidate = await service.GetAsync(project, id, cancellationToken);

			return new
			{
				candidateId = candidate.Id.Value,
				className = candidate.ClassName,
				sourceHash = candidate.SourceHash,
				translatorVersion = candidate.TranslatorVersion,
				source = await service.ReadSourceAsync(project, id, cancellationToken),
			};
		});

	/// <summary>
	/// Lists the candidates of a project.
	/// </summary>
	/// <param name="guard">Turns a failure into the published error shape.</param>
	/// <param name="service">Candidate use cases.</param>
	/// <param name="projectId">Project to list.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The candidates, oldest first.</returns>
	[McpServerTool(Name = "list_candidates")]
	[Description("List the candidates a project has built, oldest first, with where each has reached in " +
		"its lifecycle. Read this before building another: the project's allowance is finite, and a " +
		"candidate that has already been measured is a hypothesis that has already been answered.")]
	public static Task<object> ListCandidates(
		ToolGuard guard,
		CandidateService service,
		[Description("Identifier of the project.")] string projectId,
		CancellationToken cancellationToken)
		=> guard.RunAsync(nameof(ListCandidates), async () =>
		{
			var candidates = await service.ListAsync(Ids.Project(projectId), cancellationToken);

			return new { candidates = candidates.Select(Describe).ToArray() };
		});

	/// <summary>
	/// Checks whether a candidate gives the same answer twice over the same data.
	/// </summary>
	/// <param name="guard">Turns a failure into the published error shape.</param>
	/// <param name="service">Determinism checks.</param>
	/// <param name="projectId">Project the candidate belongs to.</param>
	/// <param name="candidateId">Candidate to check.</param>
	/// <param name="symbol">Symbol to run, or empty for the first of the dataset.</param>
	/// <param name="operationKey">Key that makes a repeated call return the first result.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>What was found.</returns>
	[McpServerTool(Name = "check_determinism")]
	[Description("Check whether this candidate is a function of its data: the same history in, the same " +
		"trades out. Two checks. Its source is read for anything that differs between runs - the machine " +
		"clock, numbers nobody seeded, a string's per-process hash, the file system, the network, timing - " +
		"however it is reached. And it is run twice over the development slice and the two runs compared " +
		"trade by trade, which catches what the source does not show. Costs two backtests. A candidate that " +
		"fails either check produces numbers that cannot be arrived at twice, so nothing measured of it means " +
		"what it says.")]
	public static Task<object> CheckDeterminism(
		ToolGuard guard,
		DeterminismService service,
		[Description("Identifier of the project.")] string projectId,
		[Description("Identifier of the candidate.")] string candidateId,
		[Description("Symbol to run. Leave empty to run the first symbol of the dataset.")] string symbol,
		[Description("Caller-generated key identifying this request, reused on retries of the same intent.")] string operationKey,
		CancellationToken cancellationToken)
		=> guard.RunAsync(nameof(CheckDeterminism), async () =>
		{
			var report = await service.CheckAsync(
				Ids.Project(projectId), Ids.Candidate(candidateId), symbol, operationKey, Actors.Agent, cancellationToken);

			return new
			{
				candidateId,
				isDeterministic = report.Problems.Count == 0 && report.RunsMatched,
				wasAlreadyChecked = report.WasAlreadyChecked,
				source = report.Problems.Select(p => new { rule = p.Rule, line = p.Line, message = p.Message }).ToArray(),
				runs = new
				{
					matched = report.RunsMatched,
					firstTrades = report.FirstTrades,
					secondTrades = report.SecondTrades,
					firstDivergence = report.FirstDivergence,
				},
			};
		});

	private static object Describe(Candidate candidate)
		=> new
		{
			candidateId = candidate.Id.Value,
			specId = candidate.Spec.Value,
			status = candidate.Status.ToString(),
			className = candidate.ClassName,
			sourceHash = candidate.SourceHash,
			assemblyHash = candidate.AssemblyHash,
			translatorVersion = candidate.TranslatorVersion,
			createdAt = candidate.CreatedAt,
			updatedAt = candidate.UpdatedAt,
		};
}
