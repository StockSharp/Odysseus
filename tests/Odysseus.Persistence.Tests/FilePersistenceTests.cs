namespace StockSharp.Odysseus.Persistence.Tests;

using System.Collections.Generic;
using System.Text.Json;
using System.Threading;

/// <summary>Portable, readable workspace files survive a process restart.</summary>
[TestClass]
public class FilePersistenceTests : OdysseusTestBase
{
	private string _root;

	/// <summary>Creates a workspace for this test.</summary>
	[TestInitialize]
	public void CreateRoot()
	{
		_root = Path.Combine(Path.GetTempPath(), "odysseus-tests", Guid.NewGuid().ToString("n"));
		Directory.CreateDirectory(_root);
	}

	/// <summary>Removes the workspace.</summary>
	[TestCleanup]
	public void DeleteRoot()
	{
		Directory.Delete(_root, recursive: true);
	}

	/// <summary>Project metadata is readable without Odysseus and preserves identifiers and dates.</summary>
	[TestMethod]
	public async Task ProjectMetadataIsReadableAndSurvivesRestart()
	{
		var project = ResearchProject.Create("file workspace", ResearchBudget.Default, DateTime.UtcNow);
		using (var store = new FileProjectStore(_root))
			await store.CreateAsync(project, CancellationToken);

		var path = Path.Combine(_root, project.Id.Value, "project.json");
		IsTrue(File.Exists(path), "project metadata must be a JSON file.");
		using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, CancellationToken));
		AreEqual(project.Id.Value, document.RootElement.GetProperty("id").GetString());
		AreEqual(project.Name, document.RootElement.GetProperty("name").GetString());

		using var reopened = new FileProjectStore(_root);
		AreEqual(project, await reopened.OpenAsync(project.Id, CancellationToken));
		AreEqual(0, Directory.GetFiles(_root, "*.db", SearchOption.AllDirectories).Length);
	}

	/// <summary>Strategy and run records are separate files with exact decimal parameters.</summary>
	[TestMethod]
	public async Task StrategyAndRunFilesSurviveRestart()
	{
		var now = DateTime.UtcNow;
		var project = ResearchProject.Create("results", ResearchBudget.Default, now);
		var artifact = ArtifactId.FromContent([1, 2, 3]);
		var candidate = new Candidate(CandidateId.New(), SpecId.New(), CandidateStatuses.Compiled,
			"ExampleStrategy", "source hash", "assembly hash", artifact, artifact, "v1", now, now);
		const decimal Size = 123456789.1234567890123456789m;
		var run = new RunResult(RunId.New(), candidate.Id, DatasetId.New(), DataSlices.Development,
			0, "NVDA", "baseline", "run fingerprint", new Dictionary<string, decimal> { ["size"] = Size },
			RunStatuses.Failed, null, artifact, artifact, 0, now, now.AddSeconds(1), "test failure", "test diagnosis");

		using (var store = new FileProjectStore(_root))
		{
			await store.CreateAsync(project, CancellationToken);
			await store.AddAsync(project.Id, candidate, CancellationToken);
			await store.AddAsync(project.Id, run, CancellationToken);
		}

		AreEqual(1, Directory.GetFiles(Path.Combine(_root, project.Id.Value, "candidates"), $"*-{candidate.Id.Value}.json").Length);
		AreEqual(1, Directory.GetFiles(Path.Combine(_root, project.Id.Value, "runs"), $"*-{run.Id.Value}.json").Length);
		using var reopened = new FileProjectStore(_root);
		AreEqual(candidate, await reopened.GetAsync(project.Id, candidate.Id, CancellationToken));
		var restored = await reopened.GetAsync(project.Id, run.Id, CancellationToken);
		AreEqual(Size, restored.Parameters["size"]);
		AreEqual(run.Error, restored.Error);
		AreEqual(run.Diagnosis, restored.Diagnosis);
		AreEqual(DateTimeKind.Utc, restored.StartedAt.Kind);
	}

	/// <summary>Scoped operation keys remain stable after restarting the server.</summary>
	[TestMethod]
	public async Task OperationKeysAreFilesAndRemainScopedAfterRestart()
	{
		using (var log = new FileOperationLog(_root))
		{
			AreEqual("first", await log.RecordAsync("one", "key", "first", CancellationToken));
			AreEqual("first", await log.RecordAsync("one", "key", "replacement", CancellationToken));
			AreEqual("second", await log.RecordAsync("two", "key", "second", CancellationToken));
		}

		IsTrue(File.Exists(Path.Combine(_root, "operations.json")));
		using var reopened = new FileOperationLog(_root);
		AreEqual("first", await reopened.TryGetAsync("one", "key", CancellationToken));
		AreEqual("second", await reopened.TryGetAsync("two", "key", CancellationToken));
	}

	/// <summary>Cancelled metadata updates leave the previous project intact.</summary>
	[TestMethod]
	public async Task CancelledUpdateDoesNotLoseStoredBudgetOrMetadata()
	{
		using var store = new FileProjectStore(_root);
		var project = ResearchProject.Create("before", ResearchBudget.Default, DateTime.UtcNow);
		await store.CreateAsync(project, CancellationToken);
		await store.TryClaimAsync(project.Id, 3, 1, CancellationToken);
		var before = await store.OpenAsync(project.Id, CancellationToken);
		using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
		cancelled.Cancel();

		await ThrowsAsync<OperationCanceledException>(async () =>
			await store.UpdateAsync(project.Rename("after", DateTime.UtcNow), cancelled.Token));
		AreEqual(before, await store.OpenAsync(project.Id, CancellationToken));
		AreEqual(0, Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories).Length);
	}

	/// <summary>Insertion order survives equal dates, updates, restarts and unfinished temporary files.</summary>
	[TestMethod]
	public async Task CandidateOrderIsPreservedWhenUpdatingAndReopening()
	{
		var now = DateTime.UtcNow;
		var project = ResearchProject.Create("ordered", ResearchBudget.Default, now);
		var artifact = ArtifactId.FromContent([1, 2, 3]);
		var first = new Candidate(CandidateId.Parse("cand_z"), SpecId.New(), CandidateStatuses.Compiled,
			"ExampleStrategy", "same source", "assembly", artifact, artifact, "v1", now, now);
		var second = first with { Id = CandidateId.Parse("cand_a") };

		using (var store = new FileProjectStore(_root))
		{
			await store.CreateAsync(project, CancellationToken);
			await store.AddAsync(project.Id, first, CancellationToken);
			await store.AddAsync(project.Id, second, CancellationToken);
			await store.UpdateAsync(project.Id, first with { UpdatedAt = now.AddHours(1) }, CancellationToken);
		}

		await File.WriteAllTextAsync(Path.Combine(_root, project.Id.Value, "candidates", "unfinished.json.tmp"), "{", CancellationToken);
		using var reopened = new FileProjectStore(_root);
		var third = first with { Id = CandidateId.Parse("cand_b"), CreatedAt = now.AddHours(-1) };
		await reopened.AddAsync(project.Id, third, CancellationToken);
		var listed = await reopened.ListAsync(project.Id, CancellationToken);
		IsTrue(listed.Select(candidate => candidate.Id).SequenceEqual(new[] { first.Id, second.Id, third.Id }));
		AreEqual(first.Id, (await reopened.FindBySourceAsync(project.Id, first.SourceHash, CancellationToken)).Id);
		AreEqual(now.AddHours(1), listed[0].UpdatedAt);
	}

	/// <summary>Retries arriving together retain one result for one operation key.</summary>
	[TestMethod]
	public async Task ConcurrentOperationKeysKeepTheFirstResult()
	{
		using var log = new FileOperationLog(_root);
		var results = await Task.WhenAll(Enumerable.Range(0, 16)
			.Select(index => log.RecordAsync("create", "once", "result " + index, CancellationToken).AsTask()));
		AreEqual(1, results.Distinct().Count());
		AreEqual(results[0], await log.TryGetAsync("create", "once", CancellationToken));
	}

	/// <summary>Legacy databases cannot be silently ignored or reset by opening the new stores.</summary>
	[TestMethod]
	[DataRow("operations.db")]
	[DataRow("closed-history.db")]
	[DataRow("odysseus.db")]
	public void LegacyWorkspaceRequiresMigration(string name)
	{
		var folder = name == "odysseus.db" ? Path.Combine(_root, ProjectId.New().Value) : _root;
		Directory.CreateDirectory(folder);
		File.WriteAllText(Path.Combine(folder, name), "legacy data");
		var error = Throws<InvalidOperationException>(() => new FileProjectStore(_root));
		IsTrue(error.Message.Contains("Migrate-SqliteWorkspace.py", StringComparison.Ordinal));
		Throws<InvalidOperationException>(() => new FileOperationLog(_root));
		Throws<InvalidOperationException>(() => new FileClosedHistoryLedger(_root));
		AreEqual(0, Directory.GetFiles(_root, "*.json", SearchOption.AllDirectories).Length);
	}

	/// <summary>Damaged metadata reports an error instead of returning an empty workspace.</summary>
	[TestMethod]
	public async Task DamagedProjectJsonIsNotTreatedAsMissing()
	{
		using var store = new FileProjectStore(_root);
		var project = ResearchProject.Create("damaged", ResearchBudget.Default, DateTime.UtcNow);
		await store.CreateAsync(project, CancellationToken);
		await File.WriteAllTextAsync(Path.Combine(_root, project.Id.Value, "project.json"), "{", CancellationToken);
		await ThrowsAsync<JsonException>(async () => await store.OpenAsync(project.Id, CancellationToken));
		await ThrowsAsync<JsonException>(async () => await store.ListAsync(CancellationToken));
	}

	/// <summary>Timestamps retain the store's existing UTC normalization even for local input.</summary>
	[TestMethod]
	public async Task LocalTimestampsAreStoredAndRestoredAsUtc()
	{
		var local = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Local);
		var project = ResearchProject.Create("UTC", ResearchBudget.Default, local.ToUniversalTime());
		var artifact = ArtifactId.FromContent([1, 2, 3]);
		var candidate = new Candidate(CandidateId.New(), SpecId.New(), CandidateStatuses.Compiled,
			"ExampleStrategy", "source", "assembly", artifact, artifact, "v1", local, local);
		using var store = new FileProjectStore(_root);
		await store.CreateAsync(project, CancellationToken);
		await store.AddAsync(project.Id, candidate, CancellationToken);
		var restored = await store.GetAsync(project.Id, candidate.Id, CancellationToken);
		AreEqual(DateTimeKind.Utc, restored.CreatedAt.Kind);
		AreEqual(local.ToUniversalTime(), restored.CreatedAt);
	}
}
