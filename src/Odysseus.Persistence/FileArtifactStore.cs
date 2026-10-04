namespace Odysseus.Persistence;

using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using Odysseus.Application;
using Odysseus.Domain;

/// <summary>
/// Artifact store backed by a directory tree, addressed by content hash.
/// </summary>
/// <remarks>
/// Files are laid out as <c>{root}/{project}/artifacts/{first two hex digits}/{full hex hash}</c>. The
/// name is the hash itself, so nothing outside the file has to be trusted to know what it should
/// contain, and the two leading digits keep any single directory from collecting every artifact of a
/// long research run. A write lands on a temporary name and is renamed into place, so an interrupted
/// write leaves nothing that a later verification would report as corruption.
///
/// The project is part of the path rather than a shared pool keyed by hash alone. Sharing identical
/// bytes between projects would save a little disk and cost the thing that matters: a project folder
/// that can be zipped, carried elsewhere and opened whole.
/// </remarks>
public sealed class FileArtifactStore : IArtifactStore
{
	private readonly string _root;

	/// <summary>
	/// Creates the store.
	/// </summary>
	/// <param name="root">Directory the project folders live in. Created if it does not exist.</param>
	public FileArtifactStore(string root)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(root);

		_root = Path.GetFullPath(root);

		Directory.CreateDirectory(_root);
	}

	/// <inheritdoc />
	public async ValueTask<ArtifactDescriptor> PutAsync(ProjectId project, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var id = ArtifactId.FromContent(content.Span);
		var path = PathOf(project, id);

		if (File.Exists(path))
			return Describe(id, content.Length);

		Directory.CreateDirectory(Path.GetDirectoryName(path));

		// A partially written file under the final name would later read as a corrupt artifact, so the
		// content is completed under a private name and only then given the name it is addressed by.
		var staging = $"{path}.{Guid.NewGuid():n}.tmp";

		try
		{
			await using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
				await stream.WriteAsync(content, cancellationToken);

			// Several writers of the same content race here; they are writing identical bytes under the
			// same final name, so whoever loses simply discards its own copy.
			File.Move(staging, path, overwrite: false);
		}
		catch (IOException) when (File.Exists(path))
		{
			// Another writer stored the same content first.
		}
		finally
		{
			if (File.Exists(staging))
				File.Delete(staging);
		}

		return Describe(id, content.Length);
	}

	/// <inheritdoc />
	public async ValueTask<byte[]> ReadAsync(ProjectId project, ArtifactId id, CancellationToken cancellationToken)
	{
		var path = PathOf(project, id);

		if (!File.Exists(path))
			throw new ArtifactNotFoundException(id);

		var content = await File.ReadAllBytesAsync(path, cancellationToken);
		var actual = Convert.ToHexStringLower(SHA256.HashData(content));

		if (actual != id.Sha256)
			throw new ArtifactCorruptedException(id, actual);

		return content;
	}

	/// <inheritdoc />
	public async ValueTask<bool> VerifyAsync(ProjectId project, ArtifactId id, CancellationToken cancellationToken)
	{
		var path = PathOf(project, id);

		if (!File.Exists(path))
			return false;

		await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

		var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));

		return actual == id.Sha256;
	}

	private static ArtifactDescriptor Describe(ArtifactId id, long length)
		=> new(id, id.Sha256, length);

	private string PathOf(ProjectId project, ArtifactId id)
	{
		// The identifier and the project decide the whole path, and neither alphabet can express a
		// separator or a parent directory, so no caller-supplied text can reach outside the root.
		var hex = id.Sha256;

		return Path.Combine(_root, project.Value, "artifacts", hex[..2], hex);
	}
}
