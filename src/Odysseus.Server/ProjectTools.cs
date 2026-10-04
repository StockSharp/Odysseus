namespace Odysseus.Server;

using System.Collections.Generic;

using Odysseus.Engine;

/// <summary>
/// The project tools an agent sees over MCP.
/// </summary>
/// <remarks>
/// This layer only translates arguments and shapes answers. Everything worth testing lives in
/// <see cref="ProjectService"/>, so the behaviour can be exercised without a transport, and a tool
/// stays short enough to read in one go.
/// </remarks>
[McpServerToolType]
public static class ProjectTools
{
	/// <summary>
	/// Reports what the server is and what it will accept.
	/// </summary>
	/// <param name="guard">Turns a failure into the published error shape.</param>
	/// <param name="service">Project use cases.</param>
	/// <param name="options">How this instance was started.</param>
	/// <param name="gateway">The broker ports and what is bound to them.</param>
	/// <param name="runners">Where the processes that trade keep their records.</param>
	/// <returns>The description of the running server.</returns>
	[McpServerTool(Name = "describe_server")]
	[Description("Report the server version, the mode it runs in, and whether a broker connector is " +
		"bound. Call this first: half of what this server can do depends on whether a connector was " +
		"chosen, and being refused mid-plan is a worse way to find that out. It also says where the " +
		"deployments this server starts keep their records, which is what a later session reads them " +
		"back from.")]
	public static Task<object> DescribeServer(
		ToolGuard guard,
		ProjectService service,
		ServerOptions options,
		BrokerGateway gateway,
		RunnerRegistry runners)
		=> guard.Run(nameof(DescribeServer), () =>
	{
		var description = service.Describe(ServerVersion.Current);
		var connector = gateway.Current;

		return (object)new
		{
			product = description.Product,
			version = description.Version,
			mode = description.Mode.ToString(),

			// Which half of the product is available. Without it the only way to find out is to be
			// refused by a tool, which is a discovery an agent makes after choosing a plan around it.
			hasBroker = connector is not null,
			connector = connector is null
				? null
				: new
				{
					packageId = connector.PackageId,
					packageVersion = connector.PackageVersion,
					adapter = connector.AdapterTypeName,
					sourceName = connector.SourceName,
				},

			// A broker is a connector this server downloads and loads while it runs, so the two things
			// that bound which ones it will take are reported rather than discovered by being refused.
			// The flag covers all three connector tools, because reading a connector builds it as
			// surely as choosing one does.
			connectorSelectable = options.CanLoadConnectors,
			connectorSources = options.ConnectorSources,

			// Where history lives and where it is imported from, so an agent knows before asking whether an
			// import goes to the broker or to a storage server.
			marketData = options.MarketData,
			remoteStorage = options.RemoteStorage?.Address,

			// The other half of the same idea, and reported here for the same reason: installing a
			// StockSharp product is off unless an operator named the products that may be installed, and
			// an agent that learns that from being refused has already planned around having it.
			// get_installer_state says what is missing; this says only whether there is any point asking.
			canInstallProducts = options.CanInstallProducts,
			allowedProducts = options.AllowedProducts,

			// Stated rather than implied, and stated as a property rather than as a procedure. Live
			// trading is decided by configuration read at start-up in a process this one does not
			// launch: there is nothing here to change, and nothing that can be given to this server
			// that would change it. How an operator would arrange one is not in any tool description,
			// because a helpful recipe dictated to a tired person is the failure being designed against.
			liveTradingReachableFromHere = false,

			// A deployment survives this process, so where its record lives is part of what this server
			// is. A session that finds nothing where it expected a runner is usually a session pointed
			// at a different projects root than the one that started it.
			runnerRegistry = runners.Directory,
			runnersRecorded = runners.Records().Count,
			whatHappensWhenYouDisconnect = "Deployments keep trading. They run in processes of their " +
				"own that outlive this server, so disconnecting stops nothing and starting one makes it " +
				"yours to stop. Every deployment this server starts trades on a paper account.",
			whatNeedsABroker = "import_history downloads real history; lookup_symbols and " +
				"lookup_option_contracts ask the broker which instruments exist; deploy_candidate needs a " +
				"connector named for the process that will trade it, and get_account_state reaches an " +
				"account. Everything else - import_demo_dataset, analyze_market, the backtests, " +
				"evaluate_candidate, measure_on_closed_data, complete_strategy - works on history that is " +
				"already imported. " +
				(options.CanLoadConnectors
					? "Call list_connectors and select_connector to bind one."
					: "This instance is hosted: its connector is a start-up decision, and list_connectors, " +
						"describe_connector and select_connector are all refused here."),
		};
	});

	/// <summary>
	/// Creates a research project.
	/// </summary>
	/// <param name="guard">Turns a failure into the published error shape.</param>
	/// <param name="service">Project use cases.</param>
	/// <param name="name">Name for the project.</param>
	/// <param name="operationKey">Key that makes a repeated call return the first result.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The created project.</returns>
	[McpServerTool(Name = "create_project")]
	[Description("Create a research project. Pass an operationKey you generate once per intent: repeating a " +
		"call with the same key returns the project the first call created instead of making a second one, " +
		"which is what makes a retry after a dropped connection safe.")]
	public static Task<object> CreateProject(
		ToolGuard guard,
		ProjectService service,
		[Description("Name for the project, as a person would write it.")] string name,
		[Description("Caller-generated key identifying this request, reused on retries of the same intent.")] string operationKey,
		CancellationToken cancellationToken)
		=> guard.RunAsync(nameof(CreateProject), async ()
			=> Describe(await service.CreateProjectAsync(name, operationKey, Actors.Agent, cancellationToken)));

	/// <summary>
	/// Lists the projects on this server.
	/// </summary>
	/// <param name="guard">Turns a failure into the published error shape.</param>
	/// <param name="service">Project use cases.</param>
	/// <param name="offset">How many to skip.</param>
	/// <param name="limit">How many to return.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The projects, most recently changed first.</returns>
	[McpServerTool(Name = "list_projects")]
	[Description("List the research projects on this server, most recently changed first, a page at a time.")]
	public static Task<object> ListProjects(
		ToolGuard guard,
		ProjectService service,
		CancellationToken cancellationToken,
		[Description(Page.OffsetDescription)] int offset = 0,
		[Description(Page.LimitDescription)] int limit = 0)
		=> guard.RunAsync(nameof(ListProjects), async () =>
		{
			var all = await service.ListProjectsAsync(cancellationToken);

			var (projects, window) = Page.Of(all, offset, limit, Describe);

			return new { projects, window };
		});

	/// <summary>
	/// Reads the state of one project.
	/// </summary>
	/// <param name="guard">Turns a failure into the published error shape.</param>
	/// <param name="service">Project use cases.</param>
	/// <param name="projectId">Project to read.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The project.</returns>
	[McpServerTool(Name = "get_project_state")]
	[Description("Read one project: its name, status, dataset if any, and how much of its research " +
		"budget is left. The budget is a server-side ceiling, so check it before planning a long search.")]
	public static Task<object> GetProjectState(
		ToolGuard guard,
		ProjectService service,
		[Description("Identifier returned by create_project or list_projects.")] string projectId,
		CancellationToken cancellationToken)
		=> guard.RunAsync(nameof(GetProjectState), async ()
			=> Describe(await service.OpenProjectAsync(Ids.Project(projectId), cancellationToken)));

	/// <summary>
	/// Renames a project.
	/// </summary>
	/// <param name="guard">Turns a failure into the published error shape.</param>
	/// <param name="service">Project use cases.</param>
	/// <param name="projectId">Project to rename.</param>
	/// <param name="name">New name.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The renamed project.</returns>
	[McpServerTool(Name = "rename_project")]
	[Description("Rename a project. The previous name is kept in the audit trail.")]
	public static Task<object> RenameProject(
		ToolGuard guard,
		ProjectService service,
		[Description("Identifier of the project to rename.")] string projectId,
		[Description("New name for the project.")] string name,
		CancellationToken cancellationToken)
		=> guard.RunAsync(nameof(RenameProject), async ()
			=> Describe(await service.RenameProjectAsync(Ids.Project(projectId), name, Actors.Agent, cancellationToken)));

	/// <summary>
	/// Reads the permanent record of a project.
	/// </summary>
	/// <param name="guard">Turns a failure into the published error shape.</param>
	/// <param name="service">Project use cases.</param>
	/// <param name="projectId">Project to read.</param>
	/// <param name="offset">How many to skip.</param>
	/// <param name="limit">How many to return.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The audit entries, oldest first.</returns>
	[McpServerTool(Name = "get_audit_log")]
	[Description("Read the permanent record of a project: what happened, who caused it — user, agent or the " +
		"server itself — and when. Entries are append-only and their sequence has no gaps, so a missing " +
		"step is visible. The record grows with everything the project does, so this answers a page at a " +
		"time and says how to ask for the next.")]
	public static Task<object> GetAuditLog(
		ToolGuard guard,
		ProjectService service,
		[Description("Identifier of the project whose record to read.")] string projectId,
		CancellationToken cancellationToken,
		[Description(Page.OffsetDescription)] int offset = 0,
		[Description(Page.LimitDescription)] int limit = 0)
		=> guard.RunAsync(nameof(GetAuditLog), async () =>
		{
			var trail = await service.ReadAuditAsync(Ids.Project(projectId), cancellationToken);

			var (events, window) = Page.Of(trail, offset, limit, e => new
			{
				sequence = e.Sequence,
				type = e.Type.ToString(),
				actor = e.Actor.ToString(),
				occurredAt = e.OccurredAt,
				detail = e.Detail,
				payloadHash = e.PayloadHash,
			});

			return new { events, window };
		});

	private static object Describe(ResearchProject project)
		=> new
		{
			projectId = project.Id.Value,
			name = project.Name,
			status = project.Status.ToString(),
			createdAt = project.CreatedAt,
			updatedAt = project.UpdatedAt,
			dataset = project.Dataset.IsEmpty ? null : project.Dataset.Value,
			budget = new
			{
				backtestsRemaining = project.Budget.MaxBacktests - project.Budget.ClaimedBacktests,
				backtestsTotal = project.Budget.MaxBacktests,
				candidatesRemaining = project.Budget.MaxCandidates - project.Budget.ClaimedCandidates,
				candidatesTotal = project.Budget.MaxCandidates,
			},
		};
}
