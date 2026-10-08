namespace StockSharp.Odysseus.Domain;

using System.Collections.Generic;

/// <summary>
/// One run kept beside a finished strategy.
/// </summary>
/// <param name="Id">Identity of the run.</param>
/// <param name="Slice">Part of the history it covered.</param>
/// <param name="Window">Which stretch of that part, or zero for the whole of it.</param>
/// <param name="Symbol">Symbol it traded.</param>
/// <param name="Scenario">Cost scenario it was charged under.</param>
/// <param name="Parameters">Numbers it was run with.</param>
/// <param name="Metrics">What it measured.</param>
/// <param name="BarsProcessed">Candles the strategy saw.</param>
/// <param name="FinishedAt">When it ended, in UTC.</param>
public sealed record CompletedRun(
	RunId Id,
	DataSlices Slice,
	int Window,
	string Symbol,
	string Scenario,
	IReadOnlyDictionary<string, decimal> Parameters,
	RunMetrics Metrics,
	int BarsProcessed,
	DateTime FinishedAt);

/// <summary>
/// A strategy somebody decided was finished, and everything the server knows about it.
/// </summary>
/// <remarks>
/// The one judgement in this product is made outside it: the server runs a strategy and reports what
/// came back, and whether that is worth trading is decided by the person or the model doing the
/// research. This is what is kept once that decision has been made.
///
/// What is kept is everything, because the question asked of a finished strategy months later is never
/// "what did it return" on its own. It is: which code, from which words, on which instrument, with
/// which numbers, over which bars, and what did each run actually say. A folder holding the source and
/// a headline figure answers none of that, and a strategy nobody can check is one nobody can trade.
/// </remarks>
public sealed record CompletedStrategy
{
	/// <summary>Project the research was done in.</summary>
	public required ProjectId Project { get; init; }

	/// <summary>Candidate that was finished.</summary>
	public required CandidateId Candidate { get; init; }

	/// <summary>Specification revision the code was translated from.</summary>
	public required SpecId Spec { get; init; }

	/// <summary>Which revision of the specification that was.</summary>
	public required int SpecRevision { get; init; }

	/// <summary>Name the specification gave the strategy.</summary>
	public required string Name { get; init; }

	/// <summary>The claim about the market the strategy was built to test.</summary>
	public required string Thesis { get; init; }

	/// <summary>Name of the generated class.</summary>
	public required string ClassName { get; init; }

	/// <summary>Hash of the source, which is what makes two candidates the same.</summary>
	public required string SourceHash { get; init; }

	/// <summary>Hash of the compiled assembly.</summary>
	public required string AssemblyHash { get; init; }

	/// <summary>Version of the translator that produced the source.</summary>
	public required string TranslatorVersion { get; init; }

	/// <summary>Instrument the numbers were measured on.</summary>
	public required string Symbol { get; init; }

	/// <summary>Length of one candle.</summary>
	public required TimeSpan TimeFrame { get; init; }

	/// <summary>The numbers the strategy was run with, which are part of what it is.</summary>
	public required IReadOnlyDictionary<string, decimal> Parameters { get; init; }

	/// <summary>Dataset version everything was measured on.</summary>
	public required DatasetId Dataset { get; init; }

	/// <summary>Where the data came from.</summary>
	public required string DatasetSource { get; init; }

	/// <summary>Whether the data was generated rather than observed.</summary>
	public required bool IsSyntheticData { get; init; }

	/// <summary>How the data was divided in time.</summary>
	public required DatasetSplit Split { get; init; }

	/// <summary>Every run behind the result, oldest first.</summary>
	public required IReadOnlyList<CompletedRun> Runs { get; init; }

	/// <summary>What the strategy did on the data it was allowed to see.</summary>
	public required Measurement OnOpenData { get; init; }

	/// <summary>What it did on the data that was closed while it was being formed.</summary>
	public required Measurement OnClosedData { get; init; }

	/// <summary>How many times the open data was asked before the candidate was brought to the closed slice.</summary>
	public required int TimesMeasuredOnOpenData { get; init; }

	/// <summary>Why whoever was doing the research decided this was finished.</summary>
	/// <remarks>
	/// The server cannot record the judgement itself, because it does not make it. This sentence is the
	/// only part of that decision it can keep, which is why it is not optional.
	/// </remarks>
	public required string Notes { get; init; }

	/// <summary>Who declared it finished.</summary>
	public required Actors CompletedBy { get; init; }

	/// <summary>When it was declared finished, in UTC.</summary>
	public required DateTime CompletedAt { get; init; }
}
