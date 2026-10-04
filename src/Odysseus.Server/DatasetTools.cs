namespace Odysseus.Server;

/// <summary>
/// The dataset tools an agent sees over MCP.
/// </summary>
[McpServerToolType]
public static class DatasetTools
{
	/// <summary>
	/// Imports the bundled generated dataset.
	/// </summary>
	/// <param name="guard">Turns a failure into the published error shape.</param>
	/// <param name="service">Dataset use cases.</param>
	/// <param name="projectId">Project to import into.</param>
	/// <param name="operationKey">Key that makes a repeated call return the first result.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The description of what was imported.</returns>
	[McpServerTool(Name = "import_demo_dataset")]
	[Description("Import the bundled dataset for this project, so the whole pipeline can run " +
		"without a broker account. These bars are generated, not observed: use them to prove the machinery " +
		"works, never to judge whether a strategy is any good. Every report over them says so.")]
	public static Task<object> ImportDemoDataset(
		ToolGuard guard,
		DatasetService service,
		[Description("Identifier of the project to import into.")] string projectId,
		[Description("Caller-generated key identifying this request, reused on retries of the same intent.")] string operationKey,
		CancellationToken cancellationToken)
		=> guard.RunAsync(nameof(ImportDemoDataset), async ()
			=> Describe(await service.ImportDemoAsync(Ids.Project(projectId), operationKey, Actors.Agent, cancellationToken)));

	/// <summary>
	/// Reports what is in the dataset a project works against.
	/// </summary>
	/// <param name="guard">Turns a failure into the published error shape.</param>
	/// <param name="service">Dataset use cases.</param>
	/// <param name="projectId">Project to read.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The description of the dataset.</returns>
	[McpServerTool(Name = "get_dataset_report")]
	[Description("Report the dataset this project researches: symbols, timeframe, range, how many bars each " +
		"symbol kept, and what was rejected — malformed bars, repeated moments, missing bars. Bad bars are " +
		"dropped rather than repaired, so a gap here is a gap in what any result can be based on.")]
	public static Task<object> GetDatasetReport(
		ToolGuard guard,
		DatasetService service,
		[Description("Identifier of the project whose dataset to describe.")] string projectId,
		CancellationToken cancellationToken)
		=> guard.RunAsync(nameof(GetDatasetReport), async ()
			=> Describe(await service.GetManifestAsync(Ids.Project(projectId), cancellationToken)));

	/// <summary>
	/// Describes how the data is divided in time.
	/// </summary>
	/// <param name="guard">Turns a failure into the published error shape.</param>
	/// <param name="service">Dataset use cases.</param>
	/// <param name="projectId">Project to read.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The division.</returns>
	[McpServerTool(Name = "describe_split")]
	[Description("Describe how the history is divided in time: development (the first three fifths), " +
		"validation (the next fifth) and the closed slice (the last fifth). Form hypotheses and search on " +
		"development, measure on validation through run_backtest. No tool hands out the closed slice's bars; " +
		"it is measured once, for the finalist only, and once spent it is spent for every project on this " +
		"machine that imports the same stretch of the same instrument.")]
	public static Task<object> DescribeSplit(
		ToolGuard guard,
		DatasetService service,
		[Description("Identifier of the project whose split to describe.")] string projectId,
		CancellationToken cancellationToken)
		=> guard.RunAsync(nameof(DescribeSplit), async () =>
		{
			var (split, spent) = await service.DescribeSplitAsync(Ids.Project(projectId), cancellationToken);

			return new
			{
				development = new { from = split.From, to = split.DevelopmentTo },
				validation = new { from = split.DevelopmentTo, to = split.ValidationTo },
				closed = new { from = split.ValidationTo, to = split.To, isSpent = spent },
			};
		});

	private static object Describe(DatasetManifest manifest)
		=> new
		{
			datasetId = manifest.Id.Value,
			source = manifest.Source,
			isSynthetic = manifest.IsSynthetic,
			warning = manifest.IsSynthetic
				? "These bars are generated. Results measured on them say nothing about any market."
				: null,
			symbols = manifest.Symbols,
			timeFrame = manifest.TimeFrame,
			from = manifest.Split.From,
			to = manifest.Split.To,
			records = manifest.Records,
			quality = manifest.Quality.Select(q => new
			{
				symbol = q.Symbol,
				records = q.Records,
				from = q.From,
				to = q.To,
				gaps = q.Gaps,
				duplicates = q.Duplicates,
				invalid = q.Invalid,
			}).ToArray(),
		};
}
