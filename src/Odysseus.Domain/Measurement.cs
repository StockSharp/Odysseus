namespace Odysseus.Domain;

using System.Collections.Generic;

/// <summary>
/// A part of the measurement where nothing was traded, and why.
/// </summary>
/// <param name="Slice">Which part it was, in the words the answer uses for it.</param>
/// <param name="Why">What the run found: rules that never triggered, or orders nothing would fill.</param>
/// <remarks>
/// Zero trades and no edge produce the same row of zeros, and they are opposite findings: one says the
/// hypothesis did not describe anything that happened here, the other says it did and the market paid
/// nothing for it. The run works out which; without this the measurement would report the numbers and
/// drop the only sentence that tells them apart.
/// </remarks>
public sealed record SilentSlice(string Slice, string Why);

/// <summary>
/// What a set of runs came to, as numbers.
/// </summary>
/// <param name="Candidate">Candidate the runs were of.</param>
/// <param name="HeldOutSlice">
/// Which part was held back from it: the validation slice on an ordinary pass, the closed slice on the
/// one pass that may be taken against data nobody could tune to.
/// </param>
/// <param name="Development">What it did on the part it was formed on.</param>
/// <param name="HeldOut">What it did on the part it was not.</param>
/// <param name="HeldOutStressed">What it did there again with costs half as high again.</param>
/// <param name="WalkForwardReturns">What it did in consecutive stretches, oldest first.</param>
/// <param name="Silent">Parts where nothing was traded at all, and what the run made of that.</param>
/// <param name="Shape">How much freedom the specification took.</param>
/// <param name="MeasuredAt">When it was measured, in UTC.</param>
/// <remarks>
/// There is no verdict here, and that is the point. The server runs a strategy and reports what came
/// back; whether those numbers are good enough is a judgement, and a judgement belongs to whoever is
/// doing the research rather than to a threshold somebody once wrote into a server.
///
/// What is here is arithmetic over the runs: the spread between windows, the return against the
/// drawdown it cost, the share of the result that survived higher costs. Each is a number a reader
/// would otherwise work out by hand from the same runs.
/// </remarks>
public sealed record Measurement(
	CandidateId Candidate,
	DataSlices HeldOutSlice,
	RunMetrics Development,
	RunMetrics HeldOut,
	RunMetrics HeldOutStressed,
	IReadOnlyList<decimal> WalkForwardReturns,
	IReadOnlyList<SilentSlice> Silent,
	SpecificationShape Shape,
	DateTime MeasuredAt)
{
	/// <summary>What one stretch made on average.</summary>
	public decimal WalkForwardMean
		=> WalkForwardReturns.Count == 0 ? 0m : Sum(WalkForwardReturns) / WalkForwardReturns.Count;

	/// <summary>How far apart the stretches were.</summary>
	/// <remarks>
	/// A candidate whose windows agree has one behaviour; one whose windows disagree has several, and
	/// only one of them was searched for.
	/// </remarks>
	public decimal WalkForwardSpread
	{
		get
		{
			if (WalkForwardReturns.Count < 2)
				return 0m;

			var mean = WalkForwardMean;
			var total = 0m;

			foreach (var value in WalkForwardReturns)
				total += (value - mean) * (value - mean);

			return (decimal)Math.Sqrt((double)(total / WalkForwardReturns.Count));
		}
	}

	/// <summary>How many stretches ended in profit.</summary>
	public int PositiveWindows
	{
		get
		{
			var count = 0;

			foreach (var value in WalkForwardReturns)
			{
				if (value > 0)
					count++;
			}

			return count;
		}
	}

	/// <summary>The return outside the development slice against the drawdown it cost.</summary>
	/// <remarks>Null when there was no drawdown, because there is nothing to weigh the return against.</remarks>
	public decimal? ReturnOverDrawdown
		=> HeldOut.Risk.MaxDrawdownPercent == 0
			? null
			: HeldOut.Net.ReturnPercent / HeldOut.Risk.MaxDrawdownPercent;

	/// <summary>The share of the result that survived costs half again as high.</summary>
	/// <remarks>Null when there was nothing to survive, which is a different thing from surviving none of it.</remarks>
	public decimal? CostResilience
		=> HeldOut.Net.Profit <= 0 ? null : HeldOutStressed.Net.Profit / HeldOut.Net.Profit;

	/// <summary>Rules, indicators and parameters together: how many ways the specification could be tuned.</summary>
	public int Freedom => Shape.Rules + Shape.Indicators + Shape.Parameters;

	private static decimal Sum(IReadOnlyList<decimal> values)
	{
		var total = 0m;

		foreach (var value in values)
			total += value;

		return total;
	}
}

/// <summary>How much freedom a specification took.</summary>
/// <param name="Rules">Entry and exit rules together.</param>
/// <param name="Indicators">Distinct indicators referred to.</param>
/// <param name="Parameters">Numbers declared as searchable.</param>
/// <remarks>
/// Every rule, indicator and number is another way for a search to find something that is not there, so
/// how many there are is worth reporting beside what they produced.
/// </remarks>
public sealed record SpecificationShape(int Rules, int Indicators, int Parameters);
