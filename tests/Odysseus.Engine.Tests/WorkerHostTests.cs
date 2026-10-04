namespace Odysseus.Engine.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Odysseus.Application;
using Odysseus.Domain;
using Odysseus.TestKit;

/// <summary>
/// What the server does when the process running a candidate misbehaves.
/// </summary>
/// <remarks>
/// Three of the categories the error contract publishes - a run stopped on its deadline, a run stopped
/// on its memory limit, and a worker that failed - could never be produced before, because there was no
/// worker: a candidate ran in the server, so a hang hung the server and a run that asked for too much
/// memory took it down. They are only real if something can make each of them happen on demand, which
/// is what the stub next door is for.
///
/// A candidate that throws is deliberately not one of them. That is a result, it is recorded, and it
/// reaches the caller as the same failure it always did.
/// </remarks>
[TestClass]
public class WorkerHostTests : OdysseusTestBase
{
	private static readonly DateTime _open = new(2026, 3, 2, 14, 0, 0, DateTimeKind.Utc);

	/// <summary>A worker that answers is answered, and the outcome comes back whole.</summary>
	[TestMethod]
	public async Task AWorkerThatAnswersIsAnswered()
	{
		using var host = Host("answer");

		var outcome = await host.RunAsync(Request(), CancellationToken);

		AreEqual(1, outcome.BarsProcessed, "the answer did not survive the crossing.");
	}

	/// <summary>
	/// A candidate that failed is a result, not a defect: the host raises what the runner in the server
	/// used to raise, so the run is recorded and charged exactly as it always was.
	/// </summary>
	[TestMethod]
	public async Task ACandidateThatFailedIsNotAWorkerFailure()
	{
		using var host = Host("refuse");

		var failure = await ThrowsAsync<InvalidOperationException>(() => host.RunAsync(Request(), CancellationToken));

		IsTrue(failure.Message.Contains("the candidate failed", StringComparison.Ordinal),
			$"the candidate's own message did not reach the caller: {failure.Message}");
	}

	/// <summary>
	/// A worker that is not where the deployment says it is, is this server's failure and not the
	/// candidate's.
	/// </summary>
	/// <remarks>
	/// The most ordinary deployment mistake there is: a path in the environment pointing at nothing.
	/// Starting a process that is not there raises the operating system's own error, which reaches the
	/// service as an ordinary exception raised while a candidate was being run - and that is exactly
	/// what a candidate throwing looks like. Told apart nowhere else, the attempt is charged, the
	/// strategy is recorded as having failed something it was never asked, and the operation key is
	/// spent on a run that never started.
	/// </remarks>
	[TestMethod]
	public async Task AWorkerThatIsNotThereIsNotTheCandidatesFailure()
	{
		var missing = Path.Combine(Path.GetTempPath(), $"odysseus-worker-{Guid.NewGuid():n}.exe");

		using var host = new WorkerHost(At(missing, "answer"), Engine);

		var failure = await ThrowsAsync<IsolationFailedException>(() => host.RunAsync(Request(), CancellationToken));

		AreEqual(IsolationFailures.Crashed, failure.Kind,
			"a worker that would not start was reported as something other than the harness failing.");

		IsTrue(failure.Message.Contains(missing, StringComparison.Ordinal),
			$"the refusal does not say which worker could not be started: {failure.Message}");
	}

	/// <summary>
	/// An answer to another request is discarded rather than taken as this one's measurement, and the
	/// worker that produced it is replaced.
	/// </summary>
	/// <remarks>
	/// A worker serves one request at a time and is then reused for the next, so a worker that writes
	/// one frame too many leaves the following request reading the previous one's answer. Nothing below
	/// this could notice: what arrives is a whole, well-formed outcome, and it would be written down as
	/// this candidate's measurement over this candidate's slice. The identifier crosses the pipe so that
	/// it can be told apart, and the third call is what says the worker was replaced as well as refused:
	/// one still a frame behind would go on answering the question before.
	/// </remarks>
	[TestMethod]
	public async Task AnAnswerToAnotherRequestIsNotThisRequestsMeasurement()
	{
		using var host = Host("wrong-id");

		var first = await host.RunAsync(Request(), CancellationToken);

		AreEqual(1, first.BarsProcessed, "the first request was not answered normally.");

		var failure = await ThrowsAsync<IsolationFailedException>(() => host.RunAsync(Request(), CancellationToken));

		AreEqual(IsolationFailures.Crashed, failure.Kind,
			"an answer belonging to another request was accepted as this one's result.");

		var third = await host.RunAsync(Request(), CancellationToken);

		AreEqual(1, third.BarsProcessed,
			"the worker that answered out of step was kept, so it is still one answer ahead.");
	}

	/// <summary>
	/// A server that cannot say which platform build it was assembled against runs nothing, rather than
	/// accepting whatever the worker claims to be.
	/// </summary>
	/// <remarks>
	/// A run's fingerprint deliberately does not carry the engine, so this comparison is the only thing
	/// keeping two platform builds out of one namespace of measurements. A check made only when there
	/// is something to compare is a check that stands down exactly when it is needed: the deployment
	/// whose manifests cannot be read is the one nobody can identify afterwards, and its runs would go
	/// into the same namespace as everybody else's.
	/// </remarks>
	[TestMethod]
	public async Task AServerThatCannotNameItsEngineRunsNothing()
	{
		using var host = new WorkerHost(Options("answer"), expectedEngine: string.Empty);

		var failure = await ThrowsAsync<IsolationFailedException>(() => host.RunAsync(Request(), CancellationToken));

		AreEqual(IsolationFailures.Handshake, failure.Kind,
			"a server that cannot name its own engine accepted whatever the worker claimed to be.");
	}

	/// <summary>
	/// Two deployments that can each name nothing are not thereby the same engine.
	/// </summary>
	/// <remarks>
	/// The case a comparison of strings gets wrong on its own: empty equals empty, so a host and a
	/// worker that both fail to read their manifests agree they are the same build - which is the one
	/// conclusion neither of them has any evidence for.
	/// </remarks>
	[TestMethod]
	public async Task TwoDeploymentsThatCanNameNothingAreNotTheSameEngine()
	{
		using var host = new WorkerHost(Options("no-engine"), expectedEngine: string.Empty);

		var failure = await ThrowsAsync<IsolationFailedException>(() => host.RunAsync(Request(), CancellationToken));

		AreEqual(IsolationFailures.Handshake, failure.Kind,
			"two deployments that could each say nothing were taken to be saying the same thing.");
	}

	/// <summary>A worker that stops answering is a worker failure, and says so.</summary>
	[TestMethod]
	public async Task AWorkerThatDiesIsAWorkerFailure()
	{
		using var host = Host("crash");

		var failure = await ThrowsAsync<IsolationFailedException>(() => host.RunAsync(Request(), CancellationToken));

		AreEqual(IsolationFailures.Crashed, failure.Kind);
	}

	/// <summary>
	/// A run that will not finish is stopped rather than waited on. Before there was a worker, cancelling
	/// abandoned the wait and left the work running.
	/// </summary>
	[TestMethod]
	public async Task ARunThatWillNotFinishIsStopped()
	{
		using var host = Host("hang");

		var failure = await ThrowsAsync<IsolationFailedException>(() => host.RunAsync(Request(), CancellationToken));

		AreEqual(IsolationFailures.Timeout, failure.Kind);
	}

	/// <summary>A run that takes more memory than it is allowed is stopped before the machine stops it.</summary>
	/// <remarks>
	/// Two of the four kinds are accepted, and the second one is not slack. The watchdog measures the peak
	/// working set, which no amount of memory pressure lowers, so a worker this host is still able to
	/// observe is stopped as <see cref="IsolationFailures.Memory"/>. What the operating system can still
	/// take away is the observation itself: on a machine that is already out of memory the child can be
	/// refused its first allocations and die before it has ever held the ceiling, and then the truthful
	/// answer really is <see cref="IsolationFailures.Crashed"/> - the worker went, and this host cannot
	/// invent a reason it did not see.
	///
	/// What the test exists for survives that, and it is the whole of what is asserted here: the run was
	/// stopped rather than left to eat the machine, it did not reach its deadline, and it did not come
	/// back as the candidate's own failure - which is what an <see cref="InvalidOperationException"/>
	/// carrying the strategy's message would have been. Which of the two kinds it is decides who pays for
	/// the run, and that is settled where it can be settled deterministically, in the service's own tests.
	/// </remarks>
	[TestMethod]
	public async Task ARunThatTakesTooMuchMemoryIsStopped()
	{
		using var host = new WorkerHost(
			Options("hog") with { MemoryLimitBytes = 256L * 1024 * 1024, RunDeadline = TimeSpan.FromSeconds(60) },
			Engine);

		var failure = await ThrowsAsync<IsolationFailedException>(() => host.RunAsync(Request(), CancellationToken));

		IsTrue(failure.Kind is IsolationFailures.Memory or IsolationFailures.Crashed,
			$"a run that took more memory than it is allowed ended as {failure.Kind}, which means it was " +
			"neither stopped on its limit nor stopped by the machine: " + failure.Message);
	}

	/// <summary>A worker that speaks a different protocol is refused at the handshake, never mid-run.</summary>
	[TestMethod]
	public async Task AWorkerSpeakingAnotherProtocolIsRefusedAtTheHandshake()
	{
		using var host = Host("wrong-protocol");

		var failure = await ThrowsAsync<IsolationFailedException>(() => host.RunAsync(Request(), CancellationToken));

		AreEqual(IsolationFailures.Handshake, failure.Kind);
	}

	/// <summary>
	/// A worker built against another engine is refused at the handshake.
	/// </summary>
	/// <remarks>
	/// A run's fingerprint covers the candidate, the data and the costs, and not the engine, so two runs
	/// produced by two different builds would share a fingerprint and the second would be answered from
	/// the first. Refusing here is what keeps that from being possible without re-identifying every run
	/// this product has ever recorded.
	/// </remarks>
	[TestMethod]
	public async Task AWorkerBuiltAgainstAnotherEngineIsRefused()
	{
		using var host = Host("wrong-engine");

		var failure = await ThrowsAsync<IsolationFailedException>(() => host.RunAsync(Request(), CancellationToken));

		AreEqual(IsolationFailures.Handshake, failure.Kind);
		IsTrue(failure.Message.Contains(Engine, StringComparison.Ordinal),
			$"the refusal does not name the engine this build expects: {failure.Message}");
	}

	/// <summary>A worker that never greets is refused after its handshake deadline rather than waited on.</summary>
	[TestMethod]
	public async Task AWorkerThatNeverGreetsIsRefused()
	{
		using var host = new WorkerHost(
			Options("mute") with { HandshakeDeadline = TimeSpan.FromSeconds(2) },
			Engine);

		var failure = await ThrowsAsync<IsolationFailedException>(() => host.RunAsync(Request(), CancellationToken));

		AreEqual(IsolationFailures.Handshake, failure.Kind);
	}

	/// <summary>
	/// The worker never learns the broker credentials or where the projects are.
	/// </summary>
	/// <remarks>
	/// Both are the same rule stated twice. Credentials are a path rather than a value precisely because
	/// a value in the environment is inherited by every process the server starts, and the worker is now
	/// one of them; the projects root is withheld because the bars of a slice are passed as data, so the
	/// one place that decides which bars a run may see stays where that discipline lives. A guarantee
	/// nothing asserts is a hope, so this asserts it.
	/// </remarks>
	[TestMethod]
	public void TheWorkerIsNotToldTheCredentialsOrWhereTheProjectsAre()
	{
		var info = WorkerHost.Describe(Options("answer"));

		IsFalse(info.Environment.ContainsKey("ODYSSEUS_BROKER_KEYS"), "the worker was handed the credential file.");
		IsFalse(info.Environment.ContainsKey("ODYSSEUS_BROKER_CONNECTOR"), "the worker was handed the connector file.");
		IsFalse(info.Environment.ContainsKey("ODYSSEUS_PROJECTS_ROOT"), "the worker was told where the history is.");

		IsTrue(info.RedirectStandardInput && info.RedirectStandardOutput,
			"the worker was started without the pipes the protocol runs over.");
	}

	/// <summary>The engine the stub reports, and therefore the one the host is told to expect.</summary>
	private const string Engine = "stub-engine";

	private static WorkerHost Host(string behaviour)
		=> new(Options(behaviour), Engine);

	private static WorkerOptions Options(string behaviour)
	{
		var stub = Stub();

		if (stub is null)
		{
			Fail("The misbehaving worker was not found where the build puts it. This project builds it, so its absence is a broken build or a wrong path.");
		}

		return At(stub, behaviour);
	}

	/// <summary>
	/// The same options against a named path, so a worker that is not there can be pointed at without
	/// the built stub having to exist.
	/// </summary>
	private static WorkerOptions At(string path, string behaviour)
		=> new(
			path,
			Arguments: [behaviour],
			HandshakeDeadline: TimeSpan.FromSeconds(20),
			RunDeadline: TimeSpan.FromSeconds(5),
			SearchDeadline: TimeSpan.FromSeconds(5),
			MemoryLimitBytes: 4L * 1024 * 1024 * 1024,
			RunsBeforeRecycle: 50,
			BatchSize: 4);

	/// <summary>
	/// Where the misbehaving worker was built. It is reached by path rather than by reference, which is
	/// the whole point of a worker.
	/// </summary>
	private static string Stub()
	{
		var configuration = AppContext.BaseDirectory.Contains(
			$"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}",
			StringComparison.OrdinalIgnoreCase)
			? "Debug"
			: "Release";

		var candidate = Path.Combine(
			RepositoryRoot, "tests", "Odysseus.WorkerStub", "bin", configuration, "net10.0",
			OperatingSystem.IsWindows() ? "Odysseus.WorkerStub.exe" : "Odysseus.WorkerStub");

		return File.Exists(candidate) ? candidate : null;
	}

	private static BacktestRequest Request()
		=> new(
			[1, 2, 3],
			"Odysseus.Generated.Candidate",
			new Dictionary<string, decimal>(StringComparer.Ordinal),
			"DEMO",
			TimeSpan.FromMinutes(5),
			new BarRange("bars", _open, _open.AddMinutes(5), 1),
			StartingEquity: 100_000m,
			Volume: 10m,
			PriceStep: 0.01m,
			ExecutionCosts.Default);
}
