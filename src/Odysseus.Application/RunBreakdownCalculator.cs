namespace StockSharp.Odysseus.Application;

using System.Globalization;
using System.Linq;

/// <summary>
/// Cutting a run's result apart to see where it came from.
/// </summary>
/// <remarks>
/// A run reports one number for a whole stretch of history, and one number cannot say whether an edge
/// held throughout or whether a single fortnight paid for everything around it. An agent reading only
/// the total has nothing to repair a hypothesis with: it can propose another one, but it cannot see
/// which part of this one was working.
///
/// Nothing here is a verdict and nothing here is charged. It is the same trades the run already
/// recorded, grouped four ways.
/// </remarks>
public static class RunBreakdownCalculator
{
	/// <summary>How many distinct holding times are worth naming one by one rather than grouping.</summary>
	private const int FewHoldingTimes = 8;

	/// <summary>
	/// Measures where a run's result came from.
	/// </summary>
	/// <param name="trades">Trades the run made.</param>
	/// <param name="session">The part of the day the instrument trades in, as times of day in UTC.</param>
	/// <returns>The result, cut four ways.</returns>
	public static RunBreakdown Measure(IReadOnlyList<ExecutedTrade> trades, (TimeSpan Open, TimeSpan Close) session)
	{
		ArgumentNullException.ThrowIfNull(trades);

		if (trades.Count == 0)
			return new([], [], [], [], 0m, 0, 0);

		var net = trades.Sum(t => t.Net);

		var byMonth = trades
			.GroupBy(t => new DateTime(t.EntryTime.Year, t.EntryTime.Month, 1, 0, 0, 0, DateTimeKind.Utc))
			.OrderBy(g => g.Key)
			.Select(g => Segment(g.Key.ToString("MMMM yyyy", CultureInfo.InvariantCulture), g, net))
			.ToArray();

		var best = byMonth.MaxBy(m => m.Net);

		return new(
			byMonth,
			BySession(trades, session, net),
			ByHolding(trades, net),
			ByDirection(trades, net),
			Round(net - best.Net),
			byMonth.Length,
			byMonth.Count(m => m.Net > 0));
	}

	private static IReadOnlyList<RunSegment> BySession(
		IReadOnlyList<ExecutedTrade> trades,
		(TimeSpan Open, TimeSpan Close) session,
		decimal net)
	{
		var (open, close) = session;

		var early = open.Add(TimeSpan.FromMinutes(30));
		var late = close.Subtract(TimeSpan.FromMinutes(30));

		// A position is counted where it was opened: that is the moment the rules fired, and it is the
		// moment an agent would change if this part of the session turns out to be the whole result.
		return Cut(trades, net,
		[
			("outside the active session", t => t.EntryTime.TimeOfDay < open || t.EntryTime.TimeOfDay > close),
			("first 30 minutes", t => t.EntryTime.TimeOfDay >= open && t.EntryTime.TimeOfDay < early),
			("middle of the session", t => t.EntryTime.TimeOfDay >= early && t.EntryTime.TimeOfDay < late),
			("last 30 minutes", t => t.EntryTime.TimeOfDay >= late && t.EntryTime.TimeOfDay <= close),
		]);
	}

	private static IReadOnlyList<RunSegment> ByHolding(IReadOnlyList<ExecutedTrade> trades, decimal net)
	{
		var held = trades.Select(t => t.Holding).Distinct().OrderBy(h => h).ToArray();

		// A fixed exit — five bars, one session, a stop and nothing else — is the common case, and there
		// is nothing to cut when every position was held exactly as long as every other. Cutting it anyway
		// would split ties by whatever order they arrived in and hand back parts with different results,
		// which reads as a finding about holding time and is an artefact of the sort.
		if (held.Length <= FewHoldingTimes)
		{
			return held
				.Select(h => Segment($"held {Describe(h)}", trades.Where(t => t.Holding == h).ToArray(), net))
				.ToArray();
		}

		// Otherwise quarters, cut at durations rather than at counts: trades held the same time belong in
		// the same part whichever side of a boundary the count would have put them.
		var ordered = trades.OrderBy(t => t.Holding).ToArray();
		var bounds = Enumerable.Range(1, 3).Select(q => ordered[q * ordered.Length / 4].Holding).Distinct().ToArray();
		var parts = new List<RunSegment>();
		var from = TimeSpan.MinValue;

		foreach (var to in bounds.Append(TimeSpan.MaxValue))
		{
			var inside = ordered.Where(t => t.Holding > from && (to == TimeSpan.MaxValue || t.Holding <= to)).ToArray();

			if (inside.Length > 0)
				parts.Add(Segment($"held {Describe(inside[0].Holding)} to {Describe(inside[^1].Holding)}", inside, net));

			from = to;
		}

		return parts;
	}

	private static IReadOnlyList<RunSegment> ByDirection(IReadOnlyList<ExecutedTrade> trades, decimal net)
		=> Cut(trades, net,
		[
			("held long", t => t.Direction == TradeDirections.Long),
			("held short", t => t.Direction == TradeDirections.Short),
		]);

	private static IReadOnlyList<RunSegment> Cut(
		IReadOnlyList<ExecutedTrade> trades,
		decimal net,
		IReadOnlyList<(string Name, Func<ExecutedTrade, bool> Test)> buckets)
	{
		var cut = new List<RunSegment>();

		foreach (var (name, test) in buckets)
		{
			var inside = trades.Where(test).ToArray();

			// A bucket nothing fell into is not a finding; a row of zeroes only makes the rest harder to read.
			if (inside.Length > 0)
				cut.Add(Segment(name, inside, net));
		}

		return cut;
	}

	private static RunSegment Segment(string name, IEnumerable<ExecutedTrade> trades, decimal net)
	{
		var inside = trades as IReadOnlyList<ExecutedTrade> ?? trades.ToArray();
		var made = inside.Sum(t => t.Net);

		return new(
			name,
			inside.Count,
			Round(made),
			Round(inside.Count(t => t.Net > 0) * 100m / inside.Count),
			net > 0 ? Round(made * 100m / net) : 0m);
	}

	private static string Describe(TimeSpan holding)
		=> holding switch
		{
			{ TotalMinutes: < 60 } => $"{holding.TotalMinutes:0.#} min",
			{ TotalHours: < 24 } => $"{holding.TotalHours:0.#} h",
			_ => $"{holding.TotalDays:0.#} d",
		};

	private static decimal Round(decimal value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);
}
