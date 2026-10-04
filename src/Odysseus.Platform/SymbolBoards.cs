namespace Odysseus.Platform;

using System;

using StockSharp.Messages;

using Odysseus.Domain;

/// <summary>
/// Which market a named symbol is quoted on.
/// </summary>
/// <param name="Equity">Board code for an ordinary instrument.</param>
/// <param name="Option">Board code for a listed contract.</param>
/// <remarks>
/// The caller names a symbol and nothing else. Whether it happens to be a share or a listed contract is
/// not something to be asked about: an option symbol carries its own shape - the underlying, then six
/// digits of expiry, a side and eight digits of strike - so what it is can be read off the name, and
/// history for either arrives the same way.
///
/// The two codes are a property of the market, not of the broker. They are settings rather than
/// constants because a connector serving another market answers for other boards, and a product that
/// hard-codes an American tape into the code cannot be pointed anywhere else without being edited.
/// </remarks>
internal sealed record SymbolBoards(string Equity, string Option)
{
	/// <summary>The American tapes, which is what the product was written against.</summary>
	public static SymbolBoards Default { get; } = new(BoardCodes.Nasdaq, BoardCodes.Opra);

	/// <summary>
	/// The board a symbol belongs to.
	/// </summary>
	/// <param name="symbol">Symbol the caller named.</param>
	/// <returns>The board code.</returns>
	public string Of(string symbol)
		=> ContractSymbol.IsContract(symbol) ? Option : Equity;

	/// <summary>
	/// The boards a connector was configured with, falling back to the American tapes.
	/// </summary>
	/// <param name="equity">Board for ordinary instruments, or empty.</param>
	/// <param name="option">Board for listed contracts, or empty.</param>
	/// <returns>The boards.</returns>
	public static SymbolBoards From(string equity, string option)
		=> new(
			string.IsNullOrWhiteSpace(equity) ? Default.Equity : equity.Trim(),
			string.IsNullOrWhiteSpace(option) ? Default.Option : option.Trim());
}
