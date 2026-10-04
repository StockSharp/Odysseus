namespace Odysseus.Platform;

using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Ecng.IO;

using StockSharp.Algo.Storages;

using Odysseus.Application;

/// <summary>
/// The shared market-data storage, kept in a local folder in the trading engine's own format.
/// </summary>
/// <remarks>
/// A run consumes that format directly, so bars are written in it once, when the history is imported,
/// and every run afterwards opens the folder they are already in.
/// </remarks>
public sealed class LocalBarStorage : IBarStorage
{
	/// <summary>
	/// Opens the storage in a folder.
	/// </summary>
	/// <param name="folder">Folder of the storage. Created on the first write.</param>
	public LocalBarStorage(string folder)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(folder);

		Folder = Path.GetFullPath(folder);
	}

	/// <inheritdoc />
	public string Folder { get; }

	/// <inheritdoc />
	public async ValueTask WriteAsync(
		string symbol,
		TimeSpan timeFrame,
		IReadOnlyList<Candle> candles,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
		ArgumentNullException.ThrowIfNull(candles);

		if (candles.Count == 0)
			return;

		var files = LocalFileSystem.Instance;

		files.CreateDirectory(Folder);

		var securityId = EmulationSetup.SecurityIdOf(symbol);
		var storage = new StorageRegistry { DefaultDrive = new LocalMarketDataDrive(files, Folder) };

		await storage
			.GetTimeFrameCandleMessageStorage(securityId, timeFrame)
			.SaveAsync(candles.Select(c => Describe(securityId, timeFrame, c)), cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<Candle>> ReadAsync(
		string symbol,
		TimeSpan timeFrame,
		DateTime from,
		DateTime to,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

		if (!LocalFileSystem.Instance.DirectoryExists(Folder))
			return [];

		var bars = new List<Candle>();

		await foreach (var candle in EmulationSetup
			.Open(Folder)
			.GetTimeFrameCandleMessageStorage(EmulationSetup.SecurityIdOf(symbol), timeFrame)
			.LoadAsync(from, to)
			.WithCancellation(cancellationToken))
		{
			var openTime = Utc(candle.OpenTime);

			// The range is half-open on the open time. The engine reads by whole days and takes its own
			// range as inclusive at both ends, so the exact bound belongs here.
			if (openTime < from || openTime >= to)
				continue;

			bars.Add(new(
				openTime,
				candle.OpenPrice,
				candle.HighPrice,
				candle.LowPrice,
				candle.ClosePrice,
				candle.TotalVolume));
		}

		return bars;
	}

	private static TimeFrameCandleMessage Describe(SecurityId securityId, TimeSpan timeFrame, Candle candle)
		=> new()
		{
			SecurityId = securityId,
			TypedArg = timeFrame,
			DataType = timeFrame.TimeFrame(),
			OpenTime = candle.OpenTime,
			CloseTime = candle.OpenTime + timeFrame,
			HighTime = candle.OpenTime,
			LowTime = candle.OpenTime,
			OpenPrice = candle.Open,
			HighPrice = candle.High,
			LowPrice = candle.Low,
			ClosePrice = candle.Close,
			TotalVolume = candle.Volume,
			State = CandleStates.Finished,
		};

	// A bar is a moment in UTC and is stored as one. A time that comes back saying nothing about its
	// zone is taken at its word rather than shifted by whatever zone the machine happens to be in.
	private static DateTime Utc(DateTime time)
		=> time.Kind switch
		{
			DateTimeKind.Utc => time,
			DateTimeKind.Unspecified => DateTime.SpecifyKind(time, DateTimeKind.Utc),
			_ => time.ToUniversalTime(),
		};
}
