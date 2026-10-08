namespace StockSharp.Odysseus.Application;

/// <summary>
/// Thrown when an artifact the project refers to is not in the store.
/// </summary>
public sealed class ArtifactNotFoundException : Exception
{
	/// <summary>
	/// Creates the exception.
	/// </summary>
	/// <param name="id">Artifact that is missing.</param>
	public ArtifactNotFoundException(ArtifactId id)
		: base($"Artifact {id} is not stored.")
	{
		Id = id;
	}

	/// <summary>Artifact that is missing.</summary>
	public ArtifactId Id { get; }
}
