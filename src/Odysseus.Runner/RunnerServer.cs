namespace Odysseus.Runner;

using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

using Odysseus.Engine;

/// <summary>
/// The pipe a runner answers on.
/// </summary>
/// <remarks>
/// A pipe rather than this process's own standard input and output, for three reasons and the third is
/// the one that decides it. A session that did not start the runner has to be able to reach it, and
/// standard input cannot be re-attached. Several clients may want it at once - an agent reading while a
/// person at a terminal stops. And leaving standard input alone is what lets a person's terminal keep
/// its meaning: an interrupt there is a person's decision about this process, not a frame in a protocol.
///
/// The token is read off the plan in this runner's own home, which is protected by the filesystem the
/// same way the credential file is. It is not a cryptographic barrier and is not claimed to be one; what
/// it does is stop an unrelated local process from stumbling into a stop.
/// </remarks>
internal sealed class RunnerServer
{
	/// <summary>
	/// Connections served at once.
	/// </summary>
	/// <remarks>
	/// Deliberately more than one, and deliberately unlike the installer's single instance: a reader must
	/// not be able to lock out the person trying to stop the thing it is reading.
	/// </remarks>
	private const int Connections = 4;

	private readonly string _pipe;
	private readonly RunnerService _service;

	/// <summary>
	/// Creates the server.
	/// </summary>
	/// <param name="pipe">Name of the pipe to listen on.</param>
	/// <param name="service">The deployment being served.</param>
	public RunnerServer(string pipe, RunnerService service)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(pipe);

		_pipe = pipe;
		_service = service ?? throw new ArgumentNullException(nameof(service));
	}

	/// <summary>
	/// Answers until the runner is told to stop.
	/// </summary>
	/// <param name="cancellationToken">Cancellation token.</param>
	public async Task ListenAsync(CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			NamedPipeServerStream pipe;

			try
			{
				pipe = new(
					_pipe,
					PipeDirection.InOut,
					Connections,
					PipeTransmissionMode.Byte,
					PipeOptions.Asynchronous);
			}
			catch (IOException)
			{
				// Every instance is in use. A client will free one; refusing to listen for good because
				// four readers turned up at once would leave the runner unreachable afterwards.
				await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
				continue;
			}

			try
			{
				await pipe.WaitForConnectionAsync(cancellationToken);
			}
			catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException)
			{
				await pipe.DisposeAsync();

				if (cancellationToken.IsCancellationRequested)
					return;

				continue;
			}

			// Not awaited: one slow client must not stop the runner answering anybody else, and in
			// particular must not stop it answering the person trying to end it.
			_ = ServeAsync(pipe, cancellationToken);
		}
	}

	private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
	{
		try
		{
			await WorkerProtocol.WriteAsync(pipe, _service.Greeting(), cancellationToken);

			var attached = false;

			while (!cancellationToken.IsCancellationRequested)
			{
				var request = await WorkerProtocol.ReadAsync<RunnerRequest>(pipe, cancellationToken);

				if (request is null)
					return;

				// Attach must be first, and every later request carries the token as well. A client that
				// starts anywhere else is not one of ours, and the connection is dropped rather than
				// answered: an answer would tell it what this runner is holding.
				if (!Presented(request) || (!attached && request.Command != RunnerCommands.Attach))
				{
					await WorkerProtocol.WriteAsync(
						pipe,
						new RunnerAnswer(
							request.Id,
							false,
							null,
							null,
							"This runner answers a client that attached with the token written in its home. " +
							"The token is in the runner's directory under the projects root, which is where a " +
							"session that is meant to be talking to it reads it from."),
						cancellationToken);

					return;
				}

				attached = true;

				await WorkerProtocol.WriteAsync(pipe, await AnswerAsync(request, cancellationToken), cancellationToken);
			}
		}
		catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException)
		{
			// The client went away, or this runner is shutting down. Neither is a failure of the
			// deployment, and neither is written down as one.
		}
		finally
		{
			await pipe.DisposeAsync();
		}
	}

	private async Task<RunnerAnswer> AnswerAsync(RunnerRequest request, CancellationToken cancellationToken)
	{
		try
		{
			switch (request.Command)
			{
				case RunnerCommands.Attach:
				case RunnerCommands.Observe:
					return new(request.Id, true, _service.Observe(), null, null);

				case RunnerCommands.Account:
					return new(
						request.Id,
						true,
						_service.Observe(),
						await _service.AccountAsync(cancellationToken),
						null);

				case RunnerCommands.Stop:
					return new(request.Id, true, await _service.StopAsync(request.ClosePosition, cancellationToken), null, null);

				default:
					return new(request.Id, false, _service.Observe(), null, $"'{request.Command}' is not something this runner does.");
			}
		}
		catch (Exception error) when (error is not OperationCanceledException)
		{
			// The request failed, not the runner. A client is told what went wrong and the strategy keeps
			// running, because a reading that could not be taken is not a reason to end a deployment.
			return new(request.Id, false, _service.Observe(), null, error.Message);
		}
	}

	private bool Presented(RunnerRequest request)
		=> string.Equals(request.Token, _service.Token, StringComparison.Ordinal);
}
