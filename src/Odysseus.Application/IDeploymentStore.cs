namespace Odysseus.Application;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Odysseus.Domain;

/// <summary>
/// Keeps the paper deployments of a project.
/// </summary>
public interface IDeploymentStore
{
	/// <summary>
	/// Records a deployment.
	/// </summary>
	/// <param name="project">Project the deployment belongs to.</param>
	/// <param name="deployment">Deployment to record.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	ValueTask AddAsync(ProjectId project, Deployment deployment, CancellationToken cancellationToken);

	/// <summary>
	/// Replaces a deployment that is already recorded.
	/// </summary>
	/// <param name="project">Project the deployment belongs to.</param>
	/// <param name="deployment">Deployment to save.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	ValueTask UpdateAsync(ProjectId project, Deployment deployment, CancellationToken cancellationToken);

	/// <summary>
	/// Reads one deployment.
	/// </summary>
	/// <param name="project">Project the deployment belongs to.</param>
	/// <param name="deployment">Deployment to read.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The deployment.</returns>
	/// <exception cref="DeploymentNotFoundException">The deployment does not exist.</exception>
	ValueTask<Deployment> GetAsync(ProjectId project, DeploymentId deployment, CancellationToken cancellationToken);

	/// <summary>
	/// Lists the deployments of a project, newest first.
	/// </summary>
	/// <param name="project">Project to list.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The deployments.</returns>
	ValueTask<IReadOnlyList<Deployment>> ListAsync(ProjectId project, CancellationToken cancellationToken);
}
