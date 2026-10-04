namespace Odysseus.Application;

/// <summary>How much, and how violently, the instrument moves.</summary>
/// <param name="MedianBarRangePercent">Middle bar's high-to-low range, as a percentage of its close.</param>
/// <param name="UpperBarRangePercent">Range of the widest one bar in ten, as a percentage.</param>
/// <param name="MedianDailyRangePercent">Middle day's high-to-low range, as a percentage.</param>
/// <param name="AnnualisedVolatilityPercent">Standard deviation of daily returns, annualised.</param>
public sealed record MovementProfile(
	decimal MedianBarRangePercent,
	decimal UpperBarRangePercent,
	decimal MedianDailyRangePercent,
	decimal AnnualisedVolatilityPercent);

/// <summary>Whether moves tend to continue or to come back.</summary>
/// <param name="Autocorrelation1">Correlation between a bar's return and the next one's.</param>
/// <param name="Autocorrelation5">The same over five-bar returns.</param>
/// <param name="VarianceRatio5">Variance of five-bar returns against five times the one-bar variance.</param>
/// <param name="VarianceRatio20">The same over twenty bars.</param>
/// <param name="DirectionalDayShare">Share of days that closed near their extreme rather than mid-range.</param>
/// <remarks>
/// The variance ratios are the load-bearing numbers. Above one, moves extend and a breakout has
/// something to work with; below one, they come back and it does not. Around one the instrument is
/// telling you it has no memory at this horizon, which is worth knowing before spending a budget
/// looking for one.
/// </remarks>
public sealed record PersistenceProfile(
	decimal Autocorrelation1,
	decimal Autocorrelation5,
	decimal VarianceRatio5,
	decimal VarianceRatio20,
	decimal DirectionalDayShare);

/// <summary>What actually happened after the events a strategy would trade.</summary>
/// <param name="BreakoutCount">Bars that closed above the prior twenty-bar high.</param>
/// <param name="BreakoutFollowThroughPercent">Average return over the five bars after such a close.</param>
/// <param name="BreakoutPositiveShare">Share of those that were still positive five bars later.</param>
/// <param name="BreakdownCount">Bars that closed below the prior twenty-bar low.</param>
/// <param name="BreakdownFollowThroughPercent">Average return over the five bars after such a close.</param>
/// <param name="StretchCount">Bars that closed more than two standard deviations from the mean.</param>
/// <param name="StretchReversionPercent">Average return over the five bars after such a close, signed towards the mean.</param>
/// <remarks>
/// This section exists because the ratios above are summaries and this is the event itself. A hypothesis
/// that breakouts continue can be checked against the number of breakouts there actually were and what
/// followed them, before a line of code is written.
/// </remarks>
public sealed record EdgeProfile(
	int BreakoutCount,
	decimal BreakoutFollowThroughPercent,
	decimal BreakoutPositiveShare,
	int BreakdownCount,
	decimal BreakdownFollowThroughPercent,
	int StretchCount,
	decimal StretchReversionPercent);

/// <summary>How the day is shaped.</summary>
/// <param name="Bucket">Part of the session.</param>
/// <param name="ShareOfRange">Share of the day's range that happens here.</param>
/// <param name="ShareOfVolume">Share of the day's volume that happens here.</param>
/// <param name="AverageReturnPercent">Average return across this part of the session.</param>
public sealed record SessionBucket(string Bucket, decimal ShareOfRange, decimal ShareOfVolume, decimal AverageReturnPercent);

/// <summary>What the data covers.</summary>
/// <param name="Symbol">Instrument measured.</param>
/// <param name="Bars">Bars the measurements are based on.</param>
/// <param name="From">First bar, in UTC.</param>
/// <param name="To">Last bar, in UTC.</param>
/// <param name="Sessions">Distinct trading days covered.</param>
/// <param name="MedianVolume">Middle bar's volume.</param>
/// <param name="HighVolumeShare">Share of bars whose volume exceeded twice the recent average.</param>
public sealed record CoverageProfile(
	string Symbol,
	int Bars,
	DateTime From,
	DateTime To,
	int Sessions,
	decimal MedianVolume,
	decimal HighVolumeShare);

/// <summary>
/// What was measured in one instrument's history.
/// </summary>
/// <param name="Coverage">What the measurements are based on.</param>
/// <param name="Movement">How much it moves.</param>
/// <param name="Persistence">Whether moves continue or come back.</param>
/// <param name="Edge">What followed the events a strategy would trade.</param>
/// <param name="Session">How the day is shaped.</param>
/// <param name="Caveats">Anything that limits how far these numbers can be trusted.</param>
public sealed record MarketProfile(
	CoverageProfile Coverage,
	MovementProfile Movement,
	PersistenceProfile Persistence,
	EdgeProfile Edge,
	IReadOnlyList<SessionBucket> Session,
	IReadOnlyList<string> Caveats);

/// <summary>
/// Measures what an instrument's history actually contains.
/// </summary>
/// <remarks>
/// This exists so that a hypothesis starts from the instrument rather than from a template. Asked for a
/// strategy with nothing else to go on, any agent will propose a breakout, because breakouts are what
/// the literature is full of — regardless of whether this instrument extends its moves or gives them
/// straight back. Handing over measured numbers first turns the first proposal from a guess into a
/// response to evidence.
///
/// Nothing here recommends a strategy. These are measurements, and the reasoning is the agent's.
/// </remarks>
public interface IMarketProfiler
{
	/// <summary>Fewest bars worth measuring anything on.</summary>
	int MinimumBars { get; }

	/// <summary>
	/// Measures one instrument.
	/// </summary>
	/// <param name="symbol">Instrument being measured.</param>
	/// <param name="candles">Bars of the development slice, oldest first.</param>
	/// <returns>What was measured.</returns>
	/// <exception cref="ArgumentException">There are too few bars for the measurements to mean anything.</exception>
	MarketProfile Measure(string symbol, IReadOnlyList<Candle> candles);

	/// <summary>
	/// Finds the part of the day the instrument actually trades in: the narrowest window of the day holding
	/// nine tenths of its volume.
	/// </summary>
	/// <param name="candles">Bars to look at.</param>
	/// <returns>Start and end of the active session, as times of day in UTC.</returns>
	(TimeSpan Open, TimeSpan Close) ActiveSession(IReadOnlyList<Candle> candles);
}
