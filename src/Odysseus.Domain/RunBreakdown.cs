namespace Odysseus.Domain;

using System.Collections.Generic;

/// <summary>
/// One part of a run, measured on its own.
/// </summary>
/// <param name="Name">What this part is: a month, a part of the session, a range of holding times.</param>
/// <param name="Trades">Trades that fell in it.</param>
/// <param name="Net">What they came to after costs.</param>
/// <param name="WinRatePercent">How many of them ended in profit.</param>
/// <param name="ShareOfNetPercent">
/// How much of the run's result this part accounts for. Only meaningful when the run made money: a
/// share of a loss reads as a share of a profit and says the opposite of what it means, so it is left
/// at zero and the amounts are the thing to read.
/// </param>
public sealed record RunSegment(
	string Name,
	int Trades,
	decimal Net,
	decimal WinRatePercent,
	decimal ShareOfNetPercent);

/// <summary>
/// Where a run's result came from.
/// </summary>
/// <param name="ByMonth">The result month by month.</param>
/// <param name="ByPartOfSession">The result by the part of the session a position was opened in.</param>
/// <param name="ByHoldingTime">The result by how long positions were held, cut into quarters.</param>
/// <param name="ByDirection">The result by which way positions were held.</param>
/// <param name="NetWithoutBestMonth">
/// What the run comes to with its best month removed. A candidate that turns negative here made its
/// money in one stretch of the market rather than out of an edge that holds.
/// </param>
/// <param name="MonthsTraded">Months the run traded in at all.</param>
/// <param name="MonthsInProfit">How many of those ended in profit.</param>
public sealed record RunBreakdown(
	IReadOnlyList<RunSegment> ByMonth,
	IReadOnlyList<RunSegment> ByPartOfSession,
	IReadOnlyList<RunSegment> ByHoldingTime,
	IReadOnlyList<RunSegment> ByDirection,
	decimal NetWithoutBestMonth,
	int MonthsTraded,
	int MonthsInProfit);
