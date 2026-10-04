namespace Odysseus.Domain;

using System;

/// <summary>
/// One finished candle.
/// </summary>
/// <param name="OpenTime">Moment the candle opened, in UTC.</param>
/// <param name="Open">First traded price.</param>
/// <param name="High">Highest traded price.</param>
/// <param name="Low">Lowest traded price.</param>
/// <param name="Close">Last traded price.</param>
/// <param name="Volume">Volume traded during the candle.</param>
public readonly record struct Candle(
	DateTime OpenTime,
	decimal Open,
	decimal High,
	decimal Low,
	decimal Close,
	decimal Volume)
{
	/// <summary>
	/// Whether the four prices can describe a real bar: the high is the highest and the low the lowest.
	/// </summary>
	public bool IsWellFormed
		=> High >= Low &&
			Open >= Low && Open <= High &&
			Close >= Low && Close <= High &&
			Volume >= 0;
}
