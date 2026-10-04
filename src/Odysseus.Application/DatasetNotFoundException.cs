namespace Odysseus.Application;

/// <summary>
/// Thrown when a dataset that was asked for does not exist.
/// </summary>
public sealed class DatasetNotFoundException : Exception
{
	/// <summary>
	/// Creates the exception.
	/// </summary>
	/// <param name="id">Dataset that was not found.</param>
	public DatasetNotFoundException(DatasetId id)
		: base($"Dataset {id} does not exist in this project.")
	{
		Id = id;
	}

	/// <summary>Dataset that was not found.</summary>
	public DatasetId Id { get; }
}
