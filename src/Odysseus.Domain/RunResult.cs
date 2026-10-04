namespace Odysseus.Domain;

using System;
using System.Collections.Generic;

/// <summary>
/// How a run ended.
/// </summary>
public enum RunStatuses
{
	/// <summary>The run finished and was measured.</summary>
	Completed,

	/// <summary>The candidate stopped the run: it threw, or it would not stop and was stopped.</summary>
	Failed,

	/// <summary>
	/// The machinery around the candidate stopped the run, so the candidate never answered.
	/// </summary>
	/// <remarks>
	/// Recorded and not charged. It is kept because an attempt that leaves no trace is an attempt nobody
	/// can count, and it is kept apart from <see cref="Failed"/> because the two are opposite findings:
	/// one says something about the strategy and the other says something about this server.
	/// </remarks>
	Interrupted,
}

/// <summary>
/// One run of one candidate over one slice under one set of costs.
/// </summary>
/// <param name="Id">Identity of the run.</param>
/// <param name="Candidate">Candidate that was run.</param>
/// <param name="Dataset">Dataset version the bars came from.</param>
/// <param name="Slice">Part of the history the run covered.</param>
/// <param name="Window">
/// Which window of that part, when the slice was cut further. Zero means the whole of it. Walk-forward
/// asks the same question of consecutive windows, and a candidate that only works in one of them has
/// been found by the search rather than by the market.
/// </param>
/// <param name="Symbol">Symbol that was traded.</param>
/// <param name="Scenario">Name of the cost scenario the run was charged under.</param>
/// <param name="Fingerprint">
/// What makes two runs the same run: the candidate, the data, the slice and the costs. A second
/// request carrying the same fingerprint is answered with the first result rather than re-run, which
/// is what keeps an agent exploring rather than repeating.
/// </param>
/// <param name="Parameters">
/// The numbers the strategy was run with. Without them a result cannot be reproduced or deployed: the
/// same candidate run with different numbers is a different strategy, and the fingerprint that keeps
/// two such runs apart cannot be read back into the numbers behind it.
/// </param>
/// <param name="Status">How it ended.</param>
/// <param name="Metrics">What it measured, when it completed.</param>
/// <param name="Trades">Artifact holding the trade list.</param>
/// <param name="Equity">Artifact holding the equity curve.</param>
/// <param name="BarsProcessed">Candles the strategy saw.</param>
/// <param name="StartedAt">When it started, in UTC.</param>
/// <param name="FinishedAt">When it ended, in UTC.</param>
/// <param name="Error">Why it failed, when it failed.</param>
/// <param name="Diagnosis">
/// Why it measured nothing, when it measured nothing. A run with no trades reports the same empty
/// numbers whether the rules never triggered or the orders were too large for the bars to fill, and
/// those are opposite findings.
/// </param>
public sealed record RunResult(
	RunId Id,
	CandidateId Candidate,
	DatasetId Dataset,
	DataSlices Slice,
	int Window,
	string Symbol,
	string Scenario,
	string Fingerprint,
	IReadOnlyDictionary<string, decimal> Parameters,
	RunStatuses Status,
	RunMetrics Metrics,
	ArtifactId Trades,
	ArtifactId Equity,
	int BarsProcessed,
	DateTime StartedAt,
	DateTime FinishedAt,
	string Error,
	string Diagnosis)
{
	/// <summary>How long the run took.</summary>
	public TimeSpan Elapsed => FinishedAt - StartedAt;
}
