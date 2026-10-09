namespace StockSharp.Odysseus.Persistence;

/// <summary>Scoped operation keys in a JSON file next to the projects.</summary>
public sealed class FileOperationLog : IOperationLog, IDisposable
{
	private readonly string _path;
	private readonly SemaphoreSlim _gate = new(1, 1);

	/// <summary>Creates the log for the process that owns this workspace.</summary>
	/// <param name="root">Workspace directory, created if needed.</param>
	public FileOperationLog(string root)
	{
		_path = Path.Combine(JsonFiles.PrepareRoot(root), "operations.json");
	}

	/// <inheritdoc />
	public async ValueTask<string> TryGetAsync(string scope, string key, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(key);
		await _gate.WaitAsync(cancellationToken);

		try
		{
			return (await ReadAsync(cancellationToken)).FirstOrDefault(entry => entry.Scope == scope && entry.Key == key)?.Result;
		}
		finally
		{
			_gate.Release();
		}
	}

	/// <inheritdoc />
	public async ValueTask<string> RecordAsync(string scope, string key, string result, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(key);
		ArgumentException.ThrowIfNullOrWhiteSpace(result);
		await _gate.WaitAsync(cancellationToken);

		try
		{
			var entries = await ReadAsync(cancellationToken);
			var previous = entries.FirstOrDefault(entry => entry.Scope == scope && entry.Key == key);

			if (previous is not null)
				return previous.Result;

			entries.Add(new(scope, key, result, DateTime.UtcNow));
			await JsonFiles.WriteAsync(_path, entries, cancellationToken);
			return result;
		}
		finally
		{
			_gate.Release();
		}
	}

	/// <inheritdoc />
	public void Dispose() => _gate.Dispose();

	private async ValueTask<List<Entry>> ReadAsync(CancellationToken cancellationToken)
		=> await JsonFiles.ReadAsync<List<Entry>>(_path, cancellationToken) ?? [];

	private sealed record Entry(string Scope, string Key, string Result, DateTime RecordedAt);
}
