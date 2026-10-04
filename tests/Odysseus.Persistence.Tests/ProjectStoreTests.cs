namespace Odysseus.Persistence.Tests;

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Odysseus.Application;
using Odysseus.Domain;
using Odysseus.Persistence;
using Odysseus.TestKit;

/// <summary>
/// Projects and their audit trail. A project has to come back after a restart exactly as it was left,
/// and the record of how it got there has to be impossible to quietly revise.
/// </summary>
[TestClass]
public class ProjectStoreTests : OdysseusTestBase
{
	private string _root;
	private SqliteProjectStore _store;

	private static DateTime Now => DateTime.UtcNow;

	private static ResearchBudgetState Budget
		=> new ResearchBudget(maxBacktests: 60, maxCandidates: 40, maxWallClock: TimeSpan.FromMinutes(45)).ToState();

	/// <summary>Creates a store over a temporary projects root.</summary>
	[TestInitialize]
	public void CreateStore()
	{
		_root = Path.Combine(Path.GetTempPath(), "odysseus-tests", Guid.NewGuid().ToString("n"));
		_store = new SqliteProjectStore(_root);
	}

	/// <summary>Removes the temporary root.</summary>
	[TestCleanup]
	public void DeleteStore()
	{
		_store?.Dispose();

		if (Directory.Exists(_root))
			Directory.Delete(_root, recursive: true);
	}

	/// <summary>A created project can be opened again and is unchanged.</summary>
	[TestMethod]
	public async Task CreatedProjectOpensUnchanged()
	{
		var project = ResearchProject.Create("NVDA five minute breakout", Budget, Now);

		await _store.CreateAsync(project, CancellationToken);

		var opened = await _store.OpenAsync(project.Id, CancellationToken);

		AreEqual(project.Id, opened.Id);
		AreEqual(project.Name, opened.Name);
		AreEqual(project.Status, opened.Status);
		AreEqual(project.Budget, opened.Budget);
	}

	/// <summary>
	/// The whole point of persistence: a project outlives the process that made it, and nothing about
	/// its identity lives in memory.
	/// </summary>
	[TestMethod]
	public async Task ProjectSurvivesARestart()
	{
		var project = ResearchProject.Create("survives", Budget, Now);
		await _store.CreateAsync(project, CancellationToken);

		_store.Dispose();
		_store = new SqliteProjectStore(_root);

		var opened = await _store.OpenAsync(project.Id, CancellationToken);

		AreEqual(project.Name, opened.Name);
		AreEqual(project.CreatedAt, opened.CreatedAt);
	}

	/// <summary>Moments come back as UTC, not as an unspecified local time.</summary>
	[TestMethod]
	public async Task MomentsComeBackAsUtc()
	{
		var project = ResearchProject.Create("timestamps", Budget, Now);
		await _store.CreateAsync(project, CancellationToken);

		var opened = await _store.OpenAsync(project.Id, CancellationToken);

		AreEqual(DateTimeKind.Utc, opened.CreatedAt.Kind);
		AreEqual(DateTimeKind.Utc, opened.UpdatedAt.Kind);
		AreEqual(project.CreatedAt, opened.CreatedAt);
	}

	/// <summary>Spent allowance is part of the project, or a restart would hand the budget back.</summary>
	[TestMethod]
	public async Task SpentBudgetSurvivesARestart()
	{
		var spent = new ResearchBudgetState(
			MaxBacktests: 60,
			MaxCandidates: 40,
			MaxWallClock: TimeSpan.FromMinutes(45),
			ClaimedBacktests: 2,
			ClaimedCandidates: 0,
			ClaimedWallClock: TimeSpan.Zero);

		var project = ResearchProject.Create("budget", spent, Now);
		await _store.CreateAsync(project, CancellationToken);

		_store.Dispose();
		_store = new SqliteProjectStore(_root);

		var restored = ResearchBudget.Restore((await _store.OpenAsync(project.Id, CancellationToken)).Budget);

		AreEqual(58, restored.RemainingBacktests);
	}

	/// <summary>A change is saved, not lost between calls.</summary>
	[TestMethod]
	public async Task UpdateIsPersisted()
	{
		var project = ResearchProject.Create("before", Budget, Now);
		await _store.CreateAsync(project, CancellationToken);

		await _store.UpdateAsync(project.Rename("after", Now), CancellationToken);

		AreEqual("after", (await _store.OpenAsync(project.Id, CancellationToken)).Name);
	}

	/// <summary>Opening something that was never created is an error, not an empty project.</summary>
	[TestMethod]
	public async Task MissingProjectIsReported()
	{
		await ThrowsAsync<ProjectNotFoundException>(
			async () => await _store.OpenAsync(ProjectId.New(), CancellationToken));

		await ThrowsAsync<ProjectNotFoundException>(
			async () => await _store.UpdateAsync(ResearchProject.Create("ghost", Budget, Now), CancellationToken));
	}

	/// <summary>Listing shows every project, most recently changed first.</summary>
	[TestMethod]
	public async Task ListingIsOrderedByRecency()
	{
		var older = ResearchProject.Create("older", Budget, Now.AddHours(-2));
		var newer = ResearchProject.Create("newer", Budget, Now);

		await _store.CreateAsync(older, CancellationToken);
		await _store.CreateAsync(newer, CancellationToken);

		var listed = await _store.ListAsync(CancellationToken);

		AreEqual(2, listed.Count);
		AreEqual(newer.Id, listed[0].Id);
		AreEqual(older.Id, listed[1].Id);
	}

	/// <summary>Each project keeps its own files, so one can be exported without the others.</summary>
	[TestMethod]
	public async Task EachProjectIsSelfContained()
	{
		var first = ResearchProject.Create("first", Budget, Now);
		var second = ResearchProject.Create("second", Budget, Now);

		await _store.CreateAsync(first, CancellationToken);
		await _store.CreateAsync(second, CancellationToken);

		var folders = Directory.EnumerateDirectories(_root).Select(Path.GetFileName).ToArray();

		AreEqual(2, folders.Length, "projects must not share a folder.");
		IsTrue(folders.Contains(first.Id.Value));
		IsTrue(folders.Contains(second.Id.Value));
	}

	/// <summary>Creating a project records it in the trail, with the actor that asked for it.</summary>
	[TestMethod]
	public async Task CreationIsAudited()
	{
		var project = ResearchProject.Create("audited", Budget, Now);
		await _store.CreateAsync(project, CancellationToken);

		await _store.AppendAsync(project.Id, AuditEventTypes.ProjectCreated, Actors.Agent, "created by the agent", "-", CancellationToken);

		var trail = await _store.ReadAsync(project.Id, CancellationToken);

		AreEqual(1, trail.Count);
		AreEqual(AuditEventTypes.ProjectCreated, trail[0].Type);
		AreEqual(Actors.Agent, trail[0].Actor);
		AreEqual(1L, trail[0].Sequence);
	}

	/// <summary>The trail is ordered and has no gaps, so a missing step is visible.</summary>
	[TestMethod]
	public async Task TrailIsContiguousAndOrdered()
	{
		var project = ResearchProject.Create("trail", Budget, Now);
		await _store.CreateAsync(project, CancellationToken);

		for (var i = 0; i < 5; i++)
			await _store.AppendAsync(project.Id, AuditEventTypes.RunStarted, Actors.Agent, $"run {i}", "-", CancellationToken);

		var trail = await _store.ReadAsync(project.Id, CancellationToken);

		AreEqual(5, trail.Count);

		for (var i = 0; i < trail.Count; i++)
			AreEqual(i + 1L, trail[i].Sequence, "the audit sequence has a gap or is out of order.");
	}

	/// <summary>Trails of different projects do not mix.</summary>
	[TestMethod]
	public async Task TrailsAreSeparatePerProject()
	{
		var first = ResearchProject.Create("first", Budget, Now);
		var second = ResearchProject.Create("second", Budget, Now);

		await _store.CreateAsync(first, CancellationToken);
		await _store.CreateAsync(second, CancellationToken);

		await _store.AppendAsync(first.Id, AuditEventTypes.RunStarted, Actors.Agent, "only in the first", "-", CancellationToken);

		AreEqual(1, (await _store.ReadAsync(first.Id, CancellationToken)).Count);
		AreEqual(0, (await _store.ReadAsync(second.Id, CancellationToken)).Count);
	}

	/// <summary>The trail outlives the process as well.</summary>
	[TestMethod]
	public async Task TrailSurvivesARestart()
	{
		var project = ResearchProject.Create("durable trail", Budget, Now);
		await _store.CreateAsync(project, CancellationToken);
		await _store.AppendAsync(project.Id, AuditEventTypes.DatasetImported, Actors.System, "frozen", "abc", CancellationToken);

		_store.Dispose();
		_store = new SqliteProjectStore(_root);

		var trail = await _store.ReadAsync(project.Id, CancellationToken);

		AreEqual(1, trail.Count);
		AreEqual("abc", trail[0].PayloadHash);
		AreEqual(DateTimeKind.Utc, trail[0].OccurredAt.Kind);
	}

	/// <summary>
	/// Appending from several places at once must not hand out the same position twice, or the trail
	/// stops being a sequence.
	/// </summary>
	[TestMethod]
	public async Task ConcurrentAppendsGetDistinctPositions()
	{
		var project = ResearchProject.Create("concurrent", Budget, Now);
		await _store.CreateAsync(project, CancellationToken);

		var appended = await Task.WhenAll(Enumerable
			.Range(0, 32)
			.Select(i => _store
				.AppendAsync(project.Id, AuditEventTypes.RunCompleted, Actors.Agent, $"run {i}", "-", CancellationToken)
				.AsTask()));

		var positions = appended.Select(e => e.Sequence).ToArray();

		AreEqual(positions.Length, positions.Distinct().Count(), "two entries were given the same position.");
		AreEqual(32L, positions.Max());
	}

	/// <summary>The database records the schema it was written with, so a future version can tell.</summary>
	[TestMethod]
	public async Task SchemaVersionIsRecorded()
	{
		var project = ResearchProject.Create("versioned", Budget, Now);
		await _store.CreateAsync(project, CancellationToken);

		AreEqual(SqliteProjectStore.SchemaVersion, await _store.GetSchemaVersionAsync(project.Id, CancellationToken));
	}
}
