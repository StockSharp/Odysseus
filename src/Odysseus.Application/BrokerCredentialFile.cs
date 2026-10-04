namespace Odysseus.Application;

using System.IO;
using System.Text.RegularExpressions;

/// <summary>
/// The shapes of credential a broker connector can ask for.
/// </summary>
/// <remarks>
/// Not an invention of this server: these are the optional capability interfaces the trading platform
/// declares on an adapter, so a file can be matched against what a connector actually implements rather
/// than against what somebody assumed it wanted.
/// </remarks>
public enum BrokerCredentialKinds
{
	/// <summary>Nothing was supplied.</summary>
	None,

	/// <summary>A key identifier and its secret.</summary>
	KeySecret,

	/// <summary>A user name and a password.</summary>
	LoginPassword,

	/// <summary>A single token.</summary>
	Token,

	/// <summary>A passphrase.</summary>
	Passphrase,
}

/// <summary>
/// Credentials for whichever broker connector is chosen.
/// </summary>
/// <param name="Kind">Which shape the file turned out to carry.</param>
/// <param name="Values">The values by name, lower-cased.</param>
/// <remarks>
/// Read from a file named by an environment variable rather than from the variables themselves: a value
/// in the environment is inherited by every process the server starts - which now includes the isolated
/// worker - and shows up in a process listing, while a path does neither.
/// </remarks>
public sealed record BrokerCredentials(BrokerCredentialKinds Kind, IReadOnlyDictionary<string, string> Values)
{
	/// <summary>
	/// Reads one of the named values.
	/// </summary>
	/// <param name="name">Name to read, lower-cased.</param>
	/// <returns>The value, or an empty string when the file did not carry it.</returns>
	public string Value(string name)
		=> Values.TryGetValue(name, out var value) ? value : string.Empty;
}

/// <summary>
/// The one reader of the broker credential file.
/// </summary>
/// <remarks>
/// One reader, because there used to be four and two of them disagreed: the server accepted
/// <c>key = X</c> and the command line did not, and a person whose file used the equals sign was told
/// there was no broker rather than that the file could not be read. The looser form wins - a file a
/// human writes is allowed spaces around the separator, and either separator.
/// </remarks>
public static class BrokerCredentialFile
{
	private static readonly string[] _names = ["key", "secret", "login", "password", "token", "passphrase"];

	/// <summary>
	/// Reads credentials from a file, if one was named and exists.
	/// </summary>
	/// <param name="path">Path of the file, or null when none was named.</param>
	/// <returns>The credentials, or <see langword="null"/> when there is no usable file.</returns>
	public static BrokerCredentials Read(string path)
	{
		if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
			return null;

		return Parse(File.ReadAllText(path));
	}

	/// <summary>
	/// Reads credentials out of the text of such a file.
	/// </summary>
	/// <param name="text">Contents of the file.</param>
	/// <returns>The credentials, or <see langword="null"/> when nothing usable is in it.</returns>
	public static BrokerCredentials Parse(string text)
	{
		if (string.IsNullOrWhiteSpace(text))
			return null;

		var values = new Dictionary<string, string>(StringComparer.Ordinal);

		foreach (var name in _names)
		{
			var found = Find(text, name);

			if (found is not null)
				values[name] = found;
		}

		var kind = Shape(values);

		return kind == BrokerCredentialKinds.None ? null : new(kind, values);
	}

	/// <summary>
	/// Which shape a set of named values amounts to.
	/// </summary>
	/// <param name="values">What the file carried.</param>
	/// <returns>The shape, or <see cref="BrokerCredentialKinds.None"/> when it is not a usable pair.</returns>
	private static BrokerCredentialKinds Shape(IReadOnlyDictionary<string, string> values)
	{
		if (values.ContainsKey("key") && values.ContainsKey("secret"))
			return BrokerCredentialKinds.KeySecret;

		if (values.ContainsKey("login") && values.ContainsKey("password"))
			return BrokerCredentialKinds.LoginPassword;

		if (values.ContainsKey("token"))
			return BrokerCredentialKinds.Token;

		return values.ContainsKey("passphrase") ? BrokerCredentialKinds.Passphrase : BrokerCredentialKinds.None;
	}

	/// <summary>
	/// Reads one named value out of the text.
	/// </summary>
	/// <remarks>
	/// The value runs to the end of the line rather than to the first space, because a passphrase is
	/// several words far more often than it is one. Anchoring it to a single run of non-space characters
	/// meant a file holding a real passphrase parsed as no credentials at all, and the person who wrote
	/// it was told there was no broker - which is the failure this reader exists to have stopped.
	/// </remarks>
	private static string Find(string text, string name)
	{
		var match = Regex.Match(
			text,
			$@"^\s*{name}\s*[:=]\s*(\S.*?)\s*$",
			RegexOptions.Multiline | RegexOptions.IgnoreCase);

		return match.Success ? match.Groups[1].Value : null;
	}
}
