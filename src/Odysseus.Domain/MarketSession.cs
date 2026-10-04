namespace Odysseus.Domain;

using System;

/// <summary>
/// The clock the market keeps.
/// </summary>
/// <remarks>
/// Bars are stored in UTC, and a rule about the session is written in the hours a trader would say out
/// loud: not before ten to ten, close at four. Compared without converting, those rules fire five or six
/// hours early — in a stretch of the tape that barely trades — and nothing about the numbers that come
/// back looks wrong.
///
/// The offset is not a constant. New York is five hours behind UTC in winter and four in summer, so the
/// conversion has to be done for the moment in question rather than once for the dataset.
///
/// One zone, because there is one market here. When there is a second, this is where it will be looked
/// up from rather than assumed.
/// </remarks>
public static class MarketSession
{
	private static readonly Lazy<TimeZoneInfo> _zone = new(Find);

	/// <summary>Where the market is.</summary>
	public static TimeZoneInfo Zone => _zone.Value;

	/// <summary>When the regular session opens, on the market's own clock.</summary>
	public static TimeSpan Opens { get; } = new(9, 30, 0);

	/// <summary>When it closes.</summary>
	public static TimeSpan Closes { get; } = new(16, 0, 0);

	/// <summary>
	/// Converts a moment to the market's clock.
	/// </summary>
	/// <param name="utc">Moment in UTC.</param>
	/// <returns>The time of day where the market is.</returns>
	public static TimeSpan TimeOfDay(DateTime utc)
		=> TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone).TimeOfDay;

	// Both names for the same zone: the first is what every platform but Windows calls it, the second is
	// what Windows called it before it learned the first.
	private static TimeZoneInfo Find()
	{
		try
		{
			return TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
		}
		catch (TimeZoneNotFoundException)
		{
			return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
		}
	}
}
