namespace Odysseus.Server.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Odysseus.Application;
using Odysseus.TestKit;

/// <summary>
/// Which of the connector tools a hosted server answers, and what it does instead.
/// </summary>
/// <remarks>
/// All three of them run downloaded code. Selecting one is the obvious case, but describing a package
/// builds the adapter inside it - what it is for and whether it can be told it is on a paper account
/// are properties of an instance - and listing what is cached builds one of every package there. So all
/// three are refused where the caller is a stranger, and the refusals are pinned by what the loader was
/// asked to do rather than only by the answer: a gate that stopped working would show up here as the
/// connector factory being reached at all.
/// </remarks>
[TestClass]
public class ConnectorToolsTests : OdysseusTestBase
{
	/// <summary>A hosted server does not read what it holds, because reading a connector builds it.</summary>
	[TestMethod]
	public async Task AHostedServerWillNotListTheConnectorsItHolds()
	{
		var connectors = new RecordingConnectors();

		var answer = await ConnectorTools.ListConnectors(
			Guard(),
			new(connectors, null),
			Options(ServerModes.Hosted),
			CancellationToken);

		AreEqual("PermissionDenied", Category(answer));
		AreEqual(0, connectors.Calls, "a hosted server built the connectors in its cache in order to answer.");
	}

	/// <summary>A hosted server does not describe a package either: describing it downloads and builds it.</summary>
	[TestMethod]
	public async Task AHostedServerWillNotDescribeAConnector()
	{
		var connectors = new RecordingConnectors();

		var answer = await ConnectorTools.DescribeConnector(
			Guard(),
			new(connectors, null),
			Options(ServerModes.Hosted),
			"StockSharp.Fixture",
			CancellationToken);

		AreEqual("PermissionDenied", Category(answer));
		AreEqual(0, connectors.Calls, "a hosted server downloaded a package in order to describe it.");

		IsTrue(Message(answer).Contains("hosted", StringComparison.OrdinalIgnoreCase),
			$"the refusal does not say what is refusing: {Message(answer)}");
	}

	/// <summary>A hosted server does not choose one, and nothing is bound by the attempt.</summary>
	[TestMethod]
	public async Task AHostedServerWillNotSelectAConnector()
	{
		var connectors = new RecordingConnectors();
		var gateway = new BrokerGateway(connectors, null);

		var answer = await ConnectorTools.SelectConnector(
			Guard(),
			gateway,
			Options(ServerModes.Hosted),
			"StockSharp.Fixture",
			CancellationToken);

		AreEqual("PermissionDenied", Category(answer));
		AreEqual(0, connectors.Calls, "a hosted server loaded a connector in order to refuse it.");
		IsFalse(gateway.IsBound, "a hosted server bound a connector it had refused.");
	}

	/// <summary>
	/// A local server answers all three, which is what says the refusals above are about the mode and
	/// not about the tools being broken.
	/// </summary>
	[TestMethod]
	public async Task ALocalServerReadsChoosesAndBinds()
	{
		var connectors = new RecordingConnectors();
		var gateway = new BrokerGateway(connectors, null);
		var options = Options(ServerModes.Local);

		var listed = await ConnectorTools.ListConnectors(Guard(), gateway, options, CancellationToken);

		IsNull(Member(listed, "error"), $"listing was refused: {Message(listed)}");

		var installed = Member(listed, "installed") as Array;

		IsNotNull(installed, "the answer says nothing about what is installed.");
		AreEqual(1, installed.Length, "the one connector in the cache was reported as something else.");

		var described = await ConnectorTools.DescribeConnector(
			Guard(),
			gateway,
			options,
			RecordingConnectors.PackageId,
			CancellationToken);

		IsNull(Member(described, "error"), $"describing was refused: {Message(described)}");
		AreEqual(RecordingConnectors.PackageId, Member(described, "packageId"));
		IsFalse(gateway.IsBound, "describing a connector bound it.");

		var selected = await ConnectorTools.SelectConnector(
			Guard(),
			gateway,
			options,
			RecordingConnectors.PackageId,
			CancellationToken);

		IsNull(Member(selected, "error"), $"selecting was refused: {Message(selected)}");
		IsTrue(gateway.IsBound, "selecting a connector left nothing bound.");
	}

	private static ToolGuard Guard()
		=> new(NullLogger<ToolGuard>.Instance);

	private static ServerOptions Options(ServerModes mode)
		=> new(
			mode,
			Path.Combine(Path.GetTempPath(), "odysseus-connector-tools"),
			Path.Combine(Path.GetTempPath(), "odysseus-connector-tools", "market-data"),
			null,
			null,
			null,
			["https://api.nuget.org/v3/index.json"],
			["StockSharp."],
			[]);

	/// <summary>
	/// Reads a member of a tool's answer. The answers are anonymous objects of the server's own, which
	/// is what the transport serialises; reflection reads them the way the transport does.
	/// </summary>
	/// <param name="value">The answer, or a part of one.</param>
	/// <param name="name">Member to read.</param>
	/// <returns>The value, or <see langword="null"/> when the answer does not carry it.</returns>
	private static object Member(object value, string name)
		=> value?.GetType().GetProperty(name)?.GetValue(value);

	private static string Category(object answer)
	{
		var error = Member(answer, "error");

		IsNotNull(error, $"the call was answered instead of being refused: {answer}");

		return Member(error, "category")?.ToString();
	}

	private static string Message(object answer)
		=> Member(Member(answer, "error"), "message")?.ToString() ?? answer?.ToString();

	/// <summary>
	/// A connector factory that loads nothing and remembers whether it was asked to. Every call on it
	/// stands for downloading a package or building an adapter, which is exactly what the mode decides.
	/// </summary>
	private sealed class RecordingConnectors : IConnectorFactory
	{
		public const string PackageId = "StockSharp.Fixture";

		private static readonly ConnectorDescription _connector = new(
			PackageId,
			"1.2.3",
			"0f9c0e2f",
			PackageId + ".FixtureAdapter",
			"Fixture",
			["Live"],
			"key-secret",
			true,
			false,
			[],
			"connector:stocksharp.fixture@0f9c0e2f");

		public int Calls { get; private set; }

		public IReadOnlyList<string> Allowed => ["StockSharp."];

		public IReadOnlyList<string> Sources => ["https://api.nuget.org/v3/index.json"];

		public ValueTask<ConnectorDescription> InspectAsync(ConnectorChoice choice, CancellationToken cancellationToken)
		{
			Calls++;

			return ValueTask.FromResult(_connector);
		}

		public ValueTask<BrokerBinding> OpenAsync(ConnectorChoice choice, CancellationToken cancellationToken)
		{
			Calls++;

			return ValueTask.FromResult(new BrokerBinding(_connector, null, null, null, null, null));
		}

		public ValueTask<IHistorySource> OpenStorageAsync(RemoteStorageChoice choice, CancellationToken cancellationToken)
			=> throw new NotSupportedException("No storage server here.");

		public ValueTask<IReadOnlyList<ConnectorDescription>> InstalledAsync(CancellationToken cancellationToken)
		{
			Calls++;

			return ValueTask.FromResult<IReadOnlyList<ConnectorDescription>>([_connector]);
		}
	}
}
