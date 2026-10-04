namespace Odysseus.Application;

/// <summary>
/// Thrown when a specification that was asked for does not exist.
/// </summary>
public sealed class SpecNotFoundException : Exception
{
	/// <summary>
	/// Creates the exception.
	/// </summary>
	/// <param name="id">Specification that was not found.</param>
	public SpecNotFoundException(SpecId id)
		: base($"Specification {id} does not exist in this project.")
	{
		Id = id;
	}

	/// <summary>Specification that was not found.</summary>
	public SpecId Id { get; }
}
