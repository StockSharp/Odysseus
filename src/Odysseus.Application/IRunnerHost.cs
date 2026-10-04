namespace Odysseus.Application;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Odysseus.Domain;

/// <summary>
/// Where a runner has got to.
/// </summary>
public enum RunnerPhases
{
	/// <summary>It is loading the connector and connecting; nothing has been traded yet.</summary>
	Starting,

	/// <summary>The strategy is running against the account.</summary>
	Trading,

	/// <summary>It was asked to stop and is winding down.</summary>
	Stopping,

	/// <summary>It stopped, in an orderly fashion.</summary>
	Stopped,

	/// <summary>It gave up. The reason is on the state.</summary>
	Failed,
}

/// <summary>
/// What a runner says about itself when asked.
/// </summary>
/// <param name="ObservedAt">When the runner produced this, in UTC.</param>
/// <param name="Phase">Where it has got to.</param>
/// <param name="Mode">Which account it is on. First, because everything else is read differently for each.</param>
/// <param name="IsRunning">Whether the strategy is still trading.</param>
/// <param name="OrdersPlaced">Orders the strategy sent.</param>
/// <param name="Trades">Positions it opened and closed.</param>
/// <param name="SessionDays">Distinct trading days the strategy has received candles for.</param>
/// <param name="RealizedProfit">What those came to, as the broker reports it.</param>
/// <param name="Position">What it is holding. Anything other than zero is money at risk.</param>
/// <param name="WorkingOrders">Orders still live at the venue, which is a different question from the position.</param>
/// <param name="Account">The account the broker reported, so a report can be checked against the mandate.</param>
/// <param name="MandateExpiresAt">When the permission to trade real money runs out, or null on paper.</param>
/// <param name="Error">Why it failed, when it did.</param>
public sealed record RunnerState(
	DateTime ObservedAt,
	RunnerPhases Phase,
	TradingModes Mode,
	bool IsRunning,
	int OrdersPlaced,
	int Trades,
	int SessionDays,
	decimal RealizedProfit,
	decimal Position,
	int WorkingOrders,
	string Account,
	DateTime? MandateExpiresAt,
	string Error);

/// <summary>
/// What was found when a runner was looked for.
/// </summary>
/// <remarks>
/// Four values rather than two, because "it is gone" and "it is alive and not answering" are the
/// difference between nobody holding a position and somebody holding one, and a caller that cannot tell
/// them apart will treat the second as the first.
/// </remarks>
public enum RunnerStatuses
{
	/// <summary>Connected, greeted and answering.</summary>
	Attached,

	/// <summary>Its process is alive and it is not answering. Something may still be holding a position.</summary>
	Unresponsive,

	/// <summary>Its process is not there. Whatever it held at the broker was left as it stood.</summary>
	Gone,

	/// <summary>No record at all, or a record this build refuses to talk to.</summary>
	Unknown,
}

/// <summary>
/// A runner as a session found it.
/// </summary>
/// <param name="DeploymentId">Deployment the runner is running.</param>
/// <param name="ProjectId">Project the deployment belongs to, from the runner's own record.</param>
/// <param name="CandidateId">Candidate it is running, from the runner's own record.</param>
/// <param name="Status">What was found.</param>
/// <param name="Mode">Which account it is on, from its own record even when it cannot be reached.</param>
/// <param name="ProcessId">Its process, so a person can find it without this server.</param>
/// <param name="Engine">Which build of the trading platform it carries.</param>
/// <param name="Home">Directory holding its record, its journal and the assembly it is trading.</param>
/// <param name="State">What it said about itself, or null when it said nothing.</param>
/// <param name="Detail">Why it is in this status, in words a person can act on.</param>
public sealed record RunnerHandle(
	string DeploymentId,
	string ProjectId,
	string CandidateId,
	RunnerStatuses Status,
	TradingModes Mode,
	int ProcessId,
	string Engine,
	string Home,
	RunnerState State,
	string Detail);

/// <summary>
/// What to start a runner on.
/// </summary>
/// <param name="DeploymentId">Identifier of the deployment, which names the runner's home and its pipe.</param>
/// <param name="ProjectId">Project the deployment belongs to.</param>
/// <param name="CandidateId">Candidate being run.</param>
/// <param name="ClassName">Name of the strategy inside the assembly.</param>
/// <param name="Assembly">The compiled candidate, written into the runner's home before it starts.</param>
/// <param name="Parameters">Values the run was measured with.</param>
/// <param name="Symbol">Symbol to trade.</param>
/// <param name="TimeFrame">Length of one candle, which the rules are evaluated on.</param>
/// <param name="Volume">Size of one position.</param>
/// <param name="Connector">Which connector the runner is to load, and how to configure it.</param>
/// <param name="MandatePath">
/// Path of the live mandate, when a human at a terminal asked for one. The MCP server always leaves this
/// empty, and the launcher removes the variable from the child's environment before setting it from
/// here - so live mode is never inherited from the shell that started a server.
///
/// A runner started from here still cannot reach a real account even with a path in it: the phrase that
/// confirms a mandate is not passed to any child, and a detached process has no terminal to be asked for
/// one at. Live trading is a person starting the runner themselves.
/// </param>
public sealed record RunnerLaunch(
	string DeploymentId,
	string ProjectId,
	string CandidateId,
	string ClassName,
	byte[] Assembly,
	IReadOnlyDictionary<string, decimal> Parameters,
	string Symbol,
	TimeSpan TimeFrame,
	decimal Volume,
	ConnectorChoice Connector,
	string MandatePath);

/// <summary>
/// Raised when something is asked of a runner whose process is alive and which is not answering.
/// </summary>
/// <remarks>
/// The one refusal that exists to stop a lie being written down. Recording such a deployment as stopped
/// would say something untrue about an open position: nothing was stopped, nothing was closed, and the
/// process may still be trading. So nothing is recorded and the caller is told, in those words.
/// </remarks>
public sealed class RunnerUnresponsiveException : Exception
{
	/// <summary>
	/// Creates the exception.
	/// </summary>
	/// <param name="message">Which process is not answering and what it may still be holding.</param>
	public RunnerUnresponsiveException(string message)
		: base(message)
	{
	}
}

/// <summary>
/// Raised when a runner answered a stop by saying the stop itself failed.
/// </summary>
/// <remarks>
/// The strategy may still hold a position and orders may still be working, so the deployment is not
/// recorded as stopped and the candidate is not released.
/// </remarks>
public sealed class RunnerStopFailedException : Exception
{
	/// <summary>
	/// Creates the exception.
	/// </summary>
	/// <param name="message">Which deployment failed to stop and why.</param>
	public RunnerStopFailedException(string message)
		: base(message)
	{
	}
}

/// <summary>
/// Raised when a runner is already trading the project a new one was asked for.
/// </summary>
/// <remarks>
/// The one-running-deployment rule, enforced where a process can see it. Two servers over one projects
/// root do not see each other's memory and cannot see a row the other has not committed yet, so the
/// rule is arbitrated by a file only one of them can create - and the loser is refused rather than left
/// to discover the collision at the broker, where it looks like a strategy trading against itself.
/// </remarks>
public sealed class RunnerAlreadyRunningException : Exception
{
	/// <summary>
	/// Creates the exception.
	/// </summary>
	/// <param name="message">Which deployment holds the project, and what to do about it.</param>
	public RunnerAlreadyRunningException(string message)
		: base(message)
	{
	}
}

/// <summary>
/// Raised when a runner would not start, or started and never greeted.
/// </summary>
/// <remarks>
/// Nothing was traded, and the row for it says so with this on it. Its home is left where it is rather
/// than cleaned up: what a runner managed to write before it gave up is usually the only thing that says
/// why.
/// </remarks>
public sealed class RunnerStartFailedException : Exception
{
	/// <summary>
	/// Creates the exception.
	/// </summary>
	/// <param name="message">What was tried and what came of it.</param>
	public RunnerStartFailedException(string message)
		: base(message)
	{
	}

	/// <summary>
	/// Creates the exception over an underlying failure.
	/// </summary>
	/// <param name="message">What was tried and what came of it.</param>
	/// <param name="inner">What went wrong underneath.</param>
	public RunnerStartFailedException(string message, Exception inner)
		: base(message, inner)
	{
	}
}

/// <summary>
/// Starting, finding and stopping the processes that trade.
/// </summary>
/// <remarks>
/// A deployment stopped being a thing this server holds and became a thing it can find. What survives
/// is a directory and a process; "is it running" is answered by connecting to it, not by looking in a
/// map that dies with the process that owns it.
///
/// The consequence is stated as loudly as the tool descriptions used to state the opposite:
/// disconnecting no longer stops anything. That is the feature and it is also the hazard, and it is why
/// a stop is a decision somebody makes rather than something a disconnection performs.
/// </remarks>
public interface IRunnerHost
{
	/// <summary>
	/// Starts a runner and waits for it to greet.
	/// </summary>
	/// <param name="launch">What to run and how to reach the broker.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The runner, once it has said what it is.</returns>
	/// <exception cref="RunnerAlreadyRunningException">Another runner is trading this project.</exception>
	ValueTask<RunnerHandle> LaunchAsync(RunnerLaunch launch, CancellationToken cancellationToken);

	/// <summary>
	/// Finds a runner and asks it what it has done.
	/// </summary>
	/// <param name="deploymentId">Deployment to look for.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>What was found, which is an answer even when nothing is there.</returns>
	ValueTask<RunnerHandle> AttachAsync(string deploymentId, CancellationToken cancellationToken);

	/// <summary>
	/// Stops a runner.
	/// </summary>
	/// <param name="deploymentId">Deployment to stop.</param>
	/// <param name="closePosition">Whether to close what it is holding.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The runner as it ended.</returns>
	/// <exception cref="RunnerUnresponsiveException">Its process is alive and it is not answering.</exception>
	ValueTask<RunnerHandle> StopAsync(string deploymentId, bool closePosition, CancellationToken cancellationToken);

	/// <summary>
	/// Reads the account a runner is trading on, through the runner's own connection.
	/// </summary>
	/// <param name="deploymentId">Deployment whose account to read.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The account, its holdings and its live orders.</returns>
	ValueTask<PaperAccountState> AccountAsync(string deploymentId, CancellationToken cancellationToken);

	/// <summary>
	/// Every runner the registry knows about, whether or not this session started it.
	/// </summary>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The runners.</returns>
	ValueTask<IReadOnlyList<RunnerHandle>> ListAsync(CancellationToken cancellationToken);
}
