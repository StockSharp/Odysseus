namespace StockSharp.Odysseus.Application.Tests;

using StockSharp.Odysseus.Spec;

/// <summary>
/// How large one position is.
/// </summary>
/// <remarks>
/// The specification says what share of the account a position may take. What that buys depends on what
/// one unit carries, not on what one unit costs: sized by the quoted price, a contract at 9.05 buys a
/// hundred times the exposure that was asked for - a million dollars of it on a hundred-thousand-dollar
/// account - and the emulator will not fill an order that large, so the run measures nothing and says
/// nothing about why.
/// </remarks>
[TestClass]
public class PositionSizeTests : OdysseusTestBase
{
	/// <summary>A tenth of the account, in shares, is a tenth of the account.</summary>
	[TestMethod]
	public void AShareIsSizedByItsPrice()
	{
		var size = BacktestService.PositionSize(Spec(0.10m), "NVDA", 214m);

		AreEqual(46m, size, "a tenth of a hundred thousand at 214 is forty-six shares.");
	}

	/// <summary>A contract is sized by the exposure it carries, which is a hundred times its quote.</summary>
	[TestMethod]
	public void AContractIsSizedByWhatItCarries()
	{
		var size = BacktestService.PositionSize(Spec(0.10m), "NVDA260918C00250000", 9.05m);

		AreEqual(11m, size, "a tenth of a hundred thousand against 905 dollars of exposure is eleven contracts.");
	}

	/// <summary>Something too expensive to hold at that share of the account is still held once.</summary>
	[TestMethod]
	public void NothingIsSizedToNothing()
	{
		var size = BacktestService.PositionSize(Spec(0.0001m), "NVDA260918C00250000", 9.05m);

		AreEqual(1m, size, "a position was sized to nothing rather than to the smallest thing that trades.");
	}

	/// <summary>A price of nothing is refused rather than divided by.</summary>
	[TestMethod]
	public void APriceOfNothingIsRefused()
		=> Throws<InvalidOperationException>(() => BacktestService.PositionSize(Spec(0.10m), "NVDA", 0m));

	private static StrategySpec Spec(decimal maxPositionPercent)
		=> new()
		{
			Name = "sizing",
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
			Risk = new(maxPositionPercent, 0.02m),
		};
}
