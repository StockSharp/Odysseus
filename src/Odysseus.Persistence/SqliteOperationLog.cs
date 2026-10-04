namespace Odysseus.Persistence;

using Microsoft.Data.Sqlite;

/// <summary>
/// Remembers operation keys in a database next to the projects.
/// </summary>
/// <remarks>
/// This deliberately does not live inside a project database: the first thing an operation key has to
/// survive is the creation of the project itself, which is exactly the call whose repeat would
/// otherwise produce a second one.
/// </remarks>
public sealed class SqliteOperationLog : IOperationLog, IDisposable
{
	private const string DatabaseFileName = "operations.db";

	private readonly SqliteConnection _connection;
	private readonly SemaphoreSlim _gate = new(1, 1);

	/// <summary>
	/// Creates the log.
	/// </summary>
	/// <param name="root">Directory the projects live in. Created if it does not exist.</param>
	public SqliteOperationLog(string root)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(root);

		var full = Path.GetFullPath(root);

		Directory.CreateDirectory(full);

		_connection = SqliteConnections.Open(Path.Combine(full, DatabaseFileName));

		using var command = _connection.CreateCommand();

		command.CommandText =
			"""
			CREATE TABLE IF NOT EXISTS Operations (
				Scope      TEXT NOT NULL,
				Key        TEXT NOT NULL,
				Result     TEXT NOT NULL,
				RecordedAt TEXT NOT NULL,

				PRIMARY KEY (Scope, Key)
			);
			""";

		command.ExecuteNonQuery();
	}

	/// <inheritdoc />
	public async ValueTask<string> TryGetAsync(string scope, string key, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(key);

		await _gate.WaitAsync(cancellationToken);

		try
		{
			await using var command = _connection.CreateCommand();

			command.CommandText = "SELECT Result FROM Operations WHERE Scope = $scope AND Key = $key;";
			command.Parameters.AddWithValue("$scope", scope);
			command.Parameters.AddWithValue("$key", key);

			return (string)await command.ExecuteScalarAsync(cancellationToken);
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
			await using var command = _connection.CreateCommand();

			// Whoever inserts first owns the key; a later caller is told what the first one produced
			// rather than being allowed to overwrite it.
			command.CommandText =
				"""
				INSERT INTO Operations (Scope, Key, Result, RecordedAt) VALUES ($scope, $key, $result, $recordedAt)
				ON CONFLICT (Scope, Key) DO NOTHING;

				SELECT Result FROM Operations WHERE Scope = $scope AND Key = $key;
				""";

			command.Parameters.AddWithValue("$scope", scope);
			command.Parameters.AddWithValue("$key", key);
			command.Parameters.AddWithValue("$result", result);
			command.Parameters.AddWithValue("$recordedAt", DateTime.UtcNow.ToString("O"));

			return (string)await command.ExecuteScalarAsync(cancellationToken);
		}
		finally
		{
			_gate.Release();
		}
	}

	/// <inheritdoc />
	public void Dispose()
	{
		_connection.Dispose();
		_gate.Dispose();
	}
}
