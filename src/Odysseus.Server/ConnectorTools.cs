namespace StockSharp.Odysseus.Server;

/// <summary>
/// Choosing which broker this server talks to.
/// </summary>
/// <remarks>
/// The server cannot compile itself, so a connector is not a compile-time dependency of it: it arrives
/// as a published package and is loaded into a context of its own
/// while the server runs. Nothing here names a particular broker; the product is a research server that
/// takes one, rather than a product for one venue.
///
/// All three tools here run downloaded code, not selecting alone. What a connector is - which markets
/// it serves, whether it can be told it is on a paper account - are properties of an instance, so
/// describing a package constructs the type inside it and listing what is already downloaded constructs
/// one of every package in the cache. That is why the three are bounded by the same three start-up
/// decisions an agent cannot reach: the mode, which refuses all of them when this server answers a
/// stranger; where packages may come from; and which package identifiers are allowed at all.
/// </remarks>
[McpServerToolType]
public static class ConnectorTools
{
	/// <summary>
	/// Reports what connectors this server already holds and what it will accept.
	/// </summary>
	/// <param name="guard">Turns a failure into the published error shape.</param>
	/// <param name="gateway">The four broker ports and what is bound to them.</param>
	/// <param name="options">How this instance was started.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The cache, the limits and what is bound now.</returns>
	[McpServerTool(Name = "list_connectors")]
	[Description("Report which broker connectors this server has already downloaded, which one is bound " +
		"now, where it may download from and which package names it will accept. Touches no network, but " +
		"it does build one of every connector in the cache to read it, which is why a hosted instance " +
		"refuses it. A connector is what turns import_history, the instrument lookups and paper trading " +
		"on; everything else works without one.")]
	public static Task<object> ListConnectors(
		ToolGuard guard,
		BrokerGateway gateway,
		ServerOptions options,
		CancellationToken cancellationToken)
		=> guard.RunAsync(nameof(ListConnectors), async () =>
		{
			AssertLoadingAllowed(options, "Reading the connectors this server holds builds each of them");

			var installed = await gateway.Connectors.InstalledAsync(cancellationToken);

			return new
			{
				schemaVersion = 1,
				bound = Describe(gateway.Current),

				// Nothing here says whether a connector may be chosen: an answer at all is that answer,
				// because an instance that refuses to choose one refuses to read them too. describe_server
				// reports it for a caller that has not asked for a connector yet.
				sources = gateway.Connectors.Sources,
				allowedPackagePrefixes = gateway.Connectors.Allowed,
				installed = installed.Select(Describe).ToArray(),
			};
		});

	/// <summary>
	/// Reads a connector package without binding it.
	/// </summary>
	/// <param name="guard">Turns a failure into the published error shape.</param>
	/// <param name="gateway">The four broker ports and what is bound to them.</param>
	/// <param name="options">How this instance was started.</param>
	/// <param name="packageId">Package holding the adapter.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <param name="version">Exact version, or empty for the newest release.</param>
	/// <param name="adapter">Full type name of the adapter, or empty when the package holds one.</param>
	/// <returns>What the package holds.</returns>
	[McpServerTool(Name = "describe_connector")]
	[Description("Download a connector package and report what it is without binding it: the adapter " +
		"inside it, what it is for, which credentials it takes, whether it can be proven to be on a paper " +
		"account, and every setting it accepts with the values each one allows. Reading it downloads the " +
		"package and builds the adapter, so a hosted instance refuses this as it refuses select_connector. " +
		"Read this before select_connector: the settings it reports are exactly the ones selecting will accept.")]
	public static Task<object> DescribeConnector(
		ToolGuard guard,
		BrokerGateway gateway,
		ServerOptions options,
		[Description("Package holding the connector, for example 'StockSharp.Binance'.")] string packageId,
		CancellationToken cancellationToken,
		[Description("Exact package version. Leave empty for the newest release the sources offer.")] string version = "",
		[Description("Full type name of the adapter. Leave empty when the package holds exactly one.")] string adapter = "")
		=> guard.RunAsync(nameof(DescribeConnector), async () =>
		{
			AssertLoadingAllowed(options, "Describing a connector downloads it and builds the adapter inside it");

			var choice = new ConnectorChoice(packageId, version ?? string.Empty, adapter ?? string.Empty, null);

			return Detail(await gateway.Connectors.InspectAsync(choice, cancellationToken));
		});

	/// <summary>
	/// Loads a connector and binds it to the broker ports.
	/// </summary>
	/// <param name="guard">Turns a failure into the published error shape.</param>
	/// <param name="gateway">The four broker ports and what is bound to them.</param>
	/// <param name="options">How this instance was started.</param>
	/// <param name="packageId">Package holding the adapter.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <param name="version">Exact version, or empty for the newest release.</param>
	/// <param name="adapter">Full type name of the adapter, or empty when the package holds one.</param>
	/// <param name="settings">Settings for the adapter, as a JSON object.</param>
	/// <returns>What was bound.</returns>
	[McpServerTool(Name = "select_connector")]
	[Description("Download a connector, put it into demo mode, apply the credentials this server was " +
		"started with and the settings you pass, and bind it: from then on import_history, " +
		"lookup_symbols, lookup_option_contracts, deploy_candidate and get_account_state work. " +
		"Two settings are reserved by this server rather than passed on - 'board' and 'optionBoard' name " +
		"the markets a symbol is quoted on. Credentials are never passed here; they are read from a file " +
		"the server was started with. A connector that cannot be told it is on a paper account is " +
		"refused. Selecting again loads a second connector and leaves the first one loaded, so choose " +
		"once per server if you can. A hosted instance refuses this, as it refuses the other two: its " +
		"connector is chosen when the process starts.")]
	public static Task<object> SelectConnector(
		ToolGuard guard,
		BrokerGateway gateway,
		ServerOptions options,
		[Description("Package holding the connector, for example 'StockSharp.Binance'.")] string packageId,
		CancellationToken cancellationToken,
		[Description("Exact package version. Leave empty for the newest release the sources offer.")] string version = "",
		[Description("Full type name of the adapter. Leave empty when the package holds exactly one.")] string adapter = "",
		[Description("Settings as a JSON object of name and value, for example {\"StockFeed\":\"sip\"}. Leave empty for the connector's own defaults.")] string settings = "")
		=> guard.RunAsync(nameof(SelectConnector), async () =>
		{
			AssertLoadingAllowed(options, "Choosing a connector downloads code and runs it in this server");

			var choice = new ConnectorChoice(
				packageId,
				version ?? string.Empty,
				adapter ?? string.Empty,
				ConnectorFile.ReadSettings(settings, "The settings argument"));

			var bound = await gateway.SelectAsync(choice, cancellationToken);

			return Detail(bound);
		});

	/// <summary>
	/// Refuses a call that would run a downloaded connector in a server that answers a stranger.
	/// </summary>
	/// <param name="options">How this instance was started.</param>
	/// <param name="what">What the caller asked for, and what running it amounts to.</param>
	/// <exception cref="ServerModeException">This instance is hosted.</exception>
	private static void AssertLoadingAllowed(ServerOptions options, string what)
	{
		if (options.CanLoadConnectors)
			return;

		throw new ServerModeException(
			$"{what}, which a hosted instance does not allow. The connector of a hosted instance is a " +
			"start-up decision; describe_server reports which one is bound.");
	}

	private static object Detail(ConnectorDescription connector)
		=> new
		{
			schemaVersion = 1,
			packageId = connector.PackageId,
			packageVersion = connector.PackageVersion,

			// The hash of the file this server actually read, so what was loaded can be identified later
			// by something other than a name and a number a feed could reissue.
			packageHash = connector.PackageHash,
			adapter = connector.AdapterTypeName,
			displayName = connector.DisplayName,
			categories = connector.Categories,
			credentials = connector.CredentialShape,
			paperCapable = connector.IsPaperCapable,
			needsExtraSetup = connector.NeedsExtraSetup,
			sourceName = connector.SourceName,
			settings = connector.Settings.Select(s => new
			{
				name = s.Name,
				kind = s.Kind,
				description = s.Description,
				required = s.IsRequired,
				secret = s.IsSecret,
				allowedValues = s.AllowedValues,
			}).ToArray(),
		};

	private static object Describe(ConnectorDescription connector)
		=> connector is null
			? null
			: new
			{
				packageId = connector.PackageId,
				packageVersion = connector.PackageVersion,
				adapter = connector.AdapterTypeName,
				sourceName = connector.SourceName,
				paperCapable = connector.IsPaperCapable,
			};
}
