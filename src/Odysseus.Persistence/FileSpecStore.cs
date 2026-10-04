namespace Odysseus.Persistence;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Odysseus.Application;
using Odysseus.Domain;

/// <summary>
/// Specifications kept as files inside the project they belong to.
/// </summary>
/// <remarks>
/// Each revision is its own file, named by its position and its identity, so the history of what was
/// tried is legible from the folder alone. Nothing is ever rewritten: a change is the next file.
/// </remarks>
public sealed class FileSpecStore : ISpecStore
{
	private static readonly JsonSerializerOptions _json = new()
	{
		WriteIndented = true,
		Converters = { new TypedIdJsonConverter() },
	};

	private readonly string _root;
	private readonly SemaphoreSlim _gate = new(1, 1);

	/// <summary>
	/// Creates the store.
	/// </summary>
	/// <param name="root">Directory the project folders live in.</param>
	public FileSpecStore(string root)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(root);

		_root = Path.GetFullPath(root);
	}

	/// <inheritdoc />
	public async ValueTask<SpecRevision> AddAsync(
		ProjectId project,
		string json,
		Actors author,
		DateTime now,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(json);

		if (now.Kind != DateTimeKind.Utc)
			throw new ArgumentException("Moments are recorded in UTC.", nameof(now));

		var folder = FolderOf(project);

		Directory.CreateDirectory(folder);

		// Revision numbers are handed out under a gate: two agents proposing at once must not be given
		// the same position, or the history stops being a sequence.
		await _gate.WaitAsync(cancellationToken);

		try
		{
			var revision = Directory.EnumerateFiles(folder, "*.json").Count() + 1;

			var record = new SpecRevision(
				SpecId.New(),
				revision,
				json,
				Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))),
				author,
				now);

			await File.WriteAllTextAsync(PathOf(project, record), JsonSerializer.Serialize(record, _json), cancellationToken);

			return record;
		}
		finally
		{
			_gate.Release();
		}
	}

	/// <inheritdoc />
	public async ValueTask<SpecRevision> GetAsync(ProjectId project, SpecId spec, CancellationToken cancellationToken)
	{
		var folder = FolderOf(project);

		if (!Directory.Exists(folder))
			throw new SpecNotFoundException(spec);

		var file = Directory
			.EnumerateFiles(folder, $"*-{spec.Value}.json")
			.FirstOrDefault() ?? throw new SpecNotFoundException(spec);

		return await ReadAsync(file, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<SpecRevision>> ListAsync(ProjectId project, CancellationToken cancellationToken)
	{
		var folder = FolderOf(project);

		if (!Directory.Exists(folder))
			return [];

		var revisions = new List<SpecRevision>();

		foreach (var file in Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.Ordinal))
			revisions.Add(await ReadAsync(file, cancellationToken));

		return [.. revisions.OrderBy(r => r.Revision)];
	}

	private static async ValueTask<SpecRevision> ReadAsync(string file, CancellationToken cancellationToken)
		=> JsonSerializer.Deserialize<SpecRevision>(await File.ReadAllTextAsync(file, cancellationToken), _json);

	private string FolderOf(ProjectId project)
		=> Path.Combine(_root, project.Value, "specs");

	private string PathOf(ProjectId project, SpecRevision revision)
		=> Path.Combine(
			FolderOf(project),
			$"{revision.Revision.ToString("D4", CultureInfo.InvariantCulture)}-{revision.Id.Value}.json");
}
