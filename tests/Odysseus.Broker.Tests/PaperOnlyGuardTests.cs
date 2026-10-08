namespace StockSharp.Odysseus.Broker.Tests;

using System.Collections.Generic;

using Ecng.Common;

using StockSharp.Messages;

using StockSharp.Odysseus.Platform;

/// <summary>
/// The one guarantee this product makes about money: whatever connector is loaded, it is on a paper
/// account.
/// </summary>
/// <remarks>
/// It used to be a literal <c>true</c> assigned at three call sites, with a comment next to each about
/// the order the assignments had to be in, and no test anywhere. These are all offline: the trap the
/// comments described - a demo flag whose setter quietly changes another setting - is reproduced with a
/// fake, so the ordering is now checked on every build rather than by whoever remembers to read the
/// comment.
/// </remarks>
[TestClass]
public class PaperOnlyGuardTests : OdysseusTestBase
{
	/// <summary>
	/// A connector with no way of being told it is on paper is refused rather than used carefully. There
	/// is no careful: the next order it sends is a real one.
	/// </summary>
	[TestMethod]
	public void AnAdapterThatCannotBeToldItIsPaperIsRefused()
	{
		using var adapter = new NoDemoAdapter(new IncrementalIdGenerator());

		var refusal = Throws<ConnectorNotPaperException>(() => PaperOnlyGuard.Assert(adapter));

		IsTrue(refusal.Message.Contains(nameof(NoDemoAdapter), StringComparison.Ordinal),
			$"the refusal does not name the connector it refused: {refusal.Message}");
	}

	/// <summary>
	/// A connector that accepts the flag and ignores it is refused too. Setting a property is not the
	/// guarantee; the connector agreeing that it is set is.
	/// </summary>
	[TestMethod]
	public void AnAdapterThatIgnoresTheFlagIsRefused()
	{
		using var adapter = new StubbornAdapter(new IncrementalIdGenerator());

		Throws<ConnectorNotPaperException>(() => PaperOnlyGuard.Assert(adapter));
	}

	/// <summary>A connector that can be told, and says so afterwards, is accepted.</summary>
	[TestMethod]
	public void AnAdapterThatGoesIntoDemoModeIsAccepted()
	{
		using var adapter = new FeedAdapter(new IncrementalIdGenerator());

		PaperOnlyGuard.Assert(adapter);

		IsTrue(adapter.IsDemo, "the adapter was not put into demo mode.");
	}

	/// <summary>
	/// The settings dictionary cannot be used as a lever on the guarantee, and the refusal happens before
	/// anything is downloaded, because the answer does not depend on which connector it is.
	/// </summary>
	[TestMethod]
	public void SettingsCannotTurnPaperOff()
	{
		var choice = new ConnectorChoice(
			"StockSharp.Example",
			string.Empty,
			string.Empty,
			new Dictionary<string, string>(StringComparer.Ordinal) { ["IsDemo"] = "false" });

		var refusal = Throws<ConnectorRefusedException>(() => AdapterConfigurator.Validate(choice));

		IsTrue(refusal.Message.Contains("IsDemo", StringComparison.Ordinal),
			$"the refusal does not name the setting it refused: {refusal.Message}");
	}

	/// <summary>
	/// The flag is set first and the settings second, so a connector whose demo flag changes another
	/// setting cannot silently undo what the caller asked for.
	/// </summary>
	/// <remarks>
	/// This is the trap the old comments described, caught without a network. The connector this product
	/// was written against switches its market data feed to a single exchange when it is told it is on
	/// paper, and on that feed the volumes are a few percent of the market - so a run measured after the
	/// wrong order is a run measured on a fraction of the market, and nothing about it looks wrong.
	/// </remarks>
	[TestMethod]
	public void SettingsAreAppliedAfterTheFlag()
	{
		using var adapter = new FeedAdapter(new IncrementalIdGenerator());

		AdapterConfigurator.Apply(
			adapter,
			null,
			new Dictionary<string, string>(StringComparer.Ordinal) { ["Feed"] = "consolidated" },
			TradingMandate.Paper);

		AreEqual("consolidated", adapter.Feed,
			"the demo flag was set after the settings, so it overwrote one of them.");

		IsTrue(adapter.IsDemo, "the adapter came out of configuration no longer on paper.");
	}

	/// <summary>
	/// Setting the flag before the settings is only half of it. A connector that took the flag, kept it,
	/// and then dropped it while an ordinary setting was applied is refused - because by the time the
	/// settings have been applied the flag is what decides whether the next order is real money.
	/// </summary>
	/// <remarks>
	/// The pre-settings assertion is happy with this connector and would hand it over: it goes into demo
	/// mode and says so. Only <c>Venue</c> takes it off, and <c>Venue</c> is an ordinary setting that no
	/// rule refuses. So this is the case that nothing but the read-back after the settings can catch, and
	/// the difference between catching it and not is the difference between paper and real money.
	/// </remarks>
	[TestMethod]
	public void ASettingThatTakesTheConnectorOffPaperIsRefused()
	{
		using var adapter = new VenueAdapter(new IncrementalIdGenerator());

		PaperOnlyGuard.Assert(adapter);

		IsTrue(adapter.IsDemo,
			"the fake is refused before the settings are applied, so it does not exercise the read-back.");

		var refusal = Throws<ConnectorNotPaperException>(() => AdapterConfigurator.Apply(
			adapter,
			null,
			new Dictionary<string, string>(StringComparer.Ordinal) { ["Venue"] = "live" },
			TradingMandate.Paper));

		IsTrue(refusal.Message.Contains(nameof(VenueAdapter), StringComparison.Ordinal),
			$"the refusal does not name the connector it refused: {refusal.Message}");

		IsFalse(adapter.IsDemo, "the connector was refused, so it must still be off paper, not repaired.");
	}

	/// <summary>
	/// The check after the settings is a read. It refuses a connector that came off paper; it does not
	/// put it back on.
	/// </summary>
	/// <remarks>
	/// Assigning the flag again would look like the safer of the two, and is the worse one twice over. It
	/// re-runs the flag's side effects over settings the caller has already been told were applied - here
	/// the feed the run would be measured on - and it turns a connector that quietly came off paper into
	/// one that is quietly put back on, so the connector that needed refusing is bound and traded instead.
	/// </remarks>
	[TestMethod]
	public void TheCheckAfterTheSettingsReadsTheFlagRatherThanPuttingItBack()
	{
		using var adapter = new VenueAdapter(new IncrementalIdGenerator());

		Throws<ConnectorNotPaperException>(() => AdapterConfigurator.Apply(
			adapter,
			null,
			new Dictionary<string, string>(StringComparer.Ordinal)
			{
				["Feed"] = "consolidated",
				["Venue"] = "live",
			},
			TradingMandate.Paper));

		AreEqual("consolidated", adapter.Feed,
			"the flag was assigned again rather than read, and its side effect overwrote a setting.");

		IsFalse(adapter.IsDemo,
			"the flag was put back on, so a connector that came off paper would have passed as one that " +
			"never did.");
	}

	/// <summary>
	/// Every port is built over the same source, and the source is what asserts the guarantee - so a
	/// connector that will not go into demo mode is refused before any of the four ports can use it.
	/// </summary>
	[TestMethod]
	public void EveryPortGetsAPaperAdapter()
	{
		var refusing = new ConnectorAdapterSource(
			typeof(StubbornAdapter),
			null,
			new Dictionary<string, string>(StringComparer.Ordinal),
			SymbolBoards.Default,
			"example-00000000",
			TradingMandate.Paper);

		Throws<ConnectorNotPaperException>(() => refusing.Create(new IncrementalIdGenerator()));

		var accepting = new ConnectorAdapterSource(
			typeof(FeedAdapter),
			null,
			new Dictionary<string, string>(StringComparer.Ordinal),
			SymbolBoards.Default,
			"example-00000000",
			TradingMandate.Paper);

		using var adapter = accepting.Create(new IncrementalIdGenerator());

		IsTrue(((IDemoAdapter)adapter).IsDemo, "a port was handed an adapter that is not on paper.");
	}
}
