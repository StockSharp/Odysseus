namespace StockSharp.Odysseus.Packages.Tests;

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using StockSharp.Odysseus.Packages;
using StockSharp.Odysseus.TestKit;

/// <summary>
/// Obtaining a connector package, without a network.
/// </summary>
/// <remarks>
/// A plain directory is a package source as far as the protocol is concerned, so the whole of the
/// download path - resolve a version, fetch the bytes, hash them, unpack what runs on this framework,
/// read what the package says it needs - is exercised here against a package this test builds. What is
/// not exercised is the gallery, and nothing about that is worth a network call in a unit test.
/// </remarks>
[TestClass]
public class PackageFetcherTests : OdysseusTestBase
{
	private const string Id = "Odysseus.Fixture";
	private const string Platform = "StockSharp.Messages";

	private string _feed;
	private string _cache;

	/// <summary>Makes a feed of one package and an empty cache beside it.</summary>
	[TestInitialize]
	public void MakeFeed()
	{
		var root = Path.Combine(Path.GetTempPath(), "odysseus-packages", Guid.NewGuid().ToString("n"));

		_feed = Path.Combine(root, "feed");
		_cache = Path.Combine(root, "cache");

		FixturePackage.WriteTo(_feed, Id, "1.2.3", Platform, "9.9.9");
		FixturePackage.WriteTo(_feed, Id, "1.3.0", Platform, "9.9.9");
	}

	/// <summary>Removes what the test made.</summary>
	[TestCleanup]
	public void RemoveFeed()
	{
		var root = Path.GetDirectoryName(_feed);

		if (root is not null && Directory.Exists(root))
			Directory.Delete(root, recursive: true);
	}

	/// <summary>A named version is fetched, hashed and unpacked, and its assembly is on disk afterwards.</summary>
	[TestMethod]
	public async Task ANamedVersionIsFetchedAndUnpacked()
	{
		var fetched = await Fetcher().FetchAsync(Id, "1.2.3", CancellationToken);

		AreEqual(Id, fetched.Id);
		AreEqual("1.2.3", fetched.Version);
		AreEqual(64, fetched.Sha256.Length, "the package was filed under something that is not a SHA-256.");

		IsTrue(fetched.Assemblies.ContainsKey(Id),
			$"the package unpacked {string.Join(", ", fetched.Assemblies.Keys)} and not the connector.");

		IsTrue(File.Exists(fetched.Assemblies[Id]), "the assembly was named but not written.");
	}

	/// <summary>Without a version the newest release is taken, rather than whatever the feed lists first.</summary>
	[TestMethod]
	public async Task WithoutAVersionTheNewestReleaseIsTaken()
	{
		var fetched = await Fetcher().FetchAsync(Id, string.Empty, CancellationToken);

		AreEqual("1.3.0", fetched.Version);
	}

	/// <summary>
	/// What the package says it needs is read, because a connector wanting a newer platform than the
	/// server carries has to be refused before it is loaded rather than after, inside connecting.
	/// </summary>
	[TestMethod]
	public async Task WhatThePackageNeedsIsRead()
	{
		var fetched = await Fetcher().FetchAsync(Id, "1.2.3", CancellationToken);

		var platform = fetched.Requirements.SingleOrDefault(r => r.Id == Platform);

		IsNotNull(platform, $"the package's requirements were read as {fetched.Requirements.Count} entries.");
		AreEqual("9.9.9", platform.MinimumVersion);
	}

	/// <summary>A version the feed does not offer is refused by name rather than silently substituted.</summary>
	[TestMethod]
	public async Task AVersionTheFeedDoesNotOfferIsRefused()
	{
		var refusal = await ThrowsAsync<PackageUnavailableException>(
			() => Fetcher().FetchAsync(Id, "9.9.9", CancellationToken));

		IsTrue(refusal.Message.Contains(Id, StringComparison.Ordinal), refusal.Message);
	}

	/// <summary>A second fetch is served from the cache, and what it reports is the same package.</summary>
	[TestMethod]
	public async Task ASecondFetchIsServedFromTheCache()
	{
		var fetcher = Fetcher();

		var first = await fetcher.FetchAsync(Id, "1.2.3", CancellationToken);

		Directory.Delete(_feed, recursive: true);

		var second = await fetcher.FetchAsync(Id, "1.2.3", CancellationToken);

		AreEqual(first.Sha256, second.Sha256, "the cached package is not the one that was downloaded.");
		AreEqual(1, fetcher.Cache.VersionsOf(Id).Count);
	}

	/// <summary>
	/// A cached file that is not an archive is refused as an unreadable package, naming it. It arrives
	/// as an ordinary refusal rather than as whatever the archive reader raises, because the layer above
	/// recognises the one and reports the other to the caller as a defect of the server.
	/// </summary>
	[TestMethod]
	public async Task APackageFileThatIsNotAnArchiveIsRefusedByName()
	{
		var fetcher = Fetcher();

		fetcher.Cache.Write(Id, "1.2.3", Encoding.UTF8.GetBytes("<html>the feed answered with a login page</html>"));

		var refusal = await ThrowsAsync<PackageUnavailableException>(
			() => fetcher.FetchAsync(Id, "1.2.3", CancellationToken));

		IsTrue(refusal.Message.Contains(Id, StringComparison.Ordinal), refusal.Message);
		IsTrue(refusal.Message.Contains("1.2.3", StringComparison.Ordinal), refusal.Message);
	}

	/// <summary>
	/// The same holds when the cache is read directly, which is the path that reports what this server
	/// already holds: one damaged file there is a package that cannot be read, not a broken server.
	/// </summary>
	[TestMethod]
	public void ACachedPackageThatIsNotAnArchiveIsRefusedWhenItIsReadBack()
	{
		var fetcher = Fetcher();

		fetcher.Cache.Write(Id, "1.2.3", [0x50, 0x4B, 0x03, 0x04, 0x00, 0x00]);

		var refusal = Throws<PackageUnavailableException>(() => fetcher.Cached(Id, "1.2.3"));

		IsTrue(refusal.Message.Contains(Id, StringComparison.Ordinal), refusal.Message);
	}

	private PackageFetcher Fetcher()
		=> new(new PackageSourceSet([_feed]), new PackageCache(_cache), FixturePackage.Framework);
}
