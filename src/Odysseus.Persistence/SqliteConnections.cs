namespace Odysseus.Persistence;

using Microsoft.Data.Sqlite;

/// <summary>
/// How every store in this assembly opens the database file it owns.
/// </summary>
/// <remarks>
/// Pooling is off, and that is the whole of what this exists to say.
///
/// A store opens its file once and holds the connection until it is disposed, so there is no short-lived
/// connection here for a pool to amortise. What the pool does provide is a process-wide registry of
/// every open SQLite handle, keyed by connection string, which one instance can empty on behalf of all
/// the others: <c>SqliteConnection.ClearAllPools</c> walks every pool group in the process, marks every
/// connection in it as unpoolable and reclaims the ones it judges abandoned. Called from a store's
/// <c>Dispose</c> - which is where it lands, because a pooled connection keeps its file open after the
/// connection is closed and the file has to be released - it reaches into the connections of every other
/// store alive at that moment, including the ones being opened on other threads. The one that loses that
/// race is left holding a disposed <c>sqlite3</c> handle and fails its next statement with
/// <c>ObjectDisposedException</c>, in a caller that did nothing wrong.
///
/// Unpooled, a connection is never entered in that registry, is invisible to anything clearing it, and
/// releases its file the moment it is disposed - which is what the clearing was for.
/// </remarks>
internal static class SqliteConnections
{
	/// <summary>
	/// Opens the database file, creating it if it is not there.
	/// </summary>
	/// <param name="file">Path of the database file.</param>
	/// <returns>An open connection, owned by the caller and by nothing else.</returns>
	public static SqliteConnection Open(string file)
	{
		var connectionString = new SqliteConnectionStringBuilder
		{
			DataSource = Path.GetFullPath(file),
			Pooling = false,
		}.ToString();

		var connection = new SqliteConnection(connectionString);

		connection.Open();

		return connection;
	}
}
