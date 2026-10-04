namespace Odysseus.Worker.Tests;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Odysseus.Application;
using Odysseus.Compiler;
using Odysseus.Platform;
using Odysseus.Domain;
using Odysseus.Spec;
using Odysseus.TestKit;

/// <summary>
/// A run whose orders were larger than the market that was there to fill them.
/// </summary>
/// <remarks>
/// A bar holds what was traded in it and no more, so an order bigger than the bar fills only that much and
/// the rest is cancelled. A strategy that sizes above what an instrument trades therefore trades less than
/// it asked for, and a run has to be able to say so rather than report the smaller trades as though they
/// were what the strategy meant.
/// </remarks>
[TestClass]
public class UnfilledOrderTests : OdysseusTestBase
{
	private static readonly DateTime _open = new(2026, 3, 2, 14, 0, 0, DateTimeKind.Utc);

	/// <summary>An order larger than the bar fills no more than the bar carried, and the orders are counted.</summary>
	[TestMethod]
	public async Task AnOrderLargerThanTheBarFillsOnlyWhatTheBarCarried()
	{
		var tooBig = await RunAsync(volume: 50m, barVolume: 10m);

		IsTrue(tooBig.OrdersPlaced > 0,
			"the strategy placed orders and the run reports none, so nothing distinguishes this from a " +
			"hypothesis that never triggered.");

		IsTrue(tooBig.Trades.All(t => t.Volume <= 10m),
			"an order of fifty filled more than the ten the bar carried.");
	}

	/// <summary>The same strategy, sized to what the bars carry, trades — so the size is what stopped it.</summary>
	[TestMethod]
	public async Task TheSameStrategyTradesWhenItFits()
	{
		var fits = await RunAsync(volume: 5m, barVolume: 10m);

		IsTrue(fits.Trades.Count > 0, "sized within the bar, the strategy should have traded.");
		IsTrue(fits.OrdersPlaced >= fits.Trades.Count, "every trade came from an order.");
	}

	private async Task<BacktestOutcome> RunAsync(decimal volume, decimal barVolume)
	{
		var built = new StrategyBuilder(EngineAssemblies.Paths(AppContext.BaseDirectory)).Build(Spec());

		return await new EmulatedBacktestRunner().RunAsync(
			new(
				built.Assembly,
				built.ClassName,
				new Dictionary<string, decimal>(),
				"DEMO",
				TimeSpan.FromMinutes(5),
				await StoredBars.WriteAsync(Waves(600, barVolume), "DEMO", TimeSpan.FromMinutes(5), CancellationToken),
				StartingEquity: 100_000m,
				Volume: volume,
				PriceStep: 0.01m,
				ExecutionCosts.Default),
			CancellationToken);
	}

	/// <summary>Buy above the average of the last twenty bars, let go five bars later.</summary>
	private static StrategySpec Spec()
		=> new()
		{
			Name = "Above its average",
			Thesis = "A price above its own recent average keeps going for a few bars.",
			AllowLong = true,
			AllowShort = false,
			TimeFrame = TimeSpan.FromMinutes(5),
			WarmupBars = 45,
			Entries =
			[
				new("e1", TradeDirections.Long,
					new Compare(
						new Field(CandleFields.Close),
						ComparisonOperators.GreaterThan,
						new IndicatorRef("sma", new Constant(20), CandleFields.Close))),
			],
			Exits = [new("x1", ExitKinds.TimeExit, TradeDirections.Long, Length: new Constant(5))],
			Parameters = [],
			Risk = new(0.10m, 0.02m),
		};

	/// <summary>Bars that walk up and back down on a fixed period, each carrying the same volume.</summary>
	private static IReadOnlyList<Candle> Waves(int count, decimal barVolume)
	{
		var bars = new List<Candle>(count);
		var time = _open;

		for (var i = 0; i < count; i++)
		{
			var wave = (decimal)Math.Sin(i * 2 * Math.PI / 60);
			var close = 100m + Math.Round(5m * wave, 2);
			var open = i == 0 ? close : bars[^1].Close;

			bars.Add(new(
				time,
				open,
				Math.Max(open, close) + 0.05m,
				Math.Min(open, close) - 0.05m,
				close,
				barVolume));

			time = time.AddMinutes(5);

			if (time.TimeOfDay >= TimeSpan.FromHours(20))
				time = time.Date.AddDays(time.DayOfWeek == DayOfWeek.Friday ? 3 : 1).Add(_open.TimeOfDay);
		}

		return bars;
	}
}
