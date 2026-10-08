namespace StockSharp.Odysseus.Server;

using System.Collections.Generic;

/// <summary>
/// One page of a list, and what is left of it.
/// </summary>
/// <remarks>
/// A project may hold four hundred runs and a permanent record longer than that, and every one of them
/// used to arrive in a single answer. What that costs is not the server's memory - the lists are small
/// on disk - but the reader's: an agent handed a thousand entries spends its attention on scrolling
/// rather than on the three that matter, and a client with a message limit gets nothing at all.
///
/// The window is by position rather than by cursor because every list here is append-only and read in a
/// fixed order, so a position means the same thing on the next call as it did on this one.
/// </remarks>
public static class Page
{
	/// <summary>How many are returned when the caller does not say.</summary>
	public const int DefaultSize = 50;

	/// <summary>The most that can be asked for at once.</summary>
	public const int MaximumSize = 500;

	/// <summary>What to say about the two arguments, so every list says it the same way.</summary>
	public const string OffsetDescription =
		"How many to skip, for reading a long list a page at a time. Zero starts at the beginning; the " +
		"answer says what to pass next.";

	/// <summary>What to say about the size argument.</summary>
	public const string LimitDescription =
		"How many to return at once. Zero asks for the default of 50; the most that can be asked for is 500.";

	/// <summary>
	/// Takes one page and describes where it sits.
	/// </summary>
	/// <typeparam name="TSource">What is being listed.</typeparam>
	/// <typeparam name="TResult">What each item is reported as.</typeparam>
	/// <param name="items">Everything there is, in the order it is read in.</param>
	/// <param name="offset">How many to skip. Anything below zero starts at the beginning.</param>
	/// <param name="limit">How many to take. Zero or below asks for the default.</param>
	/// <param name="describe">How to report one item.</param>
	/// <returns>The page, and the counts a caller needs to ask for the next one.</returns>
	public static (TResult[] Items, object Window) Of<TSource, TResult>(
		IReadOnlyList<TSource> items,
		int offset,
		int limit,
		Func<TSource, TResult> describe)
	{
		ArgumentNullException.ThrowIfNull(items);
		ArgumentNullException.ThrowIfNull(describe);

		var from = Math.Clamp(offset, 0, items.Count);
		var size = limit <= 0 ? DefaultSize : Math.Min(limit, MaximumSize);

		var taken = items.Skip(from).Take(size).Select(describe).ToArray();

		return (taken, new
		{
			offset = from,
			limit = size,
			returned = taken.Length,
			total = items.Count,
			hasMore = from + taken.Length < items.Count,
			nextOffset = from + taken.Length < items.Count ? from + taken.Length : (int?)null,
		});
	}
}
