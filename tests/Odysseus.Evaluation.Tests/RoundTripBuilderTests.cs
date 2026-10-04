namespace Odysseus.Evaluation.Tests;

using System;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Odysseus.Domain;
using Odysseus.Evaluation;
using Odysseus.TestKit;

/// <summary>
/// Turning the fills an engine reports into the round trips a result is measured on.
/// </summary>
/// <remarks>
/// An engine reports fills, not trades: it says a hundred shares were bought and later that a hundred
/// were sold, and nothing joins the two. Everything a reader wants to know — what the trade made, how
/// long it was held, whether it was the one that carried the result — lives in that join, so it is
/// worth being exact about, including in the cases our own generated strategies never produce.
/// </remarks>
[TestClass]
public class RoundTripBuilderTests : OdysseusTestBase
{
	private static readonly DateTime _start = new(2026, 3, 2, 14, 0, 0, DateTimeKind.Utc);

	private static Fill Buy(int minute, decimal price, decimal volume = 10m, decimal commission = 0m, decimal slippage = 0m)
		=> new("NVDA", TradeDirections.Long, _start.AddMinutes(minute), price, volume, commission, slippage);

	private static Fill Sell(int minute, decimal price, decimal volume = 10m, decimal commission = 0m, decimal slippage = 0m)
		=> new("NVDA", TradeDirections.Short, _start.AddMinutes(minute), price, volume, commission, slippage);

	/// <summary>A buy and a later sell of the same size is one long trade.</summary>
	[TestMethod]
	public void ABuyAndASellBecomeOneLongTrade()
	{
		var trade = RoundTripBuilder.Build([Buy(0, 100m), Sell(30, 102m)]).Single();

		AreEqual(TradeDirections.Long, trade.Direction);
		AreEqual(100m, trade.EntryPrice);
		AreEqual(102m, trade.ExitPrice);
		AreEqual(10m, trade.Volume);
		AreEqual(20m, trade.Gross);
		AreEqual(TimeSpan.FromMinutes(30), trade.Holding);
	}

	/// <summary>A sell first and a later buy is one short trade.</summary>
	[TestMethod]
	public void ASellAndABuyBecomeOneShortTrade()
	{
		var trade = RoundTripBuilder.Build([Sell(0, 100m), Buy(30, 97m)]).Single();

		AreEqual(TradeDirections.Short, trade.Direction);
		AreEqual(30m, trade.Gross, "a short that fell three points on ten units made thirty.");
	}

	/// <summary>Costs from both ends of the trade are charged to it.</summary>
	[TestMethod]
	public void BothEndsOfATradeCarryTheirCosts()
	{
		var trade = RoundTripBuilder
			.Build([Buy(0, 100m, commission: 1m, slippage: 0.5m), Sell(30, 102m, commission: 2m, slippage: 0.25m)])
			.Single();

		AreEqual(3m, trade.Commission);
		AreEqual(0.75m, trade.Slippage);
		AreEqual(16.25m, trade.Net, "both costs come off the result, and each only once.");
	}

	/// <summary>A position built in two fills has one entry price, weighted by size.</summary>
	[TestMethod]
	public void APositionBuiltInPiecesHasOneWeightedEntryPrice()
	{
		var trade = RoundTripBuilder.Build([Buy(0, 100m, volume: 10m), Buy(1, 110m, volume: 30m), Sell(30, 120m, volume: 40m)]).Single();

		AreEqual(107.5m, trade.EntryPrice, "the entry price is not the average the position was actually built at.");
		AreEqual(40m, trade.Volume);
		AreEqual(500m, trade.Gross);
	}

	/// <summary>
	/// A position let go in pieces is still one trade, priced at what the pieces averaged and held
	/// until the last of it was gone.
	/// </summary>
	[TestMethod]
	public void APositionClosedInPiecesIsStillOneTrade()
	{
		var trade = RoundTripBuilder
			.Build([Buy(0, 100m, volume: 20m), Sell(10, 105m, volume: 5m), Sell(30, 90m, volume: 15m)])
			.Single();

		AreEqual(93.75m, trade.ExitPrice, "the exit price is not the average the position was actually let go at.");
		AreEqual(-125m, trade.Gross);
		AreEqual(TimeSpan.FromMinutes(30), trade.Holding);
	}

	/// <summary>
	/// Scaling in and out does not raise the trade count. The count is how much evidence a result rests
	/// on, and a strategy that could raise it by breaking one position into six fills would be answering
	/// the count rather than the question behind it.
	/// </summary>
	[TestMethod]
	public void ScalingInAndOutDoesNotRaiseTheTradeCount()
	{
		var scaled = RoundTripBuilder.Build(
		[
			Buy(0, 100m, volume: 5m),
			Buy(1, 100m, volume: 5m),
			Sell(30, 102m, volume: 5m),
			Sell(31, 102m, volume: 5m),
		]);

		var atOnce = RoundTripBuilder.Build([Buy(0, 100m, volume: 10m), Sell(30, 102m, volume: 10m)]);

		AreEqual(atOnce.Count, scaled.Count);
		AreEqual(atOnce.Single().Gross, scaled.Single().Gross);
	}

	/// <summary>A fill larger than the position closes it and opens the other way with what is left.</summary>
	[TestMethod]
	public void AFillThatOvershootsClosesAndReopensTheOtherWay()
	{
		var trades = RoundTripBuilder.Build([Buy(0, 100m, volume: 10m), Sell(10, 110m, volume: 25m), Buy(20, 105m, volume: 15m)]);

		AreEqual(2, trades.Count);
		AreEqual(TradeDirections.Long, trades[0].Direction);
		AreEqual(100m, trades[0].Gross);
		AreEqual(TradeDirections.Short, trades[1].Direction);
		AreEqual(15m, trades[1].Volume, "the part of the fill beyond the position did not open a new one.");
		AreEqual(75m, trades[1].Gross);
	}

	/// <summary>Two symbols are two positions, not one net one.</summary>
	[TestMethod]
	public void SymbolsAreTrackedApart()
	{
		var trades = RoundTripBuilder.Build(
		[
			new("NVDA", TradeDirections.Long, _start, 100m, 10m, 0m, 0m),
			new("AMD", TradeDirections.Long, _start.AddMinutes(1), 50m, 10m, 0m, 0m),
			new("AMD", TradeDirections.Short, _start.AddMinutes(20), 55m, 10m, 0m, 0m),
			new("NVDA", TradeDirections.Short, _start.AddMinutes(30), 102m, 10m, 0m, 0m),
		]);

		AreEqual(2, trades.Count);
		AreEqual("AMD", trades[0].Symbol, "trades are not in the order they closed.");
		AreEqual(50m, trades[0].Gross);
		AreEqual("NVDA", trades[1].Symbol);
		AreEqual(20m, trades[1].Gross);
	}

	/// <summary>A position still open at the end of the run is not a trade.</summary>
	[TestMethod]
	public void APositionStillOpenAtTheEndIsNotATrade()
	{
		var trades = RoundTripBuilder.Build([Buy(0, 100m), Sell(10, 105m), Buy(20, 103m)]);

		AreEqual(1, trades.Count, "an open position was reported as though it had been closed.");
	}

	/// <summary>Trades are identified so a metric can name the one behind it.</summary>
	[TestMethod]
	public void EveryTradeIsIdentified()
	{
		var trades = RoundTripBuilder.Build([Buy(0, 100m), Sell(10, 105m), Buy(20, 103m), Sell(30, 108m)]);

		AreEqual(2, trades.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count());
		IsTrue(trades.All(t => !string.IsNullOrWhiteSpace(t.Id)));
	}

	/// <summary>Fills out of order are refused rather than quietly producing a negative holding time.</summary>
	[TestMethod]
	public void FillsOutOfOrderAreRefused()
		=> Throws<ArgumentException>(() => RoundTripBuilder.Build([Buy(30, 100m), Sell(0, 105m)]));

	/// <summary>A run with no fills has no trades and no complaint.</summary>
	[TestMethod]
	public void NoFillsMeansNoTrades()
		=> AreEqual(0, RoundTripBuilder.Build([]).Count);
}
