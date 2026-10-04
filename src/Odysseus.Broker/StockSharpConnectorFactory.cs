namespace Odysseus.Broker;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Ecng.Common;

using StockSharp.Messages;

using Odysseus.Application;
using Odysseus.Packages;
using Odysseus.Platform;

/// <summary>
/// Loads a broker connector out of a published package while the server runs.
/// </summary>
/// <remarks>
/// The server cannot compile itself, so a connector cannot be a compile-time dependency of it. What
/// makes this safe rather than merely dynamic is stated rather than assumed: the package must be under
/// one of the identifier prefixes the operator allowed, it comes only from the sources the operator
/// named, it may not want a newer platform than this server already carries, and the adapter it holds is
/// put into demo mode before it is configured and checked again afterwards.
///
/// Downloading is executing. That sentence is why the allow-list is a safety property and not
/// decoration: the analyzers that constrain a generated strategy say nothing about a connector, which
/// does network input and output by its nature.
/// </remarks>
public sealed class StockSharpConnectorFactory : IConnectorFactory
{
	// A remote storage server is reached through the platform's FIX transport, loaded like any connector.
	private static readonly ConnectorChoice _storageTransport = new(
		"StockSharp.Fix",
		null,
		"StockSharp.Fix.FixMessageAdapter",
		new Dictionary<string, string>(StringComparer.Ordinal));

	private readonly PackageFetcher _packages;
	private readonly HostPackages _host;
	private readonly BrokerCredentials _credentials;
	private readonly TradingMandate _mandate;
	private readonly List<ConnectorLoadContext> _contexts = [];
	private readonly Lock _sync = new();

	/// <summary>
	/// Creates the factory.
	/// </summary>
	/// <param name="packages">How a package is obtained and unpacked.</param>
	/// <param name="host">Which platform versions this server carries.</param>
	/// <param name="credentials">What the credential file held, or null when there was none.</param>
	/// <param name="allowed">Package identifier prefixes that may be downloaded at all.</param>
	/// <param name="sources">Where packages are downloaded from, as the operator named them.</param>
	/// <param name="mandate">
	/// Which account every connector this factory opens may reach. Fixed here, at construction, from
	/// configuration the process was started with: no call can change it, and nothing on any protocol
	/// carries it.
	/// </param>
	internal StockSharpConnectorFactory(
		PackageFetcher packages,
		HostPackages host,
		BrokerCredentials credentials,
		IEnumerable<string> allowed,
		IEnumerable<string> sources,
		TradingMandate mandate)
	{
		ArgumentNullException.ThrowIfNull(allowed);
		ArgumentNullException.ThrowIfNull(sources);

		_packages = packages ?? throw new ArgumentNullException(nameof(packages));
		_host = host ?? throw new ArgumentNullException(nameof(host));
		_credentials = credentials;
		_mandate = mandate ?? throw new ArgumentNullException(nameof(mandate));

		Allowed = [.. allowed.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim())];
		Sources = [.. sources];

		// The platform resolves a type written as text through this hook before giving up. Without it a
		// connector's own settings cannot name their own types, because those types live in a load context
		// nothing else enumerates.
		Converter.TypeFallback = Resolve;
	}

	/// <summary>
	/// Builds the factory a host uses.
	/// </summary>
	/// <param name="cacheRoot">Directory downloaded connectors are kept in.</param>
	/// <param name="credentials">What the credential file held, or null when there was none.</param>
	/// <param name="sources">Feed addresses or local directories, in the order they are to be tried.</param>
	/// <param name="allowed">Package identifier prefixes that may be downloaded at all.</param>
	/// <param name="mandate">
	/// Which account this process may reach. Required rather than defaulted, because a default would be a
	/// silent choice of whose money is at risk; the MCP server and the command line pass
	/// <see cref="TradingMandate.Paper"/> as a literal, and only a runner passes anything else.
	/// </param>
	/// <returns>The factory.</returns>
	/// <remarks>
	/// The package machinery is assembled here rather than by the composition root, so that a host does
	/// not have to name the package layer at all - and so that the framework a connector is unpacked for
	/// is the one this process is actually running on rather than one somebody wrote down.
	/// </remarks>
	public static StockSharpConnectorFactory Create(
		string cacheRoot,
		BrokerCredentials credentials,
		IEnumerable<string> sources,
		IEnumerable<string> allowed,
		TradingMandate mandate)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
		ArgumentNullException.ThrowIfNull(sources);
		ArgumentNullException.ThrowIfNull(allowed);
		ArgumentNullException.ThrowIfNull(mandate);

		var named = sources.ToArray();

		var fetcher = new PackageFetcher(
			new PackageSourceSet(named),
			new PackageCache(cacheRoot),
			AppContext.TargetFrameworkName ?? "net10.0");

		return new(fetcher, HostPackages.Read(AppContext.BaseDirectory), credentials, allowed, named, mandate);
	}

	/// <summary>
	/// Which account this factory's connectors may reach, as the process was configured at start-up.
	/// </summary>
	/// <remarks>
	/// Exposed so that a runner can report its own mode without asking anything that could answer
	/// differently. It is read-only on purpose: there is no setter and no call that takes one.
	/// </remarks>
	public TradingMandate Mandate => _mandate;

	/// <inheritdoc />
	public IReadOnlyList<string> Allowed { get; }

	/// <inheritdoc />
	public IReadOnlyList<string> Sources { get; }

	/// <inheritdoc />
	public async ValueTask<ConnectorDescription> InspectAsync(ConnectorChoice choice, CancellationToken cancellationToken)
	{
		var (description, _) = await LoadAsync(choice, cancellationToken);

		return description;
	}

	/// <inheritdoc />
	public async ValueTask<BrokerBinding> OpenAsync(ConnectorChoice choice, CancellationToken cancellationToken)
	{
		var (description, adapter) = await LoadAsync(choice, cancellationToken);

		var settings = Settings(choice);
		var boards = SymbolBoards.From(Reserved(choice, "board"), Reserved(choice, "optionBoard"));

		// Checked against what was loaded rather than against what was asked for: a choice naming no
		// version resolves to whatever the sources offer today, and "whatever is newest" is not what
		// anybody authorised last week. On paper this returns without looking at anything.
		LiveTradingGuard.AssertPackage(description.PackageId, description.PackageVersion, _mandate);

		var source = new ConnectorAdapterSource(
			adapter, _credentials, settings, boards, description.SourceName, _mandate);

		// Built once here rather than lazily on the first download, so a connector whose credentials or
		// settings do not fit is refused by the call that chose it and not by the call that needed it.
		source.Create(new IncrementalIdGenerator()).Dispose();

		// One trader, registered for both the trading port and the reading one, because one connector owns
		// one connection: two objects would open two of them against the same account.
		var trader = new StockSharpPaperTrader(source);

		return new(
			description,
			new StockSharpHistorySource(source),
			new StockSharpQuoteSource(source),
			trader,
			trader,
			new StockSharpSecurityLookup(source));
	}

	/// <inheritdoc />
	public async ValueTask<IHistorySource> OpenStorageAsync(RemoteStorageChoice choice, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(choice);

		var (_, transport) = await LoadAsync(_storageTransport, cancellationToken);

		return new StockSharpStorageHistorySource(transport, choice);
	}

	/// <inheritdoc />
	public ValueTask<IReadOnlyList<ConnectorDescription>> InstalledAsync(CancellationToken cancellationToken)
	{
		var found = new List<ConnectorDescription>();

		foreach (var (id, version) in _packages.Cache.Contents())
		{
			cancellationToken.ThrowIfCancellationRequested();

			FetchedPackage package;

			try
			{
				package = _packages.Cached(id, version);
			}
			catch (PackageUnavailableException)
			{
				// A folder that cannot be read is not a connector this server holds, and one damaged file
				// is no reason to answer nothing about the rest of the cache. Naming that package asks for
				// it by name, and that call says what is wrong with it.
				continue;
			}

			if (package is null)
				continue;

			foreach (var adapter in AdapterCatalog.Find(Load(package)))
				found.Add(Describe(package, adapter, new Dictionary<string, string>()));
		}

		return ValueTask.FromResult<IReadOnlyList<ConnectorDescription>>(found);
	}

	private async ValueTask<(ConnectorDescription Description, Type Adapter)> LoadAsync(
		ConnectorChoice choice,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(choice);

		AssertAllowed(choice.PackageId);
		AdapterConfigurator.Validate(choice);

		FetchedPackage package;

		try
		{
			package = await _packages.FetchAsync(choice.PackageId, choice.PackageVersion, cancellationToken);
		}
		catch (PackageUnavailableException error)
		{
			// Re-raised as a refusal so the answer carries a category an agent can branch on. A package
			// that is not there is the caller's choice being wrong, not a defect of this server.
			throw new ConnectorRefusedException(error.Message);
		}

		_host.AssertSatisfies(package);

		var adapter = AdapterCatalog.Select(Load(package), choice.AdapterTypeName);

		return (Describe(package, adapter, Settings(choice)), adapter);
	}

	/// <summary>
	/// Refuses a package the operator did not allow, before anything is downloaded.
	/// </summary>
	/// <param name="packageId">Package the caller named.</param>
	/// <exception cref="ConnectorRefusedException">The list does not cover it, or there is no list.</exception>
	/// <remarks>
	/// An empty list refuses everything rather than allowing everything. A list is only ever written to
	/// narrow what may be loaded, so one that came out empty - a variable that held nothing but
	/// separators, a configuration step that ran and produced no entry - is a rule somebody meant to
	/// impose and got wrong. Reading it as "no rule" would turn that mistake into permission to download
	/// and run any package on the internet, which is the one failure here that cannot be taken back.
	/// </remarks>
	private void AssertAllowed(string packageId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(packageId);

		if (Allowed.Count == 0)
		{
			throw new ConnectorRefusedException(
				$"This server will not load '{packageId}', or any other package: the list of package " +
				"identifier prefixes it accepts is empty, and an empty list allows nothing. Start the server " +
				"with ODYSSEUS_CONNECTOR_ALLOW naming the prefixes it may load, for example 'StockSharp.'.");
		}

		if (Allowed.Any(a => packageId.StartsWith(a, StringComparison.OrdinalIgnoreCase)))
			return;

		throw new ConnectorRefusedException(
			$"This server will not download '{packageId}'. Loading a connector downloads code and runs it " +
			"here - describing one builds the adapter as surely as choosing one does - so only packages " +
			$"under {string.Join(", ", Allowed)} are allowed. The list is a start-up decision and is not " +
			"reachable from a tool.");
	}

	private Assembly Load(FetchedPackage package)
	{
		if (!package.Assemblies.TryGetValue(package.Id, out var path))
		{
			path = package.Assemblies.Count == 1
				? package.Assemblies.Values.Single()
				: throw new ConnectorRefusedException(
					$"{package.Id} {package.Version} holds {package.Assemblies.Count} assemblies and none of them " +
					$"is called {package.Id}, so there is no telling which one is the connector.");
		}

		var context = new ConnectorLoadContext($"connector:{package.Id}/{package.Version}", package.Assemblies);

		using (_sync.EnterScope())
			_contexts.Add(context);

		return context.LoadFromAssemblyPath(path);
	}

	/// <summary>
	/// What a connector turns out to be. One instance of the adapter is built and thrown away, because
	/// two of the answers - which markets it is for and whether it says a configuration file is enough
	/// for it - are instance properties, and reporting a guess for them would be worse than the cost of
	/// a constructor that reads three attributes.
	/// </summary>
	private static ConnectorDescription Describe(
		FetchedPackage package,
		Type adapter,
		IReadOnlyDictionary<string, string> settings)
	{
		using var probe = AdapterCatalog.Create(adapter, new IncrementalIdGenerator());

		return new(
			package.Id,
			package.Version,
			package.Sha256,
			adapter.FullName,
			adapter.Name,
			Categories(probe.Categories),
			AdapterConfigurator.CredentialShapeOf(adapter),
			probe is IDemoAdapter,
			probe.ExtraSetup,
			AdapterConfigurator.Describe(adapter),
			ConnectorChoice.SourceNameOf(package.Id, settings));
	}

	private static IReadOnlyList<string> Categories(MessageAdapterCategories categories)
	{
		if (categories == default)
			return [];

		return categories.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
	}

	private static IReadOnlyDictionary<string, string> Settings(ConnectorChoice choice)
	{
		var settings = new Dictionary<string, string>(StringComparer.Ordinal);

		foreach (var (name, value) in choice.Settings ?? new Dictionary<string, string>())
		{
			if (!ConnectorChoice.ReservedSettings.Contains(name, StringComparer.Ordinal))
				settings[name] = value;
		}

		return settings;
	}

	private static string Reserved(ConnectorChoice choice, string name)
		=> choice.Settings is not null && choice.Settings.TryGetValue(name, out var value) ? value : string.Empty;

	private Type Resolve(string name)
	{
		ConnectorLoadContext[] contexts;

		using (_sync.EnterScope())
			contexts = [.. _contexts];

		foreach (var context in contexts)
		{
			foreach (var assembly in context.Assemblies)
			{
				if (assembly.GetType(name, throwOnError: false) is { } found)
					return found;
			}
		}

		return null;
	}
}
