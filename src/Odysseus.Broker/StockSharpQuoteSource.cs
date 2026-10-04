namespace Odysseus.Broker;

using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// The current price of an instrument, read through whichever connector was selected.
/// </summary>
/// <remarks>
/// A Level1 subscription is opened and closed again as soon as it carries a price. The last trade is
/// preferred; failing that, the middle of the best bid and ask.
/// </remarks>
internal sealed class StockSharpQuoteSource : IQuoteSource
{
	private static readonly TimeSpan _wait = TimeSpan.FromSeconds(30);

	private readonly IAdapterSource _adapters;

	/// <summary>
	/// Creates the source.
	/// </summary>
	/// <param name="adapters">Where the configured adapter comes from.</param>
	public StockSharpQuoteSource(IAdapterSource adapters)
	{
		_adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));
	}

	/// <inheritdoc />
	public async Task<decimal?> GetPriceAsync(string symbol, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

		using var adapter = _adapters.Create(new IncrementalIdGenerator());
		using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

		waiting.CancelAfter(_wait);

		var request = new MarketDataMessage
		{
			DataType2 = DataType.Level1,
			SecurityId = new() { SecurityCode = symbol, BoardCode = _adapters.Boards.Of(symbol) },
			IsSubscribe = true,
		};

		decimal? bid = null;
		decimal? ask = null;

		try
		{
			await foreach (var message in adapter
				.ConnectAndDownloadAsync<Level1ChangeMessage>(request)
				.WithCancellation(waiting.Token))
			{
				if (message.TryGetDecimal(Level1Fields.LastTradePrice) is { } last and > 0)
					return last;

				bid = message.TryGetDecimal(Level1Fields.BestBidPrice) ?? bid;
				ask = message.TryGetDecimal(Level1Fields.BestAskPrice) ?? ask;

				if (bid is > 0 && ask is > 0)
					return (bid.Value + ask.Value) / 2m;
			}
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			// The broker did not quote within the wait, which is the same answer as quoting nothing.
		}

		return null;
	}
}
