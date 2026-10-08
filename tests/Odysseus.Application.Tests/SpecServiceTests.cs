namespace StockSharp.Odysseus.Application.Tests;

using StockSharp.Odysseus.Persistence;
using StockSharp.Odysseus.Platform;
using StockSharp.Odysseus.Spec;

/// <summary>
/// Checking a specification, and recording one as a project's next revision.
/// </summary>
/// <remarks>
/// Checking is the tool an agent uses most, and the promise it makes is that everything wrong comes
/// back at once, in one shape, each with a place and a remedy. A document the reader cannot make sense
/// of has to arrive in that same shape: an agent handed a raw parser failure instead of a problem list
/// has been told that this server broke, when what happened is that its draft was wrong.
///
/// Recording makes a second promise, which is that nothing invalid and nothing unmeasurable becomes
/// part of the history of what was tried.
/// </remarks>
[TestClass]
public class SpecServiceTests : OdysseusTestBase
{
	private static readonly DateTime _open = new(2026, 3, 2, 14, 30, 0, DateTimeKind.Utc);

	private const string Thesis = "A price above its own recent average keeps going for a few bars.";

	private const string TimeExit =
		"""{ "id": "x1", "kind": "TimeExit", "direction": "Long", "length": { "kind": "Constant", "value": 5 } }""";

	private string _root;
	private SqliteProjectStore _store;
	private SqliteOperationLog _operations;
	private FileDatasetStore _datasets;
	private FileSpecStore _specs;
	private ProjectService _projects;
	private SpecService _service;

	/// <summary>A specification with nothing wrong with it.</summary>
	private static string Sound => Document(Thesis, Compare(Indicator(""", "source": "Close" """)), TimeExit);

	/// <summary>The same strategy, argued for in different words.</summary>
	private static string Reworded => Document(
		"Momentum above a moving average persists over a handful of candles.",
		Compare(Indicator(""", "source": "Close" """)),
		TimeExit);

	/// <summary>A specification that opens positions and never closes them.</summary>
	private static string WithNothingToCloseAPosition
		=> Document(Thesis, Compare(Indicator(""", "source": "Close" """)), string.Empty);

	/// <summary>Builds the services over temporary storage.</summary>
	[TestInitialize]
	public void CreateService()
	{
		_root = Path.Combine(Path.GetTempPath(), "odysseus-tests", Guid.NewGuid().ToString("n"));
		_store = new SqliteProjectStore(_root);
		_operations = new SqliteOperationLog(_root);
		_datasets = new FileDatasetStore(_root, new LocalBarStorage(Path.Combine(_root, "market-data")));
		_specs = new FileSpecStore(_root);

		var clock = new SystemClock();
		var budget = new ResearchBudget(400, 40, TimeSpan.FromMinutes(45)).ToState();

		_projects = new ProjectService(_store, _store, _operations, clock, ServerModes.Local, budget);
		_service = new SpecService(_store, _specs, _store, _operations, clock);
	}

	/// <summary>Releases storage.</summary>
	[TestCleanup]
	public void DeleteService()
	{
		_store?.Dispose();
		_operations?.Dispose();

		if (Directory.Exists(_root))
			Directory.Delete(_root, recursive: true);
	}

	/// <summary>
	/// A document the reader cannot make sense of comes back as a problem about the document, in the
	/// same shape as a missing exit, rather than as a failure of this server.
	/// </summary>
	/// <remarks>
	/// Each of these is refused by the published schema, so none of them is a document an agent that
	/// read the schema would send - which is exactly why the refusal has to be worth reading. Several
	/// of them used to leave the reader as an <c>InvalidOperationException</c>, a <c>FormatException</c>
	/// or an <c>ArgumentException</c>, none of which is a <c>JsonException</c>, so none of them reached
	/// the arm that turns an unreadable document into a problem list.
	/// </remarks>
	[TestMethod]
	public void EveryWayADocumentCanBeUnreadableIsAnsweredTheSameWay()
	{
		foreach (var (what, json) in Unreadable())
		{
			var check = _service.Check(json);

			IsFalse(check.IsValid, $"{what} was accepted as a specification.");

			AreEqual(1, check.Problems.Count,
				$"{what} produced {check.Problems.Count} problems; a document that will not read has one.");

			var problem = check.Problems[0];

			AreEqual("(document)", problem.Path,
				$"{what} was reported against '{problem.Path}', which is not a place in a document that has no places yet.");

			IsFalse(string.IsNullOrWhiteSpace(problem.Message), $"{what} was refused without saying what is wrong.");
			IsFalse(string.IsNullOrWhiteSpace(problem.Remedy), $"{what} was refused without saying what would fix it.");

			AreEqual(0, check.RequiredWarmup,
				$"{what} was given a warm-up of {check.RequiredWarmup} candles, which cannot be measured on rules nobody could read.");
		}
	}

	/// <summary>
	/// A candle field written as a number is refused rather than taken as a field that does not exist.
	/// </summary>
	/// <remarks>
	/// Separate from the sweep above because this one was not a crash: <c>Enum.Parse</c> accepts the
	/// decimal form of a value, defined or not, so <c>"source": "99"</c> read back as a candle field
	/// numbered ninety-nine, passed every check, and went to the translator as a strategy nobody wrote.
	/// </remarks>
	[TestMethod]
	public void ACandleFieldOutsideTheOnesThatExistIsNotInvented()
	{
		var check = _service.Check(Document(Thesis, Compare(Indicator(""", "source": "99" """)), TimeExit));

		IsFalse(check.IsValid, "an indicator was allowed to read candle field number ninety-nine.");

		AreEqual("(document)", check.Problems[0].Path,
			"a field that does not exist was reported as something other than the document being unreadable.");

		IsTrue(check.Problems[0].Message.Contains("Close", StringComparison.Ordinal),
			$"the refusal does not say which fields exist: {check.Problems[0].Message}");
	}

	/// <summary>Checking says how much warm-up the rules need, not only whether they are legal.</summary>
	/// <remarks>
	/// Worked by hand from the rules of <see cref="Sound"/>: the entry reads a twenty-candle average,
	/// this candle's close needs one, and the exit counts five candles after an entry rather than
	/// before it. The longest of those is twenty.
	/// </remarks>
	[TestMethod]
	public void CheckingSaysHowManyCandlesMustPassBeforeTheRulesCanAct()
	{
		var check = _service.Check(Sound);

		IsTrue(check.IsValid, $"the specification these tests are built on was refused: {Describe(check)}");
		AreEqual(20, check.RequiredWarmup, "the warm-up the rules need was not measured from the rules.");
	}

	/// <summary>
	/// A specification that is recorded comes back describing the same strategy that was sent.
	/// </summary>
	[TestMethod]
	public async Task AValidSpecificationSurvivesBeingRecordedAndReadBack()
	{
		var project = await ProjectWithDataAsync("round trip");

		var revision = await _service.ProposeAsync(project, Sound, "propose-1", Actors.Agent, CancellationToken);

		AreEqual(1, revision.Revision, "the first specification of a project was not recorded as its first revision.");
		AreEqual(Actors.Agent, revision.Author, "the specification was recorded against somebody else.");

		var read = await _service.GetAsync(project, revision.Id, CancellationToken);

		AreEqual(Sound, read.Json, "the specification did not come back as it was sent.");
		AreEqual(revision.Hash, read.Hash, "the recorded text and the returned text hash differently.");

		// Compared as a strategy rather than as text. A candidate is built from the tree, so text that
		// survives storage and no longer reads as the same rules has not made the trip that matters.
		AreEqual(
			SpecJson.Write(SpecJson.Read(Sound)),
			SpecJson.Write(SpecJson.Read(read.Json)),
			"the recorded specification describes a different strategy from the one proposed.");

		var trail = await _projects.ReadAuditAsync(project, CancellationToken);

		IsTrue(trail.Any(e => e.Type == AuditEventTypes.SpecRevised && e.PayloadHash == revision.Hash),
			"recording a specification left nothing in the trail to tie a later candidate back to it.");
	}

	/// <summary>
	/// A change is a new revision, and the words the earlier one was written in are still there.
	/// </summary>
	[TestMethod]
	public async Task AChangeIsANewRevisionAndTheOneItReplacedStillReads()
	{
		var project = await ProjectWithDataAsync("revisions");

		var first = await _service.ProposeAsync(project, Sound, "propose-1", Actors.Agent, CancellationToken);
		var second = await _service.ProposeAsync(project, Reworded, "propose-2", Actors.Agent, CancellationToken);

		AreNotEqual(first.Id, second.Id, "a reworded specification replaced the one before it.");
		AreEqual(2, second.Revision, "the second specification of a project is not its second revision.");
		AreNotEqual(first.Hash, second.Hash, "two specifications with different words share a hash.");

		AreEqual(Sound, (await _service.GetAsync(project, first.Id, CancellationToken)).Json,
			"recording a change rewrote the specification it followed.");

		var listed = await _service.ListAsync(project, CancellationToken);

		AreEqual(2, listed.Count, $"the project lists {listed.Count} specifications after two were recorded.");
		AreEqual(first.Id, listed[0].Id, "the revisions do not come back oldest first.");
	}

	/// <summary>
	/// A project with no data refuses a proposal, and the refusal says how to get some.
	/// </summary>
	/// <remarks>
	/// Nothing proposed against an empty project could be measured, so recording it would put a
	/// specification into the history of what was tried when nothing was tried. The refusal must also
	/// leave the request unanswered: the caller is being told to import data and ask again, and a key
	/// already spent would hand it back this refusal instead of recording the specification.
	/// </remarks>
	[TestMethod]
	public async Task AProjectWithNoDataRefusesAProposalWithoutSpendingTheRequest()
	{
		var created = await _projects.CreateProjectAsync("no data", "new-1", Actors.User, CancellationToken);

		var refusal = await ThrowsAsync<InvalidOperationException>(
			() => _service.ProposeAsync(created.Id, Sound, "propose-1", Actors.Agent, CancellationToken).AsTask());

		IsTrue(refusal.Message.Contains("import_demo_dataset", StringComparison.Ordinal),
			$"the refusal does not say how to get data into the project: {refusal.Message}");

		AreEqual(0, (await _service.ListAsync(created.Id, CancellationToken)).Count,
			"a specification that could not be measured was recorded anyway.");

		await ImportAsync(created.Id);

		var revision = await _service.ProposeAsync(created.Id, Sound, "propose-1", Actors.Agent, CancellationToken);

		AreEqual(1, revision.Revision,
			"the refused request took the key with it, so asking again after importing data was answered with the refusal.");
	}

	/// <summary>
	/// An invalid specification is refused with every problem at once and is not recorded.
	/// </summary>
	[TestMethod]
	public async Task AnInvalidSpecificationIsNeverPartOfTheHistory()
	{
		var project = await ProjectWithDataAsync("invalid");

		var refusal = await ThrowsAsync<InvalidSpecException>(
			() => _service.ProposeAsync(project, WithNothingToCloseAPosition, "propose-1", Actors.Agent, CancellationToken).AsTask());

		IsTrue(refusal.Problems.Count >= 2,
			$"a specification that never closes a position produced {refusal.Problems.Count} problem(s); " +
			"the missing exit and the entry it strands are both wrong with it.");

		IsTrue(refusal.Problems.All(p => !string.IsNullOrWhiteSpace(p.Path) && !string.IsNullOrWhiteSpace(p.Remedy)),
			"a problem was reported without a place or without a remedy.");

		foreach (var problem in refusal.Problems)
		{
			IsTrue(refusal.Message.Contains(problem.Path, StringComparison.Ordinal),
				$"'{problem.Path}' is in the problem list and not in the message, so a caller reading only the " +
				"message would correct one problem per attempt.");
		}

		AreEqual(0, (await _service.ListAsync(project, CancellationToken)).Count,
			"a refused specification was recorded as a revision.");

		IsFalse((await _projects.ReadAuditAsync(project, CancellationToken)).Any(e => e.Type == AuditEventTypes.SpecRevised),
			"a refused specification was written into the permanent record.");
	}

	/// <summary>
	/// Asking twice with one key records one revision, and the answer is the one on record.
	/// </summary>
	[TestMethod]
	public async Task RepeatingTheRequestReturnsTheRevisionItAlreadyRecorded()
	{
		var project = await ProjectWithDataAsync("retry");

		var first = await _service.ProposeAsync(project, Sound, "propose-1", Actors.Agent, CancellationToken);
		var again = await _service.ProposeAsync(project, Reworded, "propose-1", Actors.Agent, CancellationToken);

		AreEqual(first.Id, again.Id, "a retry of one request recorded a second revision.");

		AreEqual(Sound, again.Json,
			"a retry was answered with the text it sent rather than the text already on record.");

		AreEqual(1, (await _service.ListAsync(project, CancellationToken)).Count,
			"one request left the project holding more than one revision.");
	}

	/// <summary>Documents that cannot be read, and what makes each of them unreadable.</summary>
	private static IEnumerable<(string What, string Json)> Unreadable()
	{
		yield return ("a document that is not JSON at all", "{ \"name\": ");
		yield return ("a document with nothing in it", "{}");
		yield return ("a document that is only white space", "   ");
		yield return ("a document that is the JSON null", "null");
		yield return ("an expression kind that does not exist", Document(Thesis, """{ "kind": "Sideways" }""", TimeExit));
		yield return ("a condition that is not an expression at all", Document(Thesis, "42", TimeExit));

		yield return ("a constant written as text",
			Document(Thesis, Compare("""{ "kind": "Constant", "value": "20" }"""), TimeExit));

		yield return ("a parameter named with a number",
			Document(Thesis, Compare("""{ "kind": "Parameter", "name": 20 }"""), TimeExit));

		yield return ("an indicator reading a candle field that does not exist",
			Document(Thesis, Compare(Indicator(""", "source": "Middle" """)), TimeExit));

		yield return ("an offset that is not a whole number of candles",
			Document(Thesis, Compare(Indicator(""", "source": "Close", "offset": 1.5 """)), TimeExit));
	}

	private static string Describe(SpecCheck check)
		=> string.Join("; ", check.Problems.Select(p => $"{p.Path}: {p.Message}"));

	/// <summary>Enough bars for a dataset with something in every slice.</summary>
	private static IReadOnlyList<Candle> Bars()
	{
		var bars = new List<Candle>();
		var time = _open;

		for (var i = 0; i < 600; i++)
		{
			var close = 100m + Math.Round(5m * (decimal)Math.Sin(i * 2 * Math.PI / 60), 2);
			var open = bars.Count == 0 ? close : bars[^1].Close;

			bars.Add(new(time, open, Math.Max(open, close) + 0.05m, Math.Min(open, close) - 0.05m, close, 10_000m));

			time = time.AddMinutes(5);
		}

		return bars;
	}

	private static string Indicator(string extra)
		=> $$"""{ "kind": "Indicator", "name": "sma", "length": { "kind": "Constant", "value": 20 }{{extra}} }""";

	private static string Compare(string right)
		=> $$"""{ "kind": "Compare", "left": { "kind": "Field", "field": "Close" }, "operator": "GreaterThan", "right": {{right}} }""";

	private static string Document(string thesis, string condition, string exits)
		=> $$"""
		{
		  "name": "Above its average",
		  "thesis": "{{thesis}}",
		  "allowLong": true,
		  "allowShort": false,
		  "timeFrame": "00:05:00",
		  "warmupBars": 20,
		  "entries": [ { "id": "e1", "direction": "Long", "condition": {{condition}} } ],
		  "exits": [ {{exits}} ],
		  "parameters": [],
		  "risk": { "maxPositionPercent": 0.10, "maxDailyLossPercent": 0.02 }
		}
		""";

	/// <summary>A project holding data, so that a proposal against it could be measured.</summary>
	private async Task<ProjectId> ProjectWithDataAsync(string name)
	{
		var created = await _projects.CreateProjectAsync(name, Guid.NewGuid().ToString("n"), Actors.User, CancellationToken);

		await ImportAsync(created.Id);

		return created.Id;
	}

	/// <summary>Freezes enough bars onto a project for it to have data at all.</summary>
	private async Task ImportAsync(ProjectId project)
	{
		var opened = await _store.OpenAsync(project, CancellationToken);

		var imported = DatasetBuilder.Build(
			new Dictionary<string, IReadOnlyList<Candle>> { ["NVDA"] = Bars() },
			TimeSpan.FromMinutes(5),
			"test",
			isSynthetic: true);

		await _datasets.SaveAsync(project, imported, CancellationToken);
		await _store.UpdateAsync(opened.WithDataset(imported.Manifest.Id, DateTime.UtcNow), CancellationToken);
	}
}
