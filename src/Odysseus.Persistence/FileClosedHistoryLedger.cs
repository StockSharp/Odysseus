namespace StockSharp.Odysseus.Persistence;

/// <summary>Spent closed history in a JSON file shared by all projects in the workspace.</summary>
public sealed class FileClosedHistoryLedger : IClosedHistoryLedger, IDisposable
{
	private readonly string _path;
	private readonly SemaphoreSlim _gate = new(1, 1);

	/// <summary>Creates the ledger for the process that owns this workspace.</summary>
	/// <param name="root">Workspace directory, created if needed.</param>
	public FileClosedHistoryLedger(string root)
	{
		_path = Path.Combine(JsonFiles.PrepareRoot(root), "closed-history.json");
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<SpentWindow>> FindOverlappingAsync(string symbol, DateTime from, DateTime to, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
		await _gate.WaitAsync(cancellationToken);

		try
		{
			return Overlapping(await ReadAsync(cancellationToken), symbol, from, to);
		}
		finally
		{
			_gate.Release();
		}
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<SpentWindow>> ClaimAsync(SpentWindow window, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(window);
		await _gate.WaitAsync(cancellationToken);

		try
		{
			var entries = await ReadAsync(cancellationToken);
			var overlapping = Overlapping(entries, window.Symbol, window.From, window.To);

			if (overlapping.Count > 0)
				return [.. overlapping.Where(entry => entry.Candidate != window.Candidate ||
					!entry.IsSameStretch(window.Symbol, window.From, window.To))];

			entries.Add(window);
			await JsonFiles.WriteAsync(_path, entries, cancellationToken);
			return [];
		}
		finally
		{
			_gate.Release();
		}
	}

	/// <inheritdoc />
	public void Dispose() => _gate.Dispose();

	private async ValueTask<List<SpentWindow>> ReadAsync(CancellationToken cancellationToken)
		=> await JsonFiles.ReadAsync<List<SpentWindow>>(_path, cancellationToken) ?? [];

	private static IReadOnlyList<SpentWindow> Overlapping(List<SpentWindow> entries, string symbol, DateTime from, DateTime to)
		=> [.. entries.Where(entry => entry.Overlaps(symbol, from, to)).Reverse()];
}
