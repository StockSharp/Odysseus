namespace StockSharp.Odysseus.Application.Tests;

using System.Text.Json;
using System.Threading;

using StockSharp.Odysseus.Persistence;
using StockSharp.Odysseus.Platform;
using StockSharp.Odysseus.Spec;

/// <summary>
/// Declaring a piece of research finished and keeping it.
/// </summary>
/// <remarks>
/// The server measures and never decides. Calling a strategy done is the one judgement in the product,
/// and it is made by whoever is doing the research; this is what happens afterwards. What is kept has to
/// be enough to answer, months later, what the strategy was and what it actually did — the source, the
/// specification, the instrument, the numbers it was run with, the data it was run on and every run
/// behind it. A folder that holds only the source is a strategy nobody can check.
/// </remarks>
[TestClass]
public class CompletionServiceTests : OdysseusTestBase
{
	private static readonly DateTime _open = new(2026, 3, 2, 14, 30, 0, DateTimeKind.Utc);

	private string _root;
	private SqliteProjectStore _store;
	private SqliteOperationLog _operations;
	private SqliteClosedHistoryLedger _ledger;
	private FileArtifactStore _artifacts;
	private FileDatasetStore _datasets;
	private FileSpecStore _specs;
	private FileCompletedStore _completed;
	private BacktestService _backtests;
	private ProjectService _projects;
	private CandidateService _candidates;
	private EvaluationService _evaluations;
	private ClosedDataService _closed;
	private CompletionService _service;

	/// <summary>Builds the services over temporary storage.</summary>
	[TestInitialize]
	public void CreateService()
	{
		_root = Path.Combine(Path.GetTempPath(), "odysseus-tests", Guid.NewGuid().ToString("n"));
		_store = new SqliteProjectStore(_root);
		_operations = new SqliteOperationLog(_root);
		_ledger = new SqliteClosedHistoryLedger(_root);
		_artifacts = new FileArtifactStore(_root);
		_datasets = new FileDatasetStore(_root, new LocalBarStorage(Path.Combine(_root, "market-data")));
		_specs = new FileSpecStore(_root);
		_completed = new FileCompletedStore(_root);

		var clock = new SystemClock();
		var budget = new ResearchBudget(400, 40, TimeSpan.FromMinutes(45)).ToState();

		_projects = new ProjectService(_store, _store, _operations, clock, ServerModes.Local, budget);
		_candidates = new CandidateService(_store, _specs, _store, _artifacts, new Builder(), _store, _operations, clock);

		_backtests = new BacktestService(
			_store, _store, _specs, _datasets, _store, _artifacts, new Runner(), new StockSharpMarketProfiler(), _store, _operations, clock);

		_evaluations = new EvaluationService(_store, _store, _specs, _store, _backtests, _store, clock);

		_closed = new ClosedDataService(
			_store, _specs, _store, _store, _store, _datasets, _ledger, _backtests, _store, clock);

		_service = new CompletionService(
			_store, _specs, _store, _store, _store, _datasets, _artifacts, _completed, _store, clock);
	}

	/// <summary>Releases storage.</summary>
	[TestCleanup]
	public void DeleteService()
	{
		_store?.Dispose();
		_operations?.Dispose();
		_ledger?.Dispose();

		if (Directory.Exists(_root))
			Directory.Delete(_root, recursive: true);
	}

	/// <summary>
	/// What is kept is the whole of it: the code, the words it came from, the instrument, the numbers,
	/// the data and every run.
	/// </summary>
	[TestMethod]
	public async Task AFinishedStrategyIsKeptWithEverythingBehindIt()
	{
		var (project, candidate) = await MeasuredAsync();

		var completion = await _service.CompleteAsync(
			project, candidate, "Holds up on the closed slice and trades often enough to believe.",
			Actors.Agent, CancellationToken);

		IsTrue(Directory.Exists(completion.Folder), $"nothing was written to {completion.Folder}.");

		foreach (var name in completion.Files)
		{
			IsTrue(File.Exists(Path.Combine(completion.Folder, name)),
				$"'{name}' was reported as kept but is not there.");
		}

		var meta = Path.Combine(completion.Folder, "strategy.json");

		IsTrue(File.Exists(Path.Combine(completion.Folder, "Generated.cs")), "the strategy source was not kept.");
		IsTrue(File.Exists(Path.Combine(completion.Folder, "spec.json")), "the specification it came from was not kept.");
		IsTrue(File.Exists(meta), "the metadata was not kept.");

		var kept = JsonDocument.Parse(await File.ReadAllTextAsync(meta, CancellationToken)).RootElement;

		AreEqual("Generated", kept.GetProperty("className").GetString());
		AreEqual("NVDA", kept.GetProperty("symbol").GetString());

		var split = kept.GetProperty("split");

		IsTrue(split.GetProperty("to").GetDateTime() > split.GetProperty("from").GetDateTime(),
			"the range the strategy was measured on was not kept.");

		IsFalse(string.IsNullOrWhiteSpace(kept.GetProperty("datasetSource").GetString()),
			"where the bars came from was not kept.");

		IsTrue(kept.GetProperty("runs").GetArrayLength() >= 7,
			"the runs behind the result were not kept, so the numbers cannot be checked.");

		IsTrue(kept.TryGetProperty("onOpenData", out _) && kept.TryGetProperty("onClosedData", out _),
			"one of the two measurements is missing, and the gap between them is the point.");

		IsTrue(kept.GetProperty("notes").GetString().Contains("closed slice", StringComparison.Ordinal),
			"what the researcher said about it was not kept.");
	}

	/// <summary>The source kept is the source that was measured, byte for byte.</summary>
	[TestMethod]
	public async Task TheSourceKeptIsTheSourceThatWasMeasured()
	{
		var (project, candidate) = await MeasuredAsync();

		var completion = await _service.CompleteAsync(
			project, candidate, "Done.", Actors.Agent, CancellationToken);

		var built = await _store.GetAsync(project, candidate, CancellationToken);
		var expected = await _artifacts.ReadAsync(project, built.Source, CancellationToken);

		var kept = await File.ReadAllBytesAsync(
			Path.Combine(completion.Folder, "Generated.cs"), CancellationToken);

		IsTrue(expected.SequenceEqual(kept), "the file kept is not the source that was measured.");
	}

	/// <summary>Calling it finished moves the candidate, and the move is what the lifecycle records.</summary>
	[TestMethod]
	public async Task TheCandidateIsMarkedFinished()
	{
		var (project, candidate) = await MeasuredAsync();

		await _service.CompleteAsync(project, candidate, "Done.", Actors.Agent, CancellationToken);

		var built = await _store.GetAsync(project, candidate, CancellationToken);

		AreEqual(CandidateStatuses.Completed, built.Status);
	}

	/// <summary>
	/// A candidate the closed data has never seen cannot be called finished. That measurement is the
	/// only one nobody could have tuned to, and a conclusion drawn without it is a conclusion about a
	/// search.
	/// </summary>
	[TestMethod]
	public async Task ACandidateTheClosedDataNeverSawIsRefused()
	{
		var (project, candidate) = await MeasuredAsync(closed: false);

		var refusal = await ThrowsAsync<InvalidOperationException>(
			() => _service.CompleteAsync(project, candidate, "Done.", Actors.Agent, CancellationToken).AsTask());

		IsTrue(refusal.Message.Contains("measure_on_closed_data", StringComparison.Ordinal),
			$"the refusal does not say what is missing: {refusal.Message}");
	}

	/// <summary>
	/// Saying why is not optional. The one judgement in the product is made outside the server, so the
	/// reason for it is the only part of it the server can keep.
	/// </summary>
	[TestMethod]
	public async Task TheReasonIsRequired()
	{
		var (project, candidate) = await MeasuredAsync();

		await ThrowsAsync<ArgumentException>(
			() => _service.CompleteAsync(project, candidate, "   ", Actors.Agent, CancellationToken).AsTask());
	}

	/// <summary>A conclusion is drawn once; drawing it again would rewrite it.</summary>
	[TestMethod]
	public async Task AStrategyIsFinishedOnlyOnce()
	{
		var (project, candidate) = await MeasuredAsync();

		await _service.CompleteAsync(project, candidate, "Done.", Actors.Agent, CancellationToken);

		var refusal = await ThrowsAsync<InvalidOperationException>(
			() => _service.CompleteAsync(project, candidate, "Done again.", Actors.Agent, CancellationToken).AsTask());

		IsTrue(refusal.Message.Contains("already", StringComparison.Ordinal),
			$"the refusal does not say it was already finished: {refusal.Message}");
	}

	/// <summary>What has been finished can be listed back without re-running anything.</summary>
	[TestMethod]
	public async Task FinishedStrategiesCanBeListed()
	{
		var (project, candidate) = await MeasuredAsync();

		await _service.CompleteAsync(project, candidate, "Done.", Actors.Agent, CancellationToken);

		var listed = await _service.ListAsync(project, CancellationToken);

		AreEqual(1, listed.Count);
		AreEqual(candidate, listed[0].Candidate);
		AreEqual("NVDA", listed[0].Symbol);
	}

	/// <summary>Enough bars for a split with something in every slice.</summary>
	private static IReadOnlyList<Candle> Bars()
	{
		var bars = new List<Candle>();
		var time = _open;

		for (var i = 0; i < 3_000; i++)
		{
			var wave = (decimal)Math.Sin(i * 2 * Math.PI / 60);
			var close = 100m + Math.Round(5m * wave, 2);
			var open = bars.Count == 0 ? close : bars[^1].Close;

			bars.Add(new(time, open, Math.Max(open, close) + 0.05m, Math.Min(open, close) - 0.05m, close, 10_000m));

			time = time.AddMinutes(5);

			if (time.TimeOfDay >= TimeSpan.FromHours(21))
				time = time.Date.AddDays(time.DayOfWeek == DayOfWeek.Friday ? 3 : 1).Add(_open.TimeOfDay);
		}

		return bars;
	}

	private static string Spec(string name)
		=> $$"""
		{
		  "name": "Above its average {{name}}",
		  "thesis": "A price above its own recent average keeps going for a few bars.",
		  "allowLong": true,
		  "allowShort": false,
		  "timeFrame": "00:05:00",
		  "warmupBars": 45,
		  "entries": [
		    { "id": "e1", "direction": "Long", "condition": {
		        "kind": "Compare",
		        "left": { "kind": "Field", "field": "Close" },
		        "operator": "GreaterThan",
		        "right": { "kind": "Indicator", "name": "sma", "length": { "kind": "Constant", "value": 20 }, "source": "Close" } } }
		  ],
		  "exits": [ { "id": "x1", "kind": "TimeExit", "direction": "Long", "length": { "kind": "Constant", "value": 5 } } ],
		  "parameters": [],
		  "risk": { "maxPositionPercent": 0.10, "maxDailyLossPercent": 0.02 }
		}
		""";

	/// <summary>A project holding a candidate measured on the open data and, by default, the closed.</summary>
	private async Task<(ProjectId Project, CandidateId Candidate)> MeasuredAsync(bool closed = true)
	{
		var created = await _projects.CreateProjectAsync(
			"completion", Guid.NewGuid().ToString("n"), Actors.User, CancellationToken);

		var project = created.Id;

		var bars = new Dictionary<string, IReadOnlyList<Candle>> { ["NVDA"] = Bars() };
		var imported = DatasetBuilder.Build(bars, TimeSpan.FromMinutes(5), "test", isSynthetic: false);

		await _datasets.SaveAsync(project, imported, CancellationToken);

		var opened = await _store.OpenAsync(project, CancellationToken);

		await _store.UpdateAsync(opened.WithDataset(imported.Manifest.Id, DateTime.UtcNow), CancellationToken);

		var name = Guid.NewGuid().ToString("n")[..8];
		var spec = await _specs.AddAsync(project, Spec(name), Actors.Agent, DateTime.UtcNow, CancellationToken);
		var built = await _candidates.BuildAsync(project, spec.Id, Guid.NewGuid().ToString("n"), Actors.Agent, CancellationToken);

		await _evaluations.MeasureAsync(
			project, built.Id, "NVDA", null, Guid.NewGuid().ToString("n"), Actors.Agent, CancellationToken);

		if (closed)
		{
			await _closed.MeasureAsync(
				project, built.Id, Guid.NewGuid().ToString("n"), Actors.Agent, CancellationToken);
		}

		return (project, built.Id);
	}

	/// <summary>A builder that hands back something assembly-shaped, since nothing here runs it.</summary>
	private sealed class Builder : IStrategyBuilder
	{
		public BuiltStrategy Build(StrategySpec spec)
			=> new("Generated", $"// source of {spec.Name}", $"source-{spec.Name}", [1, 2, 3], $"assembly-{spec.Name}", "1.0.0");
	}

	/// <summary>A runner that reports the same modest, profitable run whatever it is handed.</summary>
	private sealed class Runner : IBacktestRunner
	{
		public Task<BacktestOutcome> RunAsync(BacktestRequest request, CancellationToken cancellationToken)
		{
			var trades = new List<ExecutedTrade>();
			var equity = new List<EquityPoint>();
			var money = 100_000m;

			for (var i = 0; i < 60; i++)
			{
				var entry = request.Bars.From.AddHours(i);
				var profit = i % 3 == 0 ? -8m : 12m;

				trades.Add(new($"t{i}", request.Symbol, TradeDirections.Long, entry, 100m, entry.AddMinutes(25),
					100m + profit / 10m, 10m, 0.5m, 0.5m));

				money += profit;
				equity.Add(new(entry.AddMinutes(25), money));
			}

			return Task.FromResult(new BacktestOutcome(trades, equity, request.Bars.Count, 0, 120));
		}
	}
}
