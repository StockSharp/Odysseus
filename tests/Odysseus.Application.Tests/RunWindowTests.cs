namespace Odysseus.Application.Tests;

/// <summary>
/// Cutting a slice into the consecutive stretches walk-forward measures apart.
/// </summary>
/// <remarks>
/// Walk-forward's whole claim is that the stretches are different stretches. A cut that handed back the
/// same bars three times would produce three runs, three numbers and a spread of zero between them,
/// and every one of those numbers would be a real measurement of a real run — of the same history,
/// three times over. Nothing downstream can tell that apart from a candidate that behaved consistently,
/// so the tiling is asserted here or nowhere.
/// </remarks>
[TestClass]
public class RunWindowTests : OdysseusTestBase
{
	/// <summary>
	/// Twelve bars cut into three parts are four bars each, and the three laid end to end are the twelve
	/// again — in the order they arrived, no bar in two parts and none in none.
	/// </summary>
	[TestMethod]
	public void ThePartsCoverTheSliceOnceEachAndInOrder()
	{
		var bars = Bars(12);

		var first = new RunWindow(1, 3).Cut(bars);
		var second = new RunWindow(2, 3).Cut(bars);
		var third = new RunWindow(3, 3).Cut(bars);

		AreEqual("1,2,3,4", Join(first));
		AreEqual("5,6,7,8", Join(second));
		AreEqual("9,10,11,12", Join(third));

		AreEqual(Join(bars), Join([.. first, .. second, .. third]),
			"the parts of the slice did not lay back down as the slice: a bar was dropped, repeated or " +
			"moved, and each part would still have been measured as if it were a stretch of its own.");
	}

	/// <summary>
	/// Ten bars do not divide into three, so the parts are three, three and four: the last takes what
	/// the division left rather than leaving the newest bar out of the walk-forward altogether.
	/// </summary>
	[TestMethod]
	public void TheLastPartTakesWhatTheDivisionLeftOver()
	{
		var bars = Bars(10);

		var parts = new[]
		{
			new RunWindow(1, 3).Cut(bars),
			new RunWindow(2, 3).Cut(bars),
			new RunWindow(3, 3).Cut(bars),
		};

		AreEqual(3, parts[0].Count);
		AreEqual(3, parts[1].Count);
		AreEqual(4, parts[2].Count, "the remainder of the division was left out of every part.");
		AreEqual("8,9,10", Join([.. parts[2].Skip(1)]));

		AreEqual(Join(bars), Join([.. parts.SelectMany(p => p)]),
			"the parts of an uneven slice did not lay back down as the slice.");
	}

	/// <summary>The whole slice is every bar of it, in the order they arrived.</summary>
	[TestMethod]
	public void TheWholeSliceIsEveryBarOfIt()
	{
		var bars = Bars(10);

		AreEqual(Join(bars), Join(RunWindow.Whole.Cut(bars)));
	}

	/// <summary>
	/// Window zero is the whole slice, and a slice cut into three parts has no whole among them. Asking
	/// for it is a caller that counted its windows from zero, and the only harmless-looking answer — the
	/// whole slice — is the one that turns three stretches into three copies of one.
	/// </summary>
	[TestMethod]
	public void TheWholeSliceIsNotOneOfThePartsItIsCutInto()
	{
		var bars = Bars(12);

		var error = Throws<ArgumentOutOfRangeException>(() => new RunWindow(0, 3).Cut(bars),
			"a window that asked for part zero of three was handed the whole slice, which is what a " +
			"walk-forward measuring one stretch three times over looks like from the outside.");

		IsTrue(error.Message.Contains("3", StringComparison.Ordinal),
			$"the refusal does not say how many parts there were: {error.Message}");
	}

	/// <summary>There is no fourth part of three, and a run over one would be a run over nothing.</summary>
	[TestMethod]
	public void APartPastTheEndIsRefused()
	{
		var bars = Bars(12);

		Throws<ArgumentOutOfRangeException>(() => new RunWindow(4, 3).Cut(bars));
	}

	private static IReadOnlyList<int> Bars(int count)
		=> [.. Enumerable.Range(1, count)];

	private static string Join(IReadOnlyList<int> bars)
		=> string.Join(",", bars);
}
