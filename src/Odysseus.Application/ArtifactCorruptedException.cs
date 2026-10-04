namespace Odysseus.Application;

using System;

using Odysseus.Domain;

/// <summary>
/// Thrown when stored bytes no longer hash to the identifier they are filed under.
/// </summary>
/// <remarks>
/// This is never a normal outcome. It means the file was changed outside the product, so the chain of
/// hashes that every report rests on is broken for that artifact.
/// </remarks>
public sealed class ArtifactCorruptedException : Exception
{
	/// <summary>
	/// Creates the exception.
	/// </summary>
	/// <param name="id">Artifact whose content no longer matches.</param>
	/// <param name="actualSha256">Hash the stored bytes actually have.</param>
	public ArtifactCorruptedException(ArtifactId id, string actualSha256)
		: base($"Artifact {id} no longer matches its content hash; the stored bytes hash to {actualSha256}.")
	{
		Id = id;
		ActualSha256 = actualSha256;
	}

	/// <summary>Artifact whose content no longer matches.</summary>
	public ArtifactId Id { get; }

	/// <summary>Hash the stored bytes actually have.</summary>
	public string ActualSha256 { get; }
}
