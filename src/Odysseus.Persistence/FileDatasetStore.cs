namespace StockSharp.Odysseus.Persistence;

using System.Text.Json.Serialization;

/// <summary>
/// Dataset descriptions kept beside their project as files, with the bars in the shared market-data storage.
/// </summary>
/// <remarks>
/// The description stays readable, because a person looking at a project folder should be able to see
/// what the research was run against without running anything.
/// </remarks>
public sealed class FileDatasetStore : IDatasetStore
{
	private static readonly JsonSerializerOptions _json = new()
	{
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.Never,
		Converters = { new TypedIdJsonConverter() },
	};

	private readonly string _root;
	private readonly IBarStorage _bars;

	/// <summary>
	/// Creates the store.
	/// </summary>
	/// <param name="root">Folder holding every project.</param>
	/// <param name="bars">The market-data storage every project shares.</param>
	public FileDatasetStore(string root, IBarStorage bars)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(root);

		_bars = bars ?? throw new ArgumentNullException(nameof(bars));
		_root = Path.GetFullPath(root);
	}

	/// <inheritdoc />
	public string BarsFolder => _bars.Folder;

	/// <inheritdoc />
	public async ValueTask SaveAsync(ProjectId project, ImportedDataset dataset, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(dataset);

		foreach (var (symbol, candles) in dataset.Bars)
			await _bars.WriteAsync(symbol, dataset.Manifest.TimeFrame, candles, cancellationToken);

		var path = PathOf(project, dataset.Manifest.Id);

		Directory.CreateDirectory(Path.GetDirectoryName(path));

		// The description is written last, so its presence is what says the bars are in place.
		await File.WriteAllTextAsync(path, JsonSerializer.Serialize(dataset.Manifest, _json), cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<DatasetManifest> GetManifestAsync(
		ProjectId project,
		DatasetId dataset,
		CancellationToken cancellationToken)
	{
		var path = PathOf(project, dataset);

		if (!File.Exists(path))
			throw new DatasetNotFoundException(dataset);

		var text = await File.ReadAllTextAsync(path, cancellationToken);

		return JsonSerializer.Deserialize<DatasetManifest>(text, _json) ?? throw new DatasetNotFoundException(dataset);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<Candle>> LoadAsync(
		ProjectId project,
		DatasetId dataset,
		string symbol,
		DataSlices slice,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

		var manifest = await GetManifestAsync(project, dataset, cancellationToken);

		if (!manifest.Symbols.Contains(symbol, StringComparer.Ordinal))
		{
			throw new ArgumentException(
				$"Dataset {dataset} holds no bars for '{symbol}'. It covers {string.Join(", ", manifest.Symbols)}.",
				nameof(symbol));
		}

		var (from, to) = manifest.Split.BoundsOf(slice);

		return await _bars.ReadAsync(symbol, manifest.TimeFrame, from, to, cancellationToken);
	}

	private string PathOf(ProjectId project, DatasetId dataset)
		=> Path.Combine(_root, project.Value, "datasets", $"{dataset.Value}.json");
}
