namespace Odysseus.Broker.Tests;

using System.IO;
using System.Text;
using System.Threading.Tasks;

using Odysseus.Packages;

/// <summary>
/// What happens to a connector that is refused, and where in the sequence it is refused.
/// </summary>
/// <remarks>
/// None of this needs a broker account or a network. A directory holding a package this test builds is
/// a feed as far as the protocol is concerned, and a directory holding a dependency manifest is what
/// the server reads its own platform versions out of - so every way a connector is turned away before
/// it is bound runs here, offline, rather than behind the credential gate, where it was reachable only
/// by whoever had an account.
///
/// The order matters as much as the outcome. A package the operator did not allow must be refused
/// before anything is downloaded, and a package wanting a newer platform than this server carries must
/// be refused before its assembly is loaded, because after the load there is no taking it back.
/// </remarks>
[TestClass]
public class ConnectorLoadingTests : OdysseusTestBase
{
	private const string Fixture = "Odysseus.Fixture";
	private const string Platform = "StockSharp.Messages";
	private const string Wanted = "9.9.9";

	private string _feed;
	private string _cache;
	private string _host;

	/// <summary>Makes a feed of one package, an empty cache, and a folder standing in for the deployment.</summary>
	[TestInitialize]
	public void MakeFeed()
	{
		var root = Path.Combine(Path.GetTempPath(), "odysseus-connectors-offline", Guid.NewGuid().ToString("n"));

		_feed = Path.Combine(root, "feed");
		_cache = Path.Combine(root, "cache");
		_host = Path.Combine(root, "host");

		FixturePackage.WriteTo(_feed, Fixture, "1.2.3", Platform, Wanted);
	}

	/// <summary>Removes what the test made.</summary>
	[TestCleanup]
	public void RemoveFeed()
	{
		var root = Path.GetDirectoryName(_feed);

		if (root is not null && Directory.Exists(root))
			Directory.Delete(root, recursive: true);
	}

	/// <summary>
	/// An allow-list that came out empty allows nothing. A list is only ever written to narrow what may
	/// be downloaded and run here, so reading an empty one as "no restriction" would turn a mistake in
	/// somebody's configuration into permission to run any package there is.
	/// </summary>
	[TestMethod]
	public async Task AnAllowListThatCameOutEmptyRefusesEveryPackage()
	{
		var fetcher = Fetcher();

		var refusal = await ThrowsAsync<ConnectorRefusedException>(
			() => Factory(fetcher, HostCarrying(Wanted), []).InspectAsync(Choice(Fixture), CancellationToken).AsTask());

		IsTrue(refusal.Message.Contains(Fixture, StringComparison.Ordinal), refusal.Message);
		AreEqual(0, fetcher.Cache.Contents().Count, "a package was downloaded for a server that allows none.");
	}

	/// <summary>
	/// A package outside the allow-list is refused before any of it is fetched, so the cache is still
	/// empty afterwards. Refusing after the download would mean the code had already arrived.
	/// </summary>
	[TestMethod]
	public async Task APackageOutsideTheAllowListIsRefusedBeforeItIsDownloaded()
	{
		var fetcher = Fetcher();

		var refusal = await ThrowsAsync<ConnectorRefusedException>(
			() => Factory(fetcher, HostCarrying(Wanted), ["StockSharp."])
				.InspectAsync(Choice(Fixture), CancellationToken)
				.AsTask());

		IsTrue(refusal.Message.Contains(Fixture, StringComparison.Ordinal), refusal.Message);
		IsTrue(refusal.Message.Contains("StockSharp.", StringComparison.Ordinal), refusal.Message);
		AreEqual(0, fetcher.Cache.Contents().Count, "a package outside the allow-list was downloaded anyway.");
	}

	/// <summary>
	/// A connector that wants a newer platform than this server carries is refused, and the refusal
	/// names both versions - the one it asked for and the one that is here - because that pair is the
	/// whole of what the operator has to act on.
	/// </summary>
	[TestMethod]
	public async Task AConnectorWantingANewerPlatformThanTheServerCarriesIsRefused()
	{
		var refusal = await ThrowsAsync<ConnectorRefusedException>(
			() => Factory(Fetcher(), HostCarrying("5.0.7"), ["Odysseus."])
				.InspectAsync(Choice(Fixture), CancellationToken)
				.AsTask());

		IsTrue(refusal.Message.Contains(Wanted, StringComparison.Ordinal),
			$"the refusal does not say which version was wanted: {refusal.Message}");

		IsTrue(refusal.Message.Contains("5.0.7", StringComparison.Ordinal),
			$"the refusal does not say which version is here: {refusal.Message}");
	}

	/// <summary>
	/// The same check passes a connector whose platform this server already carries, which is what says
	/// the refusal above is about the version rather than about every package that names the platform.
	/// The requirement is asserted first, so that a package which stopped declaring one could not make
	/// this pass by having nothing to check.
	/// </summary>
	[TestMethod]
	public async Task APlatformTheServerAlreadyCarriesIsNoRefusal()
	{
		var package = await Fetcher().FetchAsync(Fixture, "1.2.3", CancellationToken);

		AreEqual(1, package.Requirements.Count, "the package declares no dependency, so nothing was checked.");
		AreEqual(Platform, package.Requirements[0].Id);
		AreEqual(Wanted, package.Requirements[0].MinimumVersion);

		HostPackages.Read(HostCarrying("10.0.1")).AssertSatisfies(package);
	}

	/// <summary>
	/// A platform this server built from source has no package version, so a connector is not refused
	/// over one. The number such a build declares is the same for every build, and comparing it with
	/// what a connector wants would refuse every connector there is.
	/// </summary>
	[TestMethod]
	public async Task APlatformBuiltFromSourceIsNotComparedByVersion()
	{
		var package = await Fetcher().FetchAsync(Fixture, "1.2.3", CancellationToken);

		AreEqual(1, package.Requirements.Count, "the package declares no dependency, so nothing was checked.");
		AreEqual(Platform, package.Requirements[0].Id);

		HostPackages.Read(HostBuiltFromSource()).AssertSatisfies(package);
	}

	/// <summary>
	/// A package holding no adapter is refused by name. It is the shape a wrong package identifier
	/// takes: the download succeeds and there is nothing in it to connect with.
	/// </summary>
	[TestMethod]
	public void AnAssemblyWithNoAdapterIsRefused()
	{
		var refusal = Throws<ConnectorRefusedException>(
			() => AdapterCatalog.Select(typeof(ConnectorChoice).Assembly, string.Empty));

		IsTrue(refusal.Message.Contains("no broker adapter", StringComparison.Ordinal), refusal.Message);
	}

	/// <summary>
	/// A package holding several adapters and a choice naming none of them is refused with the names,
	/// rather than one of them being picked. Which broker this server talks to is not a coin toss.
	/// </summary>
	[TestMethod]
	public void SeveralAdaptersAndNoNameIsRefusedWithTheNames()
	{
		var refusal = Throws<ConnectorRefusedException>(
			() => AdapterCatalog.Select(typeof(FeedAdapter).Assembly, string.Empty));

		IsTrue(refusal.Message.Contains(typeof(FeedAdapter).FullName, StringComparison.Ordinal), refusal.Message);
		IsTrue(refusal.Message.Contains(typeof(NoDemoAdapter).FullName, StringComparison.Ordinal), refusal.Message);
	}

	/// <summary>An adapter name the package does not hold is refused with what it does hold.</summary>
	[TestMethod]
	public void AnAdapterNameThatIsNotThereIsRefusedWithWhatIsThere()
	{
		var refusal = Throws<ConnectorRefusedException>(
			() => AdapterCatalog.Select(typeof(FeedAdapter).Assembly, "Odysseus.Broker.Tests.AbsentAdapter"));

		IsTrue(refusal.Message.Contains("AbsentAdapter", StringComparison.Ordinal), refusal.Message);
		IsTrue(refusal.Message.Contains(typeof(FeedAdapter).FullName, StringComparison.Ordinal), refusal.Message);
	}

	/// <summary>A name that is there chooses that adapter, by either the full name or the short one.</summary>
	[TestMethod]
	public void TheAdapterANameChoosesIsTheOneThatComesBack()
	{
		AreEqual(typeof(FeedAdapter), AdapterCatalog.Select(typeof(FeedAdapter).Assembly, typeof(FeedAdapter).FullName));
		AreEqual(typeof(FeedAdapter), AdapterCatalog.Select(typeof(FeedAdapter).Assembly, nameof(FeedAdapter)));
	}

	/// <summary>
	/// A cache folder holding a file that is not a package is not a connector this server holds, and it
	/// does not stop the server reporting the rest of the cache. Reporting what is installed reads every
	/// folder there is, so one damaged file used to be enough to answer nothing at all.
	/// </summary>
	[TestMethod]
	public async Task ACacheEntryThatCannotBeReadIsNotAConnectorThisServerHolds()
	{
		var fetcher = Fetcher();

		fetcher.Cache.Write("Odysseus.Broken", "1.0.0", Encoding.UTF8.GetBytes("this was never a package"));

		var installed = await Factory(fetcher, HostCarrying(Wanted), ["Odysseus."]).InstalledAsync(CancellationToken);

		AreEqual(0, installed.Count, $"an unreadable cache folder was reported as {installed.Count} connectors.");
	}

	private static ConnectorChoice Choice(string packageId)
		=> new(packageId, string.Empty, string.Empty, null);

	private StockSharpConnectorFactory Factory(PackageFetcher fetcher, string host, string[] allowed)
		=> new(fetcher, HostPackages.Read(host), null, allowed, [_feed], TradingMandate.Paper);

	private PackageFetcher Fetcher()
		=> new(new PackageSourceSet([_feed]), new PackageCache(_cache), FixturePackage.Framework);

	/// <summary>
	/// Writes a dependency manifest of the shape a deployment carries, saying which version of a
	/// platform package this server holds.
	/// </summary>
	/// <param name="version">Version of the platform to claim.</param>
	/// <returns>The folder holding the manifest.</returns>
	private string HostCarrying(string version)
		=> Host(version, "package");

	/// <summary>
	/// Writes the manifest of a deployment whose platform was built from source. The entry is there
	/// all the same, under the version every such build declares.
	/// </summary>
	/// <returns>The folder holding the manifest.</returns>
	private string HostBuiltFromSource()
		=> Host("5.0.0", "project");

	private string Host(string version, string type)
	{
		Directory.CreateDirectory(_host);

		File.WriteAllText(
			Path.Combine(_host, $"Odysseus.Host.{version}.deps.json"),
			$$"""
			{
			  "libraries": {
			    "{{Platform}}/{{version}}": { "type": "{{type}}" }
			  }
			}
			""");

		return _host;
	}
}
