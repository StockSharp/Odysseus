namespace Odysseus.Server.Tests;

using System.Linq;
using System.Text.Json;

using Odysseus.Domain;
using Odysseus.Persistence;
using Odysseus.Platform;
using Odysseus.Spec;

/// <summary>
/// The paper-trading tools as an agent calls them: what each passes on to the deployment and what it
/// hands back.
/// </summary>
/// <remarks>
/// The deployments themselves are tested where they are run. What is pinned here is what the tools add on
/// the way: that the choice about an open position reaches the runner as it was made, which runners are
/// reported as nobody's, and whose account is read.
/// </remarks>
[TestClass]
public class PaperToolsTests : OdysseusTestBase
{
	private const string Spec =
		"""
		{
		  "name": "Above its average",
		  "thesis": "A price above its own recent average keeps going for a few bars.",
		  "allowLong": true,
		  "allowShort": false,
		  "timeFrame": "00:05:00",
		  "warmupBars": 45,
		  "entries": [
		    { "id": "e1", "direction": "Long", "condition": {
		        "kind": "Compare",
		        "left": { "kind": "Field", "field": "Close" },
		        "operator": "GreaterThan",
		        "right": { "kind": "Indicator", "name": "sma", "length": { "kind": "Constant", "value": 20 }, "source": "Close" } } }
		  ],
		  "exits": [ { "id": "x1", "kind": "TimeExit", "direction": "Long", "length": { "kind": "Constant", "value": 5 } } ],
		  "parameters": [],
		  "risk": { "maxPositionPercent": 0.10, "maxDailyLossPercent": 0.02 }
		}
		""";

	private string _root;
	private SqliteProjectStore _store;
	private SqliteOperationLog _operations;
	private Runners _runners;
	private DeploymentService _deployments;
	private ProjectService _projects;
	private DatasetService _datasets;
	private CandidateService _candidates;
	private FileSpecStore _specs;

	/// <summary>Builds the services over temporary storage.</summary>
	[TestInitialize]
	public void CreateServices()
	{
		_root = Path.Combine(Path.GetTempPath(), "odysseus-paper-tools", Guid.NewGuid().ToString("n"));
		_store = new SqliteProjectStore(_root);
		_operations = new SqliteOperationLog(_root);
		_specs = new FileSpecStore(_root);
		_runners = new Runners();

		var artifacts = new FileArtifactStore(_root);
		var datasets = new FileDatasetStore(_root, new LocalBarStorage(Path.Combine(_root, "market-data")));
		var clock = new SystemClock();

		_projects = new ProjectService(_store, _store, _operations, clock, ServerModes.Local, ResearchBudget.Default);
		_datasets = new DatasetService(_store, datasets, _store, _store, _operations, clock);
		_candidates = new CandidateService(_store, _specs, _store, artifacts, new Builder(), _store, _operations, clock);

		_deployments = new DeploymentService(
			_store, _store, _specs, datasets, artifacts, _store, _store, _runners, new Named(), _store, clock);
	}

	/// <summary>Releases storage.</summary>
	[TestCleanup]
	public void DeleteServices()
	{
		_store?.Dispose();
		_operations?.Dispose();

		if (Directory.Exists(_root))
			Directory.Delete(_root, recursive: true);
	}

	/// <summary>
	/// What the agent said about the open position is what the runner is told, both ways round. Closing
	/// places a trade and leaving keeps money at risk, so a choice turned over on the way is the worst
	/// thing this tool could do.
	/// </summary>
	[TestMethod]
	[DataRow(true)]
	[DataRow(false)]
	public async Task TheChoiceAboutThePositionReachesTheRunner(bool closePosition)
	{
		var (project, deployment) = await DeployedAsync();

		var answer = await Answer(PaperTools.StopDeployment(Guard(), _deployments, project, deployment, closePosition, CancellationToken));

		IsFalse(answer.TryGetProperty("error", out var error), $"the stop was refused: {error}");
		AreEqual<bool?>(closePosition, _runners.AskedToClose, "the runner was told something other than what was chosen.");
		AreEqual("Stopped", answer.GetProperty("status").GetString());
	}

	/// <summary>
	/// A runner that no row of the project accounts for is reported as an orphan, and a runner that one
	/// does is not reported twice.
	/// </summary>
	[TestMethod]
	public async Task AnOrphanIsARunnerNoRowAccountsFor()
	{
		var (project, deployment) = await DeployedAsync();

		_runners.Add("dep_orphan");

		var answer = await Answer(PaperTools.ListDeployments(Guard(), _deployments, project, CancellationToken));

		var rows = answer.GetProperty("deployments").EnumerateArray().Select(d => d.GetProperty("deploymentId").GetString()).ToArray();
		var orphans = answer.GetProperty("orphanedRunners").EnumerateArray().Select(d => d.GetProperty("deploymentId").GetString()).ToArray();

		IsTrue(rows.SequenceEqual([deployment]), $"the rows are {string.Join(", ", rows)}.");
		IsTrue(orphans.SequenceEqual(["dep_orphan"]), $"the orphans are {string.Join(", ", orphans)}.");
	}

	/// <summary>
	/// Naming a deployment reads the account its own runner trades on; naming none reads the one this
	/// server bound. The two need not be the same account, and the answer says which it is.
	/// </summary>
	[TestMethod]
	public async Task TheAccountIsReadWhereTheCallerAskedForIt()
	{
		var (_, deployment) = await DeployedAsync();

		var gateway = new BrokerGateway(new NoConnectors(), null);
		var server = new Account("server-account");

		var throughRunner = await Answer(PaperTools.GetAccountState(Guard(), server, gateway, _deployments, deployment, CancellationToken));
		var throughServer = await Answer(PaperTools.GetAccountState(Guard(), server, gateway, _deployments, "", CancellationToken));

		AreEqual(Runners.AccountName, throughRunner.GetProperty("account").GetProperty("name").GetString());
		AreEqual("the deployment's own runner", throughRunner.GetProperty("readThrough").GetString());

		AreEqual("server-account", throughServer.GetProperty("account").GetProperty("name").GetString());
		AreEqual("this server's connector", throughServer.GetProperty("readThrough").GetString());
	}

	private static ToolGuard Guard()
		=> new(NullLogger<ToolGuard>.Instance);

	private static async Task<JsonElement> Answer(Task<object> call)
		=> JsonSerializer.SerializeToElement(await call);

	private static PaperAccountState State(string name)
		=> new(DateTime.UtcNow, "stand-in", new(name, true, false, 100_000m, "USD"), [], []);

	private async Task<(string Project, string Deployment)> DeployedAsync()
	{
		var created = await _projects.CreateProjectAsync("paper", Guid.NewGuid().ToString("n"), Actors.User, CancellationToken);

		await _datasets.ImportDemoAsync(created.Id, Guid.NewGuid().ToString("n"), Actors.Agent, CancellationToken);

		var spec = await _specs.AddAsync(created.Id, Spec, Actors.Agent, DateTime.UtcNow, CancellationToken);
		var built = await _candidates.BuildAsync(created.Id, spec.Id, Guid.NewGuid().ToString("n"), Actors.Agent, CancellationToken);

		// The lifecycle refuses a jump, so the candidate walks to where a deployment will take it.
		foreach (var stage in new[]
		{
			CandidateStatuses.Backtested,
			CandidateStatuses.Validated,
			CandidateStatuses.StressTested,
			CandidateStatuses.FinalChecked,
			CandidateStatuses.Completed,
		})
		{
			built = built.WithStatus(stage, DateTime.UtcNow);

			await _store.UpdateAsync(created.Id, built, CancellationToken);
		}

		var answer = await Answer(PaperTools.DeployCandidate(Guard(), _deployments, created.Id.Value, built.Id.Value, CancellationToken));

		IsFalse(answer.TryGetProperty("error", out var error), $"the deployment was refused: {error}");

		return (created.Id.Value, answer.GetProperty("deploymentId").GetString());
	}

	/// <summary>A builder that hands back something assembly-shaped, since nothing here runs it.</summary>
	private sealed class Builder : IStrategyBuilder
	{
		public BuiltStrategy Build(StrategySpec spec)
			=> new("Generated", $"// source of {spec.Name}", $"source-{spec.Name}", [1, 2, 3], $"assembly-{spec.Name}", "1.0.0");
	}

	/// <summary>A connector that is named and never loaded, which is all a deployment needs of one.</summary>
	private sealed class Named : IDeploymentConnector
	{
		public ConnectorChoice Choose() => new("StockSharp.Example", "1.2.3", string.Empty, null);
	}

	/// <summary>No connector is bound, so the gateway reports none.</summary>
	private sealed class NoConnectors : IConnectorFactory
	{
		public IReadOnlyList<string> Allowed => [];

		public IReadOnlyList<string> Sources => [];

		public ValueTask<ConnectorDescription> InspectAsync(ConnectorChoice choice, CancellationToken cancellationToken)
			=> throw new NotSupportedException();

		public ValueTask<BrokerBinding> OpenAsync(ConnectorChoice choice, CancellationToken cancellationToken)
			=> throw new NotSupportedException();

		public ValueTask<IHistorySource> OpenStorageAsync(RemoteStorageChoice choice, CancellationToken cancellationToken)
			=> throw new NotSupportedException();

		public ValueTask<IReadOnlyList<ConnectorDescription>> InstalledAsync(CancellationToken cancellationToken)
			=> ValueTask.FromResult<IReadOnlyList<ConnectorDescription>>([]);
	}

	/// <summary>The account this server reads when no deployment is named.</summary>
	private sealed class Account(string name) : IPaperAccount
	{
		public ValueTask<PaperAccountState> ReadAsync(CancellationToken cancellationToken)
			=> ValueTask.FromResult(State(name));
	}

	/// <summary>Runners that are always there and always answer, and remember what they were told.</summary>
	private sealed class Runners : IRunnerHost
	{
		public const string AccountName = "runner-account";

		private readonly Dictionary<string, RunnerState> _states = new(StringComparer.Ordinal);

		public bool? AskedToClose { get; private set; }

		private static RunnerState Trading
			=> new(DateTime.UtcNow, RunnerPhases.Trading, TradingModes.Paper, true, 0, 0, 0, 0m, 0m, 0, AccountName, null, null);

		public void Add(string deploymentId)
			=> _states[deploymentId] = Trading;

		public ValueTask<RunnerHandle> LaunchAsync(RunnerLaunch launch, CancellationToken cancellationToken)
		{
			_states[launch.DeploymentId] = Trading;

			return ValueTask.FromResult(Handle(launch.DeploymentId));
		}

		public ValueTask<RunnerHandle> AttachAsync(string deploymentId, CancellationToken cancellationToken)
			=> ValueTask.FromResult(Handle(deploymentId));

		public ValueTask<RunnerHandle> StopAsync(string deploymentId, bool closePosition, CancellationToken cancellationToken)
		{
			AskedToClose = closePosition;
			_states[deploymentId] = _states[deploymentId] with { IsRunning = false, Phase = RunnerPhases.Stopped };

			return ValueTask.FromResult(Handle(deploymentId));
		}

		public ValueTask<PaperAccountState> AccountAsync(string deploymentId, CancellationToken cancellationToken)
			=> ValueTask.FromResult(State(AccountName));

		public ValueTask<IReadOnlyList<RunnerHandle>> ListAsync(CancellationToken cancellationToken)
			=> ValueTask.FromResult<IReadOnlyList<RunnerHandle>>([.. _states.Keys.Select(Handle)]);

		private RunnerHandle Handle(string deploymentId)
			=> new(deploymentId, "prj", "cnd", RunnerStatuses.Attached, TradingModes.Paper, 24188, "stand-in", "home",
				_states[deploymentId], "Connected and answering.");
	}
}
