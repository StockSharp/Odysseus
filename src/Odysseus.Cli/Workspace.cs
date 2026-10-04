namespace Odysseus.Cli;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Odysseus.Application;
using Odysseus.Broker;
using Odysseus.Compiler;
using Odysseus.Domain;
using Odysseus.Engine;
using Odysseus.Persistence;
using Odysseus.Platform;

/// <summary>
/// Everything a command needs, built once.
/// </summary>
/// <remarks>
/// The same stores and services the MCP server builds, assembled here without the protocol around them.
/// One command is one process, so what a person is working on is remembered in a small file beside the
/// projects rather than held in memory: without it every command would have to be told the project, the
/// candidate and the run it means.
/// </remarks>
public sealed class Workspace : IDisposable
{
	private const string CurrentFileName = "current.txt";
	private const string RootVariable = "ODYSSEUS_PROJECTS_ROOT";
	private const string MarketDataVariable = "ODYSSEUS_MARKET_DATA";
	private const string RemoteStorageVariable = "ODYSSEUS_REMOTE_STORAGE";
	private const string KeysVariable = "ODYSSEUS_BROKER_KEYS";
	private const string ConnectorVariable = "ODYSSEUS_BROKER_CONNECTOR";
	private const string SourcesVariable = "ODYSSEUS_CONNECTOR_SOURCES";
	private const string AllowVariable = "ODYSSEUS_CONNECTOR_ALLOW";

	/// <summary>The gallery a connector comes from unless the operator names another.</summary>
	private const string DefaultSource = "https://api.nuget.org/v3/index.json";

	/// <summary>The only package family this command line downloads unless the operator widens it.</summary>
	private const string DefaultAllow = "StockSharp.";

	private readonly string _root;
	private readonly SqliteProjectStore _store;
	private readonly SqliteOperationLog _operations;
	private readonly SqliteClosedHistoryLedger _ledger;
	private readonly WorkerHost _worker;
	private readonly RunnerRegistry _runners;

	private Workspace(string root, string marketData)
	{
		_root = root;
		MarketData = marketData;
		_store = new SqliteProjectStore(root);
		_operations = new SqliteOperationLog(root);
		_ledger = new SqliteClosedHistoryLedger(root);

		var artifacts = new FileArtifactStore(root);
		var datasets = new FileDatasetStore(root, new LocalBarStorage(marketData));
		var specs = new FileSpecStore(root);
		var clock = new SystemClock();

		// The same ceiling the MCP server grants a new project. A person at a terminal and an agent over
		// the protocol work on the same projects under the same root, so a budget that differed between
		// the two would be a budget either of them could raise by starting the project through the other.
		Projects = new ProjectService(
			_store, _store, _operations, clock, ServerModes.Local, ResearchBudget.Default);

		Candidates = new CandidateService(
			_store, specs, _store, artifacts, new StrategyBuilder(EngineAssemblies.Paths(AppContext.BaseDirectory)),
			_store, _operations, clock);

		// A candidate runs in a process of its own here for the same reasons it does in the server: the
		// command line is short-lived, but a run with no ceiling on its time or its memory is a run that
		// can take the machine down with it.
		_worker = new WorkerHost(WorkerOptions.Read(), EngineIdentity.Current());

		Profiler = new StockSharpMarketProfiler();

		Backtests = new BacktestService(
			_store, _store, specs, datasets, _store, artifacts,
			new WorkerBacktestRunner(_worker), Profiler, _store, _operations, clock);

		Specs = new SpecService(_store, specs, _store, _operations, clock);
		Datasets = new DatasetService(_store, datasets, _store, _store, _operations, clock);

		Evaluations = new EvaluationService(_store, _store, specs, _store, Backtests, _store, clock);

		Closed = new ClosedDataService(
			_store, specs, _store, _store, _store, datasets, _ledger, Backtests, _store, clock);

		Completions = new CompletionService(
			_store, specs, _store, _store, _store, datasets, artifacts,
			new FileCompletedStore(root), _store, clock);

		// One gateway for the broker port, bound to whichever connector the environment names. Measuring
		// what is already imported needs no broker, so the service exists either way and only downloading
		// refuses: built only when a connector loads, every command that reads a dataset would fail on a
		// machine that has none, which is most machines trying this out.
		Connector = ConnectorFile.Read(Environment.GetEnvironmentVariable(ConnectorVariable));

		var connectors = StockSharpConnectorFactory.Create(
			Path.Combine(root, "connectors"),
			BrokerCredentialFile.Read(Environment.GetEnvironmentVariable(KeysVariable)),
			Sources(),
			Allowed(),

			// The literal, at the one call site in this process that could have written anything else.
			// A command line is not where a real account is authorised: that is decided by the process
			// that trades, from a file its operator named to it.
			TradingMandate.Paper);

		var gateway = new BrokerGateway(connectors, Connector);

		// A storage server, when one is named, is where history comes from; the broker still trades.
		RemoteStorage = RemoteStorageFile.Read(Environment.GetEnvironmentVariable(RemoteStorageVariable));

		History = RemoteStorage is null ? gateway : new RemoteStorageSource(connectors, RemoteStorage);
		Broker = gateway;
		HistoryService = new HistoryService(_store, datasets, History, Profiler, _store, _operations, clock);

		// The registry is a directory rather than anything this process holds, so a deployment started by
		// the MCP server against the same projects root is visible here and the other way round.
		_runners = new(root, SystemProcessProbe.Instance);

		Runners = new RunnerLauncher(
			RunnerOptions.Read(),
			_runners,
			new(Path.Combine(root, "connectors"), Sources(), Allowed()),
			EngineIdentity.Current());
	}

	/// <summary>Where the projects live.</summary>
	public string Root => _root;

	/// <summary>Project use cases.</summary>
	public ProjectService Projects { get; }

	/// <summary>Candidate use cases.</summary>
	public CandidateService Candidates { get; }

	/// <summary>Backtest use cases.</summary>
	public BacktestService Backtests { get; }

	/// <summary>Specification use cases.</summary>
	public SpecService Specs { get; }

	/// <summary>Dataset use cases.</summary>
	public DatasetService Datasets { get; }

	/// <summary>The fixed set of runs, and what they came to.</summary>
	public EvaluationService Evaluations { get; }

	/// <summary>The one measurement against the closed history.</summary>
	public ClosedDataService Closed { get; }

	/// <summary>Declaring a strategy finished and keeping it.</summary>
	public CompletionService Completions { get; }

	/// <summary>Where history comes from.</summary>
	public IHistorySource History { get; }

	/// <summary>What measures an instrument's history.</summary>
	public IMarketProfiler Profiler { get; }

	/// <summary>The broker ports, and whichever connector is bound to them.</summary>
	public BrokerGateway Broker { get; }

	/// <summary>The connector the environment names, or null when it names none.</summary>
	public ConnectorChoice Connector { get; }

	/// <summary>Every runner this projects root knows about, and how to reach one.</summary>
	public RunnerRegistry RunnerRegistry => _runners;

	/// <summary>Starting, finding and stopping the processes that trade.</summary>
	public IRunnerHost Runners { get; }

	/// <summary>Whether a connector is bound, so that history can actually be downloaded.</summary>
	public bool HasBroker => Broker.IsBound;

	/// <summary>History use cases.</summary>
	public HistoryService HistoryService { get; }

	/// <summary>Folder of the market-data storage every project shares.</summary>
	public string MarketData { get; }

	/// <summary>The remote storage server history is imported from, or null to import from the broker.</summary>
	public RemoteStorageChoice RemoteStorage { get; }

	/// <summary>
	/// Opens the workspace the environment points at.
	/// </summary>
	/// <returns>The workspace.</returns>
	public static Workspace Open()
	{
		var root = Environment.GetEnvironmentVariable(RootVariable);
		var marketData = Environment.GetEnvironmentVariable(MarketDataVariable);

		return Open(
			string.IsNullOrWhiteSpace(root) ? Path.Combine(Directory.GetCurrentDirectory(), "projects") : root,
			string.IsNullOrWhiteSpace(marketData) ? Path.Combine(Directory.GetCurrentDirectory(), "market-data") : marketData);
	}

	/// <summary>
	/// Opens a workspace over a named projects root and market-data storage.
	/// </summary>
	/// <param name="root">Directory the projects live in, made when it is not there yet.</param>
	/// <param name="marketData">Folder of the market-data storage every project shares.</param>
	/// <returns>The workspace.</returns>
	public static Workspace Open(string root, string marketData)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(root);
		ArgumentException.ThrowIfNullOrWhiteSpace(marketData);

		return new(Path.GetFullPath(root), Path.GetFullPath(marketData));
	}

	/// <summary>
	/// The project the last commands worked on.
	/// </summary>
	/// <returns>The project.</returns>
	/// <exception cref="InvalidOperationException">Nothing has been started yet.</exception>
	public ProjectId Current()
		=> Read(0) is { Length: > 0 } value
			? ProjectId.Parse(value)
			: throw new InvalidOperationException("No project yet. Start one: odysseus new \"my study\"");

	/// <summary>The specification last proposed.</summary>
	/// <returns>The specification.</returns>
	public SpecId CurrentSpec()
		=> Read(1) is { Length: > 0 } value
			? SpecId.Parse(value)
			: throw new InvalidOperationException("No hypothesis yet: odysseus propose hypothesis.json");

	/// <summary>The candidate last built.</summary>
	/// <returns>The candidate.</returns>
	public CandidateId CurrentCandidate()
		=> Read(2) is { Length: > 0 } value
			? CandidateId.Parse(value)
			: throw new InvalidOperationException("Nothing built yet: odysseus build");

	/// <summary>The run last measured.</summary>
	/// <returns>The run.</returns>
	public RunId CurrentRun()
		=> Read(3) is { Length: > 0 } value
			? RunId.Parse(value)
			: throw new InvalidOperationException("Nothing run yet: odysseus backtest");

	/// <summary>Remembers the project, forgetting what belonged to the last one.</summary>
	/// <param name="project">Project to remember.</param>
	public void Remember(ProjectId project) => Write(project.Value, "", "", "");

	/// <summary>Remembers the specification.</summary>
	/// <param name="spec">Specification to remember.</param>
	public void RememberSpec(SpecId spec) => Write(Read(0), spec.Value, "", "");

	/// <summary>Remembers the candidate.</summary>
	/// <param name="candidate">Candidate to remember.</param>
	public void RememberCandidate(CandidateId candidate) => Write(Read(0), Read(1), candidate.Value, "");

	/// <summary>Remembers the run.</summary>
	/// <param name="run">Run to remember.</param>
	public void RememberRun(RunId run) => Write(Read(0), Read(1), Read(2), run.Value);

	/// <summary>
	/// Binds the connector the environment names, when it names one.
	/// </summary>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>Whether a connector is now bound.</returns>
	/// <remarks>
	/// Called by the commands that need a broker rather than at construction, so that a command which
	/// only reads what is already imported costs no download and needs no network.
	/// </remarks>
	public async Task<bool> UseBrokerAsync(CancellationToken cancellationToken)
	{
		if (Broker.IsBound)
			return true;

		if (Connector is null)
			return false;

		await Broker.SelectAsync(Connector, cancellationToken);

		return true;
	}

	/// <inheritdoc />
	public void Dispose()
	{
		_worker.Dispose();
		_store.Dispose();
		_operations.Dispose();
		_ledger.Dispose();
	}

	/// <summary>
	/// Where a connector may be downloaded from.
	/// </summary>
	/// <returns>The sources the operator named, or the gallery.</returns>
	/// <remarks>
	/// A list that was written and came out empty is widened back to the gallery, because an empty one
	/// narrows nothing: the fetcher falls back to the gallery whatever this says.
	/// </remarks>
	private static IReadOnlyList<string> Sources()
	{
		var named = Entries(SourcesVariable);

		if (named is null || named.Count == 0)
			return [DefaultSource];

		return named;
	}

	/// <summary>
	/// Which package identifiers may be loaded at all.
	/// </summary>
	/// <returns>The prefixes the operator named, which may be none.</returns>
	/// <remarks>
	/// The opposite of <see cref="Sources"/>, because this is the guard rather than a convenience. A
	/// variable that said nothing gets the default; one that was written and came out empty is kept
	/// empty, which refuses every package. The server reads the same variable the same way, and a guard
	/// that answered one thing here and another there would be no guard.
	/// </remarks>
	private static IReadOnlyList<string> Allowed()
	{
		var named = Entries(AllowVariable);

		if (named is null)
			return [DefaultAllow];

		return named;
	}

	/// <summary>
	/// Splits a delimited variable into its entries.
	/// </summary>
	/// <param name="variable">Name of the variable.</param>
	/// <returns>The entries, which may be none, or <see langword="null"/> when the variable said nothing.</returns>
	private static IReadOnlyList<string> Entries(string variable)
	{
		var value = Environment.GetEnvironmentVariable(variable);

		return string.IsNullOrWhiteSpace(value)
			? null
			: value.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
	}

	private string Read(int line)
	{
		var path = Path.Combine(_root, CurrentFileName);

		if (!File.Exists(path))
			return "";

		var lines = File.ReadAllLines(path);

		return line < lines.Length ? lines[line].Trim() : "";
	}

	private void Write(params string[] lines)
	{
		Directory.CreateDirectory(_root);

		File.WriteAllLines(Path.Combine(_root, CurrentFileName), lines);
	}
}

