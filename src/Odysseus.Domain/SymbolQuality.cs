namespace Odysseus.Domain;

/// <summary>
/// What was found in the bars of one symbol.
/// </summary>
/// <param name="Symbol">Symbol the bars belong to.</param>
/// <param name="Records">Bars kept.</param>
/// <param name="From">First bar time, in UTC.</param>
/// <param name="To">Last bar time, in UTC.</param>
/// <param name="Gaps">Missing bars inside the range, beyond a session break.</param>
/// <param name="Duplicates">Bars rejected because a bar with the same time was already present.</param>
/// <param name="Invalid">Bars rejected because their prices could not describe a real bar.</param>
public sealed record SymbolQuality(
	string Symbol,
	int Records,
	DateTime From,
	DateTime To,
	int Gaps,
	int Duplicates,
	int Invalid);
