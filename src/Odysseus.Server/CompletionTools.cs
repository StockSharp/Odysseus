namespace StockSharp.Odysseus.Server;

/// <summary>
/// The tool that records a conclusion, and the one that reads back what has been concluded.
/// </summary>
[McpServerToolType]
public static class CompletionTools
{
	/// <summary>
	/// Records that a candidate is finished and keeps it with everything behind it.
	/// </summary>
	/// <param name="guard">Turns a failure into the published error shape.</param>
	/// <param name="service">Completion use cases.</param>
	/// <param name="projectId">Project the candidate belongs to.</param>
	/// <param name="candidateId">Candidate that is finished.</param>
	/// <param name="notes">Why it is finished.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>What was kept and where.</returns>
	[McpServerTool(Name = "complete_strategy")]
	[Description("Declare a strategy finished and keep it. This is the one place a conclusion is " +
		"recorded, and the conclusion is yours: the server runs strategies and reports numbers, and " +
		"whether these numbers are worth trading is a judgement it never makes. Call it once you have " +
		"read the runs and decided. What gets written into the project folder is the generated C#, the " +
		"specification it was translated from, and one metadata file holding everything the server " +
		"knows — the instrument, the timeframe, the parameters it was run with, the dataset and " +
		"where its bars came from, how the data was divided, every completed run with its full metrics, both " +
		"measurements, and the reason you give here. Say that reason properly: it is the only part of " +
		"your decision the server can keep, and in six months it is the only thing that explains why " +
		"this strategy and not another. A candidate the closed data has never seen is refused.")]
	public static Task<object> CompleteStrategy(
		ToolGuard guard,
		CompletionService service,
		[Description("Identifier of the project.")] string projectId,
		[Description("Identifier of the candidate.")] string candidateId,
		[Description("Why you consider this strategy finished: what convinced you, and what you would still watch.")] string notes,
		CancellationToken cancellationToken)
		=> guard.RunAsync(nameof(CompleteStrategy), async () =>
		{
			var completion = await service.CompleteAsync(
				Ids.Project(projectId), Ids.Candidate(candidateId), notes, Actors.Agent, cancellationToken);

			var strategy = completion.Strategy;

			return new
			{
				candidateId = strategy.Candidate.Value,
				name = strategy.Name,
				className = strategy.ClassName,
				status = CandidateStatuses.Completed.ToString(),
				folder = completion.Folder,
				files = completion.Files,
				kept = Describe(strategy),
				onOpenData = MeasurementView.Of(strategy.OnOpenData),
				onClosedData = MeasurementView.Of(strategy.OnClosedData),
				note = "Kept. The folder holds the code, the specification and the numbers, and can be " +
					"opened without this server. Nothing further is measured against the closed slice.",
			};
		});

	/// <summary>
	/// Lists what a project has finished with.
	/// </summary>
	/// <param name="guard">Turns a failure into the published error shape.</param>
	/// <param name="service">Completion use cases.</param>
	/// <param name="projectId">Project to list.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The finished strategies.</returns>
	[McpServerTool(Name = "list_completed_strategies")]
	[Description("List the strategies this project has been finished with, oldest first, with what each " +
		"was measured on and why it was kept. Nothing is re-run and nothing is spent.")]
	public static Task<object> ListCompletedStrategies(
		ToolGuard guard,
		CompletionService service,
		[Description("Identifier of the project.")] string projectId,
		CancellationToken cancellationToken)
		=> guard.RunAsync(nameof(ListCompletedStrategies), async () =>
		{
			var project = Ids.Project(projectId);
			var found = await service.ListAsync(project, cancellationToken);

			return new
			{
				folder = service.FolderOf(project),
				completed = found.Select(Describe).ToArray(),
			};
		});

	private static object Describe(CompletedStrategy strategy)
		=> new
		{
			candidateId = strategy.Candidate.Value,
			specId = strategy.Spec.Value,
			specRevision = strategy.SpecRevision,
			name = strategy.Name,
			thesis = strategy.Thesis,
			className = strategy.ClassName,
			sourceHash = strategy.SourceHash,
			assemblyHash = strategy.AssemblyHash,
			translatorVersion = strategy.TranslatorVersion,
			symbol = strategy.Symbol,
			timeFrame = strategy.TimeFrame,
			parameters = strategy.Parameters,
			dataset = new
			{
				id = strategy.Dataset.Value,
				source = strategy.DatasetSource,
				isSynthetic = strategy.IsSyntheticData,
				from = strategy.Split.From,
				developmentTo = strategy.Split.DevelopmentTo,
				validationTo = strategy.Split.ValidationTo,
				to = strategy.Split.To,
			},
			runs = strategy.Runs.Select(r => new
			{
				runId = r.Id.Value,
				slice = r.Slice.ToString(),
				window = r.Window,
				symbol = r.Symbol,
				scenario = r.Scenario,
				parameters = r.Parameters,
				barsProcessed = r.BarsProcessed,
				metrics = MeasurementView.Slice(r.Metrics),
			}).ToArray(),
			timesMeasuredOnOpenData = strategy.TimesMeasuredOnOpenData,
			notes = strategy.Notes,
			completedBy = strategy.CompletedBy.ToString(),
			completedAt = strategy.CompletedAt,
		};
}
