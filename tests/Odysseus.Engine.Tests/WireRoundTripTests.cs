namespace Odysseus.Engine.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Odysseus.Application;
using Odysseus.Domain;
using Odysseus.Engine;
using Odysseus.TestKit;

/// <summary>
/// What crosses the process boundary, and whether it comes back the same.
/// </summary>
/// <remarks>
/// Everything on the ports was already plain data - that is the hexagonal design paying off - but
/// "plain data" and "round-trips through a serializer" are not the same claim, and the members typed as
/// read-only interfaces are where the difference would show up. Cheap to check here; expensive to
/// discover in the middle of a measurement.
/// </remarks>
[TestClass]
public class WireRoundTripTests : OdysseusTestBase
{
	private static readonly DateTime _open = new(2026, 3, 2, 14, 0, 0, DateTimeKind.Utc);

	/// <summary>A backtest request survives the crossing, including the bars and the parameters.</summary>
	[TestMethod]
	public async Task ABacktestRequestCrossesUnchanged()
	{
		var request = new WorkerRequest("r1", WorkerCommands.Backtest, Backtest(), null, null, 4);

		var back = await RoundTripAsync(request);

		AreEqual(request.Id, back.Id);
		AreEqual(WorkerCommands.Backtest, back.Command);
		AreEqual(4, back.BatchSize);

		AreEqual(request.Backtest.ClassName, back.Backtest.ClassName);
		AreEqual(request.Backtest.Symbol, back.Backtest.Symbol);
		AreEqual(request.Backtest.TimeFrame, back.Backtest.TimeFrame);
		AreEqual(request.Backtest.EntryDelayBars, back.Backtest.EntryDelayBars);
		AreEqual(request.Backtest.Costs.HalfSpread, back.Backtest.Costs.HalfSpread);

		AreEqual(request.Backtest.Assembly.Length, back.Backtest.Assembly.Length);
		AreEqual(request.Backtest.Assembly[2], back.Backtest.Assembly[2]);

		AreEqual(request.Backtest.Bars, back.Backtest.Bars);

		AreEqual(1, back.Backtest.Parameters.Count);
		AreEqual(20m, back.Backtest.Parameters["Length"]);
	}

	/// <summary>A search request survives it too, with the three numbers that make a search a search.</summary>
	[TestMethod]
	public async Task AnOptimizationRequestCrossesUnchanged()
	{
		var request = new WorkerRequest("r2", WorkerCommands.Optimize, null, Optimization(), null, 4);

		var back = await RoundTripAsync(request);

		AreEqual(WorkerCommands.Optimize, back.Command);
		IsNull(back.Backtest, "a search carried a backtest across with it.");

		AreEqual(request.Optimization.Population, back.Optimization.Population);
		AreEqual(request.Optimization.Generations, back.Optimization.Generations);
		AreEqual(request.Optimization.Seed, back.Optimization.Seed);
		AreEqual(request.Optimization.Bars, back.Optimization.Bars);
	}

	/// <summary>
	/// What comes back is what a measurement is computed from, so it matters more than the request does.
	/// </summary>
	[TestMethod]
	public async Task AnAnswerCrossesUnchanged()
	{
		var trade = new ExecutedTrade(
			"t1", "DEMO", TradeDirections.Long, _open, 100m, _open.AddMinutes(30), 101m, 10m, 0.02m, 0.1m);

		var answer = new WorkerAnswer(
			"r1",
			true,
			new([trade], [new(_open, 100_000m), new(_open.AddHours(1), 100_500m)], 480, 0, 3),
			[new(new Dictionary<string, decimal>(StringComparer.Ordinal) { ["Length"] = 20m }, 1.5m, 500m, 2m, 40)],
			null,
			null);

		var back = await RoundTripAsync<WorkerAnswer>(answer);

		IsTrue(back.Succeeded, "a successful answer came back as a failure.");
		AreEqual(1, back.Outcome.Trades.Count);
		AreEqual(trade, back.Outcome.Trades[0]);
		AreEqual(2, back.Outcome.Equity.Count);
		AreEqual(480, back.Outcome.BarsProcessed);
		AreEqual(3, back.Outcome.OrdersPlaced);

		AreEqual(1, back.Trials.Count);
		AreEqual(20m, back.Trials[0].Parameters["Length"]);
		AreEqual(40, back.Trials[0].Trades);
	}

	/// <summary>
	/// A candidate that failed crosses as a failure with its message, which is what the host re-raises so
	/// that a failed run is recorded exactly as it always was.
	/// </summary>
	[TestMethod]
	public async Task AFailureCarriesItsReason()
	{
		var back = await RoundTripAsync<WorkerAnswer>(new("r1", false, null, null, null, "it divided by zero"));

		IsFalse(back.Succeeded, "a failure came back as a success.");
		AreEqual("it divided by zero", back.Failure);
		IsNull(back.Outcome, "a failure carried an outcome across with it.");
	}

	/// <summary>A frame larger than the protocol carries is refused rather than allocated for.</summary>
	[TestMethod]
	public async Task AFrameThatAnnouncesTooMuchIsRefused()
	{
		using var stream = new MemoryStream([0x7F, 0xFF, 0xFF, 0xFF]);

		await ThrowsAsync<InvalidOperationException>(
			() => WorkerProtocol.ReadAsync<WorkerAnswer>(stream, CancellationToken));
	}

	/// <summary>A closed pipe is the end of the conversation rather than a malformed frame.</summary>
	[TestMethod]
	public async Task AClosedPipeReadsAsNothing()
	{
		using var stream = new MemoryStream([]);

		IsNull(await WorkerProtocol.ReadAsync<WorkerAnswer>(stream, CancellationToken),
			"a pipe that closed cleanly was read as something.");
	}

	private async Task<T> RoundTripAsync<T>(T message)
		where T : class
	{
		using var stream = new MemoryStream();

		await WorkerProtocol.WriteAsync(stream, message, CancellationToken);

		stream.Position = 0;

		return await WorkerProtocol.ReadAsync<T>(stream, CancellationToken);
	}

	private static BacktestRequest Backtest()
		=> new(
			[1, 2, 3, 4, 5],
			"Odysseus.Generated.Candidate",
			new Dictionary<string, decimal>(StringComparer.Ordinal) { ["Length"] = 20m },
			"DEMO",
			TimeSpan.FromMinutes(5),
			Bars(),
			StartingEquity: 100_000m,
			Volume: 10m,
			PriceStep: 0.01m,
			ExecutionCosts.Default,
			EntryDelayBars: 1);

	private static OptimizationRequest Optimization()
		=> new(
			[1, 2, 3],
			"Odysseus.Generated.Candidate",
			"DEMO",
			TimeSpan.FromMinutes(5),
			Bars(),
			StartingEquity: 100_000m,
			Volume: 10m,
			PriceStep: 0.01m,
			ExecutionCosts.Default,
			Population: 10,
			Generations: 5,
			Seed: 42);

	private static BarRange Bars()
		=> new("projects/p1/datasets/ds_0/bars/development", _open, _open.AddMinutes(15), 3);
}
