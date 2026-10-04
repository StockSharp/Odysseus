namespace Odysseus.Application;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Odysseus.Domain;
using Odysseus.Spec;

/// <summary>
/// Reads a strategy's source for what would make its results differ between runs over the same data.
/// </summary>
public interface IStrategyInspector
{
	/// <summary>
	/// Checks source against the rules a reproducible strategy keeps.
	/// </summary>
	/// <param name="source">The strategy's source.</param>
	/// <returns>Everything that breaks a rule, each with where it is.</returns>
	IReadOnlyList<BuildProblem> Inspect(string source);
}

/// <summary>
/// Whether a candidate gives the same answer twice.
/// </summary>
/// <param name="Problems">What its source reaches that differs from run to run.</param>
/// <param name="RunsMatched">Whether two runs over the same bars produced the same trades.</param>
/// <param name="FirstTrades">Trades of the first run.</param>
/// <param name="SecondTrades">Trades of the second run.</param>
/// <param name="FirstDivergence">Where the two runs first differed, or null when they did not.</param>
/// <param name="WasAlreadyChecked">Whether this answers a request already made under the same key.</param>
public sealed record DeterminismReport(
	IReadOnlyList<BuildProblem> Problems,
	bool RunsMatched,
	int FirstTrades,
	int SecondTrades,
	string FirstDivergence,
	bool WasAlreadyChecked);

/// <summary>
/// Checks whether a candidate is a function of its data: the same history in, the same trades out.
/// </summary>
/// <remarks>
/// Two answers, because each catches what the other cannot. Reading the source finds a clock or an
/// unseeded draw wherever it is written, even on a path the history never takes. Running twice finds what
/// the source does not show, such as an engine that decides between equal events by timing.
/// </remarks>
public sealed class DeterminismService
{
	private readonly IProjectStore _projects;
	private readonly ICandidateStore _candidates;
	private readonly ISpecStore _specs;
	private readonly IDatasetStore _datasets;
	private readonly IArtifactStore _artifacts;
	private readonly IStrategyInspector _inspector;
	private readonly IBacktestRunner _runner;
	private readonly IAuditLog _audit;
	private readonly IOperationLog _operations;

	/// <summary>
	/// Creates the service.
	/// </summary>
	/// <param name="projects">Where projects are kept.</param>
	/// <param name="candidates">Where candidates are kept.</param>
	/// <param name="specs">Where specifications are kept.</param>
	/// <param name="datasets">Where datasets are kept.</param>
	/// <param name="artifacts">Where sources, assemblies and reports are kept.</param>
	/// <param name="inspector">What reads a source for the rules.</param>
	/// <param name="runner">What runs a candidate over bars.</param>
	/// <param name="audit">Where the permanent record is kept.</param>
	/// <param name="operations">Where operation keys are remembered.</param>
	public DeterminismService(
		IProjectStore projects,
		ICandidateStore candidates,
		ISpecStore specs,
		IDatasetStore datasets,
		IArtifactStore artifacts,
		IStrategyInspector inspector,
		IBacktestRunner runner,
		IAuditLog audit,
		IOperationLog operations)
	{
		_projects = projects ?? throw new ArgumentNullException(nameof(projects));
		_candidates = candidates ?? throw new ArgumentNullException(nameof(candidates));
		_specs = specs ?? throw new ArgumentNullException(nameof(specs));
		_datasets = datasets ?? throw new ArgumentNullException(nameof(datasets));
		_artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
		_inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
		_runner = runner ?? throw new ArgumentNullException(nameof(runner));
		_audit = audit ?? throw new ArgumentNullException(nameof(audit));
		_operations = operations ?? throw new ArgumentNullException(nameof(operations));
	}

	/// <summary>
	/// Checks a candidate: its source against the rules, and two runs of it over the development slice
	/// against each other.
	/// </summary>
	/// <param name="project">Project the candidate belongs to.</param>
	/// <param name="candidate">Candidate to check.</param>
	/// <param name="symbol">Symbol to run, or null for the first of the dataset.</param>
	/// <param name="operationKey">Key that makes a repeated call return the first result.</param>
	/// <param name="actor">Who is asking.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>What was found.</returns>
	/// <remarks>The two runs cost two backtests, claimed before either starts.</remarks>
	public async ValueTask<DeterminismReport> CheckAsync(
		ProjectId project,
		CandidateId candidate,
		string symbol,
		string operationKey,
		Actors actor,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);

		var recorded = await _operations.TryGetAsync(project.Value, operationKey, cancellationToken);

		if (recorded is not null)
		{
			var first = await _artifacts.ReadAsync(project, ArtifactId.Parse(recorded), cancellationToken);

			return JsonSerializer.Deserialize<DeterminismReport>(first) with { WasAlreadyChecked = true };
		}

		var existing = await _projects.OpenAsync(project, cancellationToken);

		if (existing.Dataset.IsEmpty)
			throw new InvalidOperationException("This project has no data, so there is nothing to run the candidate over.");

		var built = await _candidates.GetAsync(project, candidate, cancellationToken);
		var manifest = await _datasets.GetManifestAsync(project, existing.Dataset, cancellationToken);

		symbol = string.IsNullOrWhiteSpace(symbol) ? manifest.Symbols[0] : symbol.Trim();

		var source = Encoding.UTF8.GetString(await _artifacts.ReadAsync(project, built.Source, cancellationToken));
		var problems = _inspector.Inspect(source);

		var bars = await _datasets.LoadAsync(project, existing.Dataset, symbol, DataSlices.Development, cancellationToken);

		if (bars.Count == 0)
			throw new InvalidOperationException($"The development slice of '{symbol}' holds no bars to run over.");

		if (!await _projects.TryClaimAsync(project, backtests: 2, candidates: 0, cancellationToken))
		{
			throw new ResearchBudgetExhaustedException(
				"Running the candidate twice costs two backtests, and this project cannot afford them.");
		}

		await using var claim = new BudgetClaim(_projects, project, backtests: 2, candidates: 0);

		var spec = SpecJson.Read((await _specs.GetAsync(project, built.Spec, cancellationToken)).Json);

		var request = new BacktestRequest(
			await _artifacts.ReadAsync(project, built.Assembly, cancellationToken),
			built.ClassName,
			spec.Parameters.ToDictionary(p => p.Name, p => p.Default, StringComparer.Ordinal),
			symbol,
			manifest.TimeFrame,
			new(
				_datasets.BarsFolder,
				bars[0].OpenTime,
				bars[^1].OpenTime + manifest.TimeFrame,
				bars.Count),
			BacktestService.StartingEquity,
			BacktestService.PositionSize(spec, symbol, bars[0].Open),
			BacktestService.PriceStep,
			ExecutionCosts.Default);

		var started = Stopwatch.GetTimestamp();

		BacktestOutcome firstRun;
		BacktestOutcome secondRun;

		try
		{
			// Run straight through the runner rather than through the backtests: those answer a run already
			// on record from the record, and a second answer from the record proves nothing.
			firstRun = await _runner.RunAsync(request, cancellationToken);
			secondRun = await _runner.RunAsync(request, cancellationToken);
		}
		finally
		{
			await _projects.ChargeTimeAsync(project, Stopwatch.GetElapsedTime(started), CancellationToken.None);
		}

		var divergence = FirstDivergence(firstRun, secondRun);

		var report = new DeterminismReport(
			problems,
			divergence is null,
			firstRun.Trades.Count,
			secondRun.Trades.Count,
			divergence,
			WasAlreadyChecked: false);

		var stored = await _artifacts.PutAsync(project, JsonSerializer.SerializeToUtf8Bytes(report), cancellationToken);

		claim.Keep();

		await _operations.RecordAsync(project.Value, operationKey, stored.Id.Value, cancellationToken);

		await _audit.AppendAsync(
			project,
			AuditEventTypes.RunCompleted,
			actor,
			$"Checked {built.ClassName} for determinism: {problems.Count} rule(s) broken in the source, " +
			$"{(report.RunsMatched ? "two runs alike" : "two runs differed")}.",
			built.SourceHash,
			cancellationToken);

		return report;
	}

	private static string FirstDivergence(BacktestOutcome first, BacktestOutcome second)
	{
		for (var i = 0; i < Math.Min(first.Trades.Count, second.Trades.Count); i++)
		{
			var a = first.Trades[i];
			var b = second.Trades[i];

			if (a.Direction != b.Direction || a.EntryTime != b.EntryTime || a.EntryPrice != b.EntryPrice
				|| a.ExitTime != b.ExitTime || a.ExitPrice != b.ExitPrice || a.Volume != b.Volume)
			{
				return $"Trade {i + 1} differs: {Describe(a)} on the first run, {Describe(b)} on the second.";
			}
		}

		if (first.Trades.Count != second.Trades.Count)
			return $"The first run made {first.Trades.Count} trades and the second {second.Trades.Count}.";

		if (first.OrdersPlaced != second.OrdersPlaced)
			return $"The trades match but the first run placed {first.OrdersPlaced} orders and the second {second.OrdersPlaced}.";

		return null;
	}

	private static string Describe(ExecutedTrade trade)
		=> $"{trade.Direction} {trade.Volume} at {trade.EntryPrice} on {trade.EntryTime:O}, out at {trade.ExitPrice} on {trade.ExitTime:O}";
}
