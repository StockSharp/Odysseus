namespace Odysseus.Persistence;

using System.Text.Json.Serialization;

/// <summary>
/// Finished strategies kept as folders inside the project they came from.
/// </summary>
/// <remarks>
/// Laid out as <c>{root}/{project}/completed/{class name}-{candidate}/</c>, holding the generated C#,
/// the specification it was translated from and one JSON file with everything else. Plain files under
/// plain names, because the point of finishing something is being able to open it without this server:
/// a folder can be zipped, mailed and read in an editor, and a row in a database cannot.
///
/// Nothing here is ever rewritten. A conclusion drawn twice is a conclusion changed, and the service
/// above refuses that before it reaches the disk.
/// </remarks>
public sealed class FileCompletedStore : ICompletedStore
{
	// Spelled the way every other document this product publishes is spelled, because this one is meant
	// to be read and parsed outside the server rather than only written back into it.
	private static readonly JsonSerializerOptions _json = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		Converters = { new TypedIdJsonConverter(), new JsonStringEnumConverter() },
	};

	private const string MetadataFileName = "strategy.json";
	private const string SpecFileName = "spec.json";

	private readonly string _root;

	/// <summary>
	/// Creates the store.
	/// </summary>
	/// <param name="root">Directory the project folders live in.</param>
	public FileCompletedStore(string root)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(root);

		_root = Path.GetFullPath(root);
	}

	/// <inheritdoc />
	public async ValueTask<string> SaveAsync(
		ProjectId project,
		CompletedStrategy strategy,
		string source,
		string specJson,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(strategy);

		var folder = Path.Combine(FolderOf(project), NameOf(strategy));

		Directory.CreateDirectory(folder);

		await File.WriteAllTextAsync(Path.Combine(folder, $"{FileName(strategy.ClassName)}.cs"), source, cancellationToken);
		await File.WriteAllTextAsync(Path.Combine(folder, SpecFileName), specJson, cancellationToken);

		await File.WriteAllTextAsync(
			Path.Combine(folder, MetadataFileName),
			JsonSerializer.Serialize(strategy, _json),
			cancellationToken);

		return folder;
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<CompletedStrategy>> ListAsync(
		ProjectId project,
		CancellationToken cancellationToken)
	{
		var folder = FolderOf(project);

		if (!Directory.Exists(folder))
			return [];

		var found = new List<CompletedStrategy>();

		foreach (var file in Directory
			.EnumerateFiles(folder, MetadataFileName, SearchOption.AllDirectories)
			.Order(StringComparer.Ordinal))
		{
			var text = await File.ReadAllTextAsync(file, cancellationToken);

			found.Add(JsonSerializer.Deserialize<CompletedStrategy>(text, _json));
		}

		return [.. found.OrderBy(s => s.CompletedAt)];
	}

	/// <inheritdoc />
	public string FolderOf(ProjectId project)
		=> Path.Combine(_root, project.Value, "completed");

	// The identifier decides the folder and its alphabet cannot express a separator, so the class name is
	// the only part a caller could have chosen. A C# class name is already letters, digits and
	// underscores; anything else in it would have failed to compile long before reaching here, and is
	// dropped rather than trusted.
	private static string NameOf(CompletedStrategy strategy)
		=> $"{FileName(strategy.ClassName)}-{strategy.Candidate.Value}";

	private static string FileName(string className)
	{
		var kept = new string([.. className.Where(c => char.IsLetterOrDigit(c) || c == '_')]);

		return kept.Length == 0 ? "Strategy" : kept;
	}
}
