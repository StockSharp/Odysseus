namespace Odysseus.Application.Tests;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Odysseus.Application;
using Odysseus.Domain;
using Odysseus.TestKit;

/// <summary>
/// The four broker ports before, during and after a connector is chosen.
/// </summary>
/// <remarks>
/// Choosing a broker while the server runs must not change a single tool signature, so everything that
/// holds a broker port holds this one object and nothing is rebuilt when the choice is made. What is
/// worth asserting is the two ends of that: that an unchosen gateway refuses in the words the product
/// already uses, and that choosing swaps every port at once.
/// </remarks>
[TestClass]
public class BrokerGatewayTests : OdysseusTestBase
{
	/// <summary>
	/// Nothing chosen refuses with the same exception the stand-in threw, naming what was being attempted
	/// - so every refusal an agent has ever been able to act on still reads the same way.
	/// </summary>
	[TestMethod]
	public async Task NothingChosenRefusesInTheProductsOwnWords()
	{
		var gateway = new BrokerGateway(new Factory(), null);

		IsFalse(gateway.IsBound, "an unchosen gateway reports a connector.");
		IsNull(gateway.Current, "an unchosen gateway describes a connector.");

		var download = await ThrowsAsync<BrokerNotConfiguredException>(
			() => gateway.GetBarsAsync("NVDA", TimeSpan.FromMinutes(5), Utc, Utc, CancellationToken));

		IsTrue(download.Message.Contains("Downloading history", StringComparison.Ordinal), download.Message);

		var read = await ThrowsAsync<BrokerNotConfiguredException>(
			async () => await gateway.ReadAsync(CancellationToken));

		IsTrue(read.Message.Contains("Reading the account", StringComparison.Ordinal), read.Message);

		var deploy = Throws<BrokerNotConfiguredException>(() => gateway.Choose());

		IsTrue(deploy.Message.Contains("deployment", StringComparison.OrdinalIgnoreCase), deploy.Message);

		var lookup = await ThrowsAsync<BrokerNotConfiguredException>(
			async () => await gateway.SearchAsync(new("NVDA", null, null, null, 10), CancellationToken));

		IsTrue(lookup.Message.Contains("Asking what instruments exist", StringComparison.Ordinal), lookup.Message);
	}

	/// <summary>Choosing binds every port at once, and the ports answer through what was chosen.</summary>
	[TestMethod]
	public async Task ChoosingBindsEveryPort()
	{
		var factory = new Factory();
		var gateway = new BrokerGateway(factory, null);

		var bound = await gateway.SelectAsync(Choice("StockSharp.Example"), CancellationToken);

		IsTrue(gateway.IsBound, "a chosen gateway reports no connector.");
		AreEqual("StockSharp.Example", bound.PackageId);
		AreEqual(bound, gateway.Current);

		var bars = await gateway.GetBarsAsync("NVDA", TimeSpan.FromMinutes(5), Utc, Utc, CancellationToken);

		AreEqual(1, bars.Count, "the bound connector was not the one that answered.");
	}

	/// <summary>Choosing again swaps every port to the second connector, without rebuilding anything.</summary>
	[TestMethod]
	public async Task ChoosingAgainSwapsEveryPort()
	{
		var gateway = new BrokerGateway(new Factory(), null);

		await gateway.SelectAsync(Choice("StockSharp.First"), CancellationToken);
		await gateway.SelectAsync(Choice("StockSharp.Second"), CancellationToken);

		AreEqual("StockSharp.Second", gateway.Current.PackageId);
		AreEqual("second-00000000", ((IHistorySource)gateway).SourceName);
	}

	/// <summary>
	/// The name a dataset records is derived from the choice, so the same choice always names the same
	/// source and a different setting names a different one.
	/// </summary>
	[TestMethod]
	public void TheSourceNameIsDerivedFromTheChoice()
	{
		var plain = new Dictionary<string, string>(StringComparer.Ordinal);
		var configured = new Dictionary<string, string>(StringComparer.Ordinal) { ["Feed"] = "consolidated" };

		var first = ConnectorChoice.SourceNameOf("StockSharp.Binance", plain);

		AreEqual(first, ConnectorChoice.SourceNameOf("StockSharp.Binance", plain));
		AreNotEqual(first, ConnectorChoice.SourceNameOf("StockSharp.Binance", configured));
		AreNotEqual(first, ConnectorChoice.SourceNameOf("StockSharp.Bybit", plain));

		IsTrue(first.StartsWith($"{ConnectorChoice.SourcePrefix}stocksharp.binance@", StringComparison.Ordinal),
			$"the source name does not say which connector package it came through: {first}");
	}

	/// <summary>
	/// The name says which connector package produced the bars, not merely which venue they are from.
	/// </summary>
	/// <remarks>
	/// The bare venue - the package identifier with the platform's prefix taken off - would not do: two
	/// packages can serve one venue, and a reader could not tell which code produced the bars.
	/// </remarks>
	[TestMethod]
	public void TheSourceNameNamesTheConnectorRatherThanTheVenue()
	{
		var name = ConnectorChoice.SourceNameOf(
			"StockSharp.Binance", new Dictionary<string, string>(StringComparer.Ordinal));

		IsTrue(name.Contains("stocksharp.binance", StringComparison.Ordinal),
			$"the source name drops the package that produced the bars: {name}");

		IsFalse(name.StartsWith("binance", StringComparison.Ordinal),
			$"the source name still reads as a venue this product is wired to: {name}");
	}

	/// <summary>
	/// A deployment needs a connector named, not bound: the process that trades loads its own. So the
	/// choice a session made is what a deployment is given, and failing that the one the operator named at
	/// start-up - which is what makes it possible to deploy from a server that has loaded nothing.
	/// </summary>
	[TestMethod]
	public async Task ADeploymentIsGivenTheChoiceRatherThanTheBinding()
	{
		var named = Choice("StockSharp.FromTheEnvironment");
		var gateway = new BrokerGateway(new Factory(), named);

		AreEqual(named, gateway.Choose(), "a server that has bound nothing did not fall back to what was named.");

		await gateway.SelectAsync(Choice("StockSharp.Chosen"), CancellationToken);

		AreEqual("StockSharp.Chosen", gateway.Choose().PackageId,
			"a deployment was given the start-up connector although a session had selected another.");

		AreEqual(gateway.Choose(), gateway.CurrentChoice);
	}

	private static DateTime Utc => new(2026, 3, 2, 14, 0, 0, DateTimeKind.Utc);

	private static ConnectorChoice Choice(string packageId)
		=> new(packageId, "1.0.0", string.Empty, new Dictionary<string, string>(StringComparer.Ordinal));

	/// <summary>A connector factory that loads nothing and answers instantly.</summary>
	private sealed class Factory : IConnectorFactory
	{
		public IReadOnlyList<string> Allowed => ["StockSharp."];

		public IReadOnlyList<string> Sources => ["https://example.invalid/index.json"];

		public ValueTask<ConnectorDescription> InspectAsync(ConnectorChoice choice, CancellationToken cancellationToken)
			=> ValueTask.FromResult(Describe(choice));

		public ValueTask<BrokerBinding> OpenAsync(ConnectorChoice choice, CancellationToken cancellationToken)
		{
			var description = Describe(choice);
			var source = new Source(description.SourceName);

			return ValueTask.FromResult(new BrokerBinding(description, source, source, source, source, source));
		}

		public ValueTask<IHistorySource> OpenStorageAsync(RemoteStorageChoice choice, CancellationToken cancellationToken)
			=> throw new NotSupportedException("No storage server here.");

		public ValueTask<IReadOnlyList<ConnectorDescription>> InstalledAsync(CancellationToken cancellationToken)
			=> ValueTask.FromResult<IReadOnlyList<ConnectorDescription>>([]);

		private static ConnectorDescription Describe(ConnectorChoice choice)
			=> new(
				choice.PackageId,
				"1.0.0",
				"0000000000000000000000000000000000000000000000000000000000000000",
				"Example.Adapter",
				"Example",
				[],
				"key-secret",
				true,
				false,
				[],
				Name(choice.PackageId));

		private static string Name(string packageId)
			=> packageId switch
			{
				"StockSharp.First" => "first-00000000",
				"StockSharp.Second" => "second-00000000",
				_ => "example-00000000",
			};
	}

	/// <summary>A connector that answers every port with the least it can.</summary>
	private sealed class Source : IHistorySource, IQuoteSource, IPaperTrader, IPaperAccount, ISecurityLookup
	{
		public Source(string sourceName)
		{
			SourceName = sourceName;
			Name = $"{sourceName}-paper";
		}

		public string SourceName { get; }

		public string Name { get; }

		public Task<IReadOnlyList<Candle>> GetBarsAsync(
			string symbol,
			TimeSpan timeFrame,
			DateTime from,
			DateTime to,
			CancellationToken cancellationToken)
			=> Task.FromResult<IReadOnlyList<Candle>>([new(from, 1m, 1m, 1m, 1m, 1m)]);

		public Task<decimal?> GetPriceAsync(string symbol, CancellationToken cancellationToken)
			=> Task.FromResult<decimal?>(1m);

		public Task<IPaperSession> StartAsync(PaperRequest request, CancellationToken cancellationToken)
			=> throw new NotSupportedException();

		public ValueTask<PaperAccountState> ReadAsync(CancellationToken cancellationToken)
			=> throw new NotSupportedException();

		public ValueTask<IReadOnlyList<FoundSecurity>> SearchAsync(SecurityQuery query, CancellationToken cancellationToken)
			=> throw new NotSupportedException();
	}
}
