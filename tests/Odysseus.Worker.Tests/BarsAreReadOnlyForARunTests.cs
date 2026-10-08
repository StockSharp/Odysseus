namespace StockSharp.Odysseus.Worker.Tests;

using Ecng.Common;

using StockSharp.Algo;
using StockSharp.BusinessEntities;
using StockSharp.Algo.Storages;
using StockSharp.Messages;

/// <summary>
/// What a run is allowed to do to the bars it is measured over.
/// </summary>
/// <remarks>
/// A run opens the shared market-data storage, and that folder is a market-data drive — a
/// directory the engine is perfectly willing to add a day to. A hundred runs of a search over one
/// dataset have to leave it exactly as they found it, or the last run and the first were not measured
/// on the same thing.
/// </remarks>
[TestClass]
public class BarsAreReadOnlyForARunTests : OdysseusTestBase
{
	private static readonly DateTime _open = new(2026, 3, 2, 14, 0, 0, DateTimeKind.Utc);

	private static readonly TimeSpan _frame = TimeSpan.FromMinutes(5);

	private const string Symbol = "DEMO";

	/// <summary>Bars opened for a run cannot be added to.</summary>
	[TestMethod]
	public async Task BarsOpenedForARunCannotBeAddedTo()
	{
		var range = await StoredBars.WriteAsync(Bars(), Symbol, _frame, CancellationToken);
		var securityId = EmulationSetup.SecurityIdOf(Symbol);

		var storage = EmulationSetup
			.Open(range.Folder)
			.GetTimeFrameCandleMessageStorage(securityId, _frame);

		var extra = new TimeFrameCandleMessage
		{
			SecurityId = securityId,
			TypedArg = _frame,
			DataType = _frame.TimeFrame(),
			OpenTime = _open.AddDays(30),
			CloseTime = _open.AddDays(30) + _frame,
			OpenPrice = 100m,
			HighPrice = 101m,
			LowPrice = 99m,
			ClosePrice = 100m,
			TotalVolume = 1_000m,
			State = CandleStates.Finished,
		};

		await ThrowsAsync<UnauthorizedAccessException>(
			() => storage.SaveAsync([extra], CancellationToken).AsTask());
	}

	/// <summary>Reading them still works, or the refusal above would be refusing everything.</summary>
	[TestMethod]
	public async Task BarsOpenedForARunCanStillBeRead()
	{
		var range = await StoredBars.WriteAsync(Bars(), Symbol, _frame, CancellationToken);

		var read = await new LocalBarStorage(range.Folder).ReadAsync(
			Symbol, _frame, range.From, range.To, CancellationToken);

		AreEqual(range.Count, read.Count, "the bars a run was pointed at did not come back.");
	}

	/// <summary>
	/// Where bars are written and what a run asks for are the same identity.
	/// </summary>
	/// <remarks>
	/// Two spellings of the same instrument would mean a run opening a folder full of bars and finding
	/// none, which looks exactly like a strategy whose rules were never met.
	/// </remarks>
	[TestMethod]
	public void TheInstrumentARunTradesIsFiledUnderTheIdentityBarsAreWrittenWith()
		=> AreEqual(
			EmulationSetup.SecurityIdOf(Symbol),
			EmulationSetup.CreateSecurity(Symbol, 0.01m).ToSecurityId());

	private static IReadOnlyList<Candle> Bars()
	{
		var bars = new List<Candle>();

		for (var i = 0; i < 120; i++)
		{
			var price = 100m + i % 7;

			bars.Add(new(_open + _frame * i, price, price + 0.5m, price - 0.5m, price + 0.25m, 1_000m));
		}

		return bars;
	}
}
