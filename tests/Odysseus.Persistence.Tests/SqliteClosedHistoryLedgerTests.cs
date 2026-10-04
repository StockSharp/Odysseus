namespace Odysseus.Persistence.Tests;

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Odysseus.Domain;
using Odysseus.Persistence;
using Odysseus.TestKit;

/// <summary>
/// Remembering which closed history has been spent.
/// </summary>
/// <remarks>
/// The closed slice is the one measurement in the product that cannot be taken twice, and a project is
/// the wrong thing to remember that in: a second project over the same dates is a second sitting of the
/// same exam by someone who has read the paper. This is the record that survives the project.
/// </remarks>
[TestClass]
public class SqliteClosedHistoryLedgerTests : OdysseusTestBase
{
	private static readonly DateTime _may = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);

	private string _root;
	private SqliteClosedHistoryLedger _ledger;

	/// <summary>Builds a ledger over temporary storage.</summary>
	[TestInitialize]
	public void CreateLedger()
	{
		_root = Path.Combine(Path.GetTempPath(), "odysseus-tests", Guid.NewGuid().ToString("n"));
		_ledger = new SqliteClosedHistoryLedger(_root);
	}

	/// <summary>Releases storage.</summary>
	[TestCleanup]
	public void DeleteLedger()
	{
		_ledger?.Dispose();

		if (Directory.Exists(_root))
			Directory.Delete(_root, recursive: true);
	}

	/// <summary>Nothing spent, nothing found.</summary>
	[TestMethod]
	public async Task AnUntouchedStretchIsFree()
	{
		var found = await _ledger.FindOverlappingAsync("NVDA", _may, _may.AddDays(30), CancellationToken);

		AreEqual(0, found.Count);
	}

	/// <summary>The same stretch asked for twice is found the second time, with who spent it.</summary>
	[TestMethod]
	public async Task ASpentStretchIsFoundAgain()
	{
		var spent = Window("NVDA", _may, _may.AddDays(30));

		await _ledger.ClaimAsync(spent, CancellationToken);

		var found = await _ledger.FindOverlappingAsync("NVDA", _may, _may.AddDays(30), CancellationToken);

		AreEqual(1, found.Count);
		AreEqual(spent.Project, found[0].Project);
		AreEqual(spent.Candidate, found[0].Candidate);
		AreEqual(spent.From, found[0].From);
		AreEqual(spent.To, found[0].To);
	}

	/// <summary>
	/// A stretch that shares a single day with a spent one is found. Importing a day more or a day less
	/// is the obvious way round a rule that only recognised an exact match, and it would work.
	/// </summary>
	[TestMethod]
	public async Task AStretchThatMerelyOverlapsIsFound()
	{
		await _ledger.ClaimAsync(Window("NVDA", _may, _may.AddDays(30)), CancellationToken);

		var later = await _ledger.FindOverlappingAsync("NVDA", _may.AddDays(29), _may.AddDays(60), CancellationToken);
		var earlier = await _ledger.FindOverlappingAsync("NVDA", _may.AddDays(-30), _may.AddDays(1), CancellationToken);
		var inside = await _ledger.FindOverlappingAsync("NVDA", _may.AddDays(10), _may.AddDays(20), CancellationToken);
		var around = await _ledger.FindOverlappingAsync("NVDA", _may.AddDays(-10), _may.AddDays(40), CancellationToken);

		IsTrue(later.Count == 1, "a stretch starting inside a spent one was treated as untouched.");
		IsTrue(earlier.Count == 1, "a stretch ending inside a spent one was treated as untouched.");
		IsTrue(inside.Count == 1, "a stretch inside a spent one was treated as untouched.");
		IsTrue(around.Count == 1, "a stretch containing a spent one was treated as untouched.");
	}

	/// <summary>Stretches that merely touch end to end do not overlap: the end is exclusive.</summary>
	[TestMethod]
	public async Task StretchesThatOnlyTouchAreSeparate()
	{
		await _ledger.ClaimAsync(Window("NVDA", _may, _may.AddDays(30)), CancellationToken);

		var after = await _ledger.FindOverlappingAsync("NVDA", _may.AddDays(30), _may.AddDays(60), CancellationToken);
		var before = await _ledger.FindOverlappingAsync("NVDA", _may.AddDays(-30), _may, CancellationToken);

		AreEqual(0, after.Count, "the stretch starting where the spent one ended was refused.");
		AreEqual(0, before.Count, "the stretch ending where the spent one began was refused.");
	}

	/// <summary>
	/// An entry that comes back knows which stretch it is, exactly, and not merely which it touches.
	/// </summary>
	/// <remarks>
	/// One caller needs more than overlap. An interrupted measurement is allowed to finish because the
	/// entry it left describes the very stretch it is finishing, and that permission is the only way
	/// past the ledger - so it is granted on exact equality rather than on overlap, and an entry against
	/// a stretch that has moved does not get it. Equality has to survive storage to be worth anything:
	/// the moments are kept as text and parsed back, and a round trip that lost a tick would refuse
	/// every interrupted measurement while quietly still admitting the stale ones by overlap.
	/// </remarks>
	[TestMethod]
	public async Task AnEntryKnowsExactlyWhichStretchItIs()
	{
		var from = _may.AddMinutes(7).AddSeconds(31).AddTicks(1234567);
		var to = from.AddDays(30);

		await _ledger.ClaimAsync(Window("NVDA", from, to), CancellationToken);

		var stored = (await _ledger.FindOverlappingAsync("NVDA", from, to, CancellationToken)).Single();

		IsTrue(stored.IsSameStretch("NVDA", from, to),
			"a stretch did not survive being written down, so no interrupted measurement can be finished.");

		IsFalse(stored.IsSameStretch("NVDA", from, to.AddMinutes(-5)),
			"a stretch ending five minutes earlier passed as the same one.");

		IsFalse(stored.IsSameStretch("NVDA", from.AddMinutes(5), to),
			"a stretch starting five minutes later passed as the same one.");

		IsTrue(stored.Overlaps("NVDA", from.AddMinutes(5), to.AddMinutes(5)),
			"a stretch that has merely moved must still overlap, or the ledger stops guarding it at all.");
	}

	/// <summary>Another instrument over the same dates is another exam.</summary>
	[TestMethod]
	public async Task AnotherSymbolOverTheSameDatesIsFree()
	{
		await _ledger.ClaimAsync(Window("NVDA", _may, _may.AddDays(30)), CancellationToken);

		var found = await _ledger.FindOverlappingAsync("AAPL", _may, _may.AddDays(30), CancellationToken);

		AreEqual(0, found.Count);
	}

	/// <summary>A symbol is the same symbol however it was typed.</summary>
	[TestMethod]
	public async Task CaseDoesNotMakeANewExam()
	{
		await _ledger.ClaimAsync(Window("NVDA", _may, _may.AddDays(30)), CancellationToken);

		var found = await _ledger.FindOverlappingAsync("nvda", _may, _may.AddDays(30), CancellationToken);

		AreEqual(1, found.Count, "the same symbol in another case was treated as a different instrument.");
	}

	/// <summary>The record outlives the process that wrote it, or it protects nothing.</summary>
	[TestMethod]
	public async Task WhatWasSpentSurvivesAReopening()
	{
		await _ledger.ClaimAsync(Window("NVDA", _may, _may.AddDays(30)), CancellationToken);

		_ledger.Dispose();
		_ledger = new SqliteClosedHistoryLedger(_root);

		var found = await _ledger.FindOverlappingAsync("NVDA", _may, _may.AddDays(30), CancellationToken);

		AreEqual(1, found.Count, "reopening the ledger forgot what had been spent.");
	}

	/// <summary>Everything that overlaps comes back, newest first.</summary>
	[TestMethod]
	public async Task EverythingThatOverlapsComesBack()
	{
		await _ledger.ClaimAsync(Window("NVDA", _may, _may.AddDays(10)), CancellationToken);
		await _ledger.ClaimAsync(Window("NVDA", _may.AddDays(20), _may.AddDays(30)), CancellationToken);

		var found = await _ledger.FindOverlappingAsync("NVDA", _may.AddDays(5), _may.AddDays(25), CancellationToken);

		AreEqual(2, found.Count);
		IsTrue(found[0].From > found[1].From, "the newest was not first.");
	}

	/// <summary>A claim over a stretch someone else spent is refused, names who, and writes nothing.</summary>
	[TestMethod]
	public async Task AClaimOverASpentStretchIsRefused()
	{
		var first = Window("NVDA", _may, _may.AddDays(30));

		AreEqual(0, (await _ledger.ClaimAsync(first, CancellationToken)).Count, "an untouched stretch was refused.");

		var refused = await _ledger.ClaimAsync(Window("NVDA", _may.AddDays(10), _may.AddDays(40)), CancellationToken);

		AreEqual(1, refused.Count, "an overlapping claim by someone else was granted.");
		AreEqual(first.Candidate, refused[0].Candidate);

		var found = await _ledger.FindOverlappingAsync("NVDA", _may, _may.AddDays(40), CancellationToken);

		AreEqual(1, found.Count, "the refused claim was written down as spent anyway.");
	}

	/// <summary>
	/// The same candidate claiming exactly the same stretch again is the same spending, so an interrupted
	/// measurement can finish without writing a second entry.
	/// </summary>
	[TestMethod]
	public async Task ClaimingTheSameStretchAgainIsTheSameSpending()
	{
		var spent = Window("NVDA", _may, _may.AddDays(30));

		await _ledger.ClaimAsync(spent, CancellationToken);

		var again = await _ledger.ClaimAsync(spent with { SpentAt = DateTime.UtcNow }, CancellationToken);

		AreEqual(0, again.Count, "the candidate's own claim on the stretch was held against it.");

		var found = await _ledger.FindOverlappingAsync("NVDA", _may, _may.AddDays(30), CancellationToken);

		AreEqual(1, found.Count, "repeating the claim recorded the stretch twice.");
	}

	/// <summary>
	/// Claims made at once, through two ledgers over the same file the way the server and the CLI open it,
	/// grant the stretch once. Checked and written in two steps, every one of them could pass the check
	/// before any wrote.
	/// </summary>
	[TestMethod]
	public async Task ClaimsMadeAtOnceGrantTheStretchOnce()
	{
		using var other = new SqliteClosedHistoryLedger(_root);

		var claims = Enumerable
			.Range(0, 16)
			.Select(i => (i % 2 == 0 ? _ledger : other).ClaimAsync(Window("NVDA", _may, _may.AddDays(30)), CancellationToken).AsTask());

		var answers = await Task.WhenAll(claims);

		AreEqual(1, answers.Count(a => a.Count == 0), "the same stretch was granted to more than one candidate.");

		var found = await _ledger.FindOverlappingAsync("NVDA", _may, _may.AddDays(30), CancellationToken);

		AreEqual(1, found.Count, "more than one spending of the stretch was written down.");
	}

	private static SpentWindow Window(string symbol, DateTime from, DateTime to)
		=> new(
			ProjectId.New(),
			CandidateId.New(),
			symbol,
			TimeSpan.FromMinutes(5),
			from,
			to,
			DateTime.UtcNow);
}
