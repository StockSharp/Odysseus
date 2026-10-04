namespace Odysseus.Runner.Tests;

using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Odysseus.Application;
using Odysseus.Domain;
using Odysseus.Engine;
using Odysseus.Runner;

/// <summary>
/// The pipe a runner answers on, and the token that is the only thing standing between it and anybody
/// else on the machine.
/// </summary>
public partial class RunnerServiceTests
{
	private const string _token = "0123456789abcdef0123456789abcdef";

	/// <summary>A client that attaches with the token is answered, and keeps being answered.</summary>
	[TestMethod]
	public async Task AClientWithTheTokenIsAnswered()
	{
		await using var runner = await ListenAsync();
		await using var client = await runner.ConnectAsync(CancellationToken);

		var attached = await client.AskAsync(new("1", RunnerCommands.Attach, _token, false), CancellationToken);
		var observed = await client.AskAsync(new("2", RunnerCommands.Observe, _token, false), CancellationToken);

		IsTrue(attached.Succeeded, attached.Failure);
		IsTrue(observed.Succeeded, observed.Failure);
		AreEqual("2", observed.Id);
		IsNotNull(observed.State, "an answered client was not told the state.");
	}

	/// <summary>A wrong token is refused, told nothing about the deployment, and cut off.</summary>
	[TestMethod]
	public async Task AWrongTokenIsRefusedAndCutOff()
		=> await RefusedAsync(new("1", RunnerCommands.Attach, "not-the-token", false));

	/// <summary>The right token on anything but an attach is still refused: a client of ours attaches first.</summary>
	[TestMethod]
	public async Task AClientThatDoesNotAttachFirstIsRefused()
		=> await RefusedAsync(new("1", RunnerCommands.Observe, _token, false));

	/// <summary>
	/// A request after the attach that leaves the token out is refused too, so a connection somebody else
	/// gets hold of after the attach is worth nothing.
	/// </summary>
	[TestMethod]
	public async Task EveryRequestMustCarryTheToken()
	{
		await using var runner = await ListenAsync();
		await using var client = await runner.ConnectAsync(CancellationToken);

		IsTrue((await client.AskAsync(new("1", RunnerCommands.Attach, _token, false), CancellationToken)).Succeeded);

		var refused = await client.AskAsync(new("2", RunnerCommands.Observe, null, false), CancellationToken);

		AssertRefused(refused);
		IsNull(await client.NextAsync(CancellationToken), "the connection stayed open after a request without the token.");
	}

	/// <summary>A request that is refused does not stop the deployment.</summary>
	[TestMethod]
	public async Task ARefusedClientStopsNothing()
	{
		await using var runner = await ListenAsync();

		await using (var stranger = await runner.ConnectAsync(CancellationToken))
		{
			await stranger.AskAsync(new("1", RunnerCommands.Attach, "not-the-token", false), CancellationToken);

			try
			{
				await stranger.AskAsync(new("2", RunnerCommands.Stop, "not-the-token", true), CancellationToken);
			}
			catch (IOException)
			{
				// Cut off after the refused attach, which is what should happen.
			}
		}

		await using var client = await runner.ConnectAsync(CancellationToken);

		var observed = await client.AskAsync(new("2", RunnerCommands.Attach, _token, false), CancellationToken);

		IsTrue(observed.Succeeded, observed.Failure);
		AreNotEqual(RunnerPhases.Stopped, observed.State.Phase, "a stranger's stop was obeyed.");
	}

	private async Task RefusedAsync(RunnerRequest request)
	{
		await using var runner = await ListenAsync();
		await using var client = await runner.ConnectAsync(CancellationToken);

		AssertRefused(await client.AskAsync(request, CancellationToken));
		IsNull(await client.NextAsync(CancellationToken), "the connection stayed open after the refusal.");
	}

	private static void AssertRefused(RunnerAnswer answer)
	{
		IsFalse(answer.Succeeded, "a client without the token was answered.");
		IsNull(answer.State, "a refusal described the deployment.");
		IsNull(answer.Account, "a refusal described the account.");
	}

	private Task<Listening> ListenAsync()
	{
		var service = Service(TradingMandate.Paper, out _, out _);

		return Task.FromResult(new Listening(service, $"odysseus-test-{Guid.NewGuid():n}"));
	}

	/// <summary>A runner's pipe, listened on for as long as a test holds it.</summary>
	private sealed class Listening : IAsyncDisposable
	{
		private readonly CancellationTokenSource _stopping = new();
		private readonly Task _listening;
		private readonly string _pipe;

		public Listening(RunnerService service, string pipe)
		{
			_pipe = pipe;
			_listening = new RunnerServer(pipe, service).ListenAsync(_stopping.Token);
		}

		public async Task<Client> ConnectAsync(CancellationToken cancellationToken)
		{
			var pipe = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, PipeOptions.Asynchronous);

			await pipe.ConnectAsync(TimeSpan.FromSeconds(10), cancellationToken);

			var hello = await WorkerProtocol.ReadAsync<RunnerHello>(pipe, cancellationToken);

			IsNotNull(hello, "the runner did not greet.");

			return new(pipe);
		}

		public async ValueTask DisposeAsync()
		{
			await _stopping.CancelAsync();

			try
			{
				await _listening;
			}
			catch (OperationCanceledException)
			{
			}

			_stopping.Dispose();
		}
	}

	/// <summary>One connection to a runner, the way a session talks to it.</summary>
	private sealed class Client(NamedPipeClientStream pipe) : IAsyncDisposable
	{
		public async Task<RunnerAnswer> AskAsync(RunnerRequest request, CancellationToken cancellationToken)
		{
			await WorkerProtocol.WriteAsync(pipe, request, cancellationToken);

			return await NextAsync(cancellationToken);
		}

		public Task<RunnerAnswer> NextAsync(CancellationToken cancellationToken)
			=> WorkerProtocol.ReadAsync<RunnerAnswer>(pipe, cancellationToken);

		public ValueTask DisposeAsync() => pipe.DisposeAsync();
	}
}
