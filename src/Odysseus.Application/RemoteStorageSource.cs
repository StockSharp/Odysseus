namespace Odysseus.Application;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Odysseus.Domain;

/// <summary>
/// History downloaded from a remote StockSharp storage server rather than from a broker.
/// </summary>
/// <remarks>
/// Connected on the first download rather than at start-up, so a server that is unreachable stops only
/// the imports and not everything else, which works on history already in the local storage.
/// </remarks>
public sealed class RemoteStorageSource : IHistorySource
{
	private readonly IConnectorFactory _factory;
	private readonly RemoteStorageChoice _choice;
	private readonly SemaphoreSlim _opening = new(1, 1);

	private IHistorySource _opened;

	/// <summary>
	/// Creates the source.
	/// </summary>
	/// <param name="factory">What loads the transport the server speaks.</param>
	/// <param name="choice">The server to read from.</param>
	public RemoteStorageSource(IConnectorFactory factory, RemoteStorageChoice choice)
	{
		_factory = factory ?? throw new ArgumentNullException(nameof(factory));
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
		var opened = await OpenAsync(cancellationToken);

		return await opened.GetBarsAsync(symbol, timeFrame, from, to, cancellationToken);
	}

	private async ValueTask<IHistorySource> OpenAsync(CancellationToken cancellationToken)
	{
		await _opening.WaitAsync(cancellationToken);

		try
		{
			return _opened ??= await _factory.OpenStorageAsync(_choice, cancellationToken);
		}
		finally
		{
			_opening.Release();
		}
	}
}
