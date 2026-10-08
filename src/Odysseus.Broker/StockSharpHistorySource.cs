namespace StockSharp.Odysseus.Broker;

using System.Threading;
using System.Threading.Tasks;

using StockSharp.Odysseus.Domain;

/// <summary>
/// History downloaded through whichever connector was selected.
/// </summary>
/// <remarks>
/// A connector is used rather than a venue's REST interface directly, because everything that comes
/// after this - live data, orders, option chains - is already implemented in it, and a hand-written
/// client would have to grow all of that again and be wrong in different places.
///
/// The download runs at the adapter level rather than through a connector object. For a bounded range of
/// finished bars there is nothing for the connector's machinery to add: no candle building from ticks,
/// no storage, no reconnect. Skipping it removes the security lookup and the websockets a connector
/// opens on connect, which a download does not need.
/// </remarks>
internal sealed class StockSharpHistorySource : IHistorySource
{
	private readonly IAdapterSource _adapters;

	/// <summary>
	/// Creates the source.
	/// </summary>
	/// <param name="adapters">Where the configured adapter comes from.</param>
	public StockSharpHistorySource(IAdapterSource adapters)
	{
		_adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));
	}

	/// <inheritdoc />
	public string SourceName => _adapters.SourceName;

	/// <inheritdoc />
	public async Task<IReadOnlyList<Candle>> GetBarsAsync(
		string symbol,
		TimeSpan timeFrame,
		DateTime from,
		DateTime to,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

		if (from.Kind != DateTimeKind.Utc || to.Kind != DateTimeKind.Utc)
			throw new ArgumentException("History is requested over UTC moments.", nameof(from));

		using var adapter = _adapters.Create(new IncrementalIdGenerator());

		var request = new MarketDataMessage
		{
			DataType2 = timeFrame.TimeFrame(),
			SecurityId = new() { SecurityCode = symbol, BoardCode = _adapters.Boards.Of(symbol) },
			From = from,

			// Without an end the subscription runs into live data and never finishes.
			To = to,
			BuildMode = MarketDataBuildModes.Load,
			IsSubscribe = true,
		};

		var candles = new List<Candle>();

		await foreach (var message in adapter
			.ConnectAndDownloadAsync<CandleMessage>(request)
			.WithCancellation(cancellationToken))
		{
			if (message.State != CandleStates.Finished)
				continue;

			candles.Add(new(
				DateTime.SpecifyKind(message.OpenTime, DateTimeKind.Utc),
				message.OpenPrice,
				message.HighPrice,
				message.LowPrice,
				message.ClosePrice,
				message.TotalVolume));
		}

		return candles;
	}
}
