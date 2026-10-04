namespace Odysseus.Domain;

using System;

/// <summary>
/// What a symbol says about itself.
/// </summary>
/// <remarks>
/// The server does not ask what kind of instrument it has been given; the name carries it. A listed
/// option contract is written as its underlying, six digits of expiry, a side and eight digits of
/// strike, and that shape is unambiguous enough to read off the name.
///
/// It matters because a contract is not one unit of anything. A quote of 9.05 is nine dollars and five
/// cents per share of a hundred shares, so a position of one contract carries nine hundred and five
/// dollars of exposure and every cent the price moves is a dollar. Treated as a share, a strategy on
/// contracts sizes its positions a hundred times too large and reports a hundredth of what it made or
/// lost - and both numbers look perfectly ordinary.
/// </remarks>
public static class ContractSymbol
{
	/// <summary>Shares one contract carries, where the venue publishes nothing else.</summary>
	/// <remarks>
	/// A hundred, which is what every ordinary listed option in the United States carries. A contract
	/// adjusted for a split or a special dividend carries something else, and this does not know it: a
	/// result measured on one is wrong by whatever the adjustment was, so the figure that was assumed is
	/// reported alongside the result rather than left to be inferred.
	/// </remarks>
	public const decimal DefaultContractSize = 100m;

	/// <summary>
	/// Whether a symbol names a listed option contract.
	/// </summary>
	/// <param name="symbol">Symbol to read.</param>
	/// <returns>Whether it has the shape of a contract.</returns>
	public static bool IsContract(string symbol)
	{
		// Root, yyMMdd, C or P, then the strike in thousandths: NVDA260828C00050000.
		const int tail = 15;

		if (symbol is null || symbol.Length <= tail)
			return false;

		var suffix = symbol.AsSpan(symbol.Length - tail);

		if (suffix[6] is not ('C' or 'P' or 'c' or 'p'))
			return false;

		for (var i = 0; i < tail; i++)
		{
			if (i != 6 && !char.IsAsciiDigit(suffix[i]))
				return false;
		}

		return true;
	}

	/// <summary>
	/// How many units of the underlying one traded unit of a symbol carries.
	/// </summary>
	/// <param name="symbol">Symbol to read.</param>
	/// <returns>The contract size: one for a share, a hundred for a listed option contract.</returns>
	public static decimal SizeOf(string symbol)
		=> IsContract(symbol) ? DefaultContractSize : 1m;
}
