namespace StockSharp.Odysseus.Engine;

using StockSharp.Odysseus.Domain;

/// <summary>What a runner was asked to do.</summary>
public enum RunnerCommands
{
	/// <summary>Present the token and take up the connection. Must be the first thing a client sends.</summary>
	Attach,

	/// <summary>Say what the strategy has done.</summary>
	Observe,

	/// <summary>Read the account the runner itself is trading on.</summary>
	Account,

	/// <summary>Stop the strategy, with or without closing what it holds.</summary>
	Stop,
}

/// <summary>
/// The first thing a runner says, before it is asked anything.
/// </summary>
/// <param name="Protocol">Version of this framing and of these records.</param>
/// <param name="Engine">Which build of the trading platform the runner carries.</param>
/// <param name="ProcessId">The runner's process, so a person can find it without this server.</param>
/// <param name="DeploymentId">Which deployment it is running, so a stale record cannot be mistaken for it.</param>
/// <param name="Mode">Which account it is on. First among the facts, because everything else reads differently for each.</param>
/// <param name="Connector">Package and version it loaded, written out.</param>
/// <param name="Account">The account the broker reported, or empty before it has connected.</param>
/// <param name="StartedAt">When the runner started, in UTC.</param>
public sealed record RunnerHello(
	int Protocol,
	string Engine,
	int ProcessId,
	string DeploymentId,
	TradingModes Mode,
	string Connector,
	string Account,
	DateTime StartedAt);

/// <summary>
/// One thing for the runner to do.
/// </summary>
/// <param name="Id">Identifier the answer carries back.</param>
/// <param name="Command">Which of the four it is.</param>
/// <param name="Token">
/// What the launcher wrote into the runner's home. It is protected by the filesystem, the same way the
/// credential file is; it is not a cryptographic barrier and is not claimed to be one. What it does is
/// stop an unrelated local process from stumbling into <see cref="RunnerCommands.Stop"/>.
/// </param>
/// <param name="ClosePosition">Whether a stop is to close what the strategy holds.</param>
public sealed record RunnerRequest(
	string Id,
	RunnerCommands Command,
	string Token,
	bool ClosePosition);

/// <summary>
/// What the runner made of it.
/// </summary>
/// <param name="Id">Identifier of the request this answers.</param>
/// <param name="Succeeded">Whether the runner did what was asked.</param>
/// <param name="State">What the runner has done, which every answer carries.</param>
/// <param name="Account">The account, when that is what was asked for.</param>
/// <param name="Failure">Why the request failed, when it did.</param>
public sealed record RunnerAnswer(
	string Id,
	bool Succeeded,
	RunnerState State,
	PaperAccountState Account,
	string Failure);

/// <summary>
/// How a session and a runner speak to each other.
/// </summary>
/// <remarks>
/// The framing is <see cref="WorkerProtocol"/>'s, verbatim and deliberately not forked: a four-byte
/// big-endian length, UTF-8 JSON, one cap and one serializer shared by both ends. What differs is the
/// transport and the lifetime. A worker is started per run over its own standard input and output and is
/// expected to end; a runner outlives the process that started it and must be reachable by a process
/// that did not, so it listens on a named pipe instead - which also leaves its own standard input free,
/// and that is what lets the live path require a terminal.
///
/// The version is separate from the worker's because the two evolve independently. The framing constants
/// stay where they are and are used by both.
/// </remarks>
public static class RunnerProtocol
{
	/// <summary>Version of the records above.</summary>
	public const int Version = 1;

	/// <summary>
	/// The pipe a runner listens on.
	/// </summary>
	/// <param name="deploymentId">Deployment the runner is running.</param>
	/// <returns>The pipe name.</returns>
	/// <remarks>
	/// Derived rather than chosen, so a session that knows the deployment can find the process without
	/// having read anything the launcher wrote. The record carries it as well, and the two must agree:
	/// a record naming another pipe is a record from a build that named them differently, and the
	/// handshake refuses over the deployment identifier anyway.
	/// </remarks>
	public static string PipeOf(string deploymentId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(deploymentId);

		return $"odysseus-runner-{deploymentId}";
	}
}
