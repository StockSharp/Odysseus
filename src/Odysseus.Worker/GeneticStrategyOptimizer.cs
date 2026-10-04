namespace Odysseus.Worker;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Ecng.Common;
using Ecng.IO;

using StockSharp.Algo;
using StockSharp.Algo.Commissions;
using StockSharp.Algo.Strategies;
using StockSharp.Algo.Strategies.Optimization;
using StockSharp.Messages;

using Odysseus.Application;
using Odysseus.Platform;

/// <summary>
/// Searches a candidate's declared numbers with the genetic optimizer StockSharp already carries.
/// </summary>
/// <remarks>
/// Genetic once there is a space worth searching: eight numbers with twenty steps each is more settings
/// than a project's whole allowance, and walking all of them would mean the allowance runs out long
/// before the interesting region is reached. With a single number to vary there is no space and nothing
/// to breed — a genetic search cannot even form a chromosome from one gene — so its declared range is
/// simply walked end to end, which is both exact and cheap.
///
/// The search is seeded. A genetic search left to its own randomness lands somewhere different every
/// time it is asked, and a chosen setting nobody can arrive at twice is not a result — it is a number
/// that happened. The seed is part of what identifies the search, so the same project re-run picks the
/// same setting.
///
/// What comes back is every setting that was evaluated, ranked by the fitness below and nothing more.
/// The caller takes the best one through the ordinary runs and reads what those measured: a search
/// that reported its own result as the result would be marking its own paper.
/// </remarks>
internal sealed class GeneticStrategyOptimizer : IStrategyOptimizer
{
	private readonly int _batchSize;

	/// <summary>
	/// Creates the optimizer.
	/// </summary>
	/// <param name="batchSize">How many settings are evaluated at once.</param>
	public GeneticStrategyOptimizer(int batchSize)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

		_batchSize = batchSize;
	}

	/// <inheritdoc />
	public async Task<IReadOnlyList<OptimizationTrial>> SearchAsync(
		OptimizationRequest request,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);

		if (request.Bars.Count == 0)
			throw new ArgumentException("There are no bars to search over.", nameof(request));

		var security = EmulationSetup.CreateSecurity(request.Symbol, request.PriceStep);
		var portfolio = EmulationSetup.CreatePortfolio(request.StartingEquity);
		var storage = EmulationSetup.Open(request.Bars.Folder);

		var strategy = StrategyLoader.Instantiate(request.Assembly, request.ClassName, request.TimeFrame, null, entryDelayBars: 0);

		strategy.Security = security;
		strategy.Portfolio = portfolio;
		strategy.Volume = request.Volume;
		strategy.WaitRulesOnStop = false;

		// Everything the search does with randomness comes from here, so seeding this one thing makes the
		// whole search repeat.
		strategy.RandomProvider = new SeededRandomProvider(request.Seed);

		var declared = strategy.Parameters.Values.Where(p => p.CanOptimize).ToArray();

		// What the search will actually vary, which is narrower than what is merely marked as varyable:
		// a parameter without bounds has nothing to be searched between and is dropped here rather than
		// counted and then silently discarded further in.
		var genes = declared.Length == 0 ? [] : strategy.ToGeneticParameters(declared);

		if (genes.Length == 0)
		{
			throw new ArgumentException(
				"This candidate declares no number the search may vary, so there is nothing to search.",
				nameof(request));
		}

		var securities = new CollectionSecurityProvider([security]);
		var portfolios = new CollectionPortfolioProvider([portfolio]);

		var from = request.Bars.From;
		var to = request.Bars.To;

		var trials = new List<OptimizationTrial>();

		if (genes.Length == 1)
		{
			using var exhaustive = new BruteForceOptimizer(securities, portfolios, storage);

			Charge(exhaustive.EmulationSettings, request.Costs);

			var settings = strategy.ToBruteForceAsync([.. genes.Select(g => g.param)], out _, out _);

			await foreach (var (tried, parameters) in exhaustive
				.RunAsync(from, to, settings, cancellationToken)
				.WithCancellation(cancellationToken))
			{
				trials.Add(Describe(tried, parameters));
			}
		}
		else
		{
			using var genetic = new GeneticOptimizer(securities, portfolios, storage, new MemoryFileSystem());

			Charge(genetic.EmulationSettings, request.Costs);

			genetic.Settings.Population = request.Population;
			genetic.Settings.PopulationMax = request.Population;
			genetic.Settings.GenerationsMax = request.Generations;

			await foreach (var (tried, parameters) in genetic
				.RunAsync(from, to, strategy, genes, Fitness, cancellationToken: cancellationToken)
				.WithCancellation(cancellationToken))
			{
				trials.Add(Describe(tried, parameters));
			}
		}

		// Settings that score the same are put in the order of their values, because the runs finish in
		// whatever order the machine lets them and the same search has to come back the same way.
		return [.. trials
			.OrderByDescending(t => t.Fitness)
			.ThenBy(t => string.Join(";", t.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}")), StringComparer.Ordinal)];
	}

	/// <inheritdoc />
	/// <remarks>
	/// Each window's numbers are chosen by the platform's walk-forward search on the stretch they are fitted
	/// on, scored the way <see cref="SearchAsync"/> scores them. What they did on the stretch after it is
	/// then measured through the ordinary backtest, so the numbers reported come from the same path every
	/// other run of the project did.
	/// </remarks>
	public async Task<IReadOnlyList<WalkForwardWindowResult>> WalkForwardAsync(
		WalkForwardRequest request,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);

		if (request.Bars.Count == 0)
			throw new ArgumentException("There are no bars to walk forward over.", nameof(request));

		var security = EmulationSetup.CreateSecurity(request.Symbol, request.PriceStep);
		var portfolio = EmulationSetup.CreatePortfolio(request.StartingEquity);
		var storage = EmulationSetup.Open(request.Bars.Folder);

		var strategy = StrategyLoader.Instantiate(request.Assembly, request.ClassName, request.TimeFrame, null, entryDelayBars: 0);

		strategy.Security = security;
		strategy.Portfolio = portfolio;
		strategy.Volume = request.Volume;
		strategy.WaitRulesOnStop = false;

		var declared = strategy.Parameters.Values.Where(p => p.CanOptimize).ToArray();

		if (declared.Length == 0)
		{
			throw new ArgumentException(
				"This candidate declares no number the search may vary, so there is nothing to walk forward.",
				nameof(request));
		}

		var windows = WalkForwardWindow.Split(request.Bars.From, request.Bars.To, request.InSample, request.OutOfSample);

		if (windows.Count == 0)
		{
			throw new ArgumentException(
				$"The slice runs from {request.Bars.From:O} to {request.Bars.To:O}, shorter than one window of " +
				$"{request.InSample.TotalDays:0.#} days fitted and {request.OutOfSample.TotalDays:0.#} days tested.",
				nameof(request));
		}

		using var optimizer = new BruteForceOptimizer(
			new CollectionSecurityProvider([security]),
			new CollectionPortfolioProvider([portfolio]),
			storage);

		Charge(optimizer.EmulationSettings, request.Costs);

		var bars = new LocalBarStorage(request.Bars.Folder);
		var runner = new EmulatedBacktestRunner();
		var results = new List<WalkForwardWindowResult>();

		await foreach (var step in new WalkForwardOptimizer(optimizer)
			.RunAsync(strategy, declared, windows, Fitness, cancellationToken)
			.WithCancellation(cancellationToken))
		{
			var window = step.Window;

			var parameters = step.Parameters.ToDictionary(
				p => p.Key,
				p => p.Value.To<decimal>(),
				StringComparer.Ordinal);

			var tested = await bars.ReadAsync(
				request.Symbol, request.TimeFrame, window.OutOfSampleFrom, window.OutOfSampleTo, cancellationToken);

			var outcome = tested.Count == 0
				? new BacktestOutcome([], [new(window.OutOfSampleFrom, request.StartingEquity)], 0, 0, 0)
				: await runner.RunAsync(
					new(
						request.Assembly,
						request.ClassName,
						parameters,
						request.Symbol,
						request.TimeFrame,
						new(request.Bars.Folder, window.OutOfSampleFrom, window.OutOfSampleTo, tested.Count),
						request.StartingEquity,
						request.Volume,
						request.PriceStep,
						request.Costs),
					cancellationToken);

			results.Add(new(
				window.InSampleFrom,
				window.InSampleTo,
				window.OutOfSampleFrom,
				window.OutOfSampleTo,
				parameters,
				step.InSampleFitness,
				outcome));
		}

		return results;
	}

	// The same charge every ordinary run pays, so a setting that only works untaxed cannot win here
	// and then fail the moment it is measured properly.
	//
	// The batch size is pinned rather than left at what the platform defaults it to, which is twice the
	// processor count. Unpinned, a search runs sixty-four emulators at once on a large machine and four
	// on a small one, so both how much memory a search needs and how long it takes would be properties of
	// the hardware rather than of the experiment - on a product whose premise is that a measurement can
	// be arrived at twice.
	private void Charge(OptimizerSettings settings, ExecutionCosts costs)
	{
		settings.CommissionRules = [new CommissionTradeVolumeRule { Value = costs.PerUnit }];
		settings.IncreaseDepthVolume = false;
		settings.SpreadSize = 0;
		settings.BatchSize = _batchSize;
	}

	/// <summary>
	/// What the search is looking for.
	/// </summary>
	/// <param name="strategy">The setting being scored, after its run.</param>
	/// <returns>The score.</returns>
	/// <remarks>
	/// Profit against the drawdown it cost, rather than profit alone. A search rewarded for profit alone
	/// walks straight to the setting that made the most money once and sat through a fall nobody would
	/// have held through, and it does so reliably, because that setting is always in the space.
	///
	/// A setting with too few trades scores nothing at all. Two trades can produce any ratio you like,
	/// and a search that could win by trading twice will find the pair that did.
	/// </remarks>
	private static decimal Fitness(Strategy strategy)
	{
		var trades = Statistic(strategy, StatisticParameterTypes.TradeCount);

		if (trades < MinimumTrades)
			return 0;

		var profit = Statistic(strategy, StatisticParameterTypes.NetProfit);

		if (profit <= 0)
			return 0;

		// A run that never declined divides by the smallest fall worth speaking of rather than by nothing.
		var drawdown = Math.Max(Math.Abs(Statistic(strategy, StatisticParameterTypes.MaxDrawdown)), 1m);

		return profit / drawdown;
	}

	/// <summary>
	/// Below this a result is a handful of observations, and the search must not be able to win with it.
	/// The floor lives here and nowhere else: nothing downstream refuses a thin result, it is only
	/// reported, so a search allowed to win on two trades would hand back a setting that reads as the
	/// best one found while resting on nothing.
	/// </summary>
	private const int MinimumTrades = 30;

	private static OptimizationTrial Describe(Strategy strategy, IStrategyParam[] parameters)
		=> new(
			parameters.ToDictionary(p => p.Id, p => p.Value.To<decimal>(), StringComparer.Ordinal),
			Fitness(strategy),
			Statistic(strategy, StatisticParameterTypes.NetProfit),
			DrawdownPercent(strategy),
			(int)Statistic(strategy, StatisticParameterTypes.TradeCount));

	private static decimal DrawdownPercent(Strategy strategy)
	{
		var begin = strategy.Portfolio?.BeginValue ?? 0m;

		return begin == 0
			? 0
			: Math.Abs(Statistic(strategy, StatisticParameterTypes.MaxDrawdown)) / begin * 100m;
	}

	private static decimal Statistic(Strategy strategy, StatisticParameterTypes type)
	{
		var parameter = strategy.StatisticManager?.Parameters?.FirstOrDefault(p => p.Type == type);

		return parameter?.Value is null ? 0m : parameter.Value.To<decimal>();
	}
}
