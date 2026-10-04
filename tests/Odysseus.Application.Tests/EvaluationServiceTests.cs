namespace Odysseus.Application.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Odysseus.Application;
using Odysseus.Domain;
using Odysseus.Persistence;
using Odysseus.Platform;
using Odysseus.Spec;
using Odysseus.TestKit;

/// <summary>
/// Putting a candidate through the fixed set of runs.
/// </summary>
/// <remarks>
/// The runs are fixed rather than chosen, and that is the whole of what this service is for. An agent
/// able to pick which evidence its candidate is measured on would pick the evidence that suits it, so
/// the six are stated here as literals rather than read back out of the service - a set that names
/// itself proves nothing about what was run.
/// </remarks>
[TestClass]
public class EvaluationServiceTests : OdysseusTestBase
{
	private static readonly DateTime _open = new(2026, 3, 2, 14, 30, 0, DateTimeKind.Utc);

	/// <summary>A builder that hands back something assembly-shaped, since nothing here runs it.</summary>
	private sealed class Builder : IStrategyBuilder
	{
		public BuiltStrategy Build(StrategySpec spec)
			=> new("Generated", $"// source of {spec.Name}", $"source-{spec.Name}", [1, 2, 3], $"assembly-{spec.Name}", "1.0.0");
	}

	/// <summary>A runner that reports the same modest, profitable run, and can be told to break.</summary>
	private sealed class Runner : IBacktestRunner
	{
		/// <summary>What a run that breaks says went wrong, which the refusal is expected to quote.</summary>
		public const string Breakage = "the strategy threw on its first candle";

		private readonly List<BacktestRequest> _requests = [];

		/// <summary>Every request the runner was handed, in the order it was handed them.</summary>
		public IReadOnlyList<BacktestRequest> Requests => _requests;

		/// <summary>Which run, counting from one, starts throwing. Zero for a runner that never does.</summary>
		public int BreaksFrom { get; set; }

		/// <summary>Whether each run wins a different amount, so that one run's numbers cannot pass for another's.</summary>
		public bool DiffersByRun { get; set; }

		public Task<BacktestOutcome> RunAsync(BacktestRequest request, CancellationToken cancellationToken)
		{
			_requests.Add(request);

			if (BreaksFrom > 0 && _requests.Count >= BreaksFrom)
				throw new InvalidOperationException(Breakage);

			var trades = new List<ExecutedTrade>();
			var equity = new List<EquityPoint>();
			var money = 100_000m;

			for (var i = 0; i < 40; i++)
			{
				var entry = request.Bars.From.AddHours(i);
				var profit = (i % 3 == 0 ? -8m : 12m) + (DiffersByRun ? _requests.Count : 0m);

				trades.Add(new($"t{i}", request.Symbol, TradeDirections.Long, entry, 100m, entry.AddMinutes(25),
					100m + profit / 10m, 10m, 0.5m, 0.5m));

				money += profit;
				equity.Add(new(entry.AddMinutes(25), money));
			}

			return Task.FromResult(new BacktestOutcome(trades, equity, request.Bars.Count, 0, 80));
		}
	}

	private string _root;
	private SqliteProjectStore _store;
	private SqliteOperationLog _operations;
	private FileArtifactStore _artifacts;
	private FileDatasetStore _datasets;
	private FileSpecStore _specs;
	private Runner _runner;
	private BacktestService _backtests;
	private ProjectService _projects;
	private CandidateService _candidates;
	private EvaluationService _service;

	/// <summary>Builds the services over temporary storage.</summary>
	[TestInitialize]
	public void CreateService()
	{
		_root = Path.Combine(Path.GetTempPath(), "odysseus-tests", Guid.NewGuid().ToString("n"));
		_store = new SqliteProjectStore(_root);
		_operations = new SqliteOperationLog(_root);
		_artifacts = new FileArtifactStore(_root);
		_datasets = new FileDatasetStore(_root, new LocalBarStorage(Path.Combine(_root, "market-data")));
		_specs = new FileSpecStore(_root);
		_runner = new Runner();

		var clock = new SystemClock();
		var budget = new ResearchBudget(400, 40, TimeSpan.FromMinutes(45)).ToState();

		_projects = new ProjectService(_store, _store, _operations, clock, ServerModes.Local, budget);
		_candidates = new CandidateService(_store, _specs, _store, _artifacts, new Builder(), _store, _operations, clock);

		_backtests = new BacktestService(
			_store, _store, _specs, _datasets, _store, _artifacts, _runner, new StockSharpMarketProfiler(), _store, _operations, clock);

		_service = new EvaluationService(_store, _store, _specs, _store, _backtests, _store, clock);
	}

	/// <summary>Releases storage.</summary>
	[TestCleanup]
	public void DeleteService()
	{
		_store?.Dispose();
		_operations?.Dispose();

		if (Directory.Exists(_root))
			Directory.Delete(_root, recursive: true);
	}

	/// <summary>
	/// Measuring a candidate asks these six questions, in this order, and no others.
	/// </summary>
	/// <remarks>
	/// Written out rather than derived. The claim being pinned is that a candidate is measured on the
	/// slice it was formed on, on a slice it was not, on that same slice again under costs half as high
	/// again, and on three consecutive stretches of the first - and a test that read the answer out of
	/// the service would go on passing whichever six it chose.
	/// </remarks>
	[TestMethod]
	public async Task TheSixRunsAreTheSameSixEveryTime()
	{
		var (project, candidate) = await CandidateAsync();

		var measured = await _service.MeasureAsync(
			project, candidate, "NVDA", null, "measure-1", Actors.Agent, CancellationToken);

		AreEqual(6, measured.Runs.Count, $"a candidate was measured over {measured.Runs.Count} runs.");

		var runs = new List<RunResult>();

		foreach (var id in measured.Runs)
			runs.Add(await _store.GetAsync(project, id, CancellationToken));

		(DataSlices Slice, int Window, string Scenario)[] expected =
		[
			(DataSlices.Development, 0, "baseline"),
			(DataSlices.Validation, 0, "baseline"),
			(DataSlices.Validation, 0, "costsX15"),
			(DataSlices.Development, 1, "baseline"),
			(DataSlices.Development, 2, "baseline"),
			(DataSlices.Development, 3, "baseline"),
		];

		for (var i = 0; i < expected.Length; i++)
		{
			AreEqual(expected[i], (runs[i].Slice, runs[i].Window, runs[i].Scenario),
				$"run {i + 1} of the six is not the one the fixed set names.");
		}

		IsFalse(runs.Any(r => r.Slice == DataSlices.Final),
			"measuring a candidate on the open data touched the closed slice.");

		IsTrue(runs.All(r => r.Status == RunStatuses.Completed),
			"a measurement was returned over a run that did not complete.");

		IsTrue(runs.All(r => r.Symbol == "NVDA"),
			"the six runs were not all about the instrument that was asked for.");
	}

	/// <summary>
	/// Every number in the measurement is the number of the run it names: development from the first,
	/// validation from the second, stressed validation from the third, the walk-forward returns from the
	/// last three in order.
	/// </summary>
	/// <remarks>
	/// With every run answering alike, a measurement that put the development numbers where validation
	/// belongs would look correct, and that swap is the one that makes an overfitted candidate look sound.
	/// </remarks>
	[TestMethod]
	public async Task EachNumberComesFromTheRunItNames()
	{
		var (project, candidate) = await CandidateAsync();

		_runner.DiffersByRun = true;

		var measured = await _service.MeasureAsync(
			project, candidate, "NVDA", null, "measure-1", Actors.Agent, CancellationToken);

		var runs = new List<RunResult>();

		foreach (var id in measured.Runs)
			runs.Add(await _store.GetAsync(project, id, CancellationToken));

		AreEqual(6, runs.Select(r => r.Metrics.Net.Profit).Distinct().Count(),
			"the runs did not differ, so this test cannot tell one from another.");

		foreach (var measurement in new[] { measured.Measurement, (await _service.FindAsync(project, candidate, CancellationToken)).Measurement })
		{
			AreEqual(runs[0].Metrics.Net.Profit, measurement.Development.Net.Profit, "development is not the first run.");
			AreEqual(runs[1].Metrics.Net.Profit, measurement.HeldOut.Net.Profit, "validation is not the second run.");
			AreEqual(runs[2].Metrics.Net.Profit, measurement.HeldOutStressed.Net.Profit, "stressed validation is not the third run.");

			IsTrue(runs.Skip(3).Select(r => r.Metrics.Net.ReturnPercent).SequenceEqual(measurement.WalkForwardReturns),
				"the walk-forward returns are not the last three runs, in order.");
		}
	}

	/// <summary>
	/// The three walk-forward runs cut the development slice into consecutive parts rather than asking
	/// the same question of the whole of it three times.
	/// </summary>
	/// <remarks>
	/// Asked of the whole slice three times the answer is the same number three times, and a candidate
	/// that only worked in one stretch would come back looking as though it worked in all three.
	/// </remarks>
	[TestMethod]
	public async Task TheWalkForwardRunsDivideTheDevelopmentSliceBetweenThem()
	{
		var (project, candidate) = await CandidateAsync();

		await _service.MeasureAsync(project, candidate, "NVDA", null, "measure-1", Actors.Agent, CancellationToken);

		var whole = _runner.Requests[0].Bars;
		var windows = _runner.Requests.Skip(3).Select(r => r.Bars).ToArray();

		AreEqual(EvaluationService.WalkForwardWindows, windows.Length,
			"the development slice was not measured in the number of stretches the service declares.");

		IsTrue(windows.All(w => w.Count > 0), "one of the walk-forward stretches held no bars at all.");

		IsTrue(windows.All(w => w.Folder == whole.Folder),
			"a walk-forward stretch was measured over a different slice from the one it divides.");

		AreEqual(whole.Count, windows.Sum(w => w.Count),
			"the three stretches do not add up to the development slice: they overlap, leave a gap, or are " +
			"each the whole of it.");

		AreEqual(whole.From, windows[0].From, "the first stretch does not begin where the slice begins.");
		AreEqual(whole.To, windows[^1].To, "the last stretch does not end where the slice ends.");

		for (var i = 1; i < windows.Length; i++)
		{
			IsTrue(windows[i].From >= windows[i - 1].To,
				$"stretch {i + 1} starts before stretch {i} ended, so the two measure the same bars twice.");
		}
	}

	/// <summary>
	/// A run that did not finish stops the measurement rather than being averaged into it.
	/// </summary>
	/// <remarks>
	/// The runs after it are never asked for, nothing is recorded, and the candidate does not move on:
	/// numbers assembled from five runs and a hole would read exactly like numbers from six.
	/// </remarks>
	[TestMethod]
	public async Task ARunThatDidNotFinishAbandonsTheMeasurement()
	{
		var (project, candidate) = await CandidateAsync();

		// The second of the six, so that one run has already completed when the next one does not.
		_runner.BreaksFrom = 2;

		var refusal = await ThrowsAsync<InvalidOperationException>(
			() => _service.MeasureAsync(
				project, candidate, "NVDA", null, "measure-1", Actors.Agent, CancellationToken).AsTask());

		IsTrue(refusal.Message.Contains("validation", StringComparison.Ordinal),
			$"the refusal does not say which of the six runs failed: {refusal.Message}");

		IsTrue(refusal.Message.Contains(Runner.Breakage, StringComparison.Ordinal),
			$"the refusal does not carry what the run itself reported: {refusal.Message}");

		AreEqual(2, _runner.Requests.Count,
			$"the measurement asked for {_runner.Requests.Count} runs; it should have stopped at the one it could not judge.");

		IsNull(await _service.FindAsync(project, candidate, CancellationToken),
			"a measurement was recorded from a set of runs that was never finished.");

		var recorded = await _store.ListAsync(project, candidate, CancellationToken);

		AreEqual(1, recorded.Count(r => r.Status == RunStatuses.Failed),
			"the run that failed was not kept as evidence that the attempt was made.");

		AreEqual(CandidateStatuses.Backtested, (await _store.GetAsync(project, candidate, CancellationToken)).Status,
			"a candidate whose measurement was abandoned was moved on as though it had been validated and stressed.");
	}

	/// <summary>
	/// A candidate that came through all six is said to have been validated and stress-tested.
	/// </summary>
	[TestMethod]
	public async Task AMeasuredCandidateSaysWhatItHasBeenThrough()
	{
		var (project, candidate) = await CandidateAsync();

		var measured = await _service.MeasureAsync(
			project, candidate, "NVDA", null, "measure-1", Actors.Agent, CancellationToken);

		AreEqual(CandidateStatuses.StressTested, (await _store.GetAsync(project, candidate, CancellationToken)).Status,
			"a candidate measured over all six runs was not moved past validation.");

		AreEqual(1, measured.TimesMeasured, "the first measurement of a candidate was not counted as its first.");

		AreEqual(EvaluationService.WalkForwardWindows, measured.Measurement.WalkForwardReturns.Count,
			"the measurement does not carry one return per walk-forward stretch.");
	}

	/// <summary>
	/// A slice that traded nothing carries the sentence saying which kind of nothing it was.
	/// </summary>
	/// <remarks>
	/// Rules that never fired and orders that never filled produce the same row of zeros and are
	/// opposite findings: the first is an answer about the hypothesis, the second means the hypothesis
	/// was never tested. Both sentences are produced here the way a run produces them, so the test
	/// states the pairing the product actually makes rather than two strings invented for it.
	/// </remarks>
	[TestMethod]
	public void ASliceThatTradedNothingSaysWhichKindOfNothing()
	{
		var bars = Bars();

		// Larger than any bar traded, which is what an order that cannot fill looks like.
		const decimal unfillable = 5_000_000m;

		var neverFired = RunDiagnosis.Explain(ordersPlaced: 0, trades: 0, unfillable, bars);
		var neverFilled = RunDiagnosis.Explain(ordersPlaced: 12, trades: 0, unfillable, bars);

		IsNotNull(neverFired, "a run whose rules never fired had nothing to say about it.");
		IsNotNull(neverFilled, "a run whose orders never filled had nothing to say about it.");

		AreNotEqual(neverFired, neverFilled,
			"the two opposite reasons a slice can be silent are described in the same words.");

		var silent = EvaluationService.Silences(
		[
			("development", Silent(neverFired)),
			("heldOut", Traded()),
			("heldOutStressed", Silent(neverFilled)),
		]);

		AreEqual(2, silent.Count, $"{silent.Count} of the three slices were reported silent.");

		AreEqual("development", silent[0].Slice, "the first silent slice is not the one that was silent.");
		AreEqual(neverFired, silent[0].Why, "the development slice was given the wrong explanation.");

		AreEqual("heldOutStressed", silent[1].Slice, "the second silent slice is not the one that was silent.");
		AreEqual(neverFilled, silent[1].Why, "the stressed slice was given the wrong explanation.");
	}

	/// <summary>
	/// A slice that traded and made nothing is not silent, and is not reported as though it were.
	/// </summary>
	/// <remarks>
	/// This is the other half of the same distinction, and the more expensive one to get wrong. Zeros
	/// because nothing was tried and zeros because what was tried paid nothing are the two findings a
	/// reader most needs told apart; reported alike, an agent abandons a hypothesis the market answered.
	/// </remarks>
	[TestMethod]
	public void ASliceThatTradedAndMadeNothingIsNotSilent()
	{
		var silent = EvaluationService.Silences([("heldOut", Traded())]);

		AreEqual(0, silent.Count,
			"a slice whose trades came to nothing was reported as a slice that never traded.");
	}

	/// <summary>
	/// Indicators are counted wherever in the rules they are, including inside one another.
	/// </summary>
	/// <remarks>
	/// Worked out by hand from <see cref="Nested"/>: no rule has an indicator at the top of it. One sits
	/// under an All, a Not, a Compare and an Add; one is the window length of that one, which the
	/// published schema allows because a length is an expression; one is under the All and a Compare;
	/// and one is under the exit's condition and an Abs. Four names, and the moving average appears
	/// twice at two lengths, so the count of distinct indicators is four rather than five.
	///
	/// A count that looked only at the top of each rule would report none of them.
	/// </remarks>
	[TestMethod]
	public void IndicatorsAreCountedWhereverInTheRulesTheyAre()
	{
		var shape = EvaluationService.Shape(SpecJson.Read(Nested));

		AreEqual(4, shape.Indicators, "the indicators buried inside the rules were not all counted once each.");
		AreEqual(2, shape.Rules, "the entry and the exit together are the rules of the specification.");
		AreEqual(1, shape.Parameters, "the numbers the search may vary were not counted.");
	}

	/// <summary>A completed run that traded nothing, and knows why.</summary>
	private static RunResult Silent(string why)
		=> Run(Nothing(trades: 0), why);

	/// <summary>A completed run that traded and came out level, so it has nothing to explain.</summary>
	private static RunResult Traded()
		=> Run(Nothing(trades: 30), null);

	private static RunResult Run(RunMetrics metrics, string diagnosis)
		=> new(
			RunId.New(),
			CandidateId.New(),
			DatasetId.New(),
			DataSlices.Development,
			0,
			"NVDA",
			"baseline",
			"fingerprint",
			new Dictionary<string, decimal>(StringComparer.Ordinal),
			RunStatuses.Completed,
			metrics,
			default,
			default,
			1_000,
			_open,
			_open.AddMinutes(1),
			null,
			diagnosis);

	/// <summary>Metrics of a run that made nothing either way, with the trade count under test.</summary>
	private static RunMetrics Nothing(int trades)
		=> new(
			new(0m, 0m, null, 0m),
			new(0m, 0m, null, 0m),
			new(0m, 0m),
			new(0m, 0m, null),
			new(trades, 0m, 0m),
			new(0m, 0m),
			new(0m, 0m, "", ""),
			0);

	/// <summary>A project holding data and a candidate compiled from a specification.</summary>
	private async Task<(ProjectId Project, CandidateId Candidate)> CandidateAsync()
	{
		var created = await _projects.CreateProjectAsync(
			"evaluation", Guid.NewGuid().ToString("n"), Actors.User, CancellationToken);

		var imported = DatasetBuilder.Build(
			new Dictionary<string, IReadOnlyList<Candle>> { ["NVDA"] = Bars() },
			TimeSpan.FromMinutes(5),
			"test",
			isSynthetic: true);

		await _datasets.SaveAsync(created.Id, imported, CancellationToken);

		var opened = await _store.OpenAsync(created.Id, CancellationToken);

		await _store.UpdateAsync(opened.WithDataset(imported.Manifest.Id, DateTime.UtcNow), CancellationToken);

		var spec = await _specs.AddAsync(created.Id, Sound, Actors.Agent, DateTime.UtcNow, CancellationToken);

		var built = await _candidates.BuildAsync(
			created.Id, spec.Id, Guid.NewGuid().ToString("n"), Actors.Agent, CancellationToken);

		return (created.Id, built.Id);
	}

	/// <summary>Enough bars for a split whose development slice divides into three stretches.</summary>
	private static IReadOnlyList<Candle> Bars()
	{
		var bars = new List<Candle>();
		var time = _open;

		for (var i = 0; i < 3_000; i++)
		{
			var close = 100m + Math.Round(5m * (decimal)Math.Sin(i * 2 * Math.PI / 60), 2);
			var open = bars.Count == 0 ? close : bars[^1].Close;

			bars.Add(new(time, open, Math.Max(open, close) + 0.05m, Math.Min(open, close) - 0.05m, close, 10_000m));

			time = time.AddMinutes(5);

			if (time.TimeOfDay >= TimeSpan.FromHours(21))
				time = time.Date.AddDays(time.DayOfWeek == DayOfWeek.Friday ? 3 : 1).Add(_open.TimeOfDay);
		}

		return bars;
	}

	private const string Sound =
		"""
		{
		  "name": "Above its average",
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

	/// <summary>A specification whose indicators are all buried, one of them inside another.</summary>
	private const string Nested =
		"""
		{
		  "name": "Buried references",
		  "thesis": "Indicators sit wherever the rules put them, including inside one another.",
		  "allowLong": true,
		  "allowShort": false,
		  "timeFrame": "00:05:00",
		  "warmupBars": 200,
		  "entries": [
		    { "id": "e1", "direction": "Long", "condition": {
		        "kind": "All",
		        "conditions": [
		          { "kind": "Not", "condition": {
		              "kind": "Compare",
		              "left": { "kind": "Field", "field": "Close" },
		              "operator": "LessThan",
		              "right": { "kind": "Add", "operands": [
		                  { "kind": "Indicator", "name": "sma", "length": { "kind": "Constant", "value": 20 }, "source": "Close" },
		                  { "kind": "Indicator", "name": "atr", "length": {
		                      "kind": "Indicator", "name": "rsi", "length": { "kind": "Constant", "value": 14 }, "source": "Close" } }
		              ] } } },
		          { "kind": "Compare",
		            "left": { "kind": "Field", "field": "Volume" },
		            "operator": "GreaterThan",
		            "right": { "kind": "Indicator", "name": "volumeSma", "length": { "kind": "Parameter", "name": "window" } } }
		        ] } }
		  ],
		  "exits": [
		    { "id": "x1", "kind": "Condition", "direction": "Long", "condition": {
		        "kind": "Compare",
		        "left": { "kind": "Field", "field": "Close" },
		        "operator": "LessThan",
		        "right": { "kind": "Abs", "value": {
		            "kind": "Indicator", "name": "sma", "length": { "kind": "Constant", "value": 50 }, "source": "Close" } } } }
		  ],
		  "parameters": [
		    { "name": "window", "type": "Integer", "default": 20, "minimum": 10, "maximum": 50, "step": 1 }
		  ],
		  "risk": { "maxPositionPercent": 0.10, "maxDailyLossPercent": 0.02 }
		}
		""";
}
