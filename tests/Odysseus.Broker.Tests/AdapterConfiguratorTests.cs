namespace StockSharp.Odysseus.Broker.Tests;

using System.Collections.Generic;
using System.Linq;

using Ecng.Common;

/// <summary>
/// Configuring a connector nobody compiled against.
/// </summary>
/// <remarks>
/// The platform offers a settings envelope every adapter implements, and it is not used here: it
/// silently ignores a name it does not know, so a typo in a connector file would produce a server that
/// connects, downloads a fraction of what was asked for, and says nothing. What is used instead is
/// reflection with a refusal, and the same reflection answers <c>describe_connector</c> - so what a
/// caller is told the connector accepts is exactly what selecting it will accept.
/// </remarks>
[TestClass]
public class AdapterConfiguratorTests : OdysseusTestBase
{
	/// <summary>
	/// A name the connector does not declare is refused, with the names it does declare in the message.
	/// </summary>
	[TestMethod]
	public void AnUnknownSettingIsRefusedByName()
	{
		using var adapter = new FeedAdapter(new IncrementalIdGenerator());

		var refusal = Throws<ConnectorRefusedException>(() => AdapterConfigurator.Apply(
			adapter,
			null,
			new Dictionary<string, string>(StringComparer.Ordinal) { ["Fed"] = "consolidated" },
			TradingMandate.Paper));

		IsTrue(refusal.Message.Contains("Fed", StringComparison.Ordinal),
			$"the refusal does not name the setting that was wrong: {refusal.Message}");

		IsTrue(refusal.Message.Contains(nameof(FeedAdapter.Feed), StringComparison.Ordinal),
			$"the refusal does not say what the connector does declare: {refusal.Message}");
	}

	/// <summary>
	/// What is described is what is accepted, so an agent that reads the description and writes a choice
	/// from it cannot be refused for a name it was told about.
	/// </summary>
	[TestMethod]
	public void WhatIsDescribedIsWhatIsAccepted()
	{
		var described = AdapterConfigurator.Describe(typeof(FeedAdapter));

		IsTrue(described.Any(s => s.Name == nameof(FeedAdapter.Feed)),
			$"the description does not mention the one setting the connector has: {string.Join(", ", described.Select(s => s.Name))}.");

		IsFalse(described.Any(s => s.Name == "IsDemo"),
			"the description offers the paper flag as a setting, which would invite a caller to try to change it.");
	}

	/// <summary>
	/// A connector that wants credentials this server was not given is refused when it is chosen, rather
	/// than when the first download fails halfway through a plan.
	/// </summary>
	[TestMethod]
	public void ACredentialShapeThatDoesNotFitIsRefused()
	{
		AreEqual("none", AdapterConfigurator.CredentialShapeOf(typeof(FeedAdapter)));

		using var adapter = new FeedAdapter(new IncrementalIdGenerator());

		var wrong = new BrokerCredentials(
			BrokerCredentialKinds.KeySecret,
			new Dictionary<string, string>(StringComparer.Ordinal) { ["key"] = "k", ["secret"] = "s" });

		var refusal = Throws<ConnectorRefusedException>(() => AdapterConfigurator.Apply(adapter, wrong, null, TradingMandate.Paper));

		IsTrue(refusal.Message.Contains("key", StringComparison.OrdinalIgnoreCase),
			$"the refusal does not say which credentials were offered: {refusal.Message}");
	}
}
