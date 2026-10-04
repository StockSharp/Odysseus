namespace Odysseus.Broker.Tests;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Odysseus.Platform;
using Odysseus.TestKit;

/// <summary>
/// Which market a named symbol is quoted on.
/// </summary>
/// <remarks>
/// The server is a routine executor: it is handed a symbol and gets its history. What that symbol is -
/// a share, a listed contract - is the caller's business, and needs no telling, because the name itself
/// says which market answers for it. These checks need no broker, which is why they are not in the
/// suite that does.
///
/// The two board codes are settings rather than constants, so a connector serving another market gets
/// its own without a line of code being changed. What is asserted here is the reading of the name, which
/// is the part that is the same everywhere.
/// </remarks>
[TestClass]
public class SymbolBoardsTests : OdysseusTestBase
{
	/// <summary>
	/// The caller names a symbol and the source follows it. A share and a listed contract are quoted on
	/// different markets, and which one a name belongs to is readable from the name itself - so nothing
	/// has to be told, configured or asked.
	/// </summary>
	/// <param name="symbol">Symbol to read.</param>
	/// <param name="board">Board it should be fetched from.</param>
	[TestMethod]
	[DataRow("NVDA", "NASDAQ")]
	[DataRow("AAPL", "NASDAQ")]
	[DataRow("BRK.B", "NASDAQ")]
	[DataRow("NVDA260828C00050000", "OPRA")]
	[DataRow("SPY250613P00700000", "OPRA")]
	[DataRow("A240119C00100000", "OPRA")]
	public void TheMarketFollowsTheSymbol(string symbol, string board)
		=> AreEqual(board, SymbolBoards.Default.Of(symbol));

	/// <summary>
	/// A name that merely ends in digits is still a share. Read too loosely, an ordinary ticker would be
	/// sent to the option tape and come back empty for no stated reason.
	/// </summary>
	/// <param name="symbol">Symbol to read.</param>
	[TestMethod]
	[DataRow("BRK260828X00050000")]
	[DataRow("NVDA26082C00050000")]
	[DataRow("C00050000")]
	public void ANameThatOnlyLooksLikeAContractIsNotOne(string symbol)
		=> AreEqual("NASDAQ", SymbolBoards.Default.Of(symbol));

	/// <summary>
	/// A connector for another market names its own boards, and the reading of the symbol is unchanged.
	/// </summary>
	[TestMethod]
	public void AConnectorMayNameItsOwnMarkets()
	{
		var boards = SymbolBoards.From("XETRA", "EUREX");

		AreEqual("XETRA", boards.Of("SAP"));
		AreEqual("EUREX", boards.Of("SAP260828C00050000"));
	}

	/// <summary>An unnamed board falls back to the market the product was written against.</summary>
	[TestMethod]
	public void AnUnnamedMarketFallsBackToTheAmericanTapes()
	{
		var boards = SymbolBoards.From(string.Empty, "   ");

		AreEqual(SymbolBoards.Default.Equity, boards.Equity);
		AreEqual(SymbolBoards.Default.Option, boards.Option);
	}
}
