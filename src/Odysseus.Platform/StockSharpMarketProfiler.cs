namespace Odysseus.Platform;

using System;
using System.Collections.Generic;
using System.Linq;

using StockSharp.Messages;

using Odysseus.Application;
using Odysseus.Domain;

using CoreMarketProfiler = StockSharp.Algo.Candles.MarketProfiler;
using CoreSessionParts = StockSharp.Algo.Candles.SessionParts;
using CoreCaveats = StockSharp.Algo.Candles.MarketProfileCaveats;

/// <summary>
/// The market profile of the trading platform, put in the words an agent reads.
/// </summary>
public sealed class StockSharpMarketProfiler : IMarketProfiler
{
	/// <inheritdoc />
	public int MinimumBars => CoreMarketProfiler.MinimumCandles;

	/// <inheritdoc />
	public MarketProfile Measure(string symbol, IReadOnlyList<Candle> candles)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
		ArgumentNullException.ThrowIfNull(candles);

		if (candles.Count < MinimumBars)
		{
			throw new ArgumentException(
				$"'{symbol}' has {candles.Count} bars in the development slice; at least {MinimumBars} are needed " +
				"before an average means anything. Widen the date range or use a shorter timeframe.",
				nameof(candles));
		}

		var profile = CoreMarketProfiler.Measure(Convert(candles));

		var caveats = new List<string>();

		if (profile.Caveats.HasFlag(CoreCaveats.FewSessions))
			caveats.Add($"Only {profile.Coverage.Sessions} trading days are covered, so anything measured per day rests on very few observations.");

		if (profile.Caveats.HasFlag(CoreCaveats.FewCandles))
			caveats.Add($"{profile.Coverage.Candles} bars is a small sample; treat the event counts below as indicative rather than settled.");

		return new(
			new(
				symbol,
				profile.Coverage.Candles,
				Utc(profile.Coverage.From),
				Utc(profile.Coverage.To),
				profile.Coverage.Sessions,
				profile.Coverage.MedianVolume,
				profile.Coverage.HighVolumeSharePercent),
			new(
				profile.Movement.MedianCandleRangePercent,
				profile.Movement.UpperCandleRangePercent,
				profile.Movement.MedianDailyRangePercent,
				profile.Movement.AnnualisedVolatilityPercent),
			new(
				profile.Persistence.Autocorrelation1,
				profile.Persistence.Autocorrelation5,
				profile.Persistence.VarianceRatio5,
				profile.Persistence.VarianceRatio20,
				profile.Persistence.DirectionalDaySharePercent),
			new(
				profile.Edge.BreakoutCount,
				profile.Edge.BreakoutFollowThroughPercent,
				profile.Edge.BreakoutPositiveSharePercent,
				profile.Edge.BreakdownCount,
				profile.Edge.BreakdownFollowThroughPercent,
				profile.Edge.StretchCount,
				profile.Edge.StretchReversionPercent),
			[.. profile.Session.Select(s => new SessionBucket(Name(s.Part), s.RangeSharePercent, s.VolumeSharePercent, s.AverageReturnPercent))],
			caveats);
	}

	/// <inheritdoc />
	public (TimeSpan Open, TimeSpan Close) ActiveSession(IReadOnlyList<Candle> candles)
	{
		ArgumentNullException.ThrowIfNull(candles);

		return CoreMarketProfiler.ActiveSession(Convert(candles));
	}

	private static ICandleMessage[] Convert(IReadOnlyList<Candle> candles)
		=> [.. candles.Select(c => new TimeFrameCandleMessage
		{
			OpenTime = c.OpenTime,
			OpenPrice = c.Open,
			HighPrice = c.High,
			LowPrice = c.Low,
			ClosePrice = c.Close,
			TotalVolume = c.Volume,
			State = CandleStates.Finished,
		})];

	private static string Name(CoreSessionParts part)
		=> part switch
		{
			CoreSessionParts.Outside => "outside the active session",
			CoreSessionParts.FirstHalfHour => "first 30 minutes",
			CoreSessionParts.Middle => "middle of the session",
			CoreSessionParts.LastHalfHour => "last 30 minutes",
			_ => throw new ArgumentOutOfRangeException(nameof(part), part, null),
		};

	private static DateTime Utc(DateTime time)
		=> time.Kind == DateTimeKind.Utc ? time : DateTime.SpecifyKind(time, DateTimeKind.Utc);
}
