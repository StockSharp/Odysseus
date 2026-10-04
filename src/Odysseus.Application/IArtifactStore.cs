namespace Odysseus.Application;

using System;
using System.Threading;
using System.Threading.Tasks;

using Odysseus.Domain;

/// <summary>
/// What the store knows about one artifact.
/// </summary>
/// <param name="Id">Identifier derived from the content.</param>
/// <param name="Sha256">Content hash in lowercase hexadecimal, as the reports quote it.</param>
/// <param name="Length">Size of the content in bytes.</param>
public sealed record ArtifactDescriptor(ArtifactId Id, string Sha256, long Length);

/// <summary>
/// Stores the files a research project produces — sources, assemblies, result series, reports —
/// addressed by their content.
/// </summary>
/// <remarks>
/// Every claim the product makes about a candidate is a chain of hashes: this specification produced
/// this source, which produced this assembly, which produced these metrics. The chain is only worth
/// something if a stored artifact can be shown to be exactly what it was when it was written, so
/// reading verifies rather than trusts.
///
/// Every operation names the project. Content-addressed storage tempts one to keep a single pool and
/// let identical bytes be shared, and the cost of that is a project that is no longer a thing you can
/// carry: its database would reference files that live somewhere else entirely, and a copied folder
/// would open with every source and assembly missing.
/// </remarks>
public interface IArtifactStore
{
	/// <summary>
	/// Stores content, or recognises that the same content is already stored.
	/// </summary>
	/// <param name="project">Project the artifact belongs to.</param>
	/// <param name="content">Content to store.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The descriptor of the stored artifact.</returns>
	ValueTask<ArtifactDescriptor> PutAsync(ProjectId project, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);

	/// <summary>
	/// Reads an artifact, verifying that it still hashes to its identifier.
	/// </summary>
	/// <param name="project">Project the artifact belongs to.</param>
	/// <param name="id">Artifact to read.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The content.</returns>
	/// <exception cref="ArtifactNotFoundException">The artifact is not stored.</exception>
	/// <exception cref="ArtifactCorruptedException">The stored bytes no longer match the identifier.</exception>
	ValueTask<byte[]> ReadAsync(ProjectId project, ArtifactId id, CancellationToken cancellationToken);

	/// <summary>
	/// Checks that an artifact is stored and intact.
	/// </summary>
	/// <param name="project">Project the artifact belongs to.</param>
	/// <param name="id">Artifact to check.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns><see langword="false"/> when the artifact is missing or no longer matches its identifier.</returns>
	ValueTask<bool> VerifyAsync(ProjectId project, ArtifactId id, CancellationToken cancellationToken);
}
