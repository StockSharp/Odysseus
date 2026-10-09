namespace StockSharp.Odysseus.Persistence.Tests;

using System.Globalization;

/// <summary>Independent workspaces and short-lived file handles.</summary>
[TestClass]
public class FileStoreIsolationTests : OdysseusTestBase
{
	private string _root;

	/// <summary>Creates a temporary workspace.</summary>
	[TestInitialize]
	public void CreateRoot()
	{
		_root = Path.Combine(Path.GetTempPath(), "odysseus-tests", Guid.NewGuid().ToString("n"));
		Directory.CreateDirectory(_root);
	}

	/// <summary>Removes the temporary workspace.</summary>
	[TestCleanup]
	public void DeleteRoot()
	{
		if (Directory.Exists(_root))
			Directory.Delete(_root, recursive: true);
	}

	/// <summary>Files can be opened exclusively between requests while the stores remain alive.</summary>
	[TestMethod]
	public async Task StoresHoldNoFilesOpenBetweenCalls()
	{
		using var store = new FileProjectStore(_root);
		using var log = new FileOperationLog(_root);
		using var ledger = new FileClosedHistoryLedger(_root);
		var project = ResearchProject.Create("released", ResearchBudget.Default, DateTime.UtcNow);

		await store.CreateAsync(project, CancellationToken);
		await log.RecordAsync("projects.create", "once", "done", CancellationToken);
		await ledger.ClaimAsync(new(project.Id, CandidateId.New(), "NVDA", TimeSpan.FromMinutes(5),
			DateTime.UtcNow.AddDays(-30), DateTime.UtcNow, DateTime.UtcNow), CancellationToken);

		foreach (var path in Directory.EnumerateFiles(_root, "*.json", SearchOption.AllDirectories))
		{
			using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
			IsTrue(exclusive.Length > 0);
		}
	}

	/// <summary>Disposing one workspace leaves stores over other workspaces usable.</summary>
	[TestMethod]
	public async Task StoresOverDifferentWorkspacesWorkAtTheSameTime()
	{
		const int Workers = 16;
		const int Rounds = 6;

		await Task.WhenAll(Enumerable.Range(0, Workers).Select(async worker =>
		{
			var root = Path.Combine(_root, worker.ToString(CultureInfo.InvariantCulture));

			for (var round = 0; round < Rounds; round++)
			{
				using var store = new FileProjectStore(root);
				using var log = new FileOperationLog(root);
				var project = ResearchProject.Create($"worker {worker} round {round}", ResearchBudget.Default, DateTime.UtcNow);

				await store.CreateAsync(project, CancellationToken);
				await log.RecordAsync("projects.create", project.Id.Value, "done", CancellationToken);
				AreEqual(project, await store.OpenAsync(project.Id, CancellationToken));
			}
		}));
	}
}
