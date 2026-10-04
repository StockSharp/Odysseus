namespace Odysseus.Application;

/// <summary>
/// The market-data storage every project shares: one folder in the trading engine's own format, holding
/// whatever history has been imported, by symbol and candle length.
/// </summary>
/// <remarks>
/// A project does not own bars. It owns a range, and reads that range out of here. A run happens in a
/// process of its own and is pointed at <see cref="Folder"/>, which is why the port names the folder
/// rather than handing back bytes.
/// </remarks>
public interface IBarStorage
{
	/// <summary>Full path of the storage folder.</summary>
	string Folder { get; }

	/// <summary>
	/// Writes the bars of one symbol.
	/// </summary>
	/// <param name="symbol">Symbol the bars belong to.</param>
	/// <param name="timeFrame">Length of one candle.</param>
	/// <param name="candles">The bars, oldest first.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>A task that completes when the bars are on disk.</returns>
	ValueTask WriteAsync(
		string symbol,
		TimeSpan timeFrame,
		IReadOnlyList<Candle> candles,
		CancellationToken cancellationToken);

	/// <summary>
	/// Reads the bars of one symbol.
	/// </summary>
	/// <param name="symbol">Symbol to read.</param>
	/// <param name="timeFrame">Length of one candle.</param>
	/// <param name="from">Earliest open time to return, in UTC.</param>
	/// <param name="to">Open time to stop before, exclusive, in UTC.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The bars, oldest first. Empty when the storage holds none.</returns>
	ValueTask<IReadOnlyList<Candle>> ReadAsync(
		string symbol,
		TimeSpan timeFrame,
		DateTime from,
		DateTime to,
		CancellationToken cancellationToken);
}
