namespace StockSharp.Odysseus.Application.Tests;

using System.Globalization;
using System.Threading;

using StockSharp.Odysseus.Persistence;
using StockSharp.Odysseus.Platform;
using StockSharp.Odysseus.Spec;

/// <summary>
/// Measuring a candidate against the part of the history it was never shown.
/// </summary>
/// <remarks>
/// This is the one measurement in the product that cannot be taken twice, and everything else exists to
/// make it mean something. A project is the wrong thing to enforce that in: the way round a per-project
/// rule is to start a project, import the same dates and ask again, by which time the answer is known.
/// </remarks>
[TestClass]
public class ClosedDataServiceTests : OdysseusTestBase
{
	private static readonly DateTime _open = new(2026, 3, 2, 14, 30, 0, DateTimeKind.Utc);

	private string _root;
	private SqliteProjectStore _store;
	private SqliteOperationLog _operations;
	private SqliteClosedHistoryLedger _ledger;
	private FileArtifactStore _artifacts;
	private FileDatasetStore _datasets;
	private FileSpecStore _specs;
	private Runner _runner;
	private BacktestService _backtests;
	private ProjectService _projects;
	private CandidateService _candidates;
	private ClosedDataService _service;

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
		_runner = new Runner();

		var clock = new SystemClock();
		var budget = new ResearchBudget(400, 40, TimeSpan.FromMinutes(45)).ToState();

		_projects = new ProjectService(_store, _store, _operations, clock, ServerModes.Local, budget);
		_candidates = new CandidateService(_store, _specs, _store, _artifacts, new Builder(), _store, _operations, clock);

		_backtests = new BacktestService(
			_store, _store, _specs, _datasets, _store, _artifacts, _runner, new StockSharpMarketProfiler(), _store, _operations, clock);

		_service = new ClosedDataService(
			_store, _specs, _store, _store, _store, _datasets, _ledger, _backtests, _store, clock);
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

	/// <summary>A candidate measured once against the closed data gets numbers back.</summary>
	[TestMethod]
	public async Task ACandidateIsMeasuredOnce()
	{
		var first = await FinalistAsync();

		var measured = await _service.MeasureAsync(
			first.Project, first.Candidate, "check-1", Actors.Agent, CancellationToken);

		IsNotNull(measured, "the candidate was not measured.");
		IsTrue(_runner.Runs >= 2, "the closed data was not actually measured.");
	}

	/// <summary>
	/// Spending the closed slice is written down with what was measured and which stretch of history it
	/// was, so the trail alone says what can no longer be asked.
	/// </summary>
	[TestMethod]
	public async Task SpendingTheClosedSliceIsRecordedWithItsDates()
	{
		var finalist = await FinalistAsync();
		var opened = await _store.OpenAsync(finalist.Project, CancellationToken);
		var manifest = await _datasets.GetManifestAsync(finalist.Project, opened.Dataset, CancellationToken);

		await _service.MeasureAsync(finalist.Project, finalist.Candidate, "check-1", Actors.Agent, CancellationToken);

		var trail = await _projects.ReadAuditAsync(finalist.Project, CancellationToken);
		var spent = trail.Single(e => e.Type == AuditEventTypes.FinalHoldoutConsumed);

		IsTrue(spent.Detail.Contains("NVDA", StringComparison.Ordinal),
			$"the record does not say what was measured: {spent.Detail}");

		foreach (var bound in new[] { manifest.Split.ValidationTo, manifest.Split.To })
		{
			var written = bound.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

			IsTrue(spent.Detail.Contains(written, StringComparison.Ordinal),
				$"the record does not say which stretch was spent ({written}): {spent.Detail}");
		}
	}

	/// <summary>
	/// Closed history one project measured on is spent for every project: another one asking for the same
	/// stretch is refused, and the refusal says who spent it.
	/// </summary>
	[TestMethod]
	public async Task TheSameClosedHistoryCannotBeAskedTwice()
	{
		var first = await FinalistAsync();

		await _service.MeasureAsync(first.Project, first.Candidate, "check-1", Actors.Agent, CancellationToken);

		var second = await FinalistAsync();

		var refusal = await ThrowsAsync<InvalidOperationException>(
			() => _service.MeasureAsync(second.Project, second.Candidate, "check-2", Actors.Agent, CancellationToken).AsTask());

		IsTrue(refusal.Message.Contains("already been spent", StringComparison.Ordinal),
			$"the refusal does not say the data was spent: {refusal.Message}");

		IsTrue(refusal.Message.Contains(first.Project.Value, StringComparison.Ordinal),
			$"the refusal does not say who spent it: {refusal.Message}");
	}

	/// <summary>
	/// Two projects asking for the same closed history at the same moment spend it once: one is measured,
	/// the other is refused. A check that only read the ledger before the runs let both through.
	/// </summary>
	[TestMethod]
	public async Task TwoProjectsAskingAtOnceSpendTheStretchOnce()
	{
		var first = await FinalistAsync();
		var second = await FinalistAsync();

		var asked = new[] { first, second }
			.Select(f => Outcome(() => _service.MeasureAsync(f.Project, f.Candidate, "check-1", Actors.Agent, CancellationToken).AsTask()))
			.ToArray();

		var outcomes = await Task.WhenAll(asked);

		AreEqual(1, outcomes.Count(o => o is null), "the closed history was measured more than once, or not at all.");

		var refusal = outcomes.Single(o => o is not null);

		IsTrue(refusal.Message.Contains("already been spent", StringComparison.Ordinal),
			$"the second project was refused for some other reason: {refusal.Message}");

		static async Task<Exception> Outcome(Func<Task> measure)
		{
			try
			{
				await measure();
				return null;
			}
			catch (InvalidOperationException refused)
			{
				return refused;
			}
		}
	}

	/// <summary>Another instrument over the same dates is another question, and is allowed.</summary>
	[TestMethod]
	public async Task AnotherInstrumentOverTheSameDatesIsAllowed()
	{
		var first = await FinalistAsync();

		await _service.MeasureAsync(first.Project, first.Candidate, "check-1", Actors.Agent, CancellationToken);

		var second = await FinalistAsync(symbol: "AAPL");

		var measured = await _service.MeasureAsync(
			second.Project, second.Candidate, "check-2", Actors.Agent, CancellationToken);

		IsNotNull(measured, "a different instrument over the same dates was refused.");
	}

	/// <summary>
	/// Generated data is exempt. It is the same bars for everyone, it exists to be run over and over by
	/// someone trying the product out, and there is no answer in it to have been seen.
	/// </summary>
	[TestMethod]
	public async Task GeneratedDataCanBeAskedAgain()
	{
		var first = await FinalistAsync(synthetic: true);

		await _service.MeasureAsync(first.Project, first.Candidate, "check-1", Actors.Agent, CancellationToken);

		var second = await FinalistAsync(synthetic: true);

		var measured = await _service.MeasureAsync(
			second.Project, second.Candidate, "check-2", Actors.Agent, CancellationToken);

		IsNotNull(measured, "the generated dataset was spent as though it were real history.");
	}

	/// <summary>
	/// The walk-forward question is asked of the closed data itself, in consecutive stretches.
	/// </summary>
	/// <remarks>
	/// Asked of the slice the candidate was formed on, it says only that a search covered that slice
	/// evenly. Asked of data nobody could tune to, it says whether the edge held through a stretch of
	/// market the candidate never saw.
	/// </remarks>
	[TestMethod]
	public async Task TheWalkForwardWindowsAreMeasuredOnTheClosedData()
	{
		var first = await FinalistAsync();

		await _service.MeasureAsync(first.Project, first.Candidate, "check-1", Actors.Agent, CancellationToken);

		var runs = await _store.ListAsync(first.Project, first.Candidate, CancellationToken);

		var closedWindows = runs
			.Where(r => r.Slice == DataSlices.Final && r.Window > 0 && r.Status == RunStatuses.Completed)
			.ToArray();

		AreEqual(EvaluationService.WalkForwardWindows, closedWindows.Length,
			"the closed data was not measured in consecutive stretches.");

		IsTrue(closedWindows.Select(r => r.Window).Order().SequenceEqual(Enumerable.Range(1, closedWindows.Length)),
			"the stretches it measured are not consecutive.");
	}

	/// <summary>
	/// What is measured is what validation ran, not what the caller would like measured.
	/// </summary>
	[TestMethod]
	public async Task WhatIsMeasuredIsWhatWasValidated()
	{
		var first = await FinalistAsync(symbol: "AAPL");

		await _service.MeasureAsync(first.Project, first.Candidate, "check-1", Actors.Agent, CancellationToken);

		var runs = await _store.ListAsync(first.Project, first.Candidate, CancellationToken);

		var closed = runs.Where(r => r.Slice == DataSlices.Final).ToArray();

		IsTrue(closed.Length > 0, "nothing was measured against the closed data.");

		var wrong = closed.Where(r => r.Symbol != "AAPL").Select(r => r.Symbol).Distinct().ToArray();

		AreEqual(0, wrong.Length,
			$"the closed data was measured on {string.Join(", ", wrong)}, which validation never ran.");
	}

	/// <summary>
	/// A candidate whose setting was never validated is refused before anything is spent.
	/// </summary>
	[TestMethod]
	public async Task ACandidateWithNoValidatedSettingIsRefused()
	{
		var finalist = await FinalistAsync(validate: false);

		var refusal = await ThrowsAsync<InvalidOperationException>(
			() => _service.MeasureAsync(finalist.Project, finalist.Candidate, "check-1", Actors.Agent, CancellationToken).AsTask());

		IsTrue(refusal.Message.Contains("validation slice", StringComparison.Ordinal),
			$"the refusal does not say what is missing: {refusal.Message}");

		var spent = await _ledger.FindOverlappingAsync("NVDA", DateTime.MinValue.ToUniversalTime(), DateTime.MaxValue.ToUniversalTime(), CancellationToken);

		AreEqual(0, spent.Count, "the closed data was spent on a candidate that could not be measured.");
	}

	/// <summary>
	/// The result says how many times the open data was asked before the candidate was brought here.
	/// </summary>
	/// <remarks>
	/// It is a count, not an accusation. Numbers reached on the fortieth pass over the open slices and
	/// numbers reached on the first look the same when they arrive; this is what tells them apart, and
	/// what it is worth is the reader's to decide.
	/// </remarks>
	[TestMethod]
	public async Task TheResultSaysHowManyTimesTheOpenDataWasAsked()
	{
		var finalist = await FinalistAsync();

		// Measured repeatedly on the open data, which is what a search over it looks like from outside.
		for (var i = 0; i < 3; i++)
			await _store.AddAsync(finalist.Project, Measured(finalist.Candidate), [], CancellationToken);

		var measured = await _service.MeasureAsync(
			finalist.Project, finalist.Candidate, "check-1", Actors.Agent, CancellationToken);

		// One was seeded by FinalistAsync, three more here.
		AreEqual(4, measured.TimesMeasuredOnOpenData,
			"the count of passes over the open data is wrong.");
	}

	/// <summary>
	/// Both sets of numbers come back together, because the distance between them is the whole point.
	/// </summary>
	[TestMethod]
	public async Task TheOpenAndClosedNumbersAreReturnedSideBySide()
	{
		var finalist = await FinalistAsync();

		var measured = await _service.MeasureAsync(
			finalist.Project, finalist.Candidate, "check-1", Actors.Agent, CancellationToken);

		IsNotNull(measured.OnOpenData, "what the candidate did on the open data was not returned.");
		IsNotNull(measured.Measurement, "what it did on the closed data was not returned.");

		AreEqual(EvaluationService.WalkForwardWindows, measured.Measurement.WalkForwardReturns.Count,
			"the closed measurement is missing its walk-forward windows.");

		AreEqual(0, measured.EarlierCandidates,
			"the first candidate of a project was reported as having come after another.");
	}

	/// <summary>
	/// A second candidate of the same project is measured, and counted: by then the slice has told
	/// somebody something, and whoever chose the second one knew it.
	/// </summary>
	[TestMethod]
	public async Task ASecondCandidateIsCounted()
	{
		var finalist = await FinalistAsync();

		await _service.MeasureAsync(finalist.Project, finalist.Candidate, "check-1", Actors.Agent, CancellationToken);

		var second = await SecondCandidateAsync(finalist.Project, "AAPL");

		var measured = await _service.MeasureAsync(
			finalist.Project, second, "check-2", Actors.Agent, CancellationToken);

		AreEqual(1, measured.EarlierCandidates,
			"the second candidate was not told that the slice had already been spent once.");
	}

	/// <summary>
	/// A measurement the caller gave up on halfway can be finished by asking again.
	/// </summary>
	/// <remarks>
	/// This is the worst thing that could go wrong in the product, and it did. The closed slice is spent
	/// by the first run that touches it, and the refusal that keeps it from being measured twice was
	/// written against the presence of a run rather than the presence of a measurement. So a client that
	/// dropped its connection a second into the five runs destroyed the one irreplaceable thing the
	/// project had - the slice counted as spent, no numbers were recorded, and every later attempt was
	/// refused as a repeat.
	///
	/// The runs themselves are already answered by fingerprint, so asking again finishes what is left
	/// rather than paying for it twice.
	/// </remarks>
	[TestMethod]
	public async Task AnInterruptedMeasurementCanBeFinished()
	{
		var finalist = await FinalistAsync();

		_runner.GiveUpAfter = _runner.Runs + 2;

		await ThrowsAsync<OperationCanceledException>(
			() => _service.MeasureAsync(finalist.Project, finalist.Candidate, "check-1", Actors.Agent, CancellationToken).AsTask());

		var interrupted = (await _store.ListAsync(finalist.Project, finalist.Candidate, CancellationToken))
			.Count(r => r.Slice == DataSlices.Final);

		IsTrue(interrupted > 0, "nothing reached the closed slice, so this is not the case under test.");

		_runner.GiveUpAfter = int.MaxValue;

		var measured = await _service.MeasureAsync(
			finalist.Project, finalist.Candidate, "check-2", Actors.Agent, CancellationToken);

		IsNotNull(measured, "the interrupted measurement could not be finished.");

		AreEqual(EvaluationService.WalkForwardWindows, measured.Measurement.WalkForwardReturns.Count,
			"the finished measurement is missing the windows it was interrupted before reaching.");
	}

	/// <summary>
	/// An entry this candidate left against some other stretch does not stand for the slice being asked
	/// about now.
	/// </summary>
	/// <remarks>
	/// The exemption that lets an interrupted attempt finish looks at the candidate, and looking only at
	/// the candidate is not enough. Importing the data again over another range moves the closed slice.
	/// An entry written against the slice as it used to be still overlaps the slice as it is now, and taken as this candidate's own record it would wave
	/// through a stretch of history nothing has accounted for. Only an entry describing exactly this
	/// stretch is that record.
	/// </remarks>
	[TestMethod]
	public async Task AnEntryOfThisCandidateAgainstAnotherStretchDoesNotExcuseIt()
	{
		var finalist = await FinalistAsync();
		var opened = await _store.OpenAsync(finalist.Project, CancellationToken);
		var manifest = await _datasets.GetManifestAsync(finalist.Project, opened.Dataset, CancellationToken);

		// The same candidate, the same instrument, a stretch that overlaps the closed slice heavily and
		// is not it: the slice as it stood before the data was imported over another range.
		await _ledger.ClaimAsync(
			new(
				finalist.Project,
				finalist.Candidate,
				"NVDA",
				manifest.TimeFrame,
				manifest.Split.ValidationTo,
				manifest.Split.To - manifest.TimeFrame,
				DateTime.UtcNow),
			CancellationToken);

		var refusal = await ThrowsAsync<InvalidOperationException>(
			() => _service.MeasureAsync(finalist.Project, finalist.Candidate, "check-1", Actors.Agent, CancellationToken).AsTask());

		IsTrue(refusal.Message.Contains("already been spent", StringComparison.Ordinal),
			$"a stale entry of this candidate's passed as its record of the slice being asked for: {refusal.Message}");

		IsTrue(refusal.Message.Contains("this candidate", StringComparison.Ordinal),
			$"the refusal does not say whose earlier measurement is in the way: {refusal.Message}");
	}

	/// <summary>Once the numbers are on record, asking again is still refused.</summary>
	[TestMethod]
	public async Task AFinishedMeasurementIsStillNotRepeated()
	{
		var finalist = await FinalistAsync();

		await _service.MeasureAsync(finalist.Project, finalist.Candidate, "check-1", Actors.Agent, CancellationToken);

		var refusal = await ThrowsAsync<InvalidOperationException>(
			() => _service.MeasureAsync(finalist.Project, finalist.Candidate, "check-2", Actors.Agent, CancellationToken).AsTask());

		IsTrue(refusal.Message.Contains("already been measured", StringComparison.Ordinal),
			$"the refusal does not say it was already measured: {refusal.Message}");
	}

	/// <summary>A measurement on the open data, standing in for one the service would have taken.</summary>
	private static Measurement Measured(CandidateId candidate)
		=> new(candidate, DataSlices.Validation, Nothing(), Nothing(), Nothing(), [1m, 1m, 1m], [], new(1, 1, 0), DateTime.UtcNow);

	private static RunMetrics Nothing()
		=> new(
			new(0m, 0m, null, 0m),
			new(0m, 0m, null, 0m),
			new(0m, 0m),
			new(0m, 0m, null),
			new(0, 0m, 0m),
			new(0m, 0m),
			new(0m, 0m, "", ""),
			0);

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

	/// <summary>A project holding a candidate that has been measured and is ready for the closed data.</summary>
	private async Task<(ProjectId Project, CandidateId Candidate)> FinalistAsync(
		bool synthetic = false,
		string symbol = "NVDA",
		bool validate = true)
	{
		var created = await _projects.CreateProjectAsync(
			"closed data", Guid.NewGuid().ToString("n"), Actors.User, CancellationToken);

		var project = created.Id;

		var bars = new Dictionary<string, IReadOnlyList<Candle>>
		{
			["NVDA"] = Bars(),
			["AAPL"] = Bars(),
		};

		var imported = DatasetBuilder.Build(bars, TimeSpan.FromMinutes(5), "test", synthetic);

		await _datasets.SaveAsync(project, imported, CancellationToken);

		var opened = await _store.OpenAsync(project, CancellationToken);

		await _store.UpdateAsync(opened.WithDataset(imported.Manifest.Id, DateTime.UtcNow), CancellationToken);

		return (project, await CandidateAsync(project, symbol, validate));
	}

	/// <summary>Another candidate in a project that already has data and one measured candidate.</summary>
	private async Task<CandidateId> SecondCandidateAsync(ProjectId project, string symbol)
		=> await CandidateAsync(project, symbol, validate: true);

	/// <summary>A candidate walked to where the closed data will accept it.</summary>
	private async Task<CandidateId> CandidateAsync(ProjectId project, string symbol, bool validate)
	{
		var name = Guid.NewGuid().ToString("n")[..8];
		var spec = await _specs.AddAsync(project, Spec(name), Actors.Agent, DateTime.UtcNow, CancellationToken);
		var built = await _candidates.BuildAsync(project, spec.Id, Guid.NewGuid().ToString("n"), Actors.Agent, CancellationToken);

		// The lifecycle refuses a jump, so the candidate walks to where the closed data will accept it.
		foreach (var stage in new[]
		{
			CandidateStatuses.Backtested,
			CandidateStatuses.Validated,
			CandidateStatuses.StressTested,
		})
		{
			built = built.WithStatus(stage, DateTime.UtcNow);

			await _store.UpdateAsync(project, built, CancellationToken);
		}

		await _store.AddAsync(project, Measured(built.Id), [], CancellationToken);

		// The closed data measures what validation ran, and is reported next to what development said, so
		// both have to exist and both have to be about the same instrument.
		DataSlices[] measured = validate
			? [DataSlices.Development, DataSlices.Validation]
			: [DataSlices.Development];

		foreach (var slice in measured)
		{
			await _backtests.RunAsync(
				project, built.Id, slice, RunWindow.Whole, symbol, RunScenario.Baseline,
				null, Guid.NewGuid().ToString("n"), Actors.Agent, CancellationToken);
		}

		return built.Id;
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
		public int Runs { get; private set; }

		/// <summary>How many runs to allow before behaving like a client that has gone away.</summary>
		public int GiveUpAfter { get; set; } = int.MaxValue;

		public Task<BacktestOutcome> RunAsync(BacktestRequest request, CancellationToken cancellationToken)
		{
			Runs++;

			if (Runs > GiveUpAfter)
				throw new OperationCanceledException();

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
