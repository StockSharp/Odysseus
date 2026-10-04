namespace Odysseus.Domain.Tests;

/// <summary>
/// Reading what a symbol carries from the symbol itself.
/// </summary>
/// <remarks>
/// A contract quoted at 9.05 is not nine dollars of exposure but nine hundred and five, and every cent
/// it moves is a dollar rather than a cent. Read as a share it sizes a hundred times too large and
/// reports a hundredth of the result, and neither number looks wrong.
/// </remarks>
[TestClass]
public class ContractSymbolTests : OdysseusTestBase
{
	/// <summary>A contract is recognised by its shape, both rights and any root length.</summary>
	[TestMethod]
	[DataRow("NVDA260918C00250000")]
	[DataRow("NVDA260828P00050000")]
	[DataRow("F260918C00012500")]
	[DataRow("SPY260826P00765000")]
	public void AContractIsRecognised(string symbol)
	{
		IsTrue(ContractSymbol.IsContract(symbol), $"'{symbol}' was not read as a contract.");
		AreEqual(ContractSymbol.DefaultContractSize, ContractSymbol.SizeOf(symbol));
	}

	/// <summary>A share is one of itself, and nothing about a share resembles a contract.</summary>
	[TestMethod]
	[DataRow("NVDA")]
	[DataRow("AAPL")]
	[DataRow("BRK.B")]
	[DataRow("DEMO1")]
	public void AShareIsOneOfItself(string symbol)
	{
		IsFalse(ContractSymbol.IsContract(symbol), $"'{symbol}' was read as a contract.");
		AreEqual(1m, ContractSymbol.SizeOf(symbol));
	}

	/// <summary>Nothing that merely looks long and numeric is taken for a contract.</summary>
	[TestMethod]
	[DataRow("NVDA260918X00250000")]
	[DataRow("NVDA26091800250000")]
	[DataRow("260918C00250000")]
	[DataRow("")]
	[DataRow(null)]
	public void NothingElseIsMistakenForOne(string symbol)
		=> IsFalse(ContractSymbol.IsContract(symbol), $"'{symbol}' was read as a contract.");
}
