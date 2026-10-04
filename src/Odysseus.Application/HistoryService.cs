namespace Odysseus.Application;

using System.Linq;

/// <summary>
/// The price an instrument trades at now.
/// </summary>
public interface IQuoteSource
{
	/// <summary>
	/// Asks the broker for the instrument's current price.
	/// </summary>
	/// <param name="symbol">Symbol to price.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The last trade price, or the middle of the best bid and ask, or <see langword="null"/> when the broker quotes neither.</returns>
	Task<decimal?> GetPriceAsync(string symbol, CancellationToken cancellationToken);
}

/// <summary>
/// Downloads bars for a set of symbols.
/// </summary>
/// <remarks>
/// The broker sits behind this so that the rest of the product does not have to know which one it is,
/// and so that the download can be replaced by a fixture in a test without a network.
/// </remarks>
public interface IHistorySource
{
	/// <summary>
	/// Identity of the source, recorded in the dataset so a result says which connector its bars came
	/// through.
	/// </summary>
	/// <remarks>
	/// Recorded in every dataset imported through it, so a result can say where its bars came from.
	/// </remarks>
	string SourceName { get; }

	/// <summary>
	/// Downloads finished candles.
	/// </summary>
	/// <param name="symbol">Symbol to download.</param>
	/// <param name="timeFrame">Length of one candle.</param>
	/// <param name="from">Start of the range, in UTC.</param>
	/// <param name="to">End of the range, in UTC.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The candles, oldest first.</returns>
	Task<IReadOnlyList<Candle>> GetBarsAsync(
		string symbol,
		TimeSpan timeFrame,
		DateTime from,
		DateTime to,
		CancellationToken cancellationToken);
}

/// <summary>
/// Turning downloaded history into a project's dataset, and measuring what is in it.
/// </summary>
public sealed class HistoryService
{
	private readonly IProjectStore _projects;
	private readonly IDatasetStore _datasets;
	private readonly IHistorySource _source;
	private readonly IMarketProfiler _profiler;
	private readonly IAuditLog _audit;
	private readonly IOperationLog _operations;
	private readonly IClock _clock;

	/// <summary>
	/// Creates the service.
	/// </summary>
	/// <param name="projects">Where projects are kept.</param>
	/// <param name="datasets">Where datasets are kept.</param>
	/// <param name="source">Where history comes from.</param>
	/// <param name="profiler">What measures an instrument's history.</param>
	/// <param name="audit">Where the permanent record is kept.</param>
	/// <param name="operations">Where operation keys are remembered.</param>
	/// <param name="clock">Source of the current moment.</param>
	public HistoryService(
		IProjectStore projects,
		IDatasetStore datasets,
		IHistorySource source,
		IMarketProfiler profiler,
		IAuditLog audit,
		IOperationLog operations,
		IClock clock)
	{
		_projects = projects ?? throw new ArgumentNullException(nameof(projects));
		_datasets = datasets ?? throw new ArgumentNullException(nameof(datasets));
		_source = source ?? throw new ArgumentNullException(nameof(source));
		_profiler = profiler ?? throw new ArgumentNullException(nameof(profiler));
		_audit = audit ?? throw new ArgumentNullException(nameof(audit));
		_operations = operations ?? throw new ArgumentNullException(nameof(operations));
		_clock = clock ?? throw new ArgumentNullException(nameof(clock));
	}

	/// <summary>
	/// How long one symbol may take to download before the import gives up on it.
	/// </summary>
	/// <remarks>
	/// A broker that stops answering is indistinguishable from a slow one, and waiting on it forever
	/// leaves the caller with no data, no error and nothing to decide from. Ninety days of five-minute
	/// bars arrive in seconds, so minutes of silence mean something is wrong rather than large.
	/// </remarks>
	public TimeSpan Patience { get; init; } = TimeSpan.FromMinutes(3);

	/// <summary>
	/// Downloads history into the shared storage and makes it the project's dataset.
	/// </summary>
	/// <param name="project">Project to import into.</param>
	/// <param name="symbols">Symbols to download.</param>
	/// <param name="timeFrame">Length of one candle.</param>
	/// <param name="from">Start of the range, in UTC.</param>
	/// <param name="to">End of the range, in UTC.</param>
	/// <param name="operationKey">Key that makes a repeated call return the first result.</param>
	/// <param name="actor">Who is asking.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The description of what was imported.</returns>
	public async ValueTask<DatasetManifest> ImportAsync(
		ProjectId project,
		IReadOnlyList<string> symbols,
		TimeSpan timeFrame,
		DateTime from,
		DateTime to,
		string operationKey,
		Actors actor,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(symbols);
		ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);

		if (symbols.Count == 0)
			throw new ArgumentException("Name at least one symbol to download.", nameof(symbols));

		if (to <= from)
			throw new ArgumentException("The range ends before it starts.", nameof(to));

		var recorded = await _operations.TryGetAsync(project.Value, operationKey, cancellationToken);

		if (recorded is not null)
			return await _datasets.GetManifestAsync(project, DatasetId.Parse(recorded), cancellationToken);

		var existing = await _projects.OpenAsync(project, cancellationToken);
		var bars = new Dictionary<string, IReadOnlyList<Candle>>(StringComparer.Ordinal);

		foreach (var symbol in symbols.Distinct(StringComparer.Ordinal))
		{
			var downloaded = await DownloadAsync(symbol, timeFrame, from, to, cancellationToken);

			if (downloaded.Count == 0)
			{
				throw new ArgumentException(
					$"The broker returned no bars for '{symbol}' between {from:yyyy-MM-dd} and {to:yyyy-MM-dd}. " +
					"Check the symbol, and check that the range covers trading days.",
					nameof(symbols));
			}

			bars.Add(symbol, downloaded);
		}

		var imported = DatasetBuilder.Build(bars, timeFrame, _source.SourceName, isSynthetic: false);

		var claimed = await _operations.RecordAsync(project.Value, operationKey, imported.Manifest.Id.Value, cancellationToken);

		if (claimed != imported.Manifest.Id.Value)
			return await _datasets.GetManifestAsync(project, DatasetId.Parse(claimed), cancellationToken);

		await _datasets.SaveAsync(project, imported, cancellationToken);
		await _projects.UpdateAsync(existing.WithDataset(imported.Manifest.Id, _clock.UtcNow), cancellationToken);

		await _audit.AppendAsync(
			project,
			AuditEventTypes.DatasetImported,
			actor,
			$"Imported {string.Join(", ", bars.Keys)} at {timeFrame} from {_source.SourceName}, " +
			$"{imported.Manifest.Records} bars from {imported.Manifest.Split.From:yyyy-MM-dd} to {imported.Manifest.Split.To:yyyy-MM-dd}.",
			"-",
			cancellationToken);

		return imported.Manifest;
	}

	/// <summary>
	/// Measures what is in the data a project researches.
	/// </summary>
	/// <param name="project">Project to measure.</param>
	/// <param name="symbol">Symbol to measure, or null for the first one in the dataset.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>What was measured.</returns>
	/// <remarks>
	/// Only the development slice is read. Measuring the validation or the closed slice would mean
	/// forming hypotheses on the data reserved for judging them.
	/// </remarks>
	public async ValueTask<MarketProfile> AnalyseAsync(
		ProjectId project,
		string symbol,
		CancellationToken cancellationToken)
	{
		var existing = await _projects.OpenAsync(project, cancellationToken);

		if (existing.Dataset.IsEmpty)
		{
			throw new InvalidOperationException(
				"This project has no data yet, so there is nothing to measure. Import history first with " +
				"import_history, or import_demo_dataset to work without a broker account.");
		}

		var manifest = await _datasets.GetManifestAsync(project, existing.Dataset, cancellationToken);

		symbol ??= manifest.Symbols[0];

		if (!manifest.Symbols.Contains(symbol, StringComparer.Ordinal))
		{
			throw new ArgumentException(
				$"This project holds no '{symbol}'. It covers {string.Join(", ", manifest.Symbols)}.",
				nameof(symbol));
		}

		var development = await _datasets.LoadAsync(
			project, existing.Dataset, symbol, DataSlices.Development, cancellationToken);

		return _profiler.Measure(symbol, development);
	}

	private async ValueTask<IReadOnlyList<Candle>> DownloadAsync(
		string symbol,
		TimeSpan timeFrame,
		DateTime from,
		DateTime to,
		CancellationToken cancellationToken)
	{
		using var patience = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

		patience.CancelAfter(Patience);

		try
		{
			return await _source.GetBarsAsync(symbol, timeFrame, from, to, patience.Token);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			throw new TimeoutException(
				$"The broker stopped answering while downloading '{symbol}', and nothing more arrived " +
				$"within {Patience.TotalMinutes:0.#} minutes. Nothing was imported; ask again.");
		}
	}
}
