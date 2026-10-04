namespace Odysseus.Persistence.Tests;

using System.Collections.Generic;

using Odysseus.Application;
using Odysseus.Platform;

/// <summary>
/// Datasets over the shared market-data storage: the project keeps a description, the bars live once in
/// the storage every project reads, and a slice reads its own stretch of them.
/// </summary>
[TestClass]
public class DatasetStoreTests : OdysseusTestBase
{
	private static readonly DateTime _start = new(2026, 3, 2, 14, 30, 0, DateTimeKind.Utc);

	private static readonly TimeSpan _frame = TimeSpan.FromMinutes(5);

	private const string Symbol = "AAA";

	private string _root;
	private string _bars;
	private FileDatasetStore _store;

	/// <summary>Creates a store over a temporary directory.</summary>
	[TestInitialize]
	public void CreateStore()
	{
		_root = Path.Combine(Path.GetTempPath(), "odysseus-tests", Guid.NewGuid().ToString("n"));
		_bars = Path.Combine(_root, "market-data");
		_store = new FileDatasetStore(Path.Combine(_root, "projects"), new LocalBarStorage(_bars));
	}

	/// <summary>Removes the temporary directory.</summary>
	[TestCleanup]
	public void DeleteStore()
	{
		if (Directory.Exists(_root))
			Directory.Delete(_root, recursive: true);
	}

	/// <summary>The description that was saved is the description that is read back.</summary>
	[TestMethod]
	public async Task ADatasetIsReadBack()
	{
		var project = ProjectId.New();
		var dataset = await SaveAsync(project);

		var read = await _store.GetManifestAsync(project, dataset.Manifest.Id, CancellationToken);

		AreEqual(dataset.Manifest.Id, read.Id);
		AreEqual(dataset.Manifest.Split, read.Split);
		AreEqual(dataset.Manifest.TimeFrame, read.TimeFrame);
		IsTrue(dataset.Manifest.Symbols.SequenceEqual(read.Symbols));
		AreEqual(dataset.Manifest.Records, read.Records);
	}

	/// <summary>A dataset nobody saved is reported as missing, not as empty.</summary>
	[TestMethod]
	public async Task AnUnknownDatasetIsNotFound()
		=> await ThrowsAsync<DatasetNotFoundException>(
			() => _store.GetManifestAsync(ProjectId.New(), DatasetId.New(), CancellationToken).AsTask());

	/// <summary>
	/// The bars go into the shared storage and the project keeps only a description of the range.
	/// </summary>
	[TestMethod]
	public async Task TheProjectKeepsTheRangeAndTheStorageKeepsTheBars()
	{
		var project = ProjectId.New();

		await SaveAsync(project);

		AreEqual(Path.GetFullPath(_bars), _store.BarsFolder);
		IsTrue(Directory.EnumerateFiles(_bars, "*", SearchOption.AllDirectories).Any(), "no bars reached the shared storage.");

		var kept = Directory.EnumerateFiles(Path.Combine(_root, "projects", project.Value), "*", SearchOption.AllDirectories).ToArray();

		AreEqual(1, kept.Length, $"the project holds more than its description: {string.Join(", ", kept)}");
		AreEqual(".json", Path.GetExtension(kept[0]));
	}

	/// <summary>
	/// A second project importing the same range reads the bars the first one stored, and adds nothing.
	/// </summary>
	[TestMethod]
	public async Task TwoProjectsOverTheSameRangeShareTheBars()
	{
		var first = ProjectId.New();
		var second = ProjectId.New();

		await SaveAsync(first);

		var before = Snapshot(_bars);
		var dataset = await SaveAsync(second);

		AreEqual(before, Snapshot(_bars), "importing the same range again changed the shared storage.");

		var read = await _store.LoadAsync(second, dataset.Manifest.Id, Symbol, DataSlices.Development, CancellationToken);

		IsTrue(read.Count > 0, "the second project read nothing back.");
	}

	/// <summary>
	/// Every bar that was saved comes back, in order, with every figure it went in with.
	/// </summary>
	/// <remarks>
	/// Prices are decimal on both sides of the storage. A cent lost to a conversion here would reappear
	/// as a difference in a result nobody could explain.
	/// </remarks>
	[TestMethod]
	public async Task EveryBarComesBackExactlyAsItWasSaved()
	{
		var project = ProjectId.New();
		var dataset = await SaveAsync(project);

		var read = new List<Candle>();

		foreach (var slice in Enum.GetValues<DataSlices>())
			read.AddRange(await _store.LoadAsync(project, dataset.Manifest.Id, Symbol, slice, CancellationToken));

		var written = dataset.Bars[Symbol];

		AreEqual(written.Count, read.Count, "the three slices together are not the dataset that was saved.");

		for (var i = 0; i < written.Count; i++)
			AreEqual(written[i], read[i], $"bar {i} came back as something other than what was written.");

		// Two moments an hour apart compare equal when one of them has forgotten which zone it is in, so
		// the zone is asserted rather than left to the comparison above.
		IsTrue(read.All(b => b.OpenTime.Kind == DateTimeKind.Utc), "a bar came back not saying it is UTC.");
	}

	/// <summary>A slice reads the bars of its own stretch of time and none from either side of it.</summary>
	[TestMethod]
	public async Task ASliceReadsItsOwnBarsAndNoOthers()
	{
		var project = ProjectId.New();
		var dataset = await SaveAsync(project);

		foreach (var slice in Enum.GetValues<DataSlices>())
		{
			var (from, to) = dataset.Manifest.Split.BoundsOf(slice);
			var bars = await _store.LoadAsync(project, dataset.Manifest.Id, Symbol, slice, CancellationToken);

			IsTrue(bars.Count > 0, $"the {slice} slice came back empty.");

			IsTrue(bars.All(b => b.OpenTime >= from && b.OpenTime < to),
				$"the {slice} slice came back holding bars from outside {from:o}..{to:o}.");
		}
	}

	/// <summary>Every file of a folder with its length, so that any change to it shows up as a difference.</summary>
	private static string Snapshot(string folder)
		=> string.Join(
			"\n",
			Directory
				.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
				.Select(f => $"{Path.GetRelativePath(folder, f)}:{new FileInfo(f).Length}")
				.OrderBy(f => f, StringComparer.Ordinal));

	private static IEnumerable<Candle> Generate(int count)
	{
		var price = 100m;

		for (var i = 0; i < count; i++)
		{
			var open = _start + _frame * i;

			// Deliberately not a round number of steps, so that a storage which rounded prices to a step
			// it inferred would be caught rather than flattered.
			price += i % 2 == 0 ? 0.257m : -0.203m;

			yield return new(open, price, price + 0.5m, price - 0.5m, price + 0.13m, 1_000m + i);
		}
	}

	private async Task<ImportedDataset> SaveAsync(ProjectId project)
	{
		var dataset = DatasetBuilder.Build(
			new Dictionary<string, IReadOnlyList<Candle>>(StringComparer.Ordinal) { [Symbol] = [.. Generate(900)] },
			_frame,
			"connector:test@00000000",
			isSynthetic: true);

		await _store.SaveAsync(project, dataset, CancellationToken);

		return dataset;
	}
}
