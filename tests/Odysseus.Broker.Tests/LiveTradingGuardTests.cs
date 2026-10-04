namespace Odysseus.Broker.Tests;

using System.Collections.Generic;

using Ecng.Common;

using StockSharp.Messages;

using Odysseus.Domain;

/// <summary>
/// The guard on the other side of the paper claim.
/// </summary>
/// <remarks>
/// Two guards, shaped the same way so that neither can drift from the other: one asserts the demo flag
/// on and the other asserts it off, and both require the interface that carries the flag. A connector
/// that cannot represent the difference between a demo account and a real one cannot be shown to be on
/// either, so it is refused in both directions - not only in the direction that spends money.
///
/// Which of the two runs is decided by the mandate the process was born with, and by nothing that
/// reached it afterwards. Nothing here is reachable from the MCP server: it passes the paper mandate as
/// a literal, and it removes the variable that could produce any other one from the environment of every
/// process it starts.
/// </remarks>
[TestClass]
public class LiveTradingGuardTests : OdysseusTestBase
{
	/// <summary>
	/// The paper guard is untouched by any of this. A connector that will not go into demo mode is still
	/// refused, and a process holding the paper mandate still runs that guard and no other.
	/// </summary>
	[TestMethod]
	public void APaperMandateStillRefusesAConnectorThatWillNotGoOnPaper()
	{
		using var adapter = new StubbornAdapter(new IncrementalIdGenerator());

		var refusal = Throws<ConnectorNotPaperException>(
			() => AdapterConfigurator.Apply(adapter, null, null, TradingMandate.Paper));

		IsTrue(refusal.Message.Contains("demo mode", StringComparison.OrdinalIgnoreCase), refusal.Message);
	}

	/// <summary>A mandate is for one adapter, and being close to it is not being it.</summary>
	[TestMethod]
	public void ALiveMandateRefusesAnAdapterItDoesNotName()
	{
		using var adapter = new FeedAdapter(new IncrementalIdGenerator());

		var refusal = Throws<ConnectorNotPaperException>(() => AdapterConfigurator.Apply(
			adapter, null, null, Mandate(typeof(VenueAdapter))));

		IsTrue(refusal.Message.Contains(typeof(VenueAdapter).FullName, StringComparison.Ordinal),
			$"the refusal does not say which adapter was authorised: {refusal.Message}");
	}

	/// <summary>
	/// A mandate is for one build of one connector. "Whatever the sources offer today" is not something a
	/// person authorised last week, so the check is against what was loaded rather than what was asked
	/// for.
	/// </summary>
	[TestMethod]
	public void ALiveMandateRefusesABuildOtherThanTheAuthorisedOne()
	{
		var mandate = Mandate(typeof(FeedAdapter));

		LiveTradingGuard.AssertPackage("StockSharp.Example", "1.2.3", mandate);

		var newer = Throws<ConnectorNotPaperException>(
			() => LiveTradingGuard.AssertPackage("StockSharp.Example", "1.2.4", mandate));

		IsTrue(newer.Message.Contains("1.2.3", StringComparison.Ordinal), newer.Message);

		var other = Throws<ConnectorNotPaperException>(
			() => LiveTradingGuard.AssertPackage("StockSharp.Somebody", "1.2.3", mandate));

		IsTrue(other.Message.Contains("StockSharp.Somebody", StringComparison.Ordinal), other.Message);
	}

	/// <summary>On paper the package check answers nothing, because a paper mandate authorises no build.</summary>
	[TestMethod]
	public void APaperMandateHasNoBuildToCheck()
		=> LiveTradingGuard.AssertPackage("StockSharp.Anything", "9.9.9", TradingMandate.Paper);

	/// <summary>
	/// A connector with no way of saying whether it is on a demo account is refused in live mode as well.
	/// Requiring the same interface in both directions is what makes the two guards symmetric: one
	/// asserts the flag on, the other asserts it off, and neither ever ignores it.
	/// </summary>
	[TestMethod]
	public void ALiveMandateRefusesAConnectorThatCannotSayWhichAccountItIsOn()
	{
		using var adapter = new NoDemoAdapter(new IncrementalIdGenerator());

		var refusal = Throws<ConnectorNotPaperException>(() => AdapterConfigurator.Apply(
			adapter, null, null, Mandate(typeof(NoDemoAdapter))));

		IsTrue(refusal.Message.Contains("demo", StringComparison.OrdinalIgnoreCase), refusal.Message);
	}

	/// <summary>A live mandate takes the adapter it names off demo, and the settings survive it.</summary>
	[TestMethod]
	public void ALiveMandateTakesTheAdapterItNamesOffDemo()
	{
		using var adapter = new FeedAdapter(new IncrementalIdGenerator());

		AdapterConfigurator.Apply(
			adapter,
			null,
			new Dictionary<string, string>(StringComparer.Ordinal) { ["Feed"] = "consolidated" },
			Mandate(typeof(FeedAdapter)));

		IsFalse(adapter.IsDemo, "a live runner was left on a demo account.");
		AreEqual("consolidated", adapter.Feed, "the flag was set after the settings, so it overwrote one of them.");
	}

	/// <summary>
	/// A setting that puts a live connector back onto a demo account is refused. It is not dangerous the
	/// way the other direction is; it is dishonest in the same way, because the state report would then
	/// say it is trading money it is not.
	/// </summary>
	[TestMethod]
	public void ASettingThatPutsALiveConnectorBackOnDemoIsRefused()
	{
		using var adapter = new DemoingAdapter(new IncrementalIdGenerator());

		var refusal = Throws<ConnectorNotPaperException>(() => AdapterConfigurator.Apply(
			adapter,
			null,
			new Dictionary<string, string>(StringComparer.Ordinal) { ["Venue"] = "demo" },
			Mandate(typeof(DemoingAdapter))));

		IsTrue(refusal.Message.Contains("demo mode", StringComparison.OrdinalIgnoreCase), refusal.Message);

		IsTrue(adapter.IsDemo,
			"the connector was refused, so it must have been left as it was rather than quietly repaired.");
	}

	/// <summary>
	/// The settings pass cannot be used as a lever on either guard: the demo flag is not a setting, in
	/// either direction, and is refused as one before anything is downloaded.
	/// </summary>
	[TestMethod]
	public void TheDemoFlagIsNotASettingInEitherDirection()
	{
		var refusal = Throws<ConnectorRefusedException>(() => AdapterConfigurator.Validate(
			new("StockSharp.Example", "1.2.3", string.Empty,
				new Dictionary<string, string>(StringComparer.Ordinal) { ["IsDemo"] = "false" })));

		IsTrue(refusal.Message.Contains("IsDemo", StringComparison.Ordinal), refusal.Message);
	}

	private static TradingMandate Mandate(Type adapter)
		=> new(
			TradingModes.Live,
			"trade real money on U1234567 until the thirtieth",
			"U1234567",
			"StockSharp.Example",
			"1.2.3",
			adapter.FullName,
			["AAPL"],
			5_000m,
			new(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc),
			"/etc/odysseus/mandate.json");
}
