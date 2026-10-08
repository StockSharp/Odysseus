namespace StockSharp.Odysseus.Application;

/// <summary>
/// What to look for.
/// </summary>
/// <param name="Text">Part of a code or a name, or empty to ask for everything that matches the rest.</param>
/// <param name="Underlying">
/// Code of the instrument the contracts are written on. Set for an option chain, empty otherwise.
/// </param>
/// <param name="ExpiringFrom">Earliest expiry to include, in UTC, or null for no lower bound.</param>
/// <param name="ExpiringTo">Latest expiry to include, in UTC, or null for no upper bound.</param>
/// <param name="Limit">Most to return.</param>
public sealed record SecurityQuery(
	string Text,
	string Underlying,
	DateTime? ExpiringFrom,
	DateTime? ExpiringTo,
	int Limit);

/// <summary>
/// One instrument the broker offers.
/// </summary>
/// <param name="Symbol">The code to trade it by, which is what a specification and a run are given.</param>
/// <param name="Name">What the broker calls it.</param>
/// <param name="Board">Exchange or consolidated tape it belongs to.</param>
/// <param name="Kind">Share, option, and so on.</param>
/// <param name="Underlying">What an option is written on, when it is one.</param>
/// <param name="Expiry">When it expires, when it does.</param>
/// <param name="Strike">Strike, when it has one.</param>
/// <param name="OptionType">Call or put, when it is an option.</param>
/// <param name="ContractSize">How many of the underlying one contract carries.</param>
public sealed record FoundSecurity(
	string Symbol,
	string Name,
	string Board,
	string Kind,
	string Underlying,
	DateTime? Expiry,
	decimal? Strike,
	string OptionType,
	decimal ContractSize);

/// <summary>
/// Asking the broker what it offers.
/// </summary>
/// <remarks>
/// Without this the symbol is something the caller has to know already. That is tolerable for a share -
/// an agent knows what NVDA is - and hopeless for an option, whose code carries the underlying, the
/// expiry, the right and the strike in nineteen characters that have to be exactly right. The product
/// handled such a contract correctly all along and gave nobody a way to name one.
///
/// It reads and nothing else. What is worth trading is not the server's to say.
/// </remarks>
public interface ISecurityLookup
{
	/// <summary>
	/// Finds instruments the broker offers.
	/// </summary>
	/// <param name="query">What to look for.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>What was found, in the order the broker reported it.</returns>
	ValueTask<IReadOnlyList<FoundSecurity>> SearchAsync(SecurityQuery query, CancellationToken cancellationToken);
}
