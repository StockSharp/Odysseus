namespace Odysseus.Application;

using System.Linq;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Which connector to use and how it is configured.
/// </summary>
/// <param name="PackageId">Package holding the adapter, for example <c>StockSharp.Binance</c>.</param>
/// <param name="PackageVersion">Exact version, or empty for the newest the sources offer.</param>
/// <param name="AdapterTypeName">Full type name of the adapter, or empty when the package holds one.</param>
/// <param name="Settings">
/// Adapter settings by property name. Values are text and are converted by the type the adapter declares,
/// so the caller never has to know whether a setting is a number, an enumeration or a list.
/// </param>
public sealed record ConnectorChoice(
	string PackageId,
	string PackageVersion,
	string AdapterTypeName,
	IReadOnlyDictionary<string, string> Settings)
{
	/// <summary>Prefix every source name carries, saying that the bars came through a connector.</summary>
	public const string SourcePrefix = "connector:";

	/// <summary>Settings that configure this server rather than the adapter, and are removed before it is touched.</summary>
	/// <remarks>
	/// They are answered here rather than by the adapter because they say which market the symbols belong
	/// to, which is a question about the caller's universe and not about the venue's API.
	/// </remarks>
	public static IReadOnlyList<string> ReservedSettings { get; } = ["board", "optionBoard"];

	/// <summary>
	/// The identity a dataset records for history that came through this choice.
	/// </summary>
	/// <param name="packageId">Package holding the adapter.</param>
	/// <param name="settings">Everything that was configured on it.</param>
	/// <returns>The source name.</returns>
	/// <remarks>
	/// Derived rather than chosen. The name is part of what a dataset hashes, and that hash keyed with
	/// the project's secret decides both the dataset's identity and where its closed slice begins - so a
	/// name the caller could write would be a lever on the split. It is derived from the package and
	/// from the settings, so it changes when the data changes and not otherwise.
	///
	/// It names the connector package in full. The venue on its own - which is what the package
	/// identifier reads as once the platform's prefix is taken off it - was the name from when this
	/// product downloaded from one broker and nothing else; two packages can serve the same venue, and
	/// a name that says only the venue tells a reader nothing about which code produced the bars.
	/// </remarks>
	public static string SourceNameOf(string packageId, IReadOnlyDictionary<string, string> settings)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(packageId);

		var material = string.Join(
			"\n",
			(settings ?? new Dictionary<string, string>())
				.OrderBy(s => s.Key, StringComparer.Ordinal)
				.Select(s => $"{s.Key}={s.Value}"));

		var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));

		return $"{SourcePrefix}{packageId.ToLowerInvariant()}@{digest[..8]}";
	}
}

/// <summary>
/// One setting an adapter declares.
/// </summary>
/// <param name="Name">Property name, which is what a choice carries.</param>
/// <param name="Kind">The type the adapter declares it as, written out.</param>
/// <param name="Description">What it is for, as the adapter describes it.</param>
/// <param name="IsRequired">Whether the adapter cannot connect without it.</param>
/// <param name="IsSecret">Whether the value must never be echoed back.</param>
/// <param name="AllowedValues">The values it accepts, when it accepts a fixed set; empty when free.</param>
public sealed record ConnectorSetting(
	string Name,
	string Kind,
	string Description,
	bool IsRequired,
	bool IsSecret,
	IReadOnlyList<string> AllowedValues);

/// <summary>
/// What a connector turned out to be, once its package was read.
/// </summary>
/// <param name="PackageId">Package it came from.</param>
/// <param name="PackageVersion">Version that was downloaded.</param>
/// <param name="PackageHash">SHA-256 of the package file this server read.</param>
/// <param name="AdapterTypeName">Full name of the adapter type inside it.</param>
/// <param name="DisplayName">What the adapter calls itself.</param>
/// <param name="Categories">What the platform says the adapter is for.</param>
/// <param name="CredentialShape">Which credentials it takes: key-secret, login-password, token, passphrase or none.</param>
/// <param name="IsPaperCapable">Whether it can be told it is on a paper account.</param>
/// <param name="NeedsExtraSetup">Whether the adapter says a configuration file is not enough for it.</param>
/// <param name="Settings">Every setting it declares.</param>
/// <param name="SourceName">The identity a dataset records for history downloaded through it.</param>
public sealed record ConnectorDescription(
	string PackageId,
	string PackageVersion,
	string PackageHash,
	string AdapterTypeName,
	string DisplayName,
	IReadOnlyList<string> Categories,
	string CredentialShape,
	bool IsPaperCapable,
	bool NeedsExtraSetup,
	IReadOnlyList<ConnectorSetting> Settings,
	string SourceName);

/// <summary>
/// The broker ports one loaded connector supplies.
/// </summary>
/// <param name="Connector">What was loaded.</param>
/// <param name="History">Downloading finished bars.</param>
/// <param name="Quotes">Reading the current price of an instrument.</param>
/// <param name="Trader">Running a candidate on the paper account.</param>
/// <param name="Account">Reading the paper account.</param>
/// <param name="Lookup">Asking what instruments exist.</param>
public sealed record BrokerBinding(
	ConnectorDescription Connector,
	IHistorySource History,
	IQuoteSource Quotes,
	IPaperTrader Trader,
	IPaperAccount Account,
	ISecurityLookup Lookup);

/// <summary>
/// Loading a broker connector that is chosen while the server runs.
/// </summary>
/// <remarks>
/// The server cannot compile itself, so a connector cannot be a compile-time dependency of it. It arrives
/// as a published package and is loaded into a context of its own.
///
/// Nothing on this port names a type of the trading platform. That is deliberate: the use cases above it
/// already speak in <see cref="IHistorySource"/> and its three neighbours, and a port that handed back a
/// vendor's adapter would put that vendor's package into the layer whose whole purpose is not to have one.
/// </remarks>
public interface IConnectorFactory
{
	/// <summary>
	/// Reads a package without selecting it: what it is, what it wants, and whether it can be paper.
	/// </summary>
	/// <param name="choice">Which connector to read.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>What the package turned out to hold.</returns>
	ValueTask<ConnectorDescription> InspectAsync(ConnectorChoice choice, CancellationToken cancellationToken);

	/// <summary>
	/// Loads the connector and builds the four ports over it.
	/// </summary>
	/// <param name="choice">Which connector to load and how to configure it.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The ports.</returns>
	ValueTask<BrokerBinding> OpenAsync(ConnectorChoice choice, CancellationToken cancellationToken);

	/// <summary>
	/// Loads the transport a remote StockSharp storage server speaks and reads history through it.
	/// </summary>
	/// <param name="choice">The server and the account to sign in with.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>History read from that server.</returns>
	ValueTask<IHistorySource> OpenStorageAsync(RemoteStorageChoice choice, CancellationToken cancellationToken);

	/// <summary>
	/// What this server already holds, so a choice can be made without a download.
	/// </summary>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>Every connector in the cache.</returns>
	ValueTask<IReadOnlyList<ConnectorDescription>> InstalledAsync(CancellationToken cancellationToken);

	/// <summary>Package identifier prefixes this server will download at all.</summary>
	IReadOnlyList<string> Allowed { get; }

	/// <summary>Where packages are downloaded from.</summary>
	IReadOnlyList<string> Sources { get; }
}
