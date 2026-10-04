namespace Odysseus.Broker.Tests;

using System.IO;

/// <summary>
/// What the suites that need a real broker are given, and how they decline when there is none.
/// </summary>
/// <remarks>
/// Both halves are read the way the server reads them - a connector file and a credential file, each
/// named by an environment variable - so a suite that passes here is evidence about the path a running
/// server takes rather than about a constructor a test called.
/// </remarks>
public static class BrokerFixture
{
	private const string ServerSource = "https://api.nuget.org/v3/index.json";
	private const string AllowedPrefix = "StockSharp.";

	/// <summary>
	/// Reads the connector the environment names, or declares the calling suite inapplicable.
	/// </summary>
	/// <param name="variable">Environment variable naming the connector file.</param>
	/// <returns>The choice.</returns>
	public static ConnectorChoice Choice(string variable)
	{
		var path = Environment.GetEnvironmentVariable(variable);

		if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
		{
			Assert.Inconclusive(
				$"No connector. Point {variable} at a file naming the package to load, for example " +
				"{\"packageId\":\"StockSharp.Binance\",\"settings\":{}}.");
		}

		return ConnectorFile.Read(path);
	}

	/// <summary>
	/// Builds a gateway over the real download and load path, with the credentials the environment names.
	/// </summary>
	/// <param name="variable">Environment variable naming the credential file.</param>
	/// <returns>The gateway, with nothing bound to it yet.</returns>
	public static BrokerGateway Gateway(string variable)
		=> new(Connectors(variable), null);

	/// <summary>
	/// The factory itself, for the one suite that needs the trading port.
	/// </summary>
	/// <param name="variable">Environment variable naming the credential file.</param>
	/// <returns>The factory, with the paper mandate this repository's tests always use.</returns>
	/// <remarks>
	/// The gateway stopped answering the trading port when a deployment became a process of its own: the
	/// thing that trades is a runner, and it opens the connector itself. A suite that wants to drive a
	/// strategy against a real account therefore opens one the way the runner does, which is also the
	/// only way to be evidence about the path a runner takes.
	/// </remarks>
	public static IConnectorFactory Connectors(string variable)
	{
		var path = Environment.GetEnvironmentVariable(variable);

		if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
			Assert.Inconclusive($"No broker credentials. Point {variable} at the file the connector's credentials are in.");

		return StockSharpConnectorFactory.Create(
			Path.Combine(Path.GetTempPath(), "odysseus-connectors"),
			BrokerCredentialFile.Read(path),
			[ServerSource],
			[AllowedPrefix],
			TradingMandate.Paper);
	}
}
