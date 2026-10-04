namespace Odysseus.Server.Tests;

using Odysseus.Broker;

/// <summary>
/// How this server reads the decisions it is started with.
/// </summary>
/// <remarks>
/// Read from the real environment rather than from an injected table, because the environment is where
/// these decisions actually come from: an operator sets a variable and starts the process, and a test
/// that handed the reader its own dictionary would prove nothing about that. The variable names and the
/// documented defaults are written out here as literals rather than taken from the class under test, so
/// that renaming a variable or changing a default is a failure and not a silent agreement.
///
/// The suite does not run in parallel - a process has one environment.
/// </remarks>
[TestClass]
public class ServerOptionsTests : OdysseusTestBase
{
	private const string RootVariable = "ODYSSEUS_PROJECTS_ROOT";
	private const string KeysVariable = "ODYSSEUS_BROKER_KEYS";
	private const string ConnectorVariable = "ODYSSEUS_BROKER_CONNECTOR";
	private const string SourcesVariable = "ODYSSEUS_CONNECTOR_SOURCES";
	private const string AllowVariable = "ODYSSEUS_CONNECTOR_ALLOW";
	private const string ProductsVariable = "ODYSSEUS_PRODUCT_ALLOW";

	private const string PublicGallery = "https://api.nuget.org/v3/index.json";
	private const string PlatformPrefix = "StockSharp.";

	private static readonly string[] _variables =
	[
		RootVariable, KeysVariable, ConnectorVariable, SourcesVariable, AllowVariable, ProductsVariable,
	];

	private readonly Dictionary<string, string> _saved = new(StringComparer.Ordinal);

	/// <summary>Puts the environment aside, so each test reads one this test wrote.</summary>
	[TestInitialize]
	public void ClearTheEnvironment()
	{
		foreach (var variable in _variables)
		{
			_saved[variable] = Environment.GetEnvironmentVariable(variable);

			Environment.SetEnvironmentVariable(variable, null);
		}
	}

	/// <summary>Gives the environment back, whatever the test did to it.</summary>
	[TestCleanup]
	public void RestoreTheEnvironment()
	{
		foreach (var (variable, value) in _saved)
			Environment.SetEnvironmentVariable(variable, value);
	}

	/// <summary>
	/// The flag is the whole of the decision, and it is a decision about the command line rather than
	/// about anything a caller can reach once the server is up.
	/// </summary>
	[TestMethod]
	public void TheHostedFlagIsWhatMakesAServerHosted()
	{
		AreEqual(ServerModes.Hosted, ServerOptions.Read(["--hosted"]).Mode);
		AreEqual(ServerModes.Hosted, ServerOptions.Read(["--HOSTED"]).Mode, "the flag is case sensitive.");
		AreEqual(ServerModes.Hosted, ServerOptions.Read(["--projects", "x", "--hosted"]).Mode);

		AreEqual(ServerModes.Local, ServerOptions.Read([]).Mode);
		AreEqual(ServerModes.Local, ServerOptions.Read(["hosted"]).Mode, "a word that is not the flag was read as it.");
	}

	/// <summary>
	/// The mode decides whether a connector may be loaded at all, and that covers reading one as much as
	/// choosing one: describing a package and listing what is cached both build the downloaded adapter.
	/// </summary>
	[TestMethod]
	public void OnlyALocalServerMayLoadAConnector()
	{
		IsTrue(ServerOptions.Read([]).CanLoadConnectors);
		IsFalse(ServerOptions.Read(["--hosted"]).CanLoadConnectors);
	}

	/// <summary>
	/// An environment that says nothing gives the published defaults: the public gallery, the platform's
	/// own package family, no credentials and no connector.
	/// </summary>
	[TestMethod]
	public void AnEnvironmentThatSaysNothingGivesTheDocumentedDefaults()
	{
		var options = ServerOptions.Read([]);

		AreEqual(1, options.ConnectorSources.Count, $"the sources are {string.Join(", ", options.ConnectorSources)}.");
		AreEqual(PublicGallery, options.ConnectorSources[0]);

		AreEqual(1, options.ConnectorAllow.Count, $"the allow-list is {string.Join(", ", options.ConnectorAllow)}.");
		AreEqual(PlatformPrefix, options.ConnectorAllow[0]);

		IsNull(options.Broker, "credentials were read out of an environment that named no file.");
		IsFalse(options.HasCredentials);

		IsNull(options.Connector, "a connector was read out of an environment that named no file.");
		IsFalse(options.HasConnector);

		AreEqual(Path.Combine(options.ProjectsRoot, "connectors"), options.ConnectorCache);
	}

	/// <summary>
	/// Nobody names a projects root on a first run, so the default has to be somewhere the process can
	/// write without being told: a folder beside where it was started.
	/// </summary>
	[TestMethod]
	public void AProjectsRootThatWasNotNamedSitsBesideTheProcess()
	{
		var root = ServerOptions.Read([]).ProjectsRoot;

		IsTrue(Path.IsPathRooted(root), $"the projects root is not an absolute path: {root}");
		AreEqual("projects", Path.GetFileName(root));
		AreEqual(Path.GetFullPath(Directory.GetCurrentDirectory()), Path.GetDirectoryName(root));
	}

	/// <summary>
	/// A relative root is made absolute while it is read, because the server changes what it means the
	/// moment anything changes the working directory.
	/// </summary>
	[TestMethod]
	public void ARelativeProjectsRootIsMadeAbsoluteWhileItIsRead()
	{
		Environment.SetEnvironmentVariable(RootVariable, Path.Combine("work", "odysseus-projects"));

		var root = ServerOptions.Read([]).ProjectsRoot;

		IsTrue(Path.IsPathRooted(root), $"a relative root was kept relative: {root}");
		AreEqual("odysseus-projects", Path.GetFileName(root));
		AreEqual("work", Path.GetFileName(Path.GetDirectoryName(root)));
	}

	/// <summary>
	/// Both lists are written as one variable, so both are split on the separator and trimmed - an
	/// operator writing a list by hand puts spaces after the semicolons.
	/// </summary>
	[TestMethod]
	public void AListIsReadAsItsEntriesRatherThanAsOneValue()
	{
		Environment.SetEnvironmentVariable(
			SourcesVariable,
			"https://one.example/index.json; https://two.example/index.json ");

		Environment.SetEnvironmentVariable(AllowVariable, "StockSharp.; Contoso.Broker.");

		var options = ServerOptions.Read([]);

		AreEqual(2, options.ConnectorSources.Count, $"the sources are {string.Join(", ", options.ConnectorSources)}.");
		AreEqual("https://one.example/index.json", options.ConnectorSources[0]);
		AreEqual("https://two.example/index.json", options.ConnectorSources[1], "the entries are not trimmed.");

		AreEqual(2, options.ConnectorAllow.Count, $"the allow-list is {string.Join(", ", options.ConnectorAllow)}.");
		AreEqual(PlatformPrefix, options.ConnectorAllow[0]);
		AreEqual("Contoso.Broker.", options.ConnectorAllow[1], "the entries are not trimmed.");
	}

	/// <summary>
	/// A variable holding nothing but whitespace said nothing, and is answered with the default rather
	/// than with a list of one empty entry - which would be a source nobody can reach and a prefix every
	/// package identifier starts with.
	/// </summary>
	[TestMethod]
	public void AVariableHoldingOnlyWhitespaceSaysNothingAndGetsTheDefault()
	{
		Environment.SetEnvironmentVariable(SourcesVariable, "   ");
		Environment.SetEnvironmentVariable(AllowVariable, "\t ");

		var options = ServerOptions.Read([]);

		AreEqual(1, options.ConnectorSources.Count, $"the sources are '{string.Join("', '", options.ConnectorSources)}'.");
		AreEqual(PublicGallery, options.ConnectorSources[0]);

		AreEqual(1, options.ConnectorAllow.Count, $"the allow-list is '{string.Join("', '", options.ConnectorAllow)}'.");
		AreEqual(PlatformPrefix, options.ConnectorAllow[0]);
	}

	/// <summary>
	/// Nothing may be installed unless an operator said which products may be. This is the opposite
	/// default from the connector allow-list, and deliberately: half the product needs a connector, and
	/// no part of the research loop needs a StockSharp product at all.
	/// </summary>
	[TestMethod]
	public void NoProductMayBeInstalledUnlessAnOperatorSaidWhich()
	{
		var options = ServerOptions.Read([]);

		AreEqual(0, options.AllowedProducts.Count, string.Join(", ", options.AllowedProducts));
		IsFalse(options.CanInstallProducts, "a server nobody configured could install a product.");
	}

	/// <summary>
	/// The list is read as its identifiers, trimmed, deduplicated and in a fixed order, so that what a
	/// server reports as installable reads the same way twice.
	/// </summary>
	[TestMethod]
	public void AProductListIsReadAsItsIdentifiers()
	{
		Environment.SetEnvironmentVariable(ProductsVariable, "1137; 9 ,10;9");

		var options = ServerOptions.Read([]);

		AreEqual(3, options.AllowedProducts.Count, string.Join(", ", options.AllowedProducts));
		AreEqual(9L, options.AllowedProducts[0]);
		AreEqual(10L, options.AllowedProducts[1]);
		AreEqual(1137L, options.AllowedProducts[2]);

		IsTrue(options.CanInstallProducts);
	}

	/// <summary>
	/// A product list that was written and came out empty allows nothing, which is what it would have
	/// allowed anyway: there is no default here to be widened back to.
	/// </summary>
	[TestMethod]
	public void AProductListThatCameOutEmptyStillAllowsNothing()
	{
		Environment.SetEnvironmentVariable(ProductsVariable, ";,;");

		var options = ServerOptions.Read([]);

		AreEqual(0, options.AllowedProducts.Count, string.Join(", ", options.AllowedProducts));
		IsFalse(options.CanInstallProducts);
	}

	/// <summary>
	/// An entry that is not a positive number names no product and is dropped rather than refused: a
	/// typo would otherwise stop a server whose research half is entirely unaffected by it, and
	/// describe_server reports what was actually understood.
	/// </summary>
	[TestMethod]
	public void AnEntryThatNamesNoProductIsDropped()
	{
		Environment.SetEnvironmentVariable(ProductsVariable, "9;designer;-1;0;9.5");

		var allowed = ServerOptions.Read([]).AllowedProducts;

		AreEqual(1, allowed.Count, string.Join(", ", allowed));
		AreEqual(9L, allowed[0]);
	}

	/// <summary>
	/// A hosted instance installs nothing whatever the list says. It is the rule that refuses the
	/// connector tools there with one word changed: a stranger does not get to choose which code this
	/// process runs, and still less which code lands on the operator's disk.
	/// </summary>
	[TestMethod]
	public void AHostedServerInstallsNothingWhateverTheListSays()
	{
		Environment.SetEnvironmentVariable(ProductsVariable, "9;10;1137");

		var options = ServerOptions.Read(["--hosted"]);

		AreEqual(3, options.AllowedProducts.Count, "the list itself was read differently in a hosted server.");
		IsFalse(options.CanInstallProducts);

		IsTrue(ServerOptions.Read([]).CanInstallProducts, "the same list allowed nothing in a local server.");
	}

	/// <summary>
	/// A source list that was written and came out empty is widened back to the gallery, which is the
	/// opposite of what the same mistake gets on the allow-list and deliberately so: an empty source
	/// list narrows nothing, because the fetcher reaches for the gallery whatever this says, and a
	/// server reporting no source while downloading from one is the worse of the two answers.
	/// </summary>
	[TestMethod]
	public void ASourceListThatCameOutEmptyIsWidenedBackToTheGallery()
	{
		Environment.SetEnvironmentVariable(SourcesVariable, ";,;");

		var sources = ServerOptions.Read([]).ConnectorSources;

		AreEqual(1, sources.Count, $"the sources are '{string.Join("', '", sources)}'.");
		AreEqual(PublicGallery, sources[0]);
	}

	/// <summary>
	/// An allow-list that was written and came out empty refuses every package, including one the
	/// default would have allowed. A list is only ever written to narrow what this server will download
	/// and run, so an empty one is a rule that misfired; reading it as "no rule" would answer a mistake
	/// with permission to run any package there is.
	/// </summary>
	/// <remarks>
	/// Driven through the loader the server builds from these options rather than stopping at the list,
	/// because an empty list only means anything where it is enforced. Nothing is downloaded: the cache
	/// directory the loader was given does not exist afterwards.
	/// </remarks>
	[TestMethod]
	public async Task AnAllowListThatCameOutEmptyRefusesEveryPackage()
	{
		Environment.SetEnvironmentVariable(AllowVariable, ";");

		var options = ServerOptions.Read([]);

		AreEqual(0, options.ConnectorAllow.Count,
			$"the list was widened to '{string.Join("', '", options.ConnectorAllow)}' instead of being kept empty.");

		var cache = Path.Combine(Path.GetTempPath(), "odysseus-empty-allow", Guid.NewGuid().ToString("n"));

		try
		{
			var connectors = StockSharpConnectorFactory.Create(
				cache,
				options.Broker,
				options.ConnectorSources,
				options.ConnectorAllow,
				TradingMandate.Paper);

			var refusal = await ThrowsAsync<ConnectorRefusedException>(
				() => connectors
					.InspectAsync(new("StockSharp.Binance", string.Empty, string.Empty, null), CancellationToken)
					.AsTask());

			IsTrue(refusal.Message.Contains("StockSharp.Binance", StringComparison.Ordinal), refusal.Message);
			IsFalse(Directory.Exists(cache), "a server that allows no package downloaded one anyway.");
		}
		finally
		{
			if (Directory.Exists(cache))
				Directory.Delete(cache, recursive: true);
		}
	}
}
