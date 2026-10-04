namespace Odysseus.Engine;

using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

using Odysseus.Application;

/// <summary>
/// Raised when what answered the pipe is not the runner that was being looked for.
/// </summary>
/// <remarks>
/// The three refusals the worker's handshake already makes, for the reasons written there, plus the one
/// a re-attachable process needs: a pipe name is derived from a deployment identifier, so a record left
/// behind by a build that named them differently - or a name a stranger got in first with - would put a
/// stop request into a process nobody meant.
/// </remarks>
public sealed class RunnerHandshakeException : Exception
{
	/// <summary>
	/// Creates the exception.
	/// </summary>
	/// <param name="message">What answered and why it was refused.</param>
	public RunnerHandshakeException(string message)
		: base(message)
	{
	}
}

/// <summary>
/// One connection to a runner.
/// </summary>
/// <remarks>
/// A client of a process that outlives it. Nothing here starts anything, nothing here kills anything, and
/// nothing here decides that a deployment is over: it connects, checks that what answered is the runner
/// that was asked for, and then reads or stops.
///
/// Several clients may be attached at once - an agent reading while a person at a terminal stops - which
/// is why the runner accepts more than one connection and why a stop is idempotent on its side. This end
/// keeps none of that state: one connection, one request at a time, closed when it is done with.
/// </remarks>
public sealed class RunnerClient : IAsyncDisposable
{
	private readonly NamedPipeClientStream _pipe;
	private readonly string _token;
	private readonly SemaphoreSlim _gate = new(1, 1);

	private bool _disposed;

	private RunnerClient(NamedPipeClientStream pipe, string token, RunnerHello hello)
	{
		_pipe = pipe;
		_token = token ?? string.Empty;

		Hello = hello;
	}

	/// <summary>What the runner said about itself when the connection opened.</summary>
	public RunnerHello Hello { get; }

	/// <summary>
	/// Connects to a runner and takes up the connection.
	/// </summary>
	/// <param name="record">What the runner wrote about itself.</param>
	/// <param name="token">What the launcher wrote into the runner's home.</param>
	/// <param name="expectedEngine">The platform build this session was assembled against.</param>
	/// <param name="deadline">How long the whole of connecting and greeting is given.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The connection, greeted and attached.</returns>
	/// <exception cref="RunnerHandshakeException">What answered is not the runner that was asked for.</exception>
	public static async Task<RunnerClient> ConnectAsync(
		RunnerRecord record,
		string token,
		string expectedEngine,
		TimeSpan deadline,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(record);

		var pipe = new NamedPipeClientStream(".", record.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous);

		try
		{
			await pipe.ConnectAsync((int)deadline.TotalMilliseconds, cancellationToken);

			var hello = await WorkerProtocol.ReadAsync<RunnerHello>(pipe, cancellationToken).WaitAsync(deadline, cancellationToken)
				?? throw new RunnerHandshakeException(
					$"The runner on '{record.Pipe}' closed the connection without greeting.");

			Refuse(record, hello, expectedEngine);

			var client = new RunnerClient(pipe, token, hello);

			var attached = await client.CallAsync(
				new(NewId(), RunnerCommands.Attach, client._token, false), deadline, cancellationToken);

			if (!attached.Succeeded)
			{
				await client.DisposeAsync();

				throw new RunnerHandshakeException(
					$"The runner running {record.DeploymentId} refused this session: {attached.Failure}");
			}

			return client;
		}
		catch
		{
			await pipe.DisposeAsync();
			throw;
		}
	}

	/// <summary>
	/// Asks the runner what the strategy has done.
	/// </summary>
	/// <param name="deadline">How long it is given to answer.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>What it said.</returns>
	public async Task<RunnerState> ObserveAsync(TimeSpan deadline, CancellationToken cancellationToken)
		=> Answered(await CallAsync(new(NewId(), RunnerCommands.Observe, _token, false), deadline, cancellationToken)).State;

	/// <summary>
	/// Reads the account the runner is trading on, through the runner's own connection.
	/// </summary>
	/// <param name="deadline">How long it is given to answer.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The account, its holdings and its live orders.</returns>
	public async Task<PaperAccountState> AccountAsync(TimeSpan deadline, CancellationToken cancellationToken)
		=> Answered(await CallAsync(new(NewId(), RunnerCommands.Account, _token, false), deadline, cancellationToken)).Account;

	/// <summary>
	/// Stops the strategy.
	/// </summary>
	/// <param name="closePosition">Whether to close what it is holding.</param>
	/// <param name="deadline">How long it is given to wind down and say what it came to.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The state it ended in.</returns>
	public async Task<RunnerState> StopAsync(bool closePosition, TimeSpan deadline, CancellationToken cancellationToken)
		=> Answered(await CallAsync(new(NewId(), RunnerCommands.Stop, _token, closePosition), deadline, cancellationToken)).State;

	/// <inheritdoc />
	public async ValueTask DisposeAsync()
	{
		if (_disposed)
			return;

		_disposed = true;

		await _pipe.DisposeAsync();

		_gate.Dispose();
	}

	private async Task<RunnerAnswer> CallAsync(RunnerRequest request, TimeSpan deadline, CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);

		await _gate.WaitAsync(cancellationToken);

		try
		{
			await WorkerProtocol.WriteAsync(_pipe, request, cancellationToken).WaitAsync(deadline, cancellationToken);

			var answer = await WorkerProtocol.ReadAsync<RunnerAnswer>(_pipe, cancellationToken)
				.WaitAsync(deadline, cancellationToken)
				?? throw new IOException("The runner closed the connection without answering.");

			// One request at a time on one connection, so an answer to another question is a connection
			// out of step with itself. Discarded rather than reported: the numbers in it are another
			// moment's, and a report is only worth having if it is of the thing that was asked about.
			if (!string.Equals(answer.Id, request.Id, StringComparison.Ordinal))
			{
				throw new IOException(
					$"The runner answered request '{answer.Id}' while it was being asked '{request.Id}'.");
			}

			return answer;
		}
		finally
		{
			_gate.Release();
		}
	}

	private static RunnerAnswer Answered(RunnerAnswer answer)
	{
		if (!answer.Succeeded)
			throw new InvalidOperationException(answer.Failure);

		return answer;
	}

	/// <summary>
	/// Refuses a greeting that is not the runner that was asked for.
	/// </summary>
	/// <param name="record">What the runner wrote about itself.</param>
	/// <param name="hello">What answered the pipe.</param>
	/// <param name="expectedEngine">The platform build this session was assembled against.</param>
	private static void Refuse(RunnerRecord record, RunnerHello hello, string expectedEngine)
	{
		if (hello.Protocol != RunnerProtocol.Version)
		{
			throw new RunnerHandshakeException(
				$"The runner speaks protocol {hello.Protocol} and this session speaks {RunnerProtocol.Version}. " +
				"The two were deployed apart, which is not an arrangement this product supports.");
		}

		// An identity nothing could be read from is not a match with another one nothing could be read
		// from. Here it also keeps a stop from being sent to a strategy this build cannot reason about.
		if (string.IsNullOrEmpty(expectedEngine) || string.IsNullOrEmpty(hello.Engine))
		{
			var mute = string.IsNullOrEmpty(expectedEngine) ? "This session" : $"The runner on '{record.Pipe}'";

			throw new RunnerHandshakeException(
				$"{mute} cannot say which build of the trading platform it carries, so the two cannot be shown " +
				"to be the same build. Deploy both with the platform's assemblies beside them.");
		}

		if (!string.Equals(hello.Engine, expectedEngine, StringComparison.Ordinal))
		{
			throw new RunnerHandshakeException(
				$"The runner carries {hello.Engine} and this session was built against {expectedEngine}. " +
				"A strategy measured by one is not a strategy measured by the other.");
		}

		if (!string.Equals(hello.DeploymentId, record.DeploymentId, StringComparison.Ordinal))
		{
			throw new RunnerHandshakeException(
				$"The runner on '{record.Pipe}' is running {hello.DeploymentId} and the record there says " +
				$"{record.DeploymentId}. Something else answered to the name, so nothing was asked of it.");
		}
	}

	private static string NewId() => Guid.NewGuid().ToString("n")[..12];
}
