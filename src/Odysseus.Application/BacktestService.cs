namespace Odysseus.Application;

using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Odysseus.Evaluation;
using Odysseus.Spec;

/// <summary>
/// A named set of assumptions a run is measured under.
/// </summary>
/// <param name="Name">What the scenario is called, which the report quotes.</param>
/// <param name="Costs">What it charges.</param>
/// <param name="EntryDelayBars">
/// Candles between an entry signal and the order it produces. Zero sends the order as the bar the rule
/// fired on closes, which fills on the bar after it — the soonest a market order can be filled.
/// </param>
public sealed record RunScenario(string Name, ExecutionCosts Costs, int EntryDelayBars = 0)
{
	/// <summary>What a run is charged unless the caller names something else.</summary>
	public static RunScenario Baseline { get; } = new("baseline", ExecutionCosts.Default);

	/// <summary>
	/// Costs half again as high.
	/// </summary>
	/// <remarks>
	/// A result that survives the baseline and not this one rests on the costs being what was assumed,
	/// which is the assumption a live account is most likely to contradict.
	/// </remarks>
	public static RunScenario Stressed { get; } = new("costsX15", ExecutionCosts.Default.Scaled(1.5m));

	/// <summary>
	/// The same costs, entered one candle after the signal.
	/// </summary>
	/// <remarks>
	/// A backtest takes the first price a signal could have been acted on: the order goes out as the bar
	/// closes and fills on the bar after it. Getting there first is the assumption a live account
	/// contradicts soonest, and nothing else in the product measures it: the walk-forward windows ask
	/// whether a result holds in another stretch of market, and this asks whether it holds when the entry
	/// is a bar later. A result that survives one and not the other rests on being fast rather than on
	/// being right.
	/// </remarks>
	public static RunScenario Delayed { get; } = new("entryOneBarLater", ExecutionCosts.Default, EntryDelayBars: 1);

	/// <summary>Every scenario a run may be charged under.</summary>
	public static IReadOnlyList<RunScenario> All { get; } = [Baseline, Stressed, Delayed];

	/// <summary>
	/// Finds a scenario by name.
	/// </summary>
	/// <param name="name">Name to look for, or empty for the baseline.</param>
	/// <returns>The scenario.</returns>
	/// <exception cref="ArgumentException">There is no scenario by that name.</exception>
	public static RunScenario Parse(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
			return Baseline;

		return All.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
			?? throw new ArgumentException(
				$"'{name}' is not a run scenario. There are: {string.Join(", ", All.Select(s => s.Name))}.",
				nameof(name));
	}
}

/// <summary>
/// One consecutive part of a slice.
/// </summary>
/// <param name="Index">Which part, counting from one. Zero means the whole slice, which is one part.</param>
/// <param name="Count">How many parts the slice is cut into.</param>
/// <remarks>
/// Walk-forward asks the same question of consecutive stretches of the same history. A candidate that
/// works in one of them and not in the others was found by the search rather than in the market, and
/// the only way to see that is to measure the stretches apart.
/// </remarks>
public sealed record RunWindow(int Index, int Count)
{
	/// <summary>The whole slice, undivided.</summary>
	public static RunWindow Whole { get; } = new(0, 1);

	/// <summary>Whether this is the whole slice rather than a part of it.</summary>
	/// <remarks>
	/// Zero is the whole slice only where the slice was not cut, because the whole of something cut into
	/// three parts is not one of the three.
	/// </remarks>
	public bool IsWhole => Index == 0 && Count == 1;

	/// <summary>
	/// Cuts the part this window covers out of a slice.
	/// </summary>
	/// <typeparam name="T">Type of the items.</typeparam>
	/// <param name="items">Items of the whole slice, oldest first.</param>
	/// <returns>The items of this window.</returns>
	/// <exception cref="ArgumentOutOfRangeException">This window is not one of the parts the slice is cut into.</exception>
	public IReadOnlyList<T> Cut<T>(IReadOnlyList<T> items)
	{
		ArgumentNullException.ThrowIfNull(items);

		if (IsWhole)
			return items;

		// Refused rather than answered with the whole slice: that answer would measure one stretch once
		// per window and report the repeats as consecutive stretches.
		if (Index < 1 || Index > Count)
		{
			throw new ArgumentOutOfRangeException(
				nameof(Index),
				Index,
				Index == 0
					? $"Window 0 is the whole slice, which is not one of {Count} parts. The parts count from one."
					: $"There is no window {Index} of {Count}.");
		}

		// The last window takes the remainder, so no bar is left out by the division.
		var size = items.Count / Count;
		var from = (Index - 1) * size;
		var to = Index == Count ? items.Count : from + size;

		return [.. items.Skip(from).Take(to - from)];
	}
}

/// <summary>
/// Running a candidate over a slice of the project's history, and measuring what it did.
/// </summary>
/// <remarks>
/// A run is the expensive thing a project does, so it is accounted for twice over: the allowance is
/// claimed before the work starts, and a run that has already been done is recognised and returned
/// rather than repeated. What makes two runs the same is stated rather than guessed — the same
/// candidate, the same data, the same slice, the same costs — so an agent that asks again gets the
/// answer instead of the bill.
/// </remarks>
public sealed class BacktestService
{
	/// <summary>Money a run starts with, which every return is measured against.</summary>
	public const decimal StartingEquity = 100_000m;

	/// <summary>
	/// Smallest price movement of the instruments this server trades. American equities tick in cents
	/// above a dollar, and that is the whole of the market it downloads.
	/// </summary>
	public const decimal PriceStep = 0.01m;

	/// <summary>
	/// Interruptions of one candidate that are given back before the candidate is answerable for them.
	/// </summary>
	/// <remarks>
	/// A failure that costs nothing can be asked for forever, so a candidate that reliably kills the
	/// process running it would be an unlimited free run. After this many it stops being free: the
	/// attempt is recorded as the candidate's own failure and charged, because a strategy the machinery
	/// cannot survive has been answered as surely as one that threw.
	///
	/// Counted per candidate rather than per fingerprint. The same compiled code takes the process down
	/// whatever numbers it is handed, and a count kept per set of parameters is a count escaped by moving
	/// one of them a hundredth.
	/// </remarks>
	public const int FreeInterruptions = 3;
	private readonly IProjectStore _projects;
	private readonly ICandidateStore _candidates;
	private readonly ISpecStore _specs;
	private readonly IDatasetStore _datasets;
	private readonly IRunStore _runs;
	private readonly IArtifactStore _artifacts;
	private readonly IBacktestRunner _runner;
	private readonly IMarketProfiler _profiler;
	private readonly IAuditLog _audit;
	private readonly IOperationLog _operations;
	private readonly IClock _clock;

	/// <summary>
	/// Creates the service.
	/// </summary>
	/// <param name="projects">Where projects are kept.</param>
	/// <param name="candidates">Where candidates are kept.</param>
	/// <param name="specs">Where specifications are kept.</param>
	/// <param name="datasets">Where datasets are kept.</param>
	/// <param name="runs">Where runs are kept.</param>
	/// <param name="artifacts">Where assemblies and result series are kept.</param>
	/// <param name="runner">What runs a candidate over bars.</param>
	/// <param name="profiler">What finds the part of the day an instrument trades in.</param>
	/// <param name="audit">Where the permanent record is kept.</param>
	/// <param name="operations">Where operation keys are remembered.</param>
	/// <param name="clock">Source of the current moment.</param>
	public BacktestService(
		IProjectStore projects,
		ICandidateStore candidates,
		ISpecStore specs,
		IDatasetStore datasets,
		IRunStore runs,
		IArtifactStore artifacts,
		IBacktestRunner runner,
		IMarketProfiler profiler,
		IAuditLog audit,
		IOperationLog operations,
		IClock clock)
	{
		_projects = projects ?? throw new ArgumentNullException(nameof(projects));
		_candidates = candidates ?? throw new ArgumentNullException(nameof(candidates));
		_specs = specs ?? throw new ArgumentNullException(nameof(specs));
		_datasets = datasets ?? throw new ArgumentNullException(nameof(datasets));
		_runs = runs ?? throw new ArgumentNullException(nameof(runs));
		_artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
		_runner = runner ?? throw new ArgumentNullException(nameof(runner));
		_profiler = profiler ?? throw new ArgumentNullException(nameof(profiler));
		_audit = audit ?? throw new ArgumentNullException(nameof(audit));
		_operations = operations ?? throw new ArgumentNullException(nameof(operations));
		_clock = clock ?? throw new ArgumentNullException(nameof(clock));
	}

	/// <summary>
	/// Runs a candidate over a slice.
	/// </summary>
	/// <param name="project">Project the candidate belongs to.</param>
	/// <param name="candidate">Candidate to run.</param>
	/// <param name="slice">Slice to run over.</param>
	/// <param name="window">Part of the slice to run over, or the whole of it.</param>
	/// <param name="symbol">Symbol to trade, or null for the first of the dataset.</param>
	/// <param name="scenario">Costs to charge.</param>
	/// <param name="parameters">Values to set on the strategy, or null for the ones it declares.</param>
	/// <param name="operationKey">Key that makes a repeated call return the first result.</param>
	/// <param name="actor">Who is asking.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The run and whether it was already there.</returns>
	public ValueTask<(RunResult Run, bool WasAlreadyRun)> RunAsync(
		ProjectId project,
		CandidateId candidate,
		DataSlices slice,
		RunWindow window,
		string symbol,
		RunScenario scenario,
		IReadOnlyDictionary<string, decimal> parameters,
		string operationKey,
		Actors actor,
		CancellationToken cancellationToken)
	{
		if (slice == DataSlices.Final)
		{
			throw new ArgumentException(
				"The closed part of the history is not run from here. It is used once, for the finalist " +
				"only, through measure_on_closed_data; running it as an ordinary slice is how a held-back " +
				"result stops being held back.",
				nameof(slice));
		}

		return RunCoreAsync(
			project, candidate, slice, window, symbol, scenario, parameters, operationKey, actor, cancellationToken);
	}

	/// <summary>
	/// Reads one run.
	/// </summary>
	/// <param name="project">Project the run belongs to.</param>
	/// <param name="run">Run to read.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The run.</returns>
	public ValueTask<RunResult> GetAsync(ProjectId project, RunId run, CancellationToken cancellationToken)
		=> _runs.GetAsync(project, run, cancellationToken);

	/// <summary>
	/// Names the symbols of the project's dataset that a run did not cover.
	/// </summary>
	/// <param name="project">Project the run belongs to.</param>
	/// <param name="measured">Symbol the run traded.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The other symbols, or an empty list when there are none.</returns>
	/// <remarks>
	/// A run trades one instrument. When the caller does not name one it gets the first of the dataset,
	/// which is a choice being made on its behalf, and a two-symbol import measured on one of them looks
	/// exactly like a one-symbol import until somebody notices. Saying what was left out is cheaper than
	/// finding out later that half the data was never touched.
	/// </remarks>
	public async ValueTask<IReadOnlyList<string>> SymbolsNotCoveredAsync(
		ProjectId project,
		string measured,
		CancellationToken cancellationToken)
	{
		var existing = await _projects.OpenAsync(project, cancellationToken);

		if (existing.Dataset.IsEmpty)
			return [];

		var manifest = await _datasets.GetManifestAsync(project, existing.Dataset, cancellationToken);

		return [.. manifest.Symbols.Where(s => !string.Equals(s, measured, StringComparison.Ordinal))];
	}

	/// <summary>
	/// Lists the runs of a project.
	/// </summary>
	/// <param name="project">Project to list.</param>
	/// <param name="candidate">Candidate to list the runs of, or the default value for all of them.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The runs, oldest first.</returns>
	public ValueTask<IReadOnlyList<RunResult>> ListAsync(
		ProjectId project,
		CandidateId candidate,
		CancellationToken cancellationToken)
		=> _runs.ListAsync(project, candidate, cancellationToken);

	/// <summary>
	/// Reads the trades of a run.
	/// </summary>
	/// <param name="project">Project the run belongs to.</param>
	/// <param name="run">Run to read.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The trades.</returns>
	public async ValueTask<IReadOnlyList<ExecutedTrade>> ReadTradesAsync(
		ProjectId project,
		RunId run,
		CancellationToken cancellationToken)
	{
		var existing = await _runs.GetAsync(project, run, cancellationToken);
		var content = await _artifacts.ReadAsync(project, existing.Trades, cancellationToken);

		return JsonSerializer.Deserialize<ExecutedTrade[]>(content);
	}

	/// <summary>
	/// Cuts a run's result apart to see where it came from.
	/// </summary>
	/// <param name="project">Project the run belongs to.</param>
	/// <param name="run">Run to explain.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The result, cut by month, by part of the session, by holding time and by direction.</returns>
	/// <remarks>
	/// Nothing is re-run and nothing is charged: these are the trades the run already recorded, grouped.
	/// </remarks>
	public async ValueTask<RunBreakdown> ExplainAsync(
		ProjectId project,
		RunId run,
		CancellationToken cancellationToken)
	{
		var existing = await _runs.GetAsync(project, run, cancellationToken);
		var content = await _artifacts.ReadAsync(project, existing.Trades, cancellationToken);
		var trades = JsonSerializer.Deserialize<ExecutedTrade[]>(content);

		// The whole slice, not the window the run measured: the bars are read for the hours the instrument
		// trades in, and those are a property of the instrument rather than of one stretch of it.
		var bars = await _datasets.LoadAsync(
			project, existing.Dataset, existing.Symbol, existing.Slice, cancellationToken);

		return RunBreakdownCalculator.Measure(trades, _profiler.ActiveSession(bars));
	}

	/// <summary>
	/// Reads the equity curve of a run.
	/// </summary>
	/// <param name="project">Project the run belongs to.</param>
	/// <param name="run">Run to read.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The curve.</returns>
	public async ValueTask<IReadOnlyList<EquityPoint>> ReadEquityAsync(
		ProjectId project,
		RunId run,
		CancellationToken cancellationToken)
	{
		var existing = await _runs.GetAsync(project, run, cancellationToken);
		var content = await _artifacts.ReadAsync(project, existing.Equity, cancellationToken);

		return JsonSerializer.Deserialize<EquityPoint[]>(content);
	}

	/// <summary>
	/// How many units one position holds. The specification says what share of the account a position may
	/// take, so the run buys that much of it at the first price of the slice and keeps the size fixed for
	/// the whole run. Fixed rather than compounding, because a size that grows with the account turns a
	/// good first month into most of the result and hides what the rules actually did.
	/// </summary>
	/// <param name="spec">Specification whose risk block says how much of the account a position may take.</param>
	/// <param name="symbol">Symbol to be traded, which says how much one unit of it carries.</param>
	/// <param name="price">Price to size against.</param>
	/// <returns>Units of the symbol.</returns>
	/// <remarks>
	/// Sized by what a unit carries rather than by what it costs. A contract quoted at 9.05 carries 905
	/// dollars of exposure, so a tenth of a hundred thousand is eleven of them - not the eleven hundred
	/// that dividing by the quoted price would buy, which is a million dollars of exposure on a
	/// hundred-thousand-dollar account.
	/// </remarks>
	internal static decimal PositionSize(StrategySpec spec, string symbol, decimal price)
	{
		if (price <= 0)
			throw new InvalidOperationException("The first bar of the slice has no price to size a position against.");

		var exposure = price * ContractSymbol.SizeOf(symbol);

		return Math.Max(1m, Math.Floor(StartingEquity * spec.Risk.MaxPositionPercent / exposure));
	}

	/// <summary>
	/// Runs a candidate over the closed part of the history.
	/// </summary>
	/// <param name="project">Project the candidate belongs to.</param>
	/// <param name="candidate">Candidate to run.</param>
	/// <param name="window">Which part of the closed slice, or the whole of it.</param>
	/// <param name="symbol">Symbol to trade, or null for the first of the dataset.</param>
	/// <param name="scenario">Costs to charge.</param>
	/// <param name="parameters">Values to set on the strategy, or null for the ones it declares.</param>
	/// <param name="operationKey">Key that makes a repeated call return the first result.</param>
	/// <param name="actor">Who is asking.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The run and whether it was already there.</returns>
	/// <remarks>
	/// Internal, and called from exactly one place. The closed slice has one door, and whether a
	/// candidate may go through it is decided by the closed-data measurement rather than by whoever is
	/// asking.
	/// </remarks>
	internal ValueTask<(RunResult Run, bool WasAlreadyRun)> RunClosedAsync(
		ProjectId project,
		CandidateId candidate,
		RunWindow window,
		string symbol,
		RunScenario scenario,
		IReadOnlyDictionary<string, decimal> parameters,
		string operationKey,
		Actors actor,
		CancellationToken cancellationToken)
		=> RunCoreAsync(
			project, candidate, DataSlices.Final, window, symbol, scenario, parameters,
			operationKey, actor, cancellationToken);

	/// <summary>
	/// The failure as the harness's, or <see langword="null"/> when it is the candidate's.
	/// </summary>
	/// <remarks>
	/// The line. A worker that died mid-sentence or greeted with a protocol this server does not speak
	/// says nothing about the strategy inside it. A worker stopped on its deadline or its memory limit
	/// says a great deal: the machinery did precisely what it exists for, and the thing that would not
	/// stop was the candidate's own arithmetic - which is why the error contract already answers those
	/// two with a remediation addressed to the specification.
	/// </remarks>
	private static HarnessFailedException Blames(Exception error)
		=> error switch
		{
			HarnessFailedException broken => broken,

			IsolationFailedException { Kind: IsolationFailures.Crashed or IsolationFailures.Handshake } worker
				=> new(HarnessFailures.Worker, worker.Message, worker),

			_ => null,
		};

	private static byte[] Serialize<T>(IReadOnlyList<T> items)
		=> JsonSerializer.SerializeToUtf8Bytes(items);

	// The bars are named by what they are - instrument, candle length and range - rather than by the
	// dataset that imported them, so importing the same range again finds the runs already made over it.
	private static string Fingerprint(
		Candidate candidate,
		DatasetManifest manifest,
		DataSlices slice,
		RunWindow window,
		string symbol,
		RunScenario scenario,
		IReadOnlyDictionary<string, decimal> parameters)
	{
		var settings = string.Join(
			",",
			parameters
				.OrderBy(p => p.Key, StringComparer.Ordinal)
				.Select(p => $"{p.Key}={p.Value.ToString(CultureInfo.InvariantCulture)}"));

		var (from, to) = manifest.Split.BoundsOf(slice);

		var material =
			$"{candidate.AssemblyHash}|{symbol}|{manifest.TimeFrame.Ticks}|{from:O}|{to:O}|{slice}|" +
			$"{window.Index}/{window.Count}|" +
			$"{scenario.Name}|{scenario.Costs.Fees}|{scenario.Costs.HalfSpread}|{scenario.EntryDelayBars}|{settings}";

		return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
	}

	private async ValueTask<(RunResult Run, bool WasAlreadyRun)> RunCoreAsync(
		ProjectId project,
		CandidateId candidate,
		DataSlices slice,
		RunWindow window,
		string symbol,
		RunScenario scenario,
		IReadOnlyDictionary<string, decimal> parameters,
		string operationKey,
		Actors actor,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(window);
		ArgumentNullException.ThrowIfNull(scenario);
		ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);

		var recorded = await _operations.TryGetAsync(project.Value, operationKey, cancellationToken);

		if (recorded is not null)
			return (await _runs.GetAsync(project, RunId.Parse(recorded), cancellationToken), true);

		var existing = await _projects.OpenAsync(project, cancellationToken);

		if (existing.Dataset.IsEmpty)
			throw new InvalidOperationException("This project has no data, so there is nothing to run against.");

		var built = await _candidates.GetAsync(project, candidate, cancellationToken);
		var manifest = await _datasets.GetManifestAsync(project, existing.Dataset, cancellationToken);

		symbol = string.IsNullOrWhiteSpace(symbol) ? manifest.Symbols[0] : symbol.Trim();

		if (!manifest.Symbols.Contains(symbol, StringComparer.Ordinal))
		{
			throw new ArgumentException(
				$"This project holds no '{symbol}'. It covers {string.Join(", ", manifest.Symbols)}.",
				nameof(symbol));
		}

		parameters ??= new Dictionary<string, decimal>(StringComparer.Ordinal);

		var fingerprint = Fingerprint(built, manifest, slice, window, symbol, scenario, parameters);
		var already = await _runs.FindAsync(project, fingerprint, cancellationToken);

		if (already is not null)
		{
			await _operations.RecordAsync(project.Value, operationKey, already.Id.Value, cancellationToken);

			return (already, true);
		}

		// What a run that produced nothing points at, written before anything is claimed or started. An
		// attempt that ended badly still has to be recordable, and a record that needs the store which
		// has just failed is a record that is missing exactly when it is wanted. These are two bytes,
		// content-addressed, so every run of every project stores the same one.
		var empty = (await StoreAsync(project, Array.Empty<ExecutedTrade>(), cancellationToken)).Id;

		// Claimed where the number is kept rather than in a copy of it, so two runs arriving together
		// cost two. Kept only once the run is on record: see BudgetClaim.
		if (!await _projects.TryClaimAsync(project, backtests: 1, candidates: 0, cancellationToken))
		{
			var budget = ResearchBudget.Restore(existing.Budget);

			throw new ResearchBudgetExhaustedException(
				budget.RemainingWallClock == TimeSpan.Zero
					? $"This project has used the {existing.Budget.MaxWallClock.TotalMinutes:0} minutes of " +
						"machine time it was granted. Read what it measured rather than measuring more."
					: $"This project has already run its {existing.Budget.MaxBacktests} backtests. Read what " +
						"they measured rather than running another; a search that keeps running past its " +
						"allowance is looking for a result rather than testing a hypothesis.");
		}

		await using var claim = new BudgetClaim(_projects, project, backtests: 1, candidates: 0);

		var bars = window.Cut(await _datasets.LoadAsync(project, existing.Dataset, symbol, slice, cancellationToken));

		if (bars.Count == 0)
		{
			throw new InvalidOperationException(
				$"The {slice} slice of '{symbol}' holds no bars. Import more history: the slices are cut by " +
				"time, so a short import leaves the later ones empty.");
		}

		var assembly = await _artifacts.ReadAsync(project, built.Assembly, cancellationToken);
		var spec = SpecJson.Read((await _specs.GetAsync(project, built.Spec, cancellationToken)).Json);

		// The bar length is part of the hypothesis: an average of twenty five-minute bars and an average
		// of twenty daily ones are different claims. Running the rules over bars of another length
		// measures a strategy nobody wrote, and it used to happen silently.
		if (spec.TimeFrame != manifest.TimeFrame)
		{
			throw new InvalidOperationException(
				$"This candidate was written for {spec.TimeFrame} bars and the project holds " +
				$"{manifest.TimeFrame} ones. Import the history at {spec.TimeFrame}, or propose the " +
				"hypothesis for the bars you have; the rules mean different things at different lengths.");
		}
		var startedAt = _clock.UtcNow;

		// The bars themselves stay here. What crosses to the run is the storage folder and the range this
		// window covers, so the run reads the bars where they already are rather than being handed a copy.
		var range = new BarRange(
			_datasets.BarsFolder,
			bars[0].OpenTime,
			bars[^1].OpenTime + manifest.TimeFrame,
			bars.Count);

		var request = new BacktestRequest(
			assembly,
			built.ClassName,
			parameters,
			symbol,
			manifest.TimeFrame,
			range,
			StartingEquity,
			PositionSize(spec, symbol, bars[0].Open),

			PriceStep,
			scenario.Costs,
			scenario.EntryDelayBars);

		RunResult run;

		// Two failures with one shape until here, and they are opposite answers.
		//
		// A failure of the candidate's own - the strategy threw, or it would not stop and the worker
		// stopped it - is a result. It is recorded, it costs the allowance, and the caller is told which
		// of the two happened, because told only that the run failed an agent would read a limit of this
		// server as a property of its hypothesis.
		//
		// A failure of the machinery - the worker died mid-sentence, the handshake did not add up, the
		// result could not be written down - is not a result about anything. The candidate was never asked
		// the question, so the attempt is recorded to be seen and counted but the allowance is given back
		// and the caller is told to ask again.
		IsolationFailedException isolation = null;
		HarnessFailedException harness = null;

		try
		{
			var outcome = await _runner.RunAsync(request, cancellationToken);

			var metrics = RunMetricsCalculator.Measure(
				outcome.Trades,
				outcome.Equity,
				StartingEquity,
				bars[^1].OpenTime - bars[0].OpenTime,
				outcome.ExecutionErrorCount);

			var trades = await StoreAsync(project, outcome.Trades, cancellationToken);
			var equity = await StoreAsync(project, outcome.Equity, cancellationToken);

			run = new(
				RunId.New(),
				candidate,
				existing.Dataset,
				slice,
				window.Index,
				symbol,
				scenario.Name,
				fingerprint,
				parameters,
				RunStatuses.Completed,
				metrics,
				trades.Id,
				equity.Id,
				outcome.BarsProcessed,
				startedAt,
				_clock.UtcNow,
				null,
				RunDiagnosis.Explain(outcome.OrdersPlaced, outcome.Trades.Count, request.Volume, bars));
		}
		catch (Exception error) when (error is not OperationCanceledException)
		{
			harness = Blames(error);

			if (harness is null)
			{
				// A failed run still costs the allowance and is still recorded. It is evidence: a candidate
				// that cannot be run is a candidate that has been answered, and hiding the attempt would let
				// the same one be proposed again.
				isolation = error as IsolationFailedException;
			}

			run = Unmeasured(
				candidate, existing.Dataset, slice, window, symbol, scenario, fingerprint, parameters,
				harness is null ? RunStatuses.Failed : RunStatuses.Interrupted, empty, startedAt,
				(harness ?? error).Message);
		}

		if (harness is not null)
		{
			var interruptions = await InterruptionsAsync(project, candidate, cancellationToken);

			if (interruptions >= FreeInterruptions)
			{
				run = run with
				{
					Status = RunStatuses.Failed,
					Error =
						$"The machinery running this candidate has now failed {interruptions + 1} times, and " +
						$"the first {FreeInterruptions} were given back. A candidate that reliably stops the " +
						"process running it is a candidate that cannot be run, which is an answer about the " +
						$"candidate, so this attempt is charged like any other. What broke: {harness.Message}",
				};

				harness = null;
			}
		}

		// An interrupted attempt does not take the operation key. The caller is being told to ask again,
		// and a key already answered would hand it back this attempt instead of running one.
		if (harness is null)
		{
			var claimed = await _operations.RecordAsync(project.Value, operationKey, run.Id.Value, cancellationToken);

			if (claimed != run.Id.Value)
				return (await _runs.GetAsync(project, RunId.Parse(claimed), cancellationToken), true);
		}

		await _runs.AddAsync(project, run, cancellationToken);

		if (harness is null)
			claim.Keep();

		// Time is charged by work that finished, because nothing knows how long a run will take before it
		// takes it. Charged for an interrupted attempt as well, unlike the backtest itself: the allowance
		// counts answers and this one answered nothing, but the machine was occupied either way, and an
		// infrastructure that keeps failing must not be free of every limit at once.
		await _projects.ChargeTimeAsync(project, run.Elapsed, cancellationToken);
		await _projects.UpdateAsync(existing with { UpdatedAt = _clock.UtcNow }, cancellationToken);

		await _audit.AppendAsync(
			project,
			run.Status == RunStatuses.Completed ? AuditEventTypes.RunCompleted : AuditEventTypes.RunStarted,
			actor,
			run.Status switch
			{
				RunStatuses.Completed =>
					$"Ran {built.ClassName} on {symbol} over {slice} under {scenario.Name}: " +
					$"{run.Metrics.Trades.Count} trades, net {run.Metrics.Net.Profit:F2}.",

				RunStatuses.Interrupted =>
					$"Tried to run {built.ClassName} on {symbol} over {slice} under {scenario.Name} and this " +
					$"server's own machinery failed, so nothing was measured and no backtest was charged " +
					$"for it: {run.Error}",

				_ => $"Ran {built.ClassName} on {symbol} over {slice} under {scenario.Name} and it failed: {run.Error}",
			},
			fingerprint,
			cancellationToken);

		if (run.Status == RunStatuses.Completed && built.Status == CandidateStatuses.Compiled)
			await _candidates.UpdateAsync(project, built.WithStatus(CandidateStatuses.Backtested, _clock.UtcNow), cancellationToken);

		if (harness is not null)
			throw harness;

		if (isolation is not null)
			throw isolation;

		return (run, false);
	}

	/// <summary>Attempts on this candidate that the machinery already broke and already gave back.</summary>
	private async ValueTask<int> InterruptionsAsync(
		ProjectId project,
		CandidateId candidate,
		CancellationToken cancellationToken)
	{
		var earlier = await _runs.ListAsync(project, candidate, cancellationToken);

		return earlier.Count(r => r.Status == RunStatuses.Interrupted);
	}

	/// <summary>
	/// Stores a result series, treating a store that will not take it as the harness failing.
	/// </summary>
	private async ValueTask<ArtifactDescriptor> StoreAsync<T>(
		ProjectId project,
		IReadOnlyList<T> items,
		CancellationToken cancellationToken)
	{
		try
		{
			return await _artifacts.PutAsync(project, Serialize(items), cancellationToken);
		}
		catch (Exception error) when (error is not OperationCanceledException)
		{
			// The message of the failure underneath is not repeated: it can carry a path off this machine.
			throw new HarnessFailedException(
				HarnessFailures.Storage,
				"What the run produced could not be written down, so there is nothing to report from it.",
				error);
		}
	}

	/// <summary>A run that ended without measuring anything, whoever's fault that was.</summary>
	private RunResult Unmeasured(
		CandidateId candidate,
		DatasetId dataset,
		DataSlices slice,
		RunWindow window,
		string symbol,
		RunScenario scenario,
		string fingerprint,
		IReadOnlyDictionary<string, decimal> parameters,
		RunStatuses status,
		ArtifactId empty,
		DateTime startedAt,
		string error)
		=> new(
			RunId.New(),
			candidate,
			dataset,
			slice,
			window.Index,
			symbol,
			scenario.Name,
			fingerprint,
			parameters,
			status,
			null,
			empty,
			empty,
			0,
			startedAt,
			_clock.UtcNow,
			error,
			null);
}

/// <summary>
/// Thrown when a run that was asked for does not exist.
/// </summary>
public sealed class RunNotFoundException : Exception
{
	/// <summary>
	/// Creates the exception.
	/// </summary>
	/// <param name="id">Run that was not found.</param>
	public RunNotFoundException(RunId id)
		: base($"Run {id} does not exist in this project.")
	{
		Id = id;
	}

	/// <summary>Run that was not found.</summary>
	public RunId Id { get; }
}
