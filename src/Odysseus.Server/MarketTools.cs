namespace StockSharp.Odysseus.Server;

/// <summary>
/// The tools that bring real history in and say what is in it.
/// </summary>
[McpServerToolType]
public static class MarketTools
{
	/// <summary>
	/// Downloads history into the shared storage and makes it the project's dataset.
	/// </summary>
	/// <param name="guard">Turns a failure into the published error shape.</param>
	/// <param name="service">History use cases.</param>
	/// <param name="projectId">Project to import into.</param>
	/// <param name="symbols">Symbols to download, separated by commas.</param>
	/// <param name="timeFrame">Length of one candle, such as 5m, 15m, 1h or 1d.</param>
	/// <param name="days">How many calendar days back to download.</param>
	/// <param name="operationKey">Key that makes a repeated call return the first result.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The description of what was imported.</returns>
	[McpServerTool(Name = "import_history")]
	[Description("Download real market history for one or more symbols into the market-data storage every " +
		"project shares, and make that range this project's dataset. The bars come from the bound broker, or " +
		"from the StockSharp storage server the operator named when there is one. Ask for enough days that " +
		"the development slice - the first three fifths - holds at least a few thousand bars, or the " +
		"measurements below it rest on too little.")]
	public static Task<object> ImportHistory(
		ToolGuard guard,
		HistoryService service,
		[Description("Identifier of the project.")] string projectId,
		[Description("Symbols to download, separated by commas, for example 'NVDA' or 'NVDA,AMD'.")] string symbols,
		[Description("Candle length: 1m, 5m, 15m, 1h or 1d.")] string timeFrame,
		[Description("How many calendar days back to download.")] int days,
		[Description("Caller-generated key identifying this request, reused on retries of the same intent.")] string operationKey,
		CancellationToken cancellationToken)
		=> guard.RunAsync(nameof(ImportHistory), async () =>
		{
			var to = DateTime.UtcNow.Date.AddDays(-1);
			var frame = ParseTimeFrame(timeFrame);

			var manifest = await service.ImportAsync(
				Ids.Project(projectId),
				[.. symbols.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
				frame,
				DateTime.SpecifyKind(to.AddDays(-Math.Abs(days)), DateTimeKind.Utc),
				DateTime.SpecifyKind(to, DateTimeKind.Utc),
				operationKey,
				Actors.Agent,
				cancellationToken);

			return new
			{
				datasetId = manifest.Id.Value,
				source = manifest.Source,
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
		});

	/// <summary>
	/// Measures what is in the data before anything is proposed for it.
	/// </summary>
	/// <param name="guard">Turns a failure into the published error shape.</param>
	/// <param name="service">History use cases.</param>
	/// <param name="projectId">Project to measure.</param>
	/// <param name="symbol">Symbol to measure, or empty for the first in the dataset.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>What was measured.</returns>
	[McpServerTool(Name = "analyze_market")]
	[Description("Measure what this instrument's history actually contains, before proposing anything for " +
		"it: how much it moves, whether moves continue or come back, what followed the events a strategy " +
		"would trade, and how the day is shaped. Read this first. A breakout hypothesis on an instrument " +
		"whose breakouts led nowhere costs a generation of candidates to disprove, and the numbers here " +
		"say so beforehand. Measured on the development slice only, so nothing here comes from data held " +
		"back for judging.")]
	public static Task<object> AnalyzeMarket(
		ToolGuard guard,
		HistoryService service,
		[Description("Identifier of the project.")] string projectId,
		[Description("Symbol to measure. Leave empty to measure the first symbol of the dataset.")] string symbol,
		CancellationToken cancellationToken)
		=> guard.RunAsync(nameof(AnalyzeMarket), async () =>
		{
			var profile = await service.AnalyseAsync(
				Ids.Project(projectId),
				string.IsNullOrWhiteSpace(symbol) ? null : symbol.Trim(),
				cancellationToken);

			return new
			{
				coverage = new
				{
					symbol = profile.Coverage.Symbol,
					bars = profile.Coverage.Bars,
					sessions = profile.Coverage.Sessions,
					from = profile.Coverage.From,
					to = profile.Coverage.To,
					medianVolume = profile.Coverage.MedianVolume,
					highVolumeBarsPercent = profile.Coverage.HighVolumeShare,
				},
				movement = new
				{
					medianBarRangePercent = profile.Movement.MedianBarRangePercent,
					upperDecileBarRangePercent = profile.Movement.UpperBarRangePercent,
					medianDailyRangePercent = profile.Movement.MedianDailyRangePercent,
					annualisedVolatilityPercent = profile.Movement.AnnualisedVolatilityPercent,
				},
				persistence = new
				{
					autocorrelationLag1 = profile.Persistence.Autocorrelation1,
					autocorrelationLag5 = profile.Persistence.Autocorrelation5,
					varianceRatio5 = profile.Persistence.VarianceRatio5,
					varianceRatio20 = profile.Persistence.VarianceRatio20,
					directionalDaysPercent = profile.Persistence.DirectionalDayShare,
					howToRead = "A variance ratio above one means moves extend, below one means they come " +
						"back, and near one means the instrument has no memory at that horizon.",
				},
				whatFollowedTheEvents = new
				{
					breakouts = profile.Edge.BreakoutCount,
					breakoutNextFiveBarsPercent = profile.Edge.BreakoutFollowThroughPercent,
					breakoutStillPositivePercent = profile.Edge.BreakoutPositiveShare,
					breakdowns = profile.Edge.BreakdownCount,
					breakdownNextFiveBarsPercent = profile.Edge.BreakdownFollowThroughPercent,
					stretchedBeyondTwoSigma = profile.Edge.StretchCount,
					reversionNextFiveBarsPercent = profile.Edge.StretchReversionPercent,
					howToRead = "These are the events themselves rather than a summary. Costs have not been " +
						"deducted, so an average move smaller than a spread is not an edge.",
				},
				session = profile.Session.Select(b => new
				{
					part = b.Bucket,
					shareOfRangePercent = b.ShareOfRange,
					shareOfVolumePercent = b.ShareOfVolume,
					averageReturnPercent = b.AverageReturnPercent,
				}).ToArray(),
				caveats = profile.Caveats,
			};
		});

	private static TimeSpan ParseTimeFrame(string value)
	{
		var text = (value ?? string.Empty).Trim().ToLowerInvariant();

		return text switch
		{
			"1m" => TimeSpan.FromMinutes(1),
			"5m" => TimeSpan.FromMinutes(5),
			"15m" => TimeSpan.FromMinutes(15),
			"1h" => TimeSpan.FromHours(1),
			"1d" => TimeSpan.FromDays(1),
			_ => throw new ArgumentException(
				$"'{value}' is not a candle length this server downloads. Use one of: 1m, 5m, 15m, 1h, 1d.",
				nameof(value)),
		};
	}
}
