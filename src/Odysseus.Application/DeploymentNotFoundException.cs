namespace StockSharp.Odysseus.Application;

/// <summary>
/// Thrown when a deployment that was asked for does not exist.
/// </summary>
public sealed class DeploymentNotFoundException : Exception
{
	/// <summary>
	/// Creates the exception.
	/// </summary>
	/// <param name="id">Deployment that was not found.</param>
	public DeploymentNotFoundException(DeploymentId id)
		: base($"Deployment {id} does not exist in this project.")
	{
		Id = id;
	}

	/// <summary>Deployment that was not found.</summary>
	public DeploymentId Id { get; }
}
