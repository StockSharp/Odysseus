namespace Odysseus.Worker.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Odysseus.Application;
using Odysseus.Compiler;
using Odysseus.Platform;
using Odysseus.Domain;
using Odysseus.Spec;
using Odysseus.TestKit;

/// <summary>
/// The limit on what one day may lose.
/// </summary>
/// <remarks>
/// The specification has always carried this figure and nothing read it. A strategy that keeps buying
/// through a day that is going against it is exactly what the limit exists to stop, and a backtest that
/// ignores it measures a strategy nobody would be allowed to run.
///
/// The day is the market's own. A boundary at midnight UTC falls in the middle of the American
/// afternoon and would hand the strategy a fresh allowance halfway through a session.
/// </remarks>
[TestClass]
public class DailyLossTests : OdysseusTestBase
{
	private static readonly DateTime _start = new(2026, 3, 2, 14, 30, 0, DateTimeKind.Utc);

	/// <summary>A day that has lost what it may lose stops opening positions.</summary>
	[TestMethod]
	public async Task ALosingDayStops()
	{
		var withLimit = await RunAsync(Spec(dailyLoss: 0.002m));
		var without = await RunAsync(Spec(dailyLoss: 0m));

		IsTrue(without.Trades.Count > 0, "the case itself never traded.");

		IsTrue(withLimit.Trades.Count < without.Trades.Count,
			$"the limit changed nothing: {withLimit.Trades.Count} trades against {without.Trades.Count} without it.");

		foreach (var day in withLimit.Trades.GroupBy(t => MarketDate(t.EntryTime)))
		{
			var lost = -day.Sum(t => t.Net);

			IsTrue(lost < 100_000m * 0.002m * 3m,
				$"{day.Key:yyyy-MM-dd} lost {lost:F2} against a limit of {100_000m * 0.002m:F2}.");
		}
	}

	/// <summary>The next day starts again, because the limit stops the day rather than the strategy.</summary>
	[TestMethod]
	public async Task TheNextDayStartsAgain()
	{
		var outcome = await RunAsync(Spec(dailyLoss: 0.002m));

		var days = outcome.Trades.Select(t => MarketDate(t.EntryTime)).Distinct().Count();

		IsTrue(days > 1, $"trading stopped for good after the first day: it traded on {days} day(s).");
	}

	private static DateTime MarketDate(DateTime utc)
		=> TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), MarketSession.Zone).Date;

	private async Task<BacktestOutcome> RunAsync(StrategySpec spec)
	{
		var built = new StrategyBuilder(EngineAssemblies.Paths(AppContext.BaseDirectory)).Build(spec);

		return await new EmulatedBacktestRunner().RunAsync(
			new(
				built.Assembly,
				built.ClassName,
				new Dictionary<string, decimal>(),
				"DEMO",
				TimeSpan.FromMinutes(5),
				await StoredBars.WriteAsync(Falling(), "DEMO", TimeSpan.FromMinutes(5), CancellationToken),
				StartingEquity: 100_000m,
				Volume: 100m,
				PriceStep: 0.01m,
				ExecutionCosts.Default),
			CancellationToken);
	}

	/// <summary>Buy whenever the last bar closed above the one before, and let go two bars later.</summary>
	private static StrategySpec Spec(decimal dailyLoss)
		=> new()
		{
			Name = "Buys into a falling market",
			Thesis = "A bar that closed up is followed by another, which this market will not oblige.",
			AllowLong = true,
			AllowShort = false,
			TimeFrame = TimeSpan.FromMinutes(5),
			WarmupBars = 5,
			Entries =
			[
				new("e1", TradeDirections.Long,
					new Compare(
						new Field(CandleFields.Close),
						ComparisonOperators.GreaterThan,
						new Field(CandleFields.Close, 1))),
			],
			Exits = [new("x1", ExitKinds.TimeExit, TradeDirections.Long, Length: new Constant(2))],
			Parameters = [],
			Risk = new(0.10m, dailyLoss),
		};

	/// <summary>
	/// A market that falls all day, every day, with enough wobble that an entry rule keeps firing.
	/// </summary>
	private static IReadOnlyList<Candle> Falling()
	{
		var bars = new List<Candle>();
		var time = _start;
		var price = 200m;

		for (var day = 0; day < 4; day++)
		{
			for (var i = 0; i < 78; i++)
			{
				var wobble = i % 2 == 0 ? 0.6m : -1.4m;

				var open = price;
				var close = Math.Max(1m, open + wobble);

				bars.Add(new(time, open, Math.Max(open, close) + 0.05m, Math.Min(open, close) - 0.05m, close, 100_000m));

				price = close;
				time = time.AddMinutes(5);
			}

			time = time.Date.AddDays(1).Add(_start.TimeOfDay);
		}

		return bars;
	}
}
