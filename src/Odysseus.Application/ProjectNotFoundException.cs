namespace Odysseus.Application;

using System;

using Odysseus.Domain;

/// <summary>
/// Thrown when a project that was asked for does not exist.
/// </summary>
public sealed class ProjectNotFoundException : Exception
{
	/// <summary>
	/// Creates the exception.
	/// </summary>
	/// <param name="id">Project that was not found.</param>
	public ProjectNotFoundException(ProjectId id)
		: base($"Project {id} does not exist.")
	{
		Id = id;
	}

	/// <summary>Project that was not found.</summary>
	public ProjectId Id { get; }
}
