namespace StockSharp.Odysseus.Application.Tests;

using StockSharp.Odysseus.Persistence;

/// <summary>
/// The project use cases the MCP tools sit on. The caller is an autonomous agent over a connection
/// that can drop mid-call, so the behaviour under a repeated call matters as much as the behaviour
/// under a first one.
/// </summary>
[TestClass]
public class ProjectServiceTests : OdysseusTestBase
{
	private string _root;
	private SqliteProjectStore _store;
	private SqliteOperationLog _operations;
	private FixedClock _clock;
	private ProjectService _service;

	private static ResearchBudgetState Budget
		=> new ResearchBudget(maxBacktests: 60, maxCandidates: 40, maxWallClock: TimeSpan.FromMinutes(45)).ToState();

	/// <summary>Builds a service over temporary storage.</summary>
	[TestInitialize]
	public void CreateService()
	{
		_root = Path.Combine(Path.GetTempPath(), "odysseus-tests", Guid.NewGuid().ToString("n"));
		_store = new SqliteProjectStore(_root);
		_operations = new SqliteOperationLog(_root);
		_clock = new FixedClock(new DateTime(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc));
		_service = new ProjectService(_store, _store, _operations, _clock, ServerModes.Local, Budget);
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

	/// <summary>A created project is saved and audited in one step.</summary>
	[TestMethod]
	public async Task CreatingAProjectRecordsIt()
	{
		var project = await _service.CreateProjectAsync("breakout study", "op-1", Actors.Agent, CancellationToken);

		AreEqual("breakout study", (await _service.OpenProjectAsync(project.Id, CancellationToken)).Name);

		var trail = await _service.ReadAuditAsync(project.Id, CancellationToken);

		AreEqual(1, trail.Count);
		AreEqual(AuditEventTypes.ProjectCreated, trail[0].Type);
		AreEqual(Actors.Agent, trail[0].Actor);
	}

	/// <summary>
	/// The reason operation keys exist: a dropped response must not turn one request into two projects.
	/// </summary>
	[TestMethod]
	public async Task RepeatingACallWithTheSameKeyReturnsTheFirstResult()
	{
		var first = await _service.CreateProjectAsync("once", "op-1", Actors.Agent, CancellationToken);
		var again = await _service.CreateProjectAsync("once", "op-1", Actors.Agent, CancellationToken);

		AreEqual(first.Id, again.Id);
		AreEqual(1, (await _service.ListProjectsAsync(CancellationToken)).Count);
	}

	/// <summary>A different key is a different request, even with the same arguments.</summary>
	[TestMethod]
	public async Task ADifferentKeyIsADifferentRequest()
	{
		await _service.CreateProjectAsync("same name", "op-1", Actors.Agent, CancellationToken);
		await _service.CreateProjectAsync("same name", "op-2", Actors.Agent, CancellationToken);

		AreEqual(2, (await _service.ListProjectsAsync(CancellationToken)).Count);
	}

	/// <summary>An agent that retries in parallel must still end up with one project.</summary>
	[TestMethod]
	public async Task ConcurrentRetriesOfOneKeyCreateOneProject()
	{
		var created = await Task.WhenAll(Enumerable
			.Range(0, 8)
			.Select(_ => _service.CreateProjectAsync("racing", "op-race", Actors.Agent, CancellationToken).AsTask()));

		AreEqual(1, created.Select(p => p.Id).Distinct().Count(), "the same key produced more than one project.");
		AreEqual(1, (await _service.ListProjectsAsync(CancellationToken)).Count);
	}

	/// <summary>An operation key survives a restart, or the retry after a crash creates a duplicate.</summary>
	[TestMethod]
	public async Task OperationKeysSurviveARestart()
	{
		var first = await _service.CreateProjectAsync("durable", "op-1", Actors.Agent, CancellationToken);

		_store.Dispose();
		_operations.Dispose();
		_store = new SqliteProjectStore(_root);
		_operations = new SqliteOperationLog(_root);
		_service = new ProjectService(_store, _store, _operations, _clock, ServerModes.Local, Budget);

		var again = await _service.CreateProjectAsync("durable", "op-1", Actors.Agent, CancellationToken);

		AreEqual(first.Id, again.Id);
	}

	/// <summary>Renaming is saved and leaves a trace of what it was before.</summary>
	[TestMethod]
	public async Task RenamingIsAudited()
	{
		var project = await _service.CreateProjectAsync("before", "op-1", Actors.User, CancellationToken);

		await _service.RenameProjectAsync(project.Id, "after", Actors.User, CancellationToken);

		var trail = await _service.ReadAuditAsync(project.Id, CancellationToken);

		AreEqual(2, trail.Count);
		AreEqual(AuditEventTypes.ProjectRenamed, trail[1].Type);
		IsTrue(trail[1].Detail.Contains("before", StringComparison.Ordinal), "the trail must say what the name was.");
	}

	/// <summary>
	/// The description says how the server was started, and says nothing it cannot back.
	/// </summary>
	/// <remarks>
	/// It used to publish three more things: the largest response any tool would return inline, whether
	/// hand-written source was accepted, and whether the security analyzers were enforced. Nothing
	/// measured a response, no tool ever took hand-written source, and the compiler runs the same
	/// analyzers whichever mode it is in - so all three told an agent something that was not so.
	/// </remarks>
	[TestMethod]
	public void DescriptionReflectsTheMode()
	{
		AreEqual(ServerModes.Local, _service.Describe("1.0.0").Mode);

		var hosted = new ProjectService(_store, _store, _operations, _clock, ServerModes.Hosted, Budget)
			.Describe("1.0.0");

		AreEqual(ServerModes.Hosted, hosted.Mode);
	}

	/// <summary>Timestamps come from the clock, not from the machine, so a run is reproducible.</summary>
	[TestMethod]
	public async Task TimestampsComeFromTheClock()
	{
		var project = await _service.CreateProjectAsync("timed", "op-1", Actors.Agent, CancellationToken);

		AreEqual(_clock.UtcNow, project.CreatedAt);
	}

	/// <summary>A call without a key is refused rather than silently losing its idempotency.</summary>
	[TestMethod]
	public async Task ACallWithoutAKeyIsRefused()
	{
		await ThrowsAsync<ArgumentException>(
			async () => await _service.CreateProjectAsync("no key", "", Actors.Agent, CancellationToken));
	}

	/// <summary>
	/// A key means something inside its project and nowhere else. Callers pick these words themselves, so
	/// two projects choosing the same obvious one would otherwise be handed each other's results.
	/// </summary>
	[TestMethod]
	public async Task AKeyOfOneProjectDoesNotAnswerForAnother()
	{
		var first = await _service.CreateProjectAsync("first", "op-1", Actors.Agent, CancellationToken);
		var second = await _service.CreateProjectAsync("second", "op-2", Actors.Agent, CancellationToken);

		AreNotEqual(first.Id, second.Id);

		// The same word, used inside each project.
		await _operations.RecordAsync(first.Id.Value, "shared", "belongs-to-first", CancellationToken);
		await _operations.RecordAsync(second.Id.Value, "shared", "belongs-to-second", CancellationToken);

		AreEqual("belongs-to-first", await _operations.TryGetAsync(first.Id.Value, "shared", CancellationToken));
		AreEqual("belongs-to-second", await _operations.TryGetAsync(second.Id.Value, "shared", CancellationToken));
	}

	private sealed class FixedClock(DateTime now) : IClock
	{
		public DateTime UtcNow { get; } = now;
	}
}
