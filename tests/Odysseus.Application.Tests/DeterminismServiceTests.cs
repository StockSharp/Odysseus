namespace Odysseus.Application.Tests;

using System;
using System.Collections.Generic;
using System.IO;
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
/// Whether a candidate gives the same answer twice: what its source reaches, and two runs set side by side.
/// </summary>
[TestClass]
public class DeterminismServiceTests : OdysseusTestBase
{
	private static readonly DateTime _open = new(2026, 3, 2, 14, 30, 0, DateTimeKind.Utc);

	/// <summary>A builder that hands back something assembly-shaped, since nothing here runs it.</summary>
	private sealed class Builder : IStrategyBuilder
	{
		public BuiltStrategy Build(StrategySpec spec)
			=> new("Generated", $"// source of {spec.Name}", $"source-{spec.Name}", [1, 2, 3], $"assembly-{spec.Name}", "1.0.0");
	}

	/// <summary>A source reader that reports whatever the test says the source breaks.</summary>
	private sealed class Inspector : IStrategyInspector
	{
		public IReadOnlyList<BuildProblem> Problems { get; set; } = [];

		public string Inspected { get; private set; }

		public IReadOnlyList<BuildProblem> Inspect(string source)
		{
			Inspected = source;
			return Problems;
		}
	}

	/// <summary>A runner whose runs the test decides, one after another.</summary>
	private sealed class Runner : IBacktestRunner
	{
		private int _runs;

		public decimal SecondEntry { get; set; } = 100m;

		public int Runs => _runs;

		public Task<BacktestOutcome> RunAsync(BacktestRequest request, CancellationToken cancellationToken)
		{
			var entry = Interlocked.Increment(ref _runs) == 1 ? 100m : SecondEntry;
			var time = request.Bars.From.AddHours(1);

			return Task.FromResult(new BacktestOutcome(
				[new("t1", request.Symbol, TradeDirections.Long, time, entry, time.AddMinutes(30), 101m, 10m, 0.5m, 0.5m)],
				[new(time, 100_000m)],
				request.Bars.Count,
				0,
				2));
		}
	}

	private string _root;
	private SqliteProjectStore _store;
	private SqliteOperationLog _operations;
	private FileArtifactStore _artifacts;
	private FileDatasetStore _datasets;
	private FileSpecStore _specs;
	private Inspector _inspector;
	private Runner _runner;
	private ProjectService _projects;
	private CandidateService _candidates;
	private DeterminismService _service;

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
		_inspector = new Inspector();
		_runner = new Runner();

		var clock = new SystemClock();
		var budget = new ResearchBudget(400, 5, TimeSpan.FromMinutes(45)).ToState();

		_projects = new ProjectService(_store, _store, _operations, clock, ServerModes.Local, budget);
		_candidates = new CandidateService(_store, _specs, _store, _artifacts, new Builder(), _store, _operations, clock);

		_service = new DeterminismService(_store, _store, _specs, _datasets, _artifacts, _inspector, _runner, _store, _operations);
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

	/// <summary>Two runs that made the same trades, from a source that breaks nothing, are one answer.</summary>
	[TestMethod]
	public async Task TwoAlikeRunsOfACleanSourceAreDeterministic()
	{
		var (project, candidate) = await ReadyAsync();

		var report = await _service.CheckAsync(project, candidate, "NVDA", Guid.NewGuid().ToString("n"), Actors.Agent, CancellationToken);

		AreEqual(0, report.Problems.Count);
		IsTrue(report.RunsMatched, report.FirstDivergence);
		IsNull(report.FirstDivergence);
		AreEqual(2, _runner.Runs, "the candidate was not run twice; a run answered from the record proves nothing.");
		IsTrue(_inspector.Inspected.Contains("source of", StringComparison.Ordinal), "the candidate's own source was not the one read.");
	}

	/// <summary>Two runs that differ say where they first differed.</summary>
	[TestMethod]
	public async Task RunsThatDifferSayWhere()
	{
		var (project, candidate) = await ReadyAsync();

		_runner.SecondEntry = 100.5m;

		var report = await _service.CheckAsync(project, candidate, "NVDA", Guid.NewGuid().ToString("n"), Actors.Agent, CancellationToken);

		IsFalse(report.RunsMatched, "two runs that entered at different prices were reported alike.");
		IsTrue(report.FirstDivergence.Contains("Trade 1", StringComparison.Ordinal), report.FirstDivergence);
	}

	/// <summary>What the source breaks is reported even when the runs happen to agree.</summary>
	[TestMethod]
	public async Task WhatTheSourceBreaksIsReported()
	{
		var (project, candidate) = await ReadyAsync();

		_inspector.Problems = [new("ODSTR012", "'System.DateTime.Now' reads the machine clock.", 42)];

		var report = await _service.CheckAsync(project, candidate, "NVDA", Guid.NewGuid().ToString("n"), Actors.Agent, CancellationToken);

		AreEqual(1, report.Problems.Count);
		AreEqual("ODSTR012", report.Problems[0].Rule);
		IsTrue(report.RunsMatched, "a clock on a path the history never took does not make the runs differ.");
	}

	/// <summary>The check costs its two runs, and the same key asked again costs nothing more.</summary>
	[TestMethod]
	public async Task TheCheckCostsTwoRunsOnce()
	{
		var (project, candidate) = await ReadyAsync();

		var before = await SpentAsync(project);
		var key = Guid.NewGuid().ToString("n");

		await _service.CheckAsync(project, candidate, "NVDA", key, Actors.Agent, CancellationToken);

		AreEqual(before + 2, await SpentAsync(project), "the check cost something other than its two runs.");

		var again = await _service.CheckAsync(project, candidate, "NVDA", key, Actors.Agent, CancellationToken);

		IsTrue(again.WasAlreadyChecked, "a repeated key was checked again.");
		AreEqual(before + 2, await SpentAsync(project), "the repeated key was charged again.");
		AreEqual(2, _runner.Runs, "the repeated key ran the candidate again.");
	}

	/// <summary>A project with one backtest left cannot pay for two runs, and neither is started.</summary>
	[TestMethod]
	public async Task TwoRunsThatCannotBePaidForAreRefused()
	{
		var (project, candidate) = await ReadyAsync();

		var left = ResearchBudget.Restore((await _store.OpenAsync(project, CancellationToken)).Budget).RemainingBacktests;

		IsTrue(await _store.TryClaimAsync(project, left - 1, 0, CancellationToken));

		var refusal = await ThrowsAsync<ResearchBudgetExhaustedException>(() => _service.CheckAsync(
			project, candidate, "NVDA", Guid.NewGuid().ToString("n"), Actors.Agent, CancellationToken).AsTask());

		IsTrue(refusal.Message.Contains("two backtests", StringComparison.Ordinal), refusal.Message);
		AreEqual(0, _runner.Runs, "the candidate was run on a budget that could not pay for the check.");
	}

	private async Task<int> SpentAsync(ProjectId project)
	{
		var existing = await _store.OpenAsync(project, CancellationToken);
		var budget = ResearchBudget.Restore(existing.Budget);

		return existing.Budget.MaxBacktests - budget.RemainingBacktests;
	}

	private async Task<(ProjectId Project, CandidateId Candidate)> ReadyAsync()
	{
		var created = await _projects.CreateProjectAsync("determinism", Guid.NewGuid().ToString("n"), Actors.User, CancellationToken);

		var project = created.Id;

		var bars = new Dictionary<string, IReadOnlyList<Candle>> { ["NVDA"] = Bars() };
		var imported = DatasetBuilder.Build(bars, TimeSpan.FromMinutes(5), "test", isSynthetic: true);

		await _datasets.SaveAsync(project, imported, CancellationToken);

		var opened = await _store.OpenAsync(project, CancellationToken);

		await _store.UpdateAsync(opened.WithDataset(imported.Manifest.Id, DateTime.UtcNow), CancellationToken);

		var spec = await _specs.AddAsync(project, Spec(), Actors.Agent, DateTime.UtcNow, CancellationToken);

		var built = await _candidates.BuildAsync(project, spec.Id, Guid.NewGuid().ToString("n"), Actors.Agent, CancellationToken);

		return (project, built.Id);
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
		        "right": { "kind": "Indicator", "name": "sma", "length": { "kind": "Constant", "value": 20 }, "source": "Close" } } }
		  ],
		  "exits": [ { "id": "x1", "kind": "TimeExit", "direction": "Long", "length": { "kind": "Constant", "value": 5 } } ],
		  "parameters": [],
		  "risk": { "maxPositionPercent": 0.10, "maxDailyLossPercent": 0.02 }
		}
		""";
}
