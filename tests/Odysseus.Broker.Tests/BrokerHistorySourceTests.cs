namespace Odysseus.Broker.Tests;

using System;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Odysseus.Application;
using Odysseus.Platform;
using Odysseus.TestKit;

/// <summary>
/// Downloading history through a real connector, against a real broker.
/// </summary>
/// <remarks>
/// There is nothing worth stubbing here: the whole point of loading a connector is that it talks to the
/// broker for us, and a stub would only confirm that our own call sequence matches our own expectation.
/// The suite declares itself inapplicable without a connector and credentials.
///
/// It now goes through the selection path rather than through a constructor, so what is exercised is
/// what a running server actually does: download the package, check it against the platform this build
/// carries, put the adapter into demo mode, apply the settings, and hand back the four ports.
/// </remarks>
[TestClass]
public class BrokerHistorySourceTests : OdysseusTestBase
{
	private const string KeysVariable = "ODYSSEUS_BROKER_KEYS";
	private const string ConnectorVariable = "ODYSSEUS_BROKER_CONNECTOR";

	private IHistorySource _source;
	private ConnectorChoice _choice;
	private BrokerGateway _gateway;

	/// <summary>Loads the connector the environment names, or declares the suite inapplicable.</summary>
	[TestInitialize]
	public async Task Connect()
	{
		_choice = BrokerFixture.Choice(ConnectorVariable);
		_gateway = BrokerFixture.Gateway(KeysVariable);

		await _gateway.SelectAsync(_choice, CancellationToken);

		_source = _gateway;
	}

	/// <summary>
	/// A bounded range downloads, finishes on its own, and comes back usable.
	/// </summary>
	/// <remarks>
	/// Finishing on its own is half the check. A subscription that never ends leaves the caller holding a
	/// call that returns neither bars nor an error, which is worse than a refusal because nothing about it
	/// says anything is wrong.
	/// </remarks>
	[TestMethod]
	public async Task ABoundedRangeDownloadsAndFinishes()
	{
		var to = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(-1), DateTimeKind.Utc);
		var from = to.AddDays(-20);

		var candles = await _source.GetBarsAsync("NVDA", TimeSpan.FromMinutes(5), from, to, CancellationToken);

		IsTrue(candles.Count > 500, $"a fortnight of five-minute bars should be hundreds; got {candles.Count}.");

		IsTrue(candles.All(c => c.OpenTime.Kind == DateTimeKind.Utc), "a bar came back without a zone.");
		IsTrue(candles.All(c => c.IsWellFormed), "a bar came back whose prices cannot describe a real bar.");

		IsTrue(candles.Zip(candles.Skip(1)).All(p => p.First.OpenTime < p.Second.OpenTime),
			"the bars are not in order.");

		IsTrue(candles.All(c => c.OpenTime >= from && c.OpenTime <= to),
			"a bar came back from outside the range that was asked for.");
	}

	/// <summary>
	/// The feed the connector file asks for is the feed that is used. Telling a connector it is on a paper
	/// account also switches its feed to a single exchange unless the settings are applied afterwards, and
	/// on that feed the volumes are a small fraction of the market - which would quietly invalidate every
	/// volume-based measurement downstream.
	/// </summary>
	/// <remarks>
	/// The ordering itself is now covered offline by <see cref="PaperOnlyGuardTests"/>, so this is no
	/// longer the only net under it. What it still checks is that the setting reaches the venue.
	/// </remarks>
	[TestMethod]
	public async Task TheConsolidatedFeedIsUsed()
	{
		var to = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(-1), DateTimeKind.Utc);
		var from = to.AddDays(-5);

		var candles = await _source.GetBarsAsync("NVDA", TimeSpan.FromMinutes(5), from, to, CancellationToken);

		var busiest = candles.Max(c => c.Volume);

		IsTrue(busiest > 1_000_000m,
			$"the busiest five-minute bar carried {busiest} shares, which is a single-exchange feed rather than " +
			"the consolidated one.");
	}

	/// <summary>
	/// The source names itself, and the name is a function of the choice rather than of the moment.
	/// </summary>
	/// <remarks>
	/// The shape rather than a literal. The name is part of what a dataset hashes, and that hash decides
	/// where the closed slice begins - so what matters is that the same choice always produces the same
	/// name, not what the name happens to spell.
	/// </remarks>
	[TestMethod]
	public async Task TheSourceNamesItsFeedTheSameWayTwice()
	{
		var first = _gateway.Current.SourceName;

		IsFalse(string.IsNullOrWhiteSpace(first), "the source did not name itself.");

		await _gateway.SelectAsync(_choice, CancellationToken);

		AreEqual(first, _gateway.Current.SourceName,
			"the same connector named itself differently twice, so a dataset's identity would depend on when it was imported.");
	}

	/// <summary>
	/// Measuring a real instrument end to end: download, take the development slice, and check that the
	/// numbers that come out are the size a real instrument produces.
	/// </summary>
	[TestMethod]
	public async Task ARealInstrumentMeasuresPlausibly()
	{
		var to = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(-1), DateTimeKind.Utc);
		var candles = await _source.GetBarsAsync("NVDA", TimeSpan.FromMinutes(5), to.AddDays(-40), to, CancellationToken);

		// Only the development slice is ever profiled, exactly as the server does it.
		var development = candles.Take((int)(candles.Count * 0.6)).ToArray();
		var profile = new StockSharpMarketProfiler().Measure("NVDA", development);

		IsTrue(profile.Movement.AnnualisedVolatilityPercent is > 5m and < 200m,
			$"annualised volatility of {profile.Movement.AnnualisedVolatilityPercent}% is outside anything a listed stock does.");

		IsTrue(profile.Persistence.VarianceRatio5 is > 0.2m and < 5m,
			$"a variance ratio of {profile.Persistence.VarianceRatio5} means the calculation is wrong, not that the stock is strange.");

		var opening = profile.Session.Single(b => b.Bucket == "first 30 minutes");

		IsTrue(opening.ShareOfVolume > 2m,
			$"the opening half hour carries {opening.ShareOfVolume}% of volume, so the active session was located " +
			"somewhere the instrument does not actually trade.");
	}
}
