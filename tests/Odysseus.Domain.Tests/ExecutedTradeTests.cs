namespace Odysseus.Domain.Tests;

using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Odysseus.Domain;
using Odysseus.TestKit;

/// <summary>
/// What a trade came to.
/// </summary>
/// <remarks>
/// A trade knows the symbol it was made on, and the symbol says how much of the underlying one traded
/// unit carries. Without that, a dollar made on a contract is reported as a cent, every metric built on
/// it is out by the same factor, and nothing about the numbers looks unusual.
/// </remarks>
[TestClass]
public class ExecutedTradeTests : OdysseusTestBase
{
	private static readonly DateTime _open = new(2026, 3, 2, 14, 30, 0, DateTimeKind.Utc);

	/// <summary>A share moves a dollar and one share makes a dollar.</summary>
	[TestMethod]
	public void AShareMakesWhatItMoved()
	{
		var trade = Trade("NVDA", entry: 100m, exit: 101m, volume: 10m);

		AreEqual(10m, trade.Gross);
	}

	/// <summary>
	/// A contract moves a dollar and one contract makes a hundred, because it carries a hundred shares.
	/// </summary>
	[TestMethod]
	public void AContractMakesWhatItCarries()
	{
		var trade = Trade("NVDA260918C00250000", entry: 9.05m, exit: 10.05m, volume: 10m);

		AreEqual(1_000m, trade.Gross, "ten contracts that gained a dollar made ten dollars rather than a thousand.");
	}

	/// <summary>Costs are money already, so they are not multiplied a second time.</summary>
	[TestMethod]
	public void CostsAreMoneyAndStayMoney()
	{
		var trade = Trade("NVDA260918C00250000", entry: 9.05m, exit: 10.05m, volume: 10m, commission: 6.5m, slippage: 3.5m);

		AreEqual(1_000m, trade.Gross);
		AreEqual(990m, trade.Net, "the charges were scaled along with the result.");
	}

	/// <summary>A short contract loses what a long one would have made.</summary>
	[TestMethod]
	public void DirectionStillDecidesTheSign()
	{
		var trade = Trade("NVDA260918C00250000", entry: 9.05m, exit: 10.05m, volume: 10m, direction: TradeDirections.Short);

		AreEqual(-1_000m, trade.Gross);
	}

	/// <summary>Turnover is money that changed hands, so it carries the contract too.</summary>
	[TestMethod]
	public void TurnoverIsWhatActuallyChangedHands()
	{
		var trade = Trade("NVDA260918C00250000", entry: 9m, exit: 10m, volume: 2m);

		AreEqual(3_800m, trade.Turnover, "two contracts bought at 9 and sold at 10 moved 3,800 dollars.");
	}

	private static ExecutedTrade Trade(
		string symbol,
		decimal entry,
		decimal exit,
		decimal volume,
		decimal commission = 0m,
		decimal slippage = 0m,
		TradeDirections direction = TradeDirections.Long)
		=> new("t1", symbol, direction, _open, entry, _open.AddMinutes(25), exit, volume, commission, slippage);
}
