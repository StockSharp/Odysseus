namespace StockSharp.Odysseus.Persistence.Tests;

using System.Collections.Concurrent;
using System.Globalization;

using Microsoft.Data.Sqlite;

/// <summary>
/// What one store's lifetime may and may not do to everything else in the process.
/// </summary>
/// <remarks>
/// A store is not a singleton and was never meant to be one. The server answers tool calls
/// concurrently, each over the project it was given, and the end of one of them is an ordinary
/// <c>Dispose</c> that happens while other calls are mid-query. So the reach of that <c>Dispose</c> is
/// part of the contract: it ends this store's databases and nothing else's. Reaching wider is not a
/// tidier way of closing a file, it is one caller closing another caller's connection - which arrives
/// wherever the other caller happened to be as a disposed handle, and reads as data loss rather than as
/// a lifetime bug.
/// </remarks>
[TestClass]
public class StoreIsolationTests : OdysseusTestBase
{
	private string _root;

	private static DateTime Now => DateTime.UtcNow;

	private static ResearchBudgetState Budget
		=> new ResearchBudget(maxBacktests: 60, maxCandidates: 40, maxWallClock: TimeSpan.FromMinutes(45)).ToState();

	/// <summary>Names a temporary root and creates it.</summary>
	[TestInitialize]
	public void CreateRoot()
	{
		_root = Path.Combine(Path.GetTempPath(), "odysseus-tests", Guid.NewGuid().ToString("n"));

		Directory.CreateDirectory(_root);
	}

	/// <summary>Removes the temporary root.</summary>
	[TestCleanup]
	public void DeleteRoot()
	{
		if (Directory.Exists(_root))
			Directory.Delete(_root, recursive: true);
	}

	/// <summary>
	/// Disposing a store leaves a connection held elsewhere in the process exactly as it was.
	/// </summary>
	/// <remarks>
	/// The stranger here stands in for the other tool call: a connection to another database entirely,
	/// closed and waiting in the provider's pool for its owner to come back to it. Reopening it has to
	/// give back the same handle, because the store that was disposed in between owned none of it.
	/// </remarks>
	[TestMethod]
	public async Task DisposingAStoreLeavesAConnectionHeldElsewhereAlone()
	{
		var stranger = new SqliteConnection($"Data Source={Path.Combine(_root, "elsewhere.db")}");

		try
		{
			stranger.Open();

			var held = stranger.Handle;

			// Closed rather than disposed: the handle stays in the provider's pool, which is where a
			// caller between two of its own queries leaves it.
			stranger.Close();

			using (var store = new SqliteProjectStore(Path.Combine(_root, "projects")))
				await store.CreateAsync(ResearchProject.Create("unrelated", Budget, Now), CancellationToken);

			stranger.Open();

			AreSame(held, stranger.Handle,
				"disposing a store closed a connection belonging to something else in the process.");
		}
		finally
		{
			stranger.Dispose();

			// This test's own pool, cleared by the one place that owns it, so the root can be removed.
			SqliteConnection.ClearPool(stranger);
		}
	}

	/// <summary>
	/// A disposed store holds none of its database files open.
	/// </summary>
	/// <remarks>
	/// The other half of the same contract, and the reason the wider reach was there to begin with: a
	/// store that ends without releasing its files leaves them locked for as long as the process lives,
	/// which is a leak whichever way the handle is being kept.
	/// </remarks>
	[TestMethod]
	public async Task ADisposedStoreHoldsNoneOfItsFilesOpen()
	{
		var store = new SqliteProjectStore(_root);
		var log = new SqliteOperationLog(_root);
		var ledger = new SqliteClosedHistoryLedger(_root);

		await store.CreateAsync(ResearchProject.Create("released", Budget, Now), CancellationToken);
		await log.RecordAsync("projects.create", "once", "done", CancellationToken);

		store.Dispose();
		log.Dispose();
		ledger.Dispose();

		// Windows refuses to delete a file somebody still has open, so this is the assertion.
		Directory.Delete(_root, recursive: true);

		IsFalse(Directory.Exists(_root), "the store's folder outlived the store.");
	}

	/// <summary>
	/// Stores over different projects can be opened, used and disposed at the same time.
	/// </summary>
	/// <remarks>
	/// This is the shape the server actually runs in, and the one no single-threaded test can speak for:
	/// several projects live at once, and one of them finishing is a <c>Dispose</c> that lands while the
	/// others are mid-query.
	/// </remarks>
	[TestMethod]
	public async Task StoresOverDifferentProjectsWorkAtTheSameTime()
	{
		const int Workers = 16;
		const int Rounds = 6;

		var failures = new ConcurrentBag<Exception>();

		await Task.WhenAll(Enumerable.Range(0, Workers).Select(worker => Task.Run(async () =>
		{
			var root = Path.Combine(_root, worker.ToString(CultureInfo.InvariantCulture));

			try
			{
				for (var round = 0; round < Rounds; round++)
				{
					using var store = new SqliteProjectStore(root);
					using var log = new SqliteOperationLog(root);

					var project = ResearchProject.Create($"worker {worker} round {round}", Budget, Now);

					await store.CreateAsync(project, CancellationToken);
					await log.RecordAsync("projects.create", project.Id.Value, "done", CancellationToken);

					var opened = await store.OpenAsync(project.Id, CancellationToken);

					AreEqual(project.Name, opened.Name);
				}
			}
			catch (Exception error)
			{
				failures.Add(error);
			}
		}, CancellationToken)));

		if (!failures.IsEmpty)
		{
			Fail($"{failures.Count} of {Workers} concurrent projects failed. " +
				$"One of them: {failures.First()}");
		}
	}
}
