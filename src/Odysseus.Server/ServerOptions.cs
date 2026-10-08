namespace StockSharp.Odysseus.Server;

using System.Collections.Generic;
using System.Globalization;
using System.IO;

/// <summary>
/// How this instance was started.
/// </summary>
/// <param name="Mode">Local on the user's machine, or hosted for strangers.</param>
/// <param name="ProjectsRoot">Directory the projects live in.</param>
/// <param name="MarketData">Folder of the market-data storage every project shares.</param>
/// <param name="Broker">Broker credentials, when any were supplied.</param>
/// <param name="Connector">The connector to select before the first tool call, when one was named.</param>
/// <param name="RemoteStorage">
/// The remote StockSharp storage server history is imported from instead of the broker, when one was named.
/// </param>
/// <param name="ConnectorSources">Where a connector package may be downloaded from.</param>
/// <param name="ConnectorAllow">Package identifier prefixes this server will load; an empty list allows none.</param>
/// <param name="AllowedProducts">
/// Numeric identifiers of the StockSharp products this server may install; an empty list, which is the
/// default, allows none.
/// </param>
public sealed record ServerOptions(
	ServerModes Mode,
	string ProjectsRoot,
	string MarketData,
	BrokerCredentials Broker,
	ConnectorChoice Connector,
	RemoteStorageChoice RemoteStorage,
	IReadOnlyList<string> ConnectorSources,
	IReadOnlyList<string> ConnectorAllow,
	IReadOnlyList<long> AllowedProducts)
{
	/// <summary>The gallery a connector comes from unless the operator names another.</summary>
	public const string DefaultSource = "https://api.nuget.org/v3/index.json";

	/// <summary>The only package family this server downloads unless the operator widens it.</summary>
	public const string DefaultAllow = "StockSharp.";
	private const string RootVariable = "ODYSSEUS_PROJECTS_ROOT";
	private const string MarketDataVariable = "ODYSSEUS_MARKET_DATA";
	private const string RemoteStorageVariable = "ODYSSEUS_REMOTE_STORAGE";
	private const string KeysVariable = "ODYSSEUS_BROKER_KEYS";
	private const string ConnectorVariable = "ODYSSEUS_BROKER_CONNECTOR";
	private const string SourcesVariable = "ODYSSEUS_CONNECTOR_SOURCES";
	private const string AllowVariable = "ODYSSEUS_CONNECTOR_ALLOW";
	private const string ProductsVariable = "ODYSSEUS_PRODUCT_ALLOW";

	/// <summary>Where downloaded connectors are kept between runs.</summary>
	public string ConnectorCache => Path.Combine(ProjectsRoot, "connectors");

	/// <summary>Whether credentials were supplied at all.</summary>
	public bool HasCredentials => Broker is not null;

	/// <summary>Whether a connector was named to be selected at start-up.</summary>
	public bool HasConnector => Connector is not null;

	/// <summary>
	/// Whether a connector may be read or chosen through a tool.
	/// </summary>
	/// <remarks>
	/// Not in the hosted mode, and for all three connector tools rather than for selecting alone.
	/// Reading a package is executing it: what a connector is - which markets it serves, whether it can
	/// be told it is on a paper account - are instance properties, so describing one and listing what is
	/// already downloaded both construct the downloaded type. The mode is a start-up decision that is
	/// deliberately unreachable from any tool, and a connector must not become the way round that.
	/// </remarks>
	public bool CanLoadConnectors => Mode != ServerModes.Hosted;

	/// <summary>
	/// Whether any StockSharp product may be installed, updated, removed or listed through a tool.
	/// </summary>
	/// <remarks>
	/// Two gates, and they mirror ones that already exist. Not in the hosted mode, for the sentence the
	/// connector tools carry with one word changed: a stranger does not get to choose which code this
	/// process runs, and still less which code lands on the operator's disk. And not at all unless the
	/// operator named the products that may be touched - unlike the connector allow-list, which defaults
	/// to the platform's own package family because half the product needs a connector, this one
	/// defaults to nothing, because no part of the research loop needs a product.
	///
	/// It covers the read-only listings too. They take the same machine-wide mutex, need the same
	/// account and make the same network round trip as an install, and an instance that may install
	/// nothing has nothing useful to say about what it might install.
	/// </remarks>
	public bool CanInstallProducts => Mode != ServerModes.Hosted && AllowedProducts.Count > 0;

	/// <summary>
	/// Reads the options from the command line and the environment.
	/// </summary>
	/// <param name="args">Command line arguments.</param>
	/// <returns>The options.</returns>
	public static ServerOptions Read(string[] args)
	{
		ArgumentNullException.ThrowIfNull(args);

		// A start-up decision and deliberately not reachable from any tool, so that whatever comes to
		// depend on the mode, an agent cannot talk its way into the wider of the two.
		var mode = args.Contains("--hosted", StringComparer.OrdinalIgnoreCase)
			? ServerModes.Hosted
			: ServerModes.Local;

		var root = Environment.GetEnvironmentVariable(RootVariable);

		if (string.IsNullOrWhiteSpace(root))
			root = Path.Combine(Directory.GetCurrentDirectory(), "projects");

		var marketData = Environment.GetEnvironmentVariable(MarketDataVariable);

		if (string.IsNullOrWhiteSpace(marketData))
			marketData = Path.Combine(Directory.GetCurrentDirectory(), "market-data");

		var sources = Entries(Environment.GetEnvironmentVariable(SourcesVariable));
		var allow = Entries(Environment.GetEnvironmentVariable(AllowVariable));

		// A source list that was written and came out empty is widened back to the gallery, because an
		// empty one narrows nothing: the fetcher falls back to the gallery whatever this says, and
		// reporting no source while downloading from one would be the worse of the two answers.
		if (sources is null || sources.Count == 0)
			sources = [DefaultSource];

		// The allow-list is the opposite, because it is the guard rather than a convenience. A list that
		// was written and came out empty is a rule somebody meant to impose and got wrong, and it is
		// kept empty - which refuses every package - rather than widened back to the default they were
		// narrowing away from.
		if (allow is null)
			allow = [DefaultAllow];

		return new(
			mode,
			Path.GetFullPath(root),
			Path.GetFullPath(marketData),
			BrokerCredentialFile.Read(Environment.GetEnvironmentVariable(KeysVariable)),
			ConnectorFile.Read(Environment.GetEnvironmentVariable(ConnectorVariable)),
			RemoteStorageFile.Read(Environment.GetEnvironmentVariable(RemoteStorageVariable)),
			sources,
			allow,
			Products(Environment.GetEnvironmentVariable(ProductsVariable)));
	}

	/// <summary>
	/// Reads the list of product identifiers the operator allowed.
	/// </summary>
	/// <param name="value">What the variable held.</param>
	/// <returns>The identifiers, which are none unless the operator wrote some.</returns>
	/// <remarks>
	/// Unset and set-but-empty come to the same thing here, which is the opposite of the connector
	/// allow-list only because the default is the opposite: there is nothing to widen back to.
	///
	/// An entry that is not a positive number names no product and is dropped rather than refused. A
	/// typo would otherwise stop a server whose research half is entirely unaffected by it, and what
	/// was actually understood is reported by describe_server and get_installer_state, so a typo is
	/// visible in the one place somebody would go and look.
	/// </remarks>
	private static IReadOnlyList<long> Products(string value)
	{
		var entries = Entries(value);

		if (entries is null)
			return [];

		var allowed = new List<long>();

		foreach (var entry in entries)
		{
			if (long.TryParse(entry, NumberStyles.None, CultureInfo.InvariantCulture, out var id) &&
				id > 0 &&
				!allowed.Contains(id))
				allowed.Add(id);
		}

		allowed.Sort();

		return allowed;
	}

	/// <summary>
	/// Splits a delimited variable into its entries.
	/// </summary>
	/// <param name="value">What the variable held.</param>
	/// <returns>The entries, which may be none, or <see langword="null"/> when the variable said nothing.</returns>
	private static IReadOnlyList<string> Entries(string value)
		=> string.IsNullOrWhiteSpace(value)
			? null
			: value.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
