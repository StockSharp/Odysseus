namespace StockSharp.Odysseus.Server;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using StockSharp.Odysseus.Broker;
using StockSharp.Odysseus.Compiler;
using StockSharp.Odysseus.Engine;
using StockSharp.Odysseus.Persistence;
using StockSharp.Odysseus.Platform;
using StockSharp.Odysseus.Products;

/// <summary>
/// Entry point of the Odysseus MCP server.
/// </summary>
public static class Program
{
	/// <summary>
	/// Starts the server and serves until the client disconnects.
	/// </summary>
	/// <param name="args">Command line arguments.</param>
	/// <returns>Process exit code.</returns>
	public static async Task<int> Main(string[] args)
	{
		var options = ServerOptions.Read(args);

		var builder = Host.CreateApplicationBuilder(args);

		// Standard output carries MCP protocol frames, so every human-readable line goes to standard
		// error. A single stray write to stdout corrupts the session for good.
		builder.Logging.ClearProviders();
		builder.Logging.AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace);

		builder.Services.AddSingleton(options);
		builder.Services.AddSingleton<ToolGuard>();
		builder.Services.AddSingleton<IClock, SystemClock>();
		builder.Services.AddSingleton(_ => new SqliteProjectStore(options.ProjectsRoot));
		builder.Services.AddSingleton<IProjectStore>(services => services.GetRequiredService<SqliteProjectStore>());
		builder.Services.AddSingleton<IAuditLog>(services => services.GetRequiredService<SqliteProjectStore>());
		builder.Services.AddSingleton<IOperationLog>(_ => new SqliteOperationLog(options.ProjectsRoot));

		// Beside the projects rather than inside one: a project cannot be the keeper of a rule whose only
		// way round is to start another project.
		builder.Services.AddSingleton<IClosedHistoryLedger>(_ => new SqliteClosedHistoryLedger(options.ProjectsRoot));
		builder.Services.AddSingleton<IArtifactStore>(_ => new FileArtifactStore(options.ProjectsRoot));
		builder.Services.AddSingleton<IBarStorage>(_ => new LocalBarStorage(options.MarketData));
		builder.Services.AddSingleton<IDatasetStore>(services => new FileDatasetStore(
			options.ProjectsRoot,
			services.GetRequiredService<IBarStorage>()));
		builder.Services.AddSingleton<ISpecStore>(_ => new FileSpecStore(options.ProjectsRoot));
		builder.Services.AddSingleton<ICompletedStore>(_ => new FileCompletedStore(options.ProjectsRoot));
		builder.Services.AddSingleton<ICandidateStore>(services => services.GetRequiredService<SqliteProjectStore>());
		builder.Services.AddSingleton<IRunStore>(services => services.GetRequiredService<SqliteProjectStore>());
		builder.Services.AddSingleton<IEvaluationStore>(services => services.GetRequiredService<SqliteProjectStore>());

		// A candidate is compiled code with no ceiling on what it will ask for, so it runs in a process of
		// its own. The server holds a client of that process and never an assembly it cannot unload.
		var worker = WorkerOptions.Read();

		builder.Services.AddSingleton(worker);
		builder.Services.AddSingleton(_ => new WorkerHost(worker, EngineIdentity.Current()));
		builder.Services.AddSingleton<IBacktestRunner>(services => new WorkerBacktestRunner(services.GetRequiredService<WorkerHost>()));
		builder.Services.AddSingleton<IStrategyOptimizer>(services => new WorkerStrategyOptimizer(services.GetRequiredService<WorkerHost>()));

		// The assemblies a generated strategy is written against are the ones this deployment carries, and
		// they are the same ones the worker carries because both are built from the same pinned packages.
		// No connector is among them: a connector arrives at run time and is never compiled against.
		builder.Services.AddSingleton(_ => new StrategyBuilder(EngineAssemblies.Paths(AppContext.BaseDirectory)));
		builder.Services.AddSingleton<IStrategyBuilder>(services => services.GetRequiredService<StrategyBuilder>());
		builder.Services.AddSingleton<IStrategyInspector>(services => services.GetRequiredService<StrategyBuilder>());

		// One object answering all four broker ports, because one connector owns one connection. It is
		// registered whether or not a connector was named: registering only when there is one looks tidier
		// and is worse, because the tools that take a broker port then fail while their arguments are
		// being bound, before any code of ours runs, and the agent is told only that something went wrong
		// invoking a tool. Bound to nothing, the same call reaches our code and is told what is missing.
		builder.Services.AddSingleton<IConnectorFactory>(_ => StockSharpConnectorFactory.Create(
			options.ConnectorCache,
			options.Broker,
			options.ConnectorSources,
			options.ConnectorAllow,

			// The literal, at the one call site in this process that could have written anything else.
			// Live trading is a property of a process this server cannot create: it is decided by a file
			// named to the process that is going to trade, and this server removes that variable from the
			// environment of every child it starts. There is nothing here to change and nothing that can
			// be given to this process that would change it.
			TradingMandate.Paper));

		// Registered unconditionally, and on most machines it is registered against a program that is not
		// there. That is the BrokerGateway rule again: a tool whose dependency is missing from the
		// container fails while its arguments are being bound, before any code of ours runs, and the
		// agent is told only that something went wrong invoking a tool. Bound to a path that holds
		// nothing, the same call reaches our code and is told what is missing and what still works.
		builder.Services.AddSingleton(_ => ProductInstallerOptions.Read(options.ProjectsRoot, options.AllowedProducts));
		builder.Services.AddSingleton<IProductInstaller>(services =>
			new ConsoleProductInstaller(services.GetRequiredService<ProductInstallerOptions>()));

		builder.Services.AddSingleton(services => new BrokerGateway(
			services.GetRequiredService<IConnectorFactory>(),
			options.Connector));

		// A storage server, when one is named, is where history comes from; the broker still trades.
		builder.Services.AddSingleton<IHistorySource>(services => options.RemoteStorage is null
			? services.GetRequiredService<BrokerGateway>()
			: new RemoteStorageSource(services.GetRequiredService<IConnectorFactory>(), options.RemoteStorage));
		builder.Services.AddSingleton<IPaperAccount>(services => services.GetRequiredService<BrokerGateway>());
		builder.Services.AddSingleton<ISecurityLookup>(services => services.GetRequiredService<BrokerGateway>());
		builder.Services.AddSingleton<IDeploymentConnector>(services => services.GetRequiredService<BrokerGateway>());

		// A deployment runs in a process of its own that outlives this one. What is registered here is a
		// client of that process and a directory to find it in - never a handle on a running strategy,
		// because a handle dies with the process holding it and takes everything it knew about somebody's
		// open position with it.
		var runner = RunnerOptions.Read();

		builder.Services.AddSingleton(runner);
		builder.Services.AddSingleton(_ => new RunnerRegistry(options.ProjectsRoot, SystemProcessProbe.Instance));
		builder.Services.AddSingleton<IRunnerHost>(services => new RunnerLauncher(
			runner,
			services.GetRequiredService<RunnerRegistry>(),
			new(options.ConnectorCache, options.ConnectorSources, options.ConnectorAllow),
			EngineIdentity.Current()));

		builder.Services.AddSingleton<IMarketProfiler, StockSharpMarketProfiler>();

		builder.Services.AddSingleton(services => new DeterminismService(
			services.GetRequiredService<IProjectStore>(),
			services.GetRequiredService<ICandidateStore>(),
			services.GetRequiredService<ISpecStore>(),
			services.GetRequiredService<IDatasetStore>(),
			services.GetRequiredService<IArtifactStore>(),
			services.GetRequiredService<IStrategyInspector>(),
			services.GetRequiredService<IBacktestRunner>(),
			services.GetRequiredService<IAuditLog>(),
			services.GetRequiredService<IOperationLog>()));

		builder.Services.AddSingleton(services => new HistoryService(
			services.GetRequiredService<IProjectStore>(),
			services.GetRequiredService<IDatasetStore>(),
			services.GetRequiredService<IHistorySource>(),
			services.GetRequiredService<IMarketProfiler>(),
			services.GetRequiredService<IAuditLog>(),
			services.GetRequiredService<IOperationLog>(),
			services.GetRequiredService<IClock>()));

		builder.Services.AddSingleton<IDeploymentStore>(services => services.GetRequiredService<SqliteProjectStore>());

		builder.Services.AddSingleton(services => new DeploymentService(
			services.GetRequiredService<IProjectStore>(),
			services.GetRequiredService<ICandidateStore>(),
			services.GetRequiredService<ISpecStore>(),
			services.GetRequiredService<IDatasetStore>(),
			services.GetRequiredService<IArtifactStore>(),
			services.GetRequiredService<IRunStore>(),
			services.GetRequiredService<IDeploymentStore>(),
			services.GetRequiredService<IRunnerHost>(),
			services.GetRequiredService<IDeploymentConnector>(),
			services.GetRequiredService<IAuditLog>(),
			services.GetRequiredService<IClock>()));

		builder.Services.AddSingleton(services => new ProjectService(
			services.GetRequiredService<IProjectStore>(),
			services.GetRequiredService<IAuditLog>(),
			services.GetRequiredService<IOperationLog>(),
			services.GetRequiredService<IClock>(),
			options.Mode,

			// The same ceiling the command line grants, because it is the same project either of them
			// creates and the same project both of them then work on.
			ResearchBudget.Default));
		builder.Services.AddSingleton(services => new DatasetService(
			services.GetRequiredService<IProjectStore>(),
			services.GetRequiredService<IDatasetStore>(),
			services.GetRequiredService<IRunStore>(),
			services.GetRequiredService<IAuditLog>(),
			services.GetRequiredService<IOperationLog>(),
			services.GetRequiredService<IClock>()));
		builder.Services.AddSingleton(services => new CandidateService(
			services.GetRequiredService<IProjectStore>(),
			services.GetRequiredService<ISpecStore>(),
			services.GetRequiredService<ICandidateStore>(),
			services.GetRequiredService<IArtifactStore>(),
			services.GetRequiredService<IStrategyBuilder>(),
			services.GetRequiredService<IAuditLog>(),
			services.GetRequiredService<IOperationLog>(),
			services.GetRequiredService<IClock>()));
		builder.Services.AddSingleton(services => new BacktestService(
			services.GetRequiredService<IProjectStore>(),
			services.GetRequiredService<ICandidateStore>(),
			services.GetRequiredService<ISpecStore>(),
			services.GetRequiredService<IDatasetStore>(),
			services.GetRequiredService<IRunStore>(),
			services.GetRequiredService<IArtifactStore>(),
			services.GetRequiredService<IBacktestRunner>(),
			services.GetRequiredService<IMarketProfiler>(),
			services.GetRequiredService<IAuditLog>(),
			services.GetRequiredService<IOperationLog>(),
			services.GetRequiredService<IClock>()));
		builder.Services.AddSingleton(services => new OptimizationService(
			services.GetRequiredService<IProjectStore>(),
			services.GetRequiredService<ICandidateStore>(),
			services.GetRequiredService<ISpecStore>(),
			services.GetRequiredService<IDatasetStore>(),
			services.GetRequiredService<IArtifactStore>(),
			services.GetRequiredService<IStrategyOptimizer>(),
			services.GetRequiredService<BacktestService>(),
			services.GetRequiredService<IAuditLog>(),
			services.GetRequiredService<IOperationLog>(),
			services.GetRequiredService<IClock>()));
		builder.Services.AddSingleton(services => new EvaluationService(
			services.GetRequiredService<IProjectStore>(),
			services.GetRequiredService<ICandidateStore>(),
			services.GetRequiredService<ISpecStore>(),
			services.GetRequiredService<IEvaluationStore>(),
			services.GetRequiredService<BacktestService>(),
			services.GetRequiredService<IAuditLog>(),
			services.GetRequiredService<IClock>()));
		builder.Services.AddSingleton(services => new ClosedDataService(
			services.GetRequiredService<ICandidateStore>(),
			services.GetRequiredService<ISpecStore>(),
			services.GetRequiredService<IRunStore>(),
			services.GetRequiredService<IEvaluationStore>(),
			services.GetRequiredService<IProjectStore>(),
			services.GetRequiredService<IDatasetStore>(),
			services.GetRequiredService<IClosedHistoryLedger>(),
			services.GetRequiredService<BacktestService>(),
			services.GetRequiredService<IAuditLog>(),
			services.GetRequiredService<IClock>()));
		builder.Services.AddSingleton(services => new CompletionService(
			services.GetRequiredService<ICandidateStore>(),
			services.GetRequiredService<ISpecStore>(),
			services.GetRequiredService<IRunStore>(),
			services.GetRequiredService<IEvaluationStore>(),
			services.GetRequiredService<IProjectStore>(),
			services.GetRequiredService<IDatasetStore>(),
			services.GetRequiredService<IArtifactStore>(),
			services.GetRequiredService<ICompletedStore>(),
			services.GetRequiredService<IAuditLog>(),
			services.GetRequiredService<IClock>()));
		builder.Services.AddSingleton(services => new SpecService(
			services.GetRequiredService<IProjectStore>(),
			services.GetRequiredService<ISpecStore>(),
			services.GetRequiredService<IAuditLog>(),
			services.GetRequiredService<IOperationLog>(),
			services.GetRequiredService<IClock>()));

		builder.Services
			.AddMcpServer(options => options.ServerInfo = new()
			{
				Name = "StockSharp.Odysseus",
				Title = "StockSharp Odysseus",
				Version = ServerVersion.Current,
			})
			.WithStdioServerTransport()
			.WithToolsFromAssembly();

		var host = builder.Build();

		var bound = await SelectAsync(host.Services.GetRequiredService<BrokerGateway>(), options);

		Console.Error.WriteLine(
			$"Odysseus {ServerVersion.Current} [{options.Mode}] projects at {options.ProjectsRoot}, " +
			(bound ?? "no broker: downloading and trading are unavailable"));

		// Said at start-up because it is the property of this build most likely to surprise somebody who
		// knew the last one: stopping this process no longer stops anything it started.
		Console.Error.WriteLine(
			$"Deployments run in {RunnerOptions.Locate()} and outlive this process. Paper only: live " +
			"trading is decided by configuration read by the process that trades, which this one strips " +
			"from every child it starts.");

		await host.RunAsync();

		return 0;
	}

	/// <summary>
	/// Selects the connector the environment named, when it named one.
	/// </summary>
	/// <param name="gateway">The four broker ports.</param>
	/// <param name="options">How this instance was started.</param>
	/// <returns>What was bound, or <see langword="null"/> when nothing was.</returns>
	/// <remarks>
	/// A connector that will not load does not stop the server. Most of what this product does needs no
	/// account at all, and a machine with no network would otherwise be unable to measure history it has
	/// already imported - so the failure is reported and the server starts without a broker, which is a
	/// state the tools already describe honestly.
	/// </remarks>
	private static async Task<string> SelectAsync(BrokerGateway gateway, ServerOptions options)
	{
		if (!options.HasConnector)
			return null;

		try
		{
			var connector = await gateway.SelectAsync(options.Connector, CancellationToken.None);

			return $"broker {connector.PackageId} {connector.PackageVersion} as {connector.SourceName}";
		}
		catch (Exception error) when (error is not OperationCanceledException)
		{
			Console.Error.WriteLine($"The connector named at start-up could not be loaded: {error.Message}");

			return null;
		}
	}
}
