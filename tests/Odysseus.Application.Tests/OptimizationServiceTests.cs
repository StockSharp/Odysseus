namespace StockSharp.Odysseus.Application.Tests;

using System.Threading;

using StockSharp.Odysseus.Persistence;
using StockSharp.Odysseus.Platform;
using StockSharp.Odysseus.Spec;

/// <summary>
/// What a search costs, and what asking for the same one twice costs.
/// </summary>
/// <remarks>
/// A search is the most expensive thing an agent can ask this server for: it claims the whole ceiling
/// of what it could evaluate before it starts, because an abandoned search would otherwise be free. That
/// makes the retry the dangerous case. Every other tool answers a repeated operation key from the first
/// result; this one declared the log, assigned it and never called it, so a client that timed out and
/// asked again bought a second search out of the same allowance and neither answer said so.
/// </remarks>
[TestClass]
public class OptimizationServiceTests : OdysseusTestBase
{
	// Backtests one search can evaluate, which is what it claims before it starts, and what one whole
	// search costs: that claim plus the single run that measures the settings it chose.
	private const int WorstCase = OptimizationService.Population * OptimizationService.Generations;
	private const int WholeSearch = WorstCase + 1;

	private static readonly DateTime _open = new(2026, 3, 2, 14, 30, 0, DateTimeKind.Utc);

	private string _root;
	private SqliteProjectStore _store;
	private SqliteOperationLog _operations;
	private FileArtifactStore _artifacts;
	private FileDatasetStore _datasets;
	private FileSpecStore _specs;
	private Optimizer _optimizer;
	private ProjectService _projects;
	private CandidateService _candidates;
	private OptimizationService _service;

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
		_optimizer = new Optimizer();

		var clock = new SystemClock();
		var budget = new ResearchBudget(400, 5, TimeSpan.FromMinutes(45)).ToState();

		_projects = new ProjectService(_store, _store, _operations, clock, ServerModes.Local, budget);
		_candidates = new CandidateService(_store, _specs, _store, _artifacts, new Builder(), _store, _operations, clock);

		var backtests = new BacktestService(
			_store, _store, _specs, _datasets, _store, _artifacts, new Runner(), new StockSharpMarketProfiler(), _store, _operations, clock);

		_service = new OptimizationService(
			_store, _store, _specs, _datasets, _artifacts, _optimizer, backtests, _store, _operations, clock);
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

	/// <summary>The same request twice is one search and one charge.</summary>
	[TestMethod]
	public async Task TheSameRequestTwiceIsOneSearch()
	{
		var (project, candidate) = await ReadyAsync();

		var key = Guid.NewGuid().ToString("n");

		var first = await _service.SearchAsync(project, candidate, "NVDA", 7, key, Actors.Agent, CancellationToken);

		var spent = await SpentAsync(project);

		var second = await _service.SearchAsync(project, candidate, "NVDA", 7, key, Actors.Agent, CancellationToken);

		AreEqual(1, _optimizer.Searches, "the repeated request bought a second search.");
		AreEqual(spent, await SpentAsync(project), "the repeated request was charged again.");

		IsFalse(first.WasAlreadySearched);
		IsTrue(second.WasAlreadySearched, "the replay does not say that it is one.");

		AreEqual(first.Run.Id, second.Run.Id, "the replay points at a different run than the search it repeats.");
	}

	/// <summary>A different key is a different search and is charged as one.</summary>
	/// <remarks>
	/// A whole worst case again and not a backtest more. The settings this search picks were measured by
	/// the search before it, and a run already on record is answered from the record rather than measured
	/// a second time, so what a repeated search costs is the search itself.
	/// </remarks>
	[TestMethod]
	public async Task ADifferentRequestIsChargedAgain()
	{
		var (project, candidate) = await ReadyAsync();

		await _service.SearchAsync(
			project, candidate, "NVDA", 7, Guid.NewGuid().ToString("n"), Actors.Agent, CancellationToken);

		var spent = await SpentAsync(project);

		await _service.SearchAsync(
			project, candidate, "NVDA", 8, Guid.NewGuid().ToString("n"), Actors.Agent, CancellationToken);

		AreEqual(2, _optimizer.Searches);

		AreEqual(spent + WorstCase, await SpentAsync(project),
			"a second search cost something other than the whole of what a search can evaluate.");
	}

	/// <summary>
	/// A walk-forward claims every window's search and test before anything runs: the declared length takes
	/// seven values from 10 to 40 in steps of 5, so each window costs seven settings and one test.
	/// </summary>
	[TestMethod]
	public async Task AWalkForwardClaimsEveryWindowBeforeItRuns()
	{
		var (project, candidate) = await ReadyAsync();

		_optimizer.Spent = () => SpentAsync(project);

		await _service.WalkForwardAsync(
			project, candidate, "NVDA", 3, 1, Guid.NewGuid().ToString("n"), Actors.Agent, CancellationToken);

		var request = _optimizer.WalkedForward;

		IsNotNull(request, "the walk-forward never reached the optimizer.");
		AreEqual(TimeSpan.FromDays(3), request.InSample);
		AreEqual(TimeSpan.FromDays(1), request.OutOfSample);

		var span = request.Bars.To - request.Bars.From;
		var windows = 0;

		for (var start = TimeSpan.Zero; start + TimeSpan.FromDays(4) <= span; start += TimeSpan.FromDays(1))
			windows++;

		IsTrue(windows > 0, $"a development slice of {span.TotalDays:0.#} days holds no window, so the test measures nothing.");

		AreEqual(windows * (7 + 1), await SpentAsync(project),
			$"{windows} windows of seven settings and one test each were charged as something else.");
	}

	/// <summary>The same key answers from the first walk-forward and costs nothing more.</summary>
	[TestMethod]
	public async Task TheSameWalkForwardTwiceIsOneWalkForward()
	{
		var (project, candidate) = await ReadyAsync();

		_optimizer.Windows =
		[
			new(
				_open, _open.AddDays(3), _open.AddDays(3), _open.AddDays(4),
				new Dictionary<string, decimal>(StringComparer.Ordinal) { ["length"] = 25m },
				1.5m,
				new(
					[new("t1", "NVDA", TradeDirections.Long, _open.AddDays(3).AddHours(1), 100m, _open.AddDays(3).AddHours(2), 102m, 10m, 0.5m, 0.5m)],
					[new(_open.AddDays(3), 100_000m), new(_open.AddDays(3).AddHours(2), 100_019m)],
					80,
					0,
					2)),
		];

		var key = Guid.NewGuid().ToString("n");

		var first = await _service.WalkForwardAsync(project, candidate, "NVDA", 3, 1, key, Actors.Agent, CancellationToken);
		var spent = await SpentAsync(project);
		var second = await _service.WalkForwardAsync(project, candidate, "NVDA", 3, 1, key, Actors.Agent, CancellationToken);

		IsFalse(first.WasAlreadyRun);
		IsTrue(second.WasAlreadyRun, "a repeated key was walked forward again.");
		AreEqual(spent, await SpentAsync(project), "the repeated key was charged again.");

		AreEqual(1, second.Windows.Count);
		AreEqual(25m, second.Windows[0].Parameters["length"]);
		AreEqual(1, second.Windows[0].OutOfSample.Trades.Count, "the tested stretch's one trade was not measured.");
		AreEqual(first.Windows[0].OutOfSample.Net.Profit, second.Windows[0].OutOfSample.Net.Profit);
	}

	/// <summary>
	/// A search claims the whole of what it could evaluate before it evaluates anything, and a finished
	/// one has cost exactly that plus the single run that measured what it chose.
	/// </summary>
	/// <remarks>
	/// The ceiling of what a search can cost - a population, for a number of generations - is known before
	/// it starts, and claiming it afterwards would make an abandoned search free: the agent walks away, the
	/// machine keeps evaluating, and nothing was charged. Claiming the whole of it up front is also what
	/// makes two searches running together cost two rather than one.
	///
	/// So the number is the point. "More than nothing was charged" is true of a search that claims one
	/// backtest at a time as it goes, which is the arrangement this one exists not to be; and it is true of
	/// a search charged after the fact. Both of those pass unless the figure is pinned, and the figure has
	/// to be read from inside the running search, since by the time it returns an up-front claim and a
	/// claim-as-you-go look identical.
	/// </remarks>
	[TestMethod]
	public async Task ASearchClaimsItsWholeWorstCaseBeforeItRuns()
	{
		var (project, candidate) = await ReadyAsync();

		_optimizer.Spent = () => SpentAsync(project);

		await _service.SearchAsync(
			project, candidate, "NVDA", 7, Guid.NewGuid().ToString("n"), Actors.Agent, CancellationToken);

		AreEqual(WorstCase, _optimizer.SpentWhenAsked,
			$"the search was evaluating settings with {_optimizer.SpentWhenAsked} backtests claimed rather than " +
			$"the {WorstCase} it could cost.");

		AreEqual(WholeSearch, await SpentAsync(project),
			"a finished search cost something other than its worst case plus the one run that measured what it chose.");
	}

	/// <summary>
	/// A search that broke gives back the whole of what it claimed, since nothing it claimed for was run.
	/// </summary>
	[TestMethod]
	public async Task ASearchThatBrokeGivesItsWholeClaimBack()
	{
		var (project, candidate) = await ReadyAsync();

		var spent = await SpentAsync(project);

		_optimizer.Breaks = true;

		await ThrowsAsync<InvalidOperationException>(() => _service.SearchAsync(
			project, candidate, "NVDA", 7, Guid.NewGuid().ToString("n"), Actors.Agent, CancellationToken).AsTask());

		AreEqual(spent, await SpentAsync(project),
			"a search that evaluated nothing left part of its worst case charged to the project.");
	}

	/// <summary>
	/// The time a search occupied the machine is charged to the project's wall-clock allowance, as the
	/// time of every backtest is. A search is the most expensive thing a project can ask for, and an
	/// allowance it does not count is not a limit on it.
	/// </summary>
	[TestMethod]
	public async Task TheTimeASearchTookIsCharged()
	{
		var (project, candidate) = await ReadyAsync();

		_optimizer.Takes = TimeSpan.FromSeconds(1);

		var before = await RemainingTimeAsync(project);

		await _service.SearchAsync(project, candidate, "NVDA", 7, Guid.NewGuid().ToString("n"), Actors.Agent, CancellationToken);

		var charged = before - await RemainingTimeAsync(project);

		IsTrue(charged >= TimeSpan.FromMilliseconds(900),
			$"a search that occupied the machine for a second was charged {charged.TotalMilliseconds:0} ms.");
	}

	/// <summary>
	/// A search whose worst case does not fit in what is left is refused before it starts, and what is
	/// left stays left.
	/// </summary>
	[TestMethod]
	public async Task ASearchThatCannotFinishIsRefused()
	{
		var (project, candidate) = await ReadyAsync();

		await LeaveOneAsync(project);

		var refusal = await ThrowsAsync<ResearchBudgetExhaustedException>(() => _service.SearchAsync(
			project, candidate, "NVDA", 7, Guid.NewGuid().ToString("n"), Actors.Agent, CancellationToken).AsTask());

		IsTrue(refusal.Message.Contains("1 backtests", StringComparison.Ordinal), refusal.Message);
		AreEqual(0, _optimizer.Searches, "a search was started that could not be paid for.");
		AreEqual(1, await LeftAsync(project), "the refusal changed what was left.");
	}

	/// <summary>A walk-forward that does not fit is refused the same way, before any window runs.</summary>
	[TestMethod]
	public async Task AWalkForwardThatCannotFinishIsRefused()
	{
		var (project, candidate) = await ReadyAsync();

		await LeaveOneAsync(project);

		var refusal = await ThrowsAsync<ResearchBudgetExhaustedException>(() => _service.WalkForwardAsync(
			project, candidate, "NVDA", 3, 1, Guid.NewGuid().ToString("n"), Actors.Agent, CancellationToken).AsTask());

		IsTrue(refusal.Message.Contains("1 left", StringComparison.Ordinal), refusal.Message);
		IsNull(_optimizer.WalkedForward, "a walk-forward was started that could not be paid for.");
		AreEqual(1, await LeftAsync(project), "the refusal changed what was left.");
	}

	private static IReadOnlyList<Candle> Bars()
	{
		var bars = new List<Candle>();
		var time = _open;

		for (var i = 0; i < 2_000; i++)
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

	private static string Spec()
		=> $$"""
		{
		  "name": "Above its average {{Guid.NewGuid().ToString("n")[..8]}}",
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
		        "right": { "kind": "Indicator", "name": "sma", "length": { "kind": "Parameter", "name": "length" }, "source": "Close" } } }
		  ],
		  "exits": [ { "id": "x1", "kind": "TimeExit", "direction": "Long", "length": { "kind": "Constant", "value": 5 } } ],
		  "parameters": [ { "name": "length", "type": "Integer", "default": 20, "minimum": 10, "maximum": 40, "step": 5, "optimizable": true } ],
		  "risk": { "maxPositionPercent": 0.10, "maxDailyLossPercent": 0.02 }
		}
		""";

	private async Task LeaveOneAsync(ProjectId project)
		=> IsTrue(await _store.TryClaimAsync(project, await LeftAsync(project) - 1, 0, CancellationToken));

	private async Task<int> LeftAsync(ProjectId project)
		=> ResearchBudget.Restore((await _store.OpenAsync(project, CancellationToken)).Budget).RemainingBacktests;

	private async Task<TimeSpan> RemainingTimeAsync(ProjectId project)
	{
		var existing = await _store.OpenAsync(project, CancellationToken);

		return ResearchBudget.Restore(existing.Budget).RemainingWallClock;
	}

	private async Task<int> SpentAsync(ProjectId project)
	{
		var existing = await _store.OpenAsync(project, CancellationToken);
		var budget = ResearchBudget.Restore(existing.Budget);

		return existing.Budget.MaxBacktests - budget.RemainingBacktests;
	}

	private async Task<(ProjectId Project, CandidateId Candidate)> ReadyAsync()
	{
		var created = await _projects.CreateProjectAsync(
			"search", Guid.NewGuid().ToString("n"), Actors.User, CancellationToken);

		var project = created.Id;

		var bars = new Dictionary<string, IReadOnlyList<Candle>> { ["NVDA"] = Bars() };
		var imported = DatasetBuilder.Build(bars, TimeSpan.FromMinutes(5), "test", isSynthetic: true);

		await _datasets.SaveAsync(project, imported, CancellationToken);

		var opened = await _store.OpenAsync(project, CancellationToken);

		await _store.UpdateAsync(opened.WithDataset(imported.Manifest.Id, DateTime.UtcNow), CancellationToken);

		var spec = await _specs.AddAsync(project, Spec(), Actors.Agent, DateTime.UtcNow, CancellationToken);

		var built = await _candidates.BuildAsync(
			project, spec.Id, Guid.NewGuid().ToString("n"), Actors.Agent, CancellationToken);

		return (project, built.Id);
	}

	/// <summary>A builder that hands back something assembly-shaped, since nothing here runs it.</summary>
	private sealed class Builder : IStrategyBuilder
	{
		public BuiltStrategy Build(StrategySpec spec)
			=> new("Generated", $"// source of {spec.Name}", $"source-{spec.Name}", [1, 2, 3], $"assembly-{spec.Name}", "1.0.0");
	}

	/// <summary>A runner that reports the same modest run whatever it is handed.</summary>
	private sealed class Runner : IBacktestRunner
	{
		public Task<BacktestOutcome> RunAsync(BacktestRequest request, CancellationToken cancellationToken)
		{
			var trades = new List<ExecutedTrade>();
			var equity = new List<EquityPoint>();
			var money = 100_000m;

			for (var i = 0; i < 20; i++)
			{
				var entry = request.Bars.From.AddHours(i);

				trades.Add(new($"t{i}", request.Symbol, TradeDirections.Long, entry, 100m, entry.AddMinutes(25),
					101m, 10m, 0.5m, 0.5m));

				money += 10m;
				equity.Add(new(entry.AddMinutes(25), money));
			}

			return Task.FromResult(new BacktestOutcome(trades, equity, request.Bars.Count, 0, 40));
		}
	}

	/// <summary>
	/// A search that reports a fixed table, counts how often it was asked, and can look at what the
	/// project has been charged while it is running - which is the only moment the up-front claim is
	/// visible from.
	/// </summary>
	private sealed class Optimizer : IStrategyOptimizer
	{
		public int Searches { get; private set; }

		public Func<Task<int>> Spent { get; set; }

		public int SpentWhenAsked { get; private set; } = -1;

		public bool Breaks { get; set; }

		/// <summary>What a walk-forward reports, window by window.</summary>
		public IReadOnlyList<WalkForwardWindowResult> Windows { get; set; } = [];

		/// <summary>The walk-forward that was asked for, so a test can see what it was handed.</summary>
		public WalkForwardRequest WalkedForward { get; private set; }

		/// <summary>How long a search occupies the machine.</summary>
		public TimeSpan Takes { get; set; }

		public async Task<IReadOnlyList<OptimizationTrial>> SearchAsync(
			OptimizationRequest request,
			CancellationToken cancellationToken)
		{
			Searches++;

			if (Spent is not null)
				SpentWhenAsked = await Spent();

			if (Takes > TimeSpan.Zero)
				await Task.Delay(Takes, cancellationToken);

			if (Breaks)
				throw new InvalidOperationException("The search broke half way through.");

			IReadOnlyList<OptimizationTrial> trials =
			[
				new(new Dictionary<string, decimal>(StringComparer.Ordinal) { ["length"] = 20m }, 3m, 300m, 1m, 40),
				new(new Dictionary<string, decimal>(StringComparer.Ordinal) { ["length"] = 30m }, 1m, 100m, 2m, 30),
			];

			return trials;
		}

		public async Task<IReadOnlyList<WalkForwardWindowResult>> WalkForwardAsync(
			WalkForwardRequest request,
			CancellationToken cancellationToken)
		{
			WalkedForward = request;

			if (Takes > TimeSpan.Zero)
				await Task.Delay(Takes, cancellationToken);

			if (Breaks)
				throw new InvalidOperationException("The walk-forward broke half way through.");

			return Windows;
		}
	}
}
