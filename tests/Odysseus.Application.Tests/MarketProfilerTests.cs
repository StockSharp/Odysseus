namespace StockSharp.Odysseus.Application.Tests;

using StockSharp.Odysseus.Platform;

/// <summary>
/// What <c>analyze_market</c> actually measures, checked against series whose statistics can be worked
/// out on paper.
/// </summary>
/// <remarks>
/// Every expectation below is derived from the shape of the series rather than from a previous run, so a
/// change of behaviour fails here instead of being adopted. Where a statistic has no closed form the
/// assertion is a band whose ends come from the same derivation - the average of a set of known terms
/// lies between the smallest and the largest of them, and a perfectly persistent series has a variance
/// ratio of about its horizon.
/// </remarks>
[TestClass]
public class MarketProfilerTests : OdysseusTestBase
{
	private static readonly DateTime _start = new(2025, 1, 6, 14, 30, 0, DateTimeKind.Utc);

	private static readonly TimeSpan _frame = TimeSpan.FromMinutes(5);

	private static readonly IMarketProfiler _profiler = new StockSharpMarketProfiler();

	/// <summary>Nothing can be measured on nothing, and saying so beats dividing by zero.</summary>
	[TestMethod]
	public void AnEmptySeriesIsRefused()
	{
		var error = Throws<ArgumentException>(() => _profiler.Measure("AAA", []));

		IsTrue(error.Message.Contains("has 0 bars", StringComparison.Ordinal), error.Message);
		IsTrue(error.Message.Contains($"at least {_profiler.MinimumBars}", StringComparison.Ordinal), error.Message);
	}

	/// <summary>One bar is a price, not a history.</summary>
	[TestMethod]
	public void ASingleBarIsRefused()
	{
		var error = Throws<ArgumentException>(() => _profiler.Measure("AAA", [Level(100m)]));

		IsTrue(error.Message.Contains("has 1 bars", StringComparison.Ordinal), error.Message);
	}

	/// <summary>The stated minimum is the minimum: one bar short is refused, the minimum itself is not.</summary>
	[TestMethod]
	public void TheMinimumIsExactlyWhereItSays()
	{
		var justShort = Series(1, _profiler.MinimumBars - 1, (_, _) => Level(100m));
		var justEnough = Series(1, _profiler.MinimumBars, (_, _) => Level(100m));

		Throws<ArgumentException>(() => _profiler.Measure("AAA", justShort));

		AreEqual(_profiler.MinimumBars, _profiler.Measure("AAA", justEnough).Coverage.Bars);
	}

	/// <summary>A measurement needs to know what it is measuring, and what it is measuring it on.</summary>
	[TestMethod]
	public void MissingArgumentsAreRefused()
	{
		var bars = Series(50, 6, (_, _) => Level(100m));

		Throws<ArgumentNullException>(() => _profiler.Measure(null, bars));
		Throws<ArgumentException>(() => _profiler.Measure(" ", bars));
		Throws<ArgumentNullException>(() => _profiler.Measure("AAA", null));
	}

	/// <summary>
	/// Fifty days of one unchanging price: every range is zero, and so is every volatility drawn from one.
	/// </summary>
	[TestMethod]
	public void AFlatSeriesDoesNotMove()
	{
		var profile = _profiler.Measure("AAA", Series(50, 6, (_, _) => Level(100m)));

		AreEqual(0m, profile.Movement.MedianBarRangePercent);
		AreEqual(0m, profile.Movement.UpperBarRangePercent);
		AreEqual(0m, profile.Movement.MedianDailyRangePercent);
		AreEqual(0m, profile.Movement.AnnualisedVolatilityPercent, "a price that never changes has no volatility.");
	}

	/// <summary>
	/// The same series has nothing to remember either. Autocorrelation of a constant is undefined and
	/// reported as zero; a variance ratio of a constant is reported as one, which is the value that says
	/// the horizon carries no information.
	/// </summary>
	[TestMethod]
	public void AFlatSeriesHasNoMemory()
	{
		var profile = _profiler.Measure("AAA", Series(50, 6, (_, _) => Level(100m)));

		AreEqual(0m, profile.Persistence.Autocorrelation1);
		AreEqual(0m, profile.Persistence.Autocorrelation5);
		AreEqual(1m, profile.Persistence.VarianceRatio5, "an absent variance ratio has to read as no information, not as mean reversion.");
		AreEqual(1m, profile.Persistence.VarianceRatio20);
		AreEqual(0m, profile.Persistence.DirectionalDayShare, "a day with no range closed nowhere in particular.");
	}

	/// <summary>
	/// And nothing ever happened on it: no close is above a prior high or below a prior low, and with a
	/// zero standard deviation nothing is two of them from anywhere.
	/// </summary>
	[TestMethod]
	public void AFlatSeriesHasNoEvents()
	{
		var edge = _profiler.Measure("AAA", Series(50, 6, (_, _) => Level(100m))).Edge;

		AreEqual(0, edge.BreakoutCount);
		AreEqual(0m, edge.BreakoutFollowThroughPercent);
		AreEqual(0m, edge.BreakoutPositiveShare);
		AreEqual(0, edge.BreakdownCount);
		AreEqual(0m, edge.BreakdownFollowThroughPercent);
		AreEqual(0, edge.StretchCount);
		AreEqual(0m, edge.StretchReversionPercent);
	}

	/// <summary>Coverage repeats back what it was handed, unrounded and unsummarised.</summary>
	[TestMethod]
	public void CoverageReportsWhatItWasGiven()
	{
		var profile = _profiler.Measure("AAA", Series(50, 6, (_, _) => Level(100m)));

		AreEqual("AAA", profile.Coverage.Symbol);
		AreEqual(300, profile.Coverage.Bars);
		AreEqual(50, profile.Coverage.Sessions);
		AreEqual(_start, profile.Coverage.From);
		AreEqual(_start.AddDays(49).AddMinutes(25), profile.Coverage.To, "To is the last bar's open, not the end of it.");
		AreEqual(10m, profile.Coverage.MedianVolume);
		AreEqual(0m, profile.Coverage.HighVolumeShare);
	}

	/// <summary>
	/// A price alternating between 100 and 101 forever. Its returns alternate around their own mean, so
	/// every neighbouring pair of deviations has an opposite sign: with 299 returns the lag-one
	/// autocorrelation is exactly -298/299 and the lag-five, which is also an odd lag, is -294/299.
	/// Five-bar blocks hold three moves one way and two the other, so consecutive blocks differ by a single
	/// move rather than by five, and the ratio works out to 299/1475. Twenty-bar blocks hold ten of each
	/// and are therefore all identical, which is a variance ratio of nothing at all.
	/// </summary>
	[TestMethod]
	public void APerfectOscillationComesStraightBack()
	{
		var bars = Series(50, 6, (day, bar) => Level((day * 6 + bar) % 2 == 0 ? 100m : 101m));
		var profile = _profiler.Measure("AAA", bars);

		AreEqual(-0.9967m, profile.Persistence.Autocorrelation1);
		AreEqual(-0.9833m, profile.Persistence.Autocorrelation5);
		AreEqual(0.2027m, profile.Persistence.VarianceRatio5);
		AreEqual(0m, profile.Persistence.VarianceRatio20, "identical blocks have no variance between them.");

		// Every day runs 100, 101, 100, 101, 100, 101, so it opens at one end of its range and closes at
		// the other. Closing at an extreme is what the measurement is counting, and it counts them all.
		AreEqual(100m, profile.Persistence.DirectionalDayShare);

		// Each bar's high equals its low, so the movement lives entirely between bars: a range of one
		// point over a close of 101 is 100/101 of a percent.
		AreEqual(0m, profile.Movement.MedianBarRangePercent);
		AreEqual(0.9901m, profile.Movement.MedianDailyRangePercent);
		AreEqual(0m, profile.Movement.AnnualisedVolatilityPercent, "every day closes at 101, so no day moved against the one before it.");

		// A close of 100 or 101 is never above the last twenty highs or below the last twenty lows, and
		// half a point from a mean of 100.5 is a quarter of the two standard deviations required.
		AreEqual(0, profile.Edge.BreakoutCount);
		AreEqual(0, profile.Edge.BreakdownCount);
		AreEqual(0, profile.Edge.StretchCount);
	}

	/// <summary>
	/// A price rising by one point a bar. Every close clears the highest of the twenty before it, so every
	/// bar the window allows is a breakout, and none is a breakdown; five bars later the price is always
	/// five points higher, so every one of them followed through.
	/// </summary>
	[TestMethod]
	public void APureTrendBreaksOutOnEveryBar()
	{
		var profile = _profiler.Measure("AAA", Series(50, 6, (day, bar) => Level(100m + day * 6 + bar)));

		// The window needs twenty bars behind it and five ahead: 300 - 20 - 5 of the 300 qualify.
		AreEqual(275, profile.Edge.BreakoutCount);
		AreEqual(0, profile.Edge.BreakdownCount);
		AreEqual(100m, profile.Edge.BreakoutPositiveShare);

		// Bar i returns five points on a price of 100 + i over the next five bars, so each follow-through
		// is 500/(100 + i) percent for i from 20 to 294, and the average of them lies between the ends.
		IsTrue(profile.Edge.BreakoutFollowThroughPercent > 500m / 394m, $"{profile.Edge.BreakoutFollowThroughPercent} is below every term averaged into it.");
		IsTrue(profile.Edge.BreakoutFollowThroughPercent < 500m / 120m, $"{profile.Edge.BreakoutFollowThroughPercent} is above every term averaged into it.");
		IsTrue(profile.Edge.BreakoutFollowThroughPercent is > 2.17m and < 2.18m, $"{profile.Edge.BreakoutFollowThroughPercent} is not the mean of 500/(100 + i) over that range.");

		// The same bars are all stretched: ten and a half points above a twenty-bar mean is far more than
		// two standard deviations of a one-bar return. Being stretched upwards, each one is signed against
		// its own follow-through, so the two averages are the same number with opposite signs.
		AreEqual(275, profile.Edge.StretchCount);
		AreEqual(-profile.Edge.BreakoutFollowThroughPercent, profile.Edge.StretchReversionPercent);

		// Each day runs six points and closes at the top of them, on a close of 105 + 6d. Ordered, the
		// twenty-sixth of the fifty is the twenty-fifth day: 500/249.
		AreEqual(0m, profile.Movement.MedianBarRangePercent, "each bar's high is its low; the movement is between bars.");
		AreEqual(2.0080m, profile.Movement.MedianDailyRangePercent);
		AreEqual(100m, profile.Persistence.DirectionalDayShare);
	}

	/// <summary>
	/// The same trend seen through the ratios: increments that all point the same way accumulate, so the
	/// variance of an n-bar move is about n times the variance of a one-bar move rather than equal to it.
	/// </summary>
	[TestMethod]
	public void APureTrendExtendsItsMoves()
	{
		var profile = _profiler.Measure("AAA", Series(50, 6, (day, bar) => Level(100m + day * 6 + bar)));

		IsTrue(profile.Persistence.Autocorrelation1 is > 0.98m and <= 1m, $"{profile.Persistence.Autocorrelation1} is not the autocorrelation of a smooth monotone series.");
		IsTrue(profile.Persistence.VarianceRatio5 is > 4.5m and < 5.5m, $"a perfectly persistent series has a five-bar variance ratio of about five, not {profile.Persistence.VarianceRatio5}.");
		IsTrue(profile.Persistence.VarianceRatio20 is > 19m and < 22m, $"a perfectly persistent series has a twenty-bar variance ratio of about twenty, not {profile.Persistence.VarianceRatio20}.");
	}

	/// <summary>
	/// Three events planted in a flat series, far enough apart that no window sees two of them.
	/// </summary>
	/// <remarks>
	/// Bars 50 and 150 close at 110 over twenty highs of 100, and are back at 100 five bars later: a loss
	/// of ten on a hundred and ten, or -1000/110 percent, neither of which was positive. Bar 200 closes at
	/// 90 under twenty lows of 100 and is back at 100 five bars later, gaining 1000/90 percent. All three
	/// are also stretched well past two standard deviations of a one-bar return, and each is signed
	/// towards the mean, so all three reversions are positive and their average is 2900/297.
	/// </remarks>
	[TestMethod]
	public void EventsAreCountedWithWhatFollowedThem()
	{
		var bars = Series(50, 6, (day, bar) => (day * 6 + bar) switch
		{
			50 or 150 => new Candle(default, 100m, 110m, 100m, 110m, 10m),
			200 => new Candle(default, 100m, 100m, 90m, 90m, 10m),
			_ => Level(100m),
		});

		var edge = _profiler.Measure("AAA", bars).Edge;

		AreEqual(2, edge.BreakoutCount);
		AreEqual(-9.0909m, edge.BreakoutFollowThroughPercent);
		AreEqual(0m, edge.BreakoutPositiveShare, "both breakouts gave the whole move back.");
		AreEqual(1, edge.BreakdownCount);
		AreEqual(11.1111m, edge.BreakdownFollowThroughPercent);
		AreEqual(3, edge.StretchCount);
		AreEqual(9.7643m, edge.StretchReversionPercent);
	}

	/// <summary>
	/// The middle bar and the widest bar in ten are the ones the ordering points at, which for an even
	/// count is the upper of the two middles. Three hundred bar ranges rising by a hundredth of a percent
	/// from zero put the middle at index 150 and the ninetieth percentile at index 270.
	/// </summary>
	[TestMethod]
	public void TheMiddleAndTheWidestInTenAreWhereTheOrderingPutsThem()
	{
		var bars = Series(50, 6, (day, bar) =>
		{
			var index = day * 6 + bar;

			return new Candle(default, 100m, 100m + index / 100m, 100m, 100m, 10m);
		});

		var movement = _profiler.Measure("AAA", bars).Movement;

		AreEqual(1.5000m, movement.MedianBarRangePercent);
		AreEqual(2.7000m, movement.UpperBarRangePercent);

		// A day's range is its widest bar, and the days rise the same way: the twenty-sixth of fifty is
		// the day whose last bar is index 155.
		AreEqual(1.5500m, movement.MedianDailyRangePercent);
	}

	/// <summary>
	/// A volume spike is one measured against the thirty bars before it, not against the whole series, and
	/// the share is taken over the bars that had thirty bars behind them.
	/// </summary>
	[TestMethod]
	public void AVolumeSpikeIsMeasuredAgainstTheThirtyBarsBeforeIt()
	{
		var bars = Series(50, 6, (day, bar) =>
		{
			var index = day * 6 + bar;

			return new Candle(default, 100m, 100m, 100m, 100m, (index is 100 or 200) ? 100m : 10m);
		});

		var coverage = _profiler.Measure("AAA", bars).Coverage;

		// Two of the 300 - 30 bars that could be judged carried ten times the recent average.
		AreEqual(0.7407m, coverage.HighVolumeShare);
		AreEqual(10m, coverage.MedianVolume, "two spikes do not move the middle of three hundred bars.");
	}

	/// <summary>
	/// A day whose last bar closed at zero cannot be divided by, and is left out of the daily ranges
	/// rather than crashing the measurement.
	/// </summary>
	/// <remarks>
	/// A bar of four zero prices is well formed by the domain's own definition - its high is not below its
	/// low and its volume is not negative - so the importer keeps it, and a broken feed reaches here.
	/// </remarks>
	[TestMethod]
	public void ADayThatClosedAtZeroDoesNotDivideByZero()
	{
		var bars = Series(50, 6, (day, bar) => day == 10 && bar == 5
			? new Candle(default, 0m, 0m, 0m, 0m, 0m)
			: Level(100m));

		var profile = _profiler.Measure("AAA", bars);

		AreEqual(50, profile.Coverage.Sessions, "the day still happened; only its range is unmeasurable.");
		AreEqual(0m, profile.Movement.MedianDailyRangePercent);
	}

	/// <summary>A series of nothing but zero prices measures to zero everywhere instead of throwing.</summary>
	[TestMethod]
	public void AllZeroPricesMeasureToNothing()
	{
		var profile = _profiler.Measure("AAA", Series(50, 6, (_, _) => new Candle(default, 0m, 0m, 0m, 0m, 0m)));

		AreEqual(300, profile.Coverage.Bars);
		AreEqual(0m, profile.Coverage.MedianVolume);
		AreEqual(0m, profile.Coverage.HighVolumeShare);
		AreEqual(0m, profile.Movement.MedianBarRangePercent);
		AreEqual(0m, profile.Movement.UpperBarRangePercent);
		AreEqual(0m, profile.Movement.MedianDailyRangePercent);
		AreEqual(0m, profile.Movement.AnnualisedVolatilityPercent);
		AreEqual(0m, profile.Persistence.Autocorrelation1);
		AreEqual(1m, profile.Persistence.VarianceRatio5);
		AreEqual(0m, profile.Persistence.DirectionalDayShare);
		AreEqual(0, profile.Edge.BreakoutCount);
		AreEqual(0, profile.Edge.BreakdownCount);
		AreEqual(0, profile.Edge.StretchCount);
		IsTrue(profile.Session.All(b => b.ShareOfRange == 0m && b.ShareOfVolume == 0m), "nothing traded, so nothing can be a share of it.");
	}

	/// <summary>Nothing to look at means no session to find.</summary>
	[TestMethod]
	public void AnEmptySeriesHasNoActiveSession()
	{
		var (open, close) = _profiler.ActiveSession([]);

		AreEqual(TimeSpan.Zero, open);
		AreEqual(TimeSpan.FromHours(24), close);
	}

	/// <summary>Without volume to concentrate, the session is everything the day covers.</summary>
	[TestMethod]
	public void WithoutVolumeTheSessionIsTheWholeSpan()
	{
		var bars = new[] { At(9, 0, 0m), At(10, 0, 0m), At(12, 0, 0m) };

		var (open, close) = _profiler.ActiveSession(bars);

		AreEqual(TimeSpan.FromHours(9), open);
		AreEqual(TimeSpan.FromHours(12), close);
	}

	/// <summary>
	/// The session is the narrowest window carrying nine tenths of the volume, so a lone pre-market print
	/// and a lone evening one stay outside it however far apart they are.
	/// </summary>
	[TestMethod]
	public void TheSessionIsTheNarrowestWindowHoldingTheVolume()
	{
		var bars = new[]
		{
			At(4, 0, 5m),
			At(14, 30, 100m),
			At(15, 30, 100m),
			At(16, 30, 100m),
			At(23, 0, 5m),
		};

		var (open, close) = _profiler.ActiveSession(bars);

		AreEqual(new TimeSpan(14, 30, 0), open);
		AreEqual(new TimeSpan(16, 30, 0), close);
	}

	/// <summary>One time of day is a session of no length, which is what daily bars are.</summary>
	[TestMethod]
	public void OneTimeOfDayIsAnInstantSession()
	{
		var bars = new[] { At(14, 30, 10m), At(14, 30, 20m) };

		var (open, close) = _profiler.ActiveSession(bars);

		AreEqual(new TimeSpan(14, 30, 0), open);
		AreEqual(new TimeSpan(14, 30, 0), close);
	}

	/// <summary>
	/// Half-hourly bars from 09:00 to 16:00 with the volume between 10:00 and 15:00. Ten consecutive
	/// hundreds are the narrowest window reaching nine tenths of 1104, and the first such window ends at
	/// 14:30, which puts the session at 10:00 to 14:30 and every bar in exactly one part of the day.
	/// </summary>
	[TestMethod]
	public void TheDayIsDividedIntoPartsThatEachHoldTheirOwnBars()
	{
		var bars = Series(new DateTime(2025, 1, 6, 9, 0, 0, DateTimeKind.Utc), TimeSpan.FromMinutes(30), 20, 15, (_, bar) =>
		{
			var minute = 9 * 60 + 30 * bar;

			return new Candle(default, 100m, 100.5m, 99.5m, 100m, (minute is >= 600 and <= 900) ? 100m : 1m);
		});

		var profile = _profiler.Measure("AAA", bars);
		var session = profile.Session;

		AreEqual(4, session.Count);
		AreEqual("outside the active session", session[0].Bucket);
		AreEqual("first 30 minutes", session[1].Bucket);
		AreEqual("middle of the session", session[2].Bucket);
		AreEqual("last 30 minutes", session[3].Bucket);

		// Every bar has the same range, so the share of range is the share of bars: five of fifteen
		// outside, then one, seven and two.
		AreEqual(33.3333m, session[0].ShareOfRange);
		AreEqual(6.6667m, session[1].ShareOfRange);
		AreEqual(46.6667m, session[2].ShareOfRange);
		AreEqual(13.3333m, session[3].ShareOfRange);

		// Volume is not spread evenly: outside holds 104 of the 1104, and the rest holds 100, 700 and 200.
		AreEqual(9.4203m, session[0].ShareOfVolume);
		AreEqual(9.0580m, session[1].ShareOfVolume);
		AreEqual(63.4058m, session[2].ShareOfVolume);
		AreEqual(18.1159m, session[3].ShareOfVolume);

		AreEqual(100m, session.Sum(b => b.ShareOfRange), "the parts of the day have to add up to the day.");
		AreEqual(100m, session.Sum(b => b.ShareOfVolume));

		IsTrue(session.All(b => b.AverageReturnPercent == 0m), "every bar closed where it opened.");
		AreEqual(1.0000m, profile.Movement.MedianBarRangePercent);
	}

	/// <summary>
	/// Daily bars all carry the same time of day, so the active session is one instant and the opening
	/// half hour is the whole of it. It cannot also be the closing half hour: a bar counted in two parts
	/// of the day would report two hundred percent of the day's range.
	/// </summary>
	[TestMethod]
	public void AnInstantSessionDoesNotCountTheSameBarTwice()
	{
		var bars = Series(_start, TimeSpan.FromDays(1), 250, 1, (_, _) => new Candle(default, 100m, 101m, 99m, 100m, 10m));

		var session = _profiler.Measure("AAA", bars).Session;

		AreEqual(1, session.Count, $"the day was divided into {session.Count} parts that share their bars.");
		AreEqual("first 30 minutes", session[0].Bucket);
		AreEqual(100m, session[0].ShareOfRange);
		AreEqual(100m, session[0].ShareOfVolume);
		AreEqual(100m, session.Sum(b => b.ShareOfRange), "the parts of the day have to add up to the day.");
		AreEqual(100m, session.Sum(b => b.ShareOfVolume));
	}

	/// <summary>Few days and few bars are both said out loud rather than left for the reader to notice.</summary>
	[TestMethod]
	public void ThinHistoryIsCaveated()
	{
		var profile = _profiler.Measure("AAA", Series(5, 40, (_, _) => Level(100m)));

		AreEqual(2, profile.Caveats.Count);
		IsTrue(profile.Caveats.Any(c => c.Contains("5 trading days", StringComparison.Ordinal)), string.Join(" | ", profile.Caveats));
		IsTrue(profile.Caveats.Any(c => c.Contains("200 bars is a small sample", StringComparison.Ordinal)), string.Join(" | ", profile.Caveats));
	}

	/// <summary>Enough days and enough bars are not caveated, or the caveat would mean nothing.</summary>
	[TestMethod]
	public void ThickHistoryIsNotCaveated()
	{
		var profile = _profiler.Measure("AAA", Series(25, 42, (_, _) => Level(100m)));

		AreEqual(1050, profile.Coverage.Bars);
		AreEqual(25, profile.Coverage.Sessions);
		AreEqual(0, profile.Caveats.Count, string.Join(" | ", profile.Caveats));
	}

	private static Candle Level(decimal price)
		=> new(default, price, price, price, price, 10m);

	private static Candle At(int hour, int minute, decimal volume)
		=> new(new DateTime(2025, 1, 6, hour, minute, 0, DateTimeKind.Utc), 100m, 100m, 100m, 100m, volume);

	private static IReadOnlyList<Candle> Series(int days, int barsPerDay, Func<int, int, Candle> shape)
		=> Series(_start, _frame, days, barsPerDay, shape);

	private static IReadOnlyList<Candle> Series(
		DateTime first,
		TimeSpan step,
		int days,
		int barsPerDay,
		Func<int, int, Candle> shape)
	{
		var bars = new List<Candle>(days * barsPerDay);

		for (var day = 0; day < days; day++)
		{
			for (var bar = 0; bar < barsPerDay; bar++)
				bars.Add(shape(day, bar) with { OpenTime = first.AddDays(day) + step * bar });
		}

		return bars;
	}
}
