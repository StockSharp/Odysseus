namespace Odysseus.Application;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Odysseus.Domain;

/// <summary>
/// What the server can tell a connecting agent about itself.
/// </summary>
/// <param name="Product">Product name.</param>
/// <param name="Version">Version of the server.</param>
/// <param name="Mode">How the server was started.</param>
public sealed record ServerDescription(
	string Product,
	string Version,
	ServerModes Mode);

/// <summary>
/// The project use cases behind the MCP tools.
/// </summary>
/// <remarks>
/// The tool layer stays a thin translation of arguments; everything worth testing lives here, so the
/// behaviour can be exercised without standing up a transport.
/// </remarks>
public sealed class ProjectService
{
	private readonly IProjectStore _projects;
	private readonly IAuditLog _audit;
	private readonly IOperationLog _operations;

	// Creating a project cannot belong to a project, so it belongs to the installation. Renaming and
	// every later step name their own project instead.
	private const string ServerScope = "server";
	private readonly IClock _clock;
	private readonly ServerModes _mode;
	private readonly ResearchBudgetState _defaultBudget;

	/// <summary>
	/// Creates the service.
	/// </summary>
	/// <param name="projects">Where projects are kept.</param>
	/// <param name="audit">Where the permanent record is kept.</param>
	/// <param name="operations">Where operation keys are remembered.</param>
	/// <param name="clock">Source of the current moment.</param>
	/// <param name="mode">How the server was started.</param>
	/// <param name="defaultBudget">Allowance a new project starts with.</param>
	public ProjectService(
		IProjectStore projects,
		IAuditLog audit,
		IOperationLog operations,
		IClock clock,
		ServerModes mode,
		ResearchBudgetState defaultBudget)
	{
		_projects = projects ?? throw new ArgumentNullException(nameof(projects));
		_audit = audit ?? throw new ArgumentNullException(nameof(audit));
		_operations = operations ?? throw new ArgumentNullException(nameof(operations));
		_clock = clock ?? throw new ArgumentNullException(nameof(clock));
		_defaultBudget = defaultBudget ?? throw new ArgumentNullException(nameof(defaultBudget));
		_mode = mode;
	}

	/// <summary>
	/// Describes the server, so an agent can see what it is allowed to do before it tries.
	/// </summary>
	/// <param name="version">Version of the running server.</param>
	/// <returns>The description.</returns>
	public ServerDescription Describe(string version)
		=> new(
			Product: "Odysseus",
			Version: version,
			Mode: _mode);

	/// <summary>
	/// Creates a research project, or returns the one an earlier call with the same key created.
	/// </summary>
	/// <param name="name">Name for the project.</param>
	/// <param name="operationKey">Key that makes a repeated call return the first result.</param>
	/// <param name="actor">Who is asking.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The project.</returns>
	public async ValueTask<ResearchProject> CreateProjectAsync(
		string name,
		string operationKey,
		Actors actor,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);

		var recorded = await _operations.TryGetAsync(ServerScope, operationKey, cancellationToken);

		if (recorded is not null)
			return await _projects.OpenAsync(ProjectId.Parse(recorded), cancellationToken);

		var project = ResearchProject.Create(name, _defaultBudget, _clock.UtcNow);

		// The key is claimed before the project exists, so two calls racing on the same key cannot both
		// go on to create one. The loser adopts the winner's project instead of making a second.
		var claimed = await _operations.RecordAsync(ServerScope, operationKey, project.Id.Value, cancellationToken);

		if (claimed != project.Id.Value)
			return await _projects.OpenAsync(ProjectId.Parse(claimed), cancellationToken);

		await _projects.CreateAsync(project, cancellationToken);

		await _audit.AppendAsync(
			project.Id,
			AuditEventTypes.ProjectCreated,
			actor,
			$"Project '{project.Name}' created.",
			"-",
			cancellationToken);

		return project;
	}

	/// <summary>
	/// Opens a project.
	/// </summary>
	/// <param name="id">Project to open.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The project.</returns>
	public ValueTask<ResearchProject> OpenProjectAsync(ProjectId id, CancellationToken cancellationToken)
		=> _projects.OpenAsync(id, cancellationToken);

	/// <summary>
	/// Lists the projects on this server.
	/// </summary>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The projects, most recently changed first.</returns>
	public ValueTask<IReadOnlyList<ResearchProject>> ListProjectsAsync(CancellationToken cancellationToken)
		=> _projects.ListAsync(cancellationToken);

	/// <summary>
	/// Renames a project.
	/// </summary>
	/// <param name="id">Project to rename.</param>
	/// <param name="name">New name.</param>
	/// <param name="actor">Who is asking.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The renamed project.</returns>
	public async ValueTask<ResearchProject> RenameProjectAsync(
		ProjectId id,
		string name,
		Actors actor,
		CancellationToken cancellationToken)
	{
		var project = await _projects.OpenAsync(id, cancellationToken);
		var renamed = project.Rename(name, _clock.UtcNow);

		await _projects.UpdateAsync(renamed, cancellationToken);

		await _audit.AppendAsync(
			id,
			AuditEventTypes.ProjectRenamed,
			actor,
			$"Renamed from '{project.Name}' to '{renamed.Name}'.",
			"-",
			cancellationToken);

		return renamed;
	}

	/// <summary>
	/// Reads the permanent record of a project.
	/// </summary>
	/// <param name="id">Project to read.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The entries, oldest first.</returns>
	public ValueTask<IReadOnlyList<AuditEvent>> ReadAuditAsync(ProjectId id, CancellationToken cancellationToken)
		=> _audit.ReadAsync(id, cancellationToken);
}
