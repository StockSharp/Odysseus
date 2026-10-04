namespace Odysseus.Domain;

/// <summary>
/// One order fill of a run.
/// </summary>
/// <param name="Symbol">Symbol traded.</param>
/// <param name="Side">Which way the fill went.</param>
/// <param name="Time">Moment of the fill, in UTC.</param>
/// <param name="Price">Price filled at.</param>
/// <param name="Volume">Size filled.</param>
/// <param name="Commission">Commission charged on this fill, as a positive amount.</param>
/// <param name="Slippage">
/// How far the fill landed from the price the order was sent at, as a positive amount. Reported by
/// whoever executed the order rather than assumed, and already contained in <paramref name="Price"/>.
/// </param>
public readonly record struct Fill(
	string Symbol,
	TradeDirections Side,
	DateTime Time,
	decimal Price,
	decimal Volume,
	decimal Commission,
	decimal Slippage);
