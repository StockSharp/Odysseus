namespace Odysseus.Application.Tests;

/// <summary>
/// The small file that says which broker a server was started pointed at.
/// </summary>
/// <remarks>
/// A file rather than a set of environment variables, for the reason the credential file gives: a value
/// in the environment is inherited by every process the server starts. It also keeps a connector's own
/// settings - which are its vocabulary, not this product's - out of a start-up contract this product
/// would then have to know about.
/// </remarks>
[TestClass]
public class ConnectorFileTests : OdysseusTestBase
{
	/// <summary>The whole of it: a package, a version, an adapter and the settings for it.</summary>
	[TestMethod]
	public void AConnectorIsRead()
	{
		var choice = ConnectorFile.Parse(
			"""
			{
			  "packageId": "StockSharp.Example",
			  "version": "5.0.40",
			  "adapter": "StockSharp.Example.ExampleMessageAdapter",
			  "settings": { "Feed": "consolidated", "Depth": 5 }
			}
			""",
			"the test");

		AreEqual("StockSharp.Example", choice.PackageId);
		AreEqual("5.0.40", choice.PackageVersion);
		AreEqual("StockSharp.Example.ExampleMessageAdapter", choice.AdapterTypeName);
		AreEqual("consolidated", choice.Settings["Feed"]);

		// Written as whatever reads naturally and converted by the type the adapter declares, so a caller
		// never has to know that a setting happens to be a number.
		AreEqual("5", choice.Settings["Depth"]);
	}

	/// <summary>Everything but the package is optional, because most connectors hold one adapter.</summary>
	[TestMethod]
	public void OnlyThePackageIsRequired()
	{
		var choice = ConnectorFile.Parse("""{ "packageId": "StockSharp.Example" }""", "the test");

		AreEqual("StockSharp.Example", choice.PackageId);
		AreEqual(string.Empty, choice.PackageVersion);
		AreEqual(string.Empty, choice.AdapterTypeName);
		AreEqual(0, choice.Settings.Count);
	}

	/// <summary>A file that names no package is refused by name rather than read as no connector.</summary>
	[TestMethod]
	public void AFileNamingNoPackageIsRefused()
	{
		var refusal = Throws<ConnectorRefusedException>(() => ConnectorFile.Parse("""{ "version": "1.0.0" }""", "the test"));

		IsTrue(refusal.Message.Contains("packageId", StringComparison.Ordinal), refusal.Message);
	}

	/// <summary>A file that is not JSON is refused with what is wrong with it.</summary>
	[TestMethod]
	public void AFileThatIsNotJsonIsRefused()
		=> Throws<ConnectorRefusedException>(() => ConnectorFile.Parse("packageId = StockSharp.Example", "the test"));

	/// <summary>
	/// A setting that is an object or a list is refused, because an adapter takes single values and a
	/// list arrives as text it splits itself.
	/// </summary>
	[TestMethod]
	public void ASettingThatIsNotASingleValueIsRefused()
	{
		var refusal = Throws<ConnectorRefusedException>(() => ConnectorFile.Parse(
			"""{ "packageId": "StockSharp.Example", "settings": { "Sections": ["Stock", "Option"] } }""",
			"the test"));

		IsTrue(refusal.Message.Contains("Sections", StringComparison.Ordinal), refusal.Message);
	}

	/// <summary>No file at all is no connector, which is a state the server describes rather than fails on.</summary>
	[TestMethod]
	public void NoFileIsNoConnector()
	{
		IsNull(ConnectorFile.Read(null));
		IsNull(ConnectorFile.Read("   "));
	}

	/// <summary>Settings passed as a tool argument are read the same way the file's are.</summary>
	[TestMethod]
	public void SettingsPassedOnTheirOwnAreReadTheSameWay()
	{
		var settings = ConnectorFile.ReadSettings("""{ "Feed": "consolidated" }""", "the settings argument");

		AreEqual("consolidated", settings["Feed"]);
		AreEqual(0, ConnectorFile.ReadSettings(string.Empty, "the settings argument").Count);
	}
}
