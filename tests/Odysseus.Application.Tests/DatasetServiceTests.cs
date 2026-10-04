namespace Odysseus.Application.Tests;

using System.Globalization;

using Odysseus.Persistence;
using Odysseus.Platform;

/// <summary>
/// Importing a dataset into a project, and what an agent is allowed to learn about it afterwards.
/// </summary>
[TestClass]
public class DatasetServiceTests : OdysseusTestBase
{
	private string _root;
	private SqliteProjectStore _store;
	private SqliteOperationLog _operations;
	private FileDatasetStore _datasets;
	private ProjectService _projects;
	private DatasetService _service;

	/// <summary>Builds the services over temporary storage.</summary>
	[TestInitialize]
	public void CreateService()
	{
		_root = Path.Combine(Path.GetTempPath(), "odysseus-tests", Guid.NewGuid().ToString("n"));
		_store = new SqliteProjectStore(_root);
		_operations = new SqliteOperationLog(_root);
		_datasets = new FileDatasetStore(_root, new LocalBarStorage(Path.Combine(_root, "market-data")));

		var clock = new Clock();
		var budget = new ResearchBudget(60, 40, TimeSpan.FromMinutes(45)).ToState();

		_projects = new ProjectService(_store, _store, _operations, clock, ServerModes.Local, budget);
		_service = new DatasetService(_store, _datasets, _store, _store, _operations, clock);
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

	/// <summary>Importing makes the data the project's and records it permanently.</summary>
	[TestMethod]
	public async Task ImportingMakesTheDataTheProjects()
	{
		var project = await NewProjectAsync();

		var manifest = await _service.ImportDemoAsync(project.Id, "import-1", Actors.Agent, CancellationToken);

		var reopened = await _projects.OpenProjectAsync(project.Id, CancellationToken);

		AreEqual(manifest.Id, reopened.Dataset);
		AreEqual(ProjectStatuses.Ready, reopened.Status, "a project with data is ready to research.");

		var trail = await _projects.ReadAuditAsync(project.Id, CancellationToken);

		var imported = trail.Single(e => e.Type == AuditEventTypes.DatasetImported);

		IsTrue(imported.Detail.Contains(manifest.Split.From.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), StringComparison.Ordinal),
			$"the record does not say which range was imported: {imported.Detail}");
	}

	/// <summary>A retry of the same import does not produce a second dataset.</summary>
	[TestMethod]
	public async Task RepeatedImportReturnsTheFirstDataset()
	{
		var project = await NewProjectAsync();

		var first = await _service.ImportDemoAsync(project.Id, "import-1", Actors.Agent, CancellationToken);
		var again = await _service.ImportDemoAsync(project.Id, "import-1", Actors.Agent, CancellationToken);

		AreEqual(first.Id, again.Id);
	}

	/// <summary>The bars come back exactly as they were written.</summary>
	[TestMethod]
	public async Task BarsSurviveStorage()
	{
		var project = await NewProjectAsync();
		var manifest = await _service.ImportDemoAsync(project.Id, "import-1", Actors.Agent, CancellationToken);

		var symbol = manifest.Symbols[0];

		var development = await _datasets.LoadAsync(project.Id, manifest.Id, symbol, DataSlices.Development, CancellationToken);
		var validation = await _datasets.LoadAsync(project.Id, manifest.Id, symbol, DataSlices.Validation, CancellationToken);
		var final = await _datasets.LoadAsync(project.Id, manifest.Id, symbol, DataSlices.Final, CancellationToken);

		IsTrue(development.Count > 0 && validation.Count > 0 && final.Count > 0, "every slice must hold bars.");

		AreEqual(
			manifest.Quality.Single(q => q.Symbol == symbol).Records,
			development.Count + validation.Count + final.Count,
			"the slices together must account for every bar, without overlap or loss.");

		IsTrue(development[^1].OpenTime < validation[0].OpenTime, "the slices must not overlap in time.");
		IsTrue(validation[^1].OpenTime < final[0].OpenTime, "the slices must not overlap in time.");
	}

	/// <summary>
	/// Asking about data before any exists is refused with a message that says what to do, not merely
	/// that something is missing.
	/// </summary>
	[TestMethod]
	public async Task AskingBeforeImportingSaysWhatToDo()
	{
		var project = await NewProjectAsync();

		var error = await ThrowsAsync<InvalidOperationException>(
			async () => await _service.GetManifestAsync(project.Id, CancellationToken));

		IsTrue(error.Message.Contains("import_demo_dataset", StringComparison.Ordinal),
			"the refusal must name the tool that fixes it.");
	}

	/// <summary>Asking for a symbol the dataset does not cover lists the ones it does.</summary>
	[TestMethod]
	public async Task AnUnknownSymbolListsTheKnownOnes()
	{
		var project = await NewProjectAsync();
		var manifest = await _service.ImportDemoAsync(project.Id, "import-1", Actors.Agent, CancellationToken);

		var error = await ThrowsAsync<ArgumentException>(
			async () => await _datasets.LoadAsync(project.Id, manifest.Id, "NOPE", DataSlices.Development, CancellationToken));

		foreach (var symbol in manifest.Symbols)
		{
			IsTrue(error.Message.Contains(symbol, StringComparison.Ordinal),
				$"the refusal must list '{symbol}' as one of the symbols that do exist.");
		}
	}

	/// <summary>
	/// The division is described whole - where each slice begins and ends - with whether the closed slice
	/// has been spent.
	/// </summary>
	[TestMethod]
	public async Task TheSplitIsDescribedWhole()
	{
		var project = await NewProjectAsync();
		var manifest = await _service.ImportDemoAsync(project.Id, "import-1", Actors.Agent, CancellationToken);

		var (split, spent) = await _service.DescribeSplitAsync(project.Id, CancellationToken);

		AreEqual(manifest.Split, split);
		IsFalse(spent, "nothing has been measured on the closed slice yet.");
	}

	/// <summary>Generated data stays marked as generated all the way through storage.</summary>
	[TestMethod]
	public async Task SyntheticMarkingSurvivesStorage()
	{
		var project = await NewProjectAsync();

		await _service.ImportDemoAsync(project.Id, "import-1", Actors.Agent, CancellationToken);

		IsTrue((await _service.GetManifestAsync(project.Id, CancellationToken)).IsSynthetic,
			"a reader of the stored dataset must still be told the prices were invented.");
	}

	private async Task<ResearchProject> NewProjectAsync()
		=> await _projects.CreateProjectAsync("dataset study", Guid.NewGuid().ToString("n"), Actors.User, CancellationToken);

	private sealed class Clock : IClock
	{
		public DateTime UtcNow => DateTime.UtcNow;
	}
}
