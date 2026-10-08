namespace StockSharp.Odysseus.Application;

/// <summary>
/// A remote StockSharp storage server to import history from instead of a broker.
/// </summary>
/// <param name="Address">Host and port of the server, for example <c>10.0.0.5:5002</c>.</param>
/// <param name="Login">Account to sign in with, or empty for a server anybody may read.</param>
/// <param name="Password">Password of the account, or empty.</param>
public sealed record RemoteStorageChoice(string Address, string Login, string Password)
{
	/// <summary>Prefix of the source name a dataset imported from such a server records.</summary>
	public const string SourcePrefix = "storage:";

	/// <summary>The source name a dataset imported from this server records.</summary>
	public string SourceName => $"{SourcePrefix}{Address}";

	/// <inheritdoc />
	public override string ToString()
		=> string.IsNullOrEmpty(Login) ? Address : $"{Login}@{Address}";
}
