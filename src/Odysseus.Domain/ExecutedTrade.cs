namespace Odysseus.Domain;

/// <summary>
/// One position that was opened and closed during a run.
/// </summary>
/// <param name="Id">Identifier of the trade inside its run, so a metric can name the trade behind it.</param>
/// <param name="Symbol">Symbol traded.</param>
/// <param name="Direction">Which way the position was held.</param>
/// <param name="EntryTime">Moment the position was opened, in UTC.</param>
/// <param name="EntryPrice">Price the position was opened at.</param>
/// <param name="ExitTime">Moment the position was closed, in UTC.</param>
/// <param name="ExitPrice">Price the position was closed at.</param>
/// <param name="Volume">Size of the position.</param>
/// <param name="Commission">Fees charged for opening and closing, as a positive amount.</param>
/// <param name="Slippage">What crossing the spread cost over both fills, as a positive amount.</param>
/// <remarks>
/// The prices are the ones the run matched at, and both costs are charges against the result rather
/// than something already inside those prices. They are kept apart because a reader asked to accept a
/// marginal candidate wants to know which of the two took the edge: fees are a fact of the account and
/// the spread is a property of the instrument, and only one of them can be traded around.
/// </remarks>
public sealed record ExecutedTrade(
	string Id,
	string Symbol,
	TradeDirections Direction,
	DateTime EntryTime,
	decimal EntryPrice,
	DateTime ExitTime,
	decimal ExitPrice,
	decimal Volume,
	decimal Commission,
	decimal Slippage)
{
	/// <summary>
	/// How much of the underlying one traded unit carries: one for a share, a hundred for a contract.
	/// </summary>
	/// <remarks>
	/// Read from the symbol rather than stored, so a trade recorded before any of this existed reports
	/// the same money as one recorded after it.
	/// </remarks>
	public decimal ContractSize => ContractSymbol.SizeOf(Symbol);

	/// <summary>Result of the trade before costs, in the account currency.</summary>
	public decimal Gross
		=> (Direction == TradeDirections.Long ? ExitPrice - EntryPrice : EntryPrice - ExitPrice)
			* Volume * ContractSize;

	/// <summary>Result of the trade after costs, in the account currency.</summary>
	public decimal Net => Gross - Commission - Slippage;

	/// <summary>How long the position was held.</summary>
	public TimeSpan Holding => ExitTime - EntryTime;

	/// <summary>Money that changed hands opening and closing the position.</summary>
	public decimal Turnover => (EntryPrice + ExitPrice) * Volume * ContractSize;
}
