namespace StockSharp.Odysseus.Broker.Tests;

using System.Collections.Generic;
using System.Threading.Tasks;

using StockSharp.Odysseus.Compiler;
using StockSharp.Odysseus.Domain;
using StockSharp.Odysseus.Platform;
using StockSharp.Odysseus.Spec;

/// <summary>
/// Putting a strategy on a real paper account.
/// </summary>
/// <remarks>
/// Everything the deployment rules do is settled without a broker. This is the other half: whether a
/// connection is made at all, whether the venue answers with the instrument and the account, whether the
/// strategy starts on top of them and whether it comes off cleanly. None of that can be learned from a
/// stand-in, because a stand-in agrees with whatever we already believe.
///
/// One share, and the rules need forty-five bars before they can say anything - nearly four hours of a
/// five-minute subscription that begins empty. A minute of this therefore establishes the connection and
/// trades nothing, which is what is wanted from a check that runs against somebody's account.
/// </remarks>
[TestCategory("Integration")]
[TestClass]
public class BrokerPaperTraderTests : OdysseusTestBase
{
	private const string KeysVariable = "ODYSSEUS_BROKER_KEYS";
	private const string ConnectorVariable = "ODYSSEUS_BROKER_CONNECTOR";

	private IPaperTrader _trader;

	/// <inheritdoc />
	protected override TimeSpan Timeout => TimeSpan.FromMinutes(6);

	/// <summary>Loads the connector the environment names, or declares the suite inapplicable.</summary>
	[TestInitialize]
	public async Task Connect()
	{
		// Opened the way a runner opens one, because the runner is now the only thing that trades.
		var binding = await BrokerFixture
			.Connectors(KeysVariable)
			.OpenAsync(BrokerFixture.Choice(ConnectorVariable), CancellationToken);

		_trader = binding.Trader;
	}

	/// <summary>A strategy starts against the account, runs, and comes off leaving nothing behind.</summary>
	[TestMethod]
	public async Task AStrategyReachesTheAccountAndComesOffAgain()
	{
		var built = new StrategyBuilder(EngineAssemblies.Paths(AppContext.BaseDirectory)).Build(Spec());

		await using var session = await _trader.StartAsync(
			new(built.Assembly, built.ClassName, new Dictionary<string, decimal>(), "NVDA", TimeSpan.FromMinutes(5), 1m),
			CancellationToken);

		IsTrue(session.IsRunning, "the strategy did not start.");

		await Task.Delay(TimeSpan.FromSeconds(60), CancellationToken);

		IsNull(session.Error, $"the strategy stopped itself: {session.Error}");
		IsTrue(session.IsRunning, "the strategy stopped on its own within the first minute.");

		await session.StopAsync(closePosition: true, CancellationToken);

		IsFalse(session.IsRunning, "the strategy is still running after being stopped.");
		AreEqual(0m, session.Position, "the account was left holding something after a stop that was to close it.");
	}

	/// <summary>Buy above the average of the last twenty bars, let go five bars later.</summary>
	private static StrategySpec Spec()
		=> new()
		{
			Name = "Above its average",
			Thesis = "A price above its own recent average keeps going for a few bars.",
			AllowLong = true,
			AllowShort = false,
			TimeFrame = TimeSpan.FromMinutes(5),
			WarmupBars = 45,
			Entries =
			[
				new("e1", TradeDirections.Long,
					new Compare(
						new Field(CandleFields.Close),
						ComparisonOperators.GreaterThan,
						new IndicatorRef("sma", new Constant(20), CandleFields.Close))),
			],
			Exits = [new("x1", ExitKinds.TimeExit, TradeDirections.Long, Length: new Constant(5))],
			Parameters = [],
			Risk = new(0.10m, 0.02m),
		};
}
