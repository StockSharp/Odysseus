namespace Odysseus.Persistence;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Data.Sqlite;

using Odysseus.Application;
using Odysseus.Domain;

/// <summary>
/// Remembers spent closed history in a database next to the projects.
/// </summary>
/// <remarks>
/// Beside the projects rather than inside one, because a record a project owns is a record that a new
/// project does not have to obey, and starting a new project is precisely the way the guarantee would
/// otherwise be escaped.
/// </remarks>
public sealed class SqliteClosedHistoryLedger : IClosedHistoryLedger, IDisposable
{
	private const string DatabaseFileName = "closed-history.db";

	private readonly SqliteConnection _connection;
	private readonly SemaphoreSlim _gate = new(1, 1);

	/// <summary>
	/// Creates the ledger.
	/// </summary>
	/// <param name="root">Directory the projects live in. Created if it does not exist.</param>
	public SqliteClosedHistoryLedger(string root)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(root);

		var full = Path.GetFullPath(root);

		Directory.CreateDirectory(full);

		_connection = SqliteConnections.Open(Path.Combine(full, DatabaseFileName));

		using var command = _connection.CreateCommand();

		command.CommandText =
			"""
			CREATE TABLE IF NOT EXISTS Spent (
				Sequence  INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
				Project   TEXT    NOT NULL,
				Candidate TEXT    NOT NULL,
				Symbol    TEXT    NOT NULL,
				TimeFrame TEXT    NOT NULL,
				FromUtc   TEXT    NOT NULL,
				ToUtc     TEXT    NOT NULL,
				SpentAt   TEXT    NOT NULL
			);

			CREATE INDEX IF NOT EXISTS SpentBySymbol ON Spent (Symbol);
			""";

		command.ExecuteNonQuery();
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<SpentWindow>> FindOverlappingAsync(
		string symbol,
		DateTime from,
		DateTime to,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

		await _gate.WaitAsync(cancellationToken);

		try
		{
			return await OverlappingAsync(null, symbol, from, to, cancellationToken);
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
			// Immediate, so the write lock is taken before the read: another process opening the same file
			// waits here instead of reading the same empty answer and writing its own claim after this one.
			await using var transaction = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken);

			var overlapping = await OverlappingAsync(transaction, window.Symbol, window.From, window.To, cancellationToken);

			var blocking = overlapping
				.Where(e => e.Candidate != window.Candidate || !e.IsSameStretch(window.Symbol, window.From, window.To))
				.ToArray();

			// Anything already there either blocks the claim or is this candidate's own record of exactly
			// this stretch, which is the same spending and is not written twice.
			if (overlapping.Count > 0)
			{
				await transaction.CommitAsync(cancellationToken);
				return blocking;
			}

			await using var command = _connection.CreateCommand();

			command.Transaction = transaction;
			command.CommandText =
				"""
				INSERT INTO Spent (Project, Candidate, Symbol, TimeFrame, FromUtc, ToUtc, SpentAt)
				VALUES ($project, $candidate, $symbol, $timeFrame, $from, $to, $spentAt);
				""";

			command.Parameters.AddWithValue("$project", window.Project.Value);
			command.Parameters.AddWithValue("$candidate", window.Candidate.Value);
			command.Parameters.AddWithValue("$symbol", window.Symbol);
			command.Parameters.AddWithValue("$timeFrame", window.TimeFrame.ToString(null, CultureInfo.InvariantCulture));
			command.Parameters.AddWithValue("$from", Format(window.From));
			command.Parameters.AddWithValue("$to", Format(window.To));
			command.Parameters.AddWithValue("$spentAt", Format(window.SpentAt));

			await command.ExecuteNonQueryAsync(cancellationToken);
			await transaction.CommitAsync(cancellationToken);

			return [];
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

	private async ValueTask<IReadOnlyList<SpentWindow>> OverlappingAsync(
		SqliteTransaction transaction,
		string symbol,
		DateTime from,
		DateTime to,
		CancellationToken cancellationToken)
	{
		await using var command = _connection.CreateCommand();

		command.Transaction = transaction;

		// Compared as text, which is why the moments are written in a format that sorts the way time
		// does. Overlap is asked of the database rather than of the caller: a stretch that shares a
		// single bar with a spent one is the same exam re-sat.
		command.CommandText =
			"""
			SELECT Project, Candidate, Symbol, TimeFrame, FromUtc, ToUtc, SpentAt
			FROM Spent
			WHERE Symbol = $symbol COLLATE NOCASE AND FromUtc < $to AND ToUtc > $from
			ORDER BY Sequence DESC;
			""";

		command.Parameters.AddWithValue("$symbol", symbol);
		command.Parameters.AddWithValue("$from", Format(from));
		command.Parameters.AddWithValue("$to", Format(to));

		await using var reader = await command.ExecuteReaderAsync(cancellationToken);

		var spent = new List<SpentWindow>();

		while (await reader.ReadAsync(cancellationToken))
		{
			spent.Add(new(
				ProjectId.Parse(reader.GetString(0)),
				CandidateId.Parse(reader.GetString(1)),
				reader.GetString(2),
				TimeSpan.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
				Parse(reader.GetString(4)),
				Parse(reader.GetString(5)),
				Parse(reader.GetString(6))));
		}

		return spent;
	}

	private static string Format(DateTime moment)
		=> moment.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

	private static DateTime Parse(string text)
		=> DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
}
