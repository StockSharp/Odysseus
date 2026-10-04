namespace Odysseus.Broker;

using System.Net;
using System.Threading;
using System.Threading.Tasks;

using StockSharp.Algo.Storages;

using Odysseus.Domain;
using Odysseus.Platform;

/// <summary>
/// History read from a remote StockSharp storage server through the platform's own remote drive.
/// </summary>
/// <remarks>
/// The server speaks the transport the platform's FIX package provides, loaded the same way a connector
/// is, so this assembly references no transport of its own. A connection is opened per download and
/// closed after it: imports are rare, and a connection held open between them is one more thing to fail.
/// </remarks>
internal sealed class StockSharpStorageHistorySource : IHistorySource
{
	private readonly Type _transport;
	private readonly RemoteStorageChoice _choice;

	/// <summary>
	/// Creates the source.
	/// </summary>
	/// <param name="transport">The adapter type the server is reached through.</param>
	/// <param name="choice">The server and the account to sign in with.</param>
	public StockSharpStorageHistorySource(Type transport, RemoteStorageChoice choice)
	{
		_transport = transport ?? throw new ArgumentNullException(nameof(transport));
		_choice = choice ?? throw new ArgumentNullException(nameof(choice));
	}

	/// <inheritdoc />
	public string SourceName => _choice.SourceName;

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

		using var adapter = AdapterCatalog.Create(_transport, new IncrementalIdGenerator());
		using var drive = new RemoteMarketDataDrive(_choice.Address.To<EndPoint>(), adapter);

		drive.Credentials.Email = _choice.Login;
		drive.Credentials.Password = _choice.Password.Secure();

		var securityId = new SecurityId { SecurityCode = symbol, BoardCode = SymbolBoards.Default.Of(symbol) };
		var storage = new StorageRegistry { DefaultDrive = drive };

		var candles = new List<Candle>();

		await foreach (var candle in storage
			.GetTimeFrameCandleMessageStorage(securityId, timeFrame)
			.LoadAsync(from, to)
			.WithCancellation(cancellationToken))
		{
			var openTime = DateTime.SpecifyKind(candle.OpenTime, DateTimeKind.Utc);

			// The storage reads by whole days and takes its range as inclusive at both ends.
			if (openTime < from || openTime >= to)
				continue;

			candles.Add(new(openTime, candle.OpenPrice, candle.HighPrice, candle.LowPrice, candle.ClosePrice, candle.TotalVolume));
		}

		return candles;
	}
}
