namespace StockSharp.Odysseus.Engine;

using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;

/// <summary>
/// The worker process, seen from the server.
/// </summary>
/// <remarks>
/// A candidate is compiled code that this server generated but did not write down anywhere it can be
/// unloaded from, and a backtest is an unbounded amount of work on somebody else's arithmetic. Running
/// it in the server process gave three problems with one shape: every run added an assembly that can
/// never be collected, a hang had no floor because cancelling a call abandons the wait and not the work,
/// and a search that asked for more memory than the machine had took the server down with it. A process
/// answers all three, because killing one is a thing the operating system will actually do.
///
/// One worker at a time, serving one request at a time, and replaced when it has served enough of them.
/// A pool of one is what turns the unbounded accumulation into a bounded leak inside a process nobody
/// minds losing, and it removes any need for a collectible load context on the far side.
///
/// The worker is started with the broker variables removed from its environment, for the reason the
/// credential file's own documentation gives: a value in the environment is inherited by every process
/// the server starts, and the worker is now such a process. It is also never told where the projects
/// are. The bars of a slice are passed to it as data, so the one place that decides which bars a run may
/// see stays in the layer where that discipline lives.
/// </remarks>
public sealed class WorkerHost : IDisposable
{
	private readonly WorkerOptions _options;
	private readonly string _expectedEngine;
	private readonly SemaphoreSlim _gate = new(1, 1);

	private Worker _worker;
	private bool _disposed;

	/// <summary>
	/// Creates the host.
	/// </summary>
	/// <param name="options">Where the worker is and what it is allowed.</param>
	/// <param name="expectedEngine">
	/// The platform build this server was assembled against. An empty one is a server that cannot say
	/// which build it carries, and a worker is then refused rather than trusted.
	/// </param>
	public WorkerHost(WorkerOptions options, string expectedEngine)
	{
		_options = options ?? throw new ArgumentNullException(nameof(options));
		_expectedEngine = expectedEngine ?? string.Empty;
	}

	/// <summary>
	/// How the worker is launched, and what it is deliberately not told.
	/// </summary>
	/// <param name="options">Where the worker is.</param>
	/// <returns>The start information, ready to be started.</returns>
	/// <remarks>
	/// An executable is run directly; an assembly without one is run by the runtime host, which is what a
	/// framework-dependent deployment carries. Public because what is removed from the environment here is
	/// a guarantee rather than a detail, and a guarantee that cannot be asserted is a hope.
	/// </remarks>
	public static ProcessStartInfo Describe(WorkerOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);

		var runtimeHosted = options.WorkerPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

		var info = new ProcessStartInfo(runtimeHosted ? "dotnet" : options.WorkerPath)
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			WorkingDirectory = Path.GetDirectoryName(options.WorkerPath) ?? AppContext.BaseDirectory,
		};

		if (runtimeHosted)
			info.ArgumentList.Add(options.WorkerPath);

		foreach (var argument in options.Arguments ?? Array.Empty<string>())
			info.ArgumentList.Add(argument);

		// The reason the credentials are a path rather than a value: a value in the environment is
		// inherited by every process the server starts, and this is one of them. The worker is given the
		// bars of a slice as data, and never a place it could go and read more of them from.
		info.Environment.Remove("ODYSSEUS_BROKER_KEYS");
		info.Environment.Remove("ODYSSEUS_BROKER_CONNECTOR");
		info.Environment.Remove("ODYSSEUS_PROJECTS_ROOT");

		return info;
	}

	/// <summary>
	/// Runs one backtest in the worker.
	/// </summary>
	/// <param name="request">What to run.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>What happened.</returns>
	public async Task<BacktestOutcome> RunAsync(BacktestRequest request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);

		var answer = await CallAsync(
			new(NewId(), WorkerCommands.Backtest, request, null, null, _options.BatchSize),
			_options.RunDeadline,
			cancellationToken);

		return answer.Outcome
			?? throw new IsolationFailedException(
				IsolationFailures.Crashed,
				"The worker reported a backtest that succeeded and carried no result.");
	}

	/// <summary>
	/// Runs one parameter search in the worker.
	/// </summary>
	/// <param name="request">What to search.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>Every setting that was evaluated, best first.</returns>
	/// <remarks>
	/// The whole search is one request. The platform's genetic optimizer drives a live strategy through
	/// a fitness callback it invokes per chromosome, so there is no seam at which the host could keep the
	/// search and ship individual evaluations out - and no reason to want one, since a search is where
	/// the cost of starting a process is amortised best.
	/// </remarks>
	public async Task<IReadOnlyList<OptimizationTrial>> SearchAsync(
		OptimizationRequest request,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);

		var answer = await CallAsync(
			new(NewId(), WorkerCommands.Optimize, null, request, null, _options.BatchSize),
			_options.SearchDeadline,
			cancellationToken);

		return answer.Trials ?? [];
	}

	/// <summary>
	/// Runs one walk-forward in the worker.
	/// </summary>
	/// <param name="request">What to run.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>One result per window, oldest first.</returns>
	public async Task<IReadOnlyList<WalkForwardWindowResult>> WalkForwardAsync(
		WalkForwardRequest request,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);

		var answer = await CallAsync(
			new(NewId(), WorkerCommands.WalkForward, null, null, request, _options.BatchSize),
			_options.SearchDeadline,
			cancellationToken);

		return answer.WalkForward ?? [];
	}

	/// <inheritdoc />
	public void Dispose()
	{
		if (_disposed)
			return;

		_disposed = true;

		Retire();
		_gate.Dispose();
	}

	private static string Quoted(string id)
		=> string.IsNullOrEmpty(id) ? "'(none)'" : $"'{id}'";

	private static string NewId()
		=> Guid.NewGuid().ToString("n")[..12];

	private async Task<WorkerAnswer> CallAsync(
		WorkerRequest request,
		TimeSpan deadline,
		CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);

		await _gate.WaitAsync(cancellationToken);

		try
		{
			var worker = await StartedAsync(cancellationToken);

			return await ExchangeAsync(worker, request, deadline, cancellationToken);
		}
		finally
		{
			_gate.Release();
		}
	}

	private async Task<Worker> StartedAsync(CancellationToken cancellationToken)
	{
		if (_worker is { IsUsable: true })
			return _worker;

		Retire();

		var worker = Worker.Start(_options);

		try
		{
			var hello = await WorkerProtocol.ReadAsync<WorkerHello>(worker.Output, cancellationToken)
				.WaitAsync(_options.HandshakeDeadline, cancellationToken);

			if (hello is null)
			{
				throw new IsolationFailedException(
					IsolationFailures.Handshake,
					$"The worker at {_options.WorkerPath} exited without greeting. {worker.Diagnostics()}");
			}

			if (hello.Protocol != WorkerProtocol.Version)
			{
				throw new IsolationFailedException(
					IsolationFailures.Handshake,
					$"The worker speaks protocol {hello.Protocol} and this server speaks " +
					$"{WorkerProtocol.Version}. Deploy the server and the worker together.");
			}

			// An identity nothing could be read from is not a match with another one nothing could be
			// read from. A run's fingerprint does not carry the engine, so this comparison is the only
			// thing keeping two platform builds out of one namespace of measurements. Refused here
			// rather than at startup, because a deployment that cannot name its engine can still be
			// asked what it already measured; running a candidate is the one thing it must not do.
			if (_expectedEngine.Length == 0 || string.IsNullOrEmpty(hello.Engine))
			{
				var mute = _expectedEngine.Length == 0 ? "This server" : $"The worker at {_options.WorkerPath}";

				throw new IsolationFailedException(
					IsolationFailures.Handshake,
					$"{mute} cannot say which build of the trading platform it carries, so the two cannot be " +
					"shown to be the same build - and a run measured by one is not a run measured by the " +
					"other. Deploy both with the platform's assemblies beside them.");
			}

			if (!string.Equals(hello.Engine, _expectedEngine, StringComparison.Ordinal))
			{
				throw new IsolationFailedException(
					IsolationFailures.Handshake,
					$"The worker carries {hello.Engine} and this server was built against {_expectedEngine}. " +
					"A run measured by one is not a run measured by the other, so the two are deployed " +
					"together or not at all.");
			}
		}
		catch (TimeoutException)
		{
			worker.Dispose();

			throw new IsolationFailedException(
				IsolationFailures.Handshake,
				$"The worker at {_options.WorkerPath} did not greet within " +
				$"{_options.HandshakeDeadline.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} seconds.");
		}
		catch
		{
			worker.Dispose();
			throw;
		}

		_worker = worker;

		return worker;
	}

	private async Task<WorkerAnswer> ExchangeAsync(
		Worker worker,
		WorkerRequest request,
		TimeSpan deadline,
		CancellationToken cancellationToken)
	{
		using var watchdog = new Watchdog(worker, deadline, _options.MemoryLimitBytes);

		WorkerAnswer answer;

		try
		{
			await using (cancellationToken.Register(worker.Kill))
			{
				await WorkerProtocol.WriteAsync(worker.Input, request, cancellationToken);

				answer = await WorkerProtocol.ReadAsync<WorkerAnswer>(worker.Output, cancellationToken);
			}
		}
		catch (Exception error) when (error is not OperationCanceledException)
		{
			// The pipe broke, which is what killing the process on the other end looks like from here.
			answer = null;

			if (watchdog.Breach is null)
			{
				Retire();

				throw new IsolationFailedException(
					IsolationFailures.Crashed,
					$"The worker stopped answering: {error.Message} {worker.Diagnostics()}");
			}
		}

		if (watchdog.Breach is { } breach)
		{
			Retire();

			throw Describe(breach, deadline);
		}

		cancellationToken.ThrowIfCancellationRequested();

		if (answer is null)
		{
			Retire();

			throw new IsolationFailedException(
				IsolationFailures.Crashed,
				$"The worker exited without answering. {worker.Diagnostics()}");
		}

		// A worker serves one request at a time and is then reused, so one written frame too many leaves
		// the next request reading the previous one's answer - a whole, well-formed outcome of another
		// experiment, which nothing further down could tell from this one's. The worker is out of step
		// with the pipe rather than broken in it, so it is replaced rather than asked again.
		if (!string.Equals(answer.Id, request.Id, StringComparison.Ordinal))
		{
			Retire();

			throw new IsolationFailedException(
				IsolationFailures.Crashed,
				$"The worker answered request {Quoted(answer.Id)} while it was being asked {Quoted(request.Id)}. " +
				"An answer to another question is not a measurement of this one, so it is discarded and the " +
				$"worker replaced. {worker.Diagnostics()}");
		}

		worker.Served();

		if (worker.ShouldRetire(_options))
			Retire();

		if (!answer.Succeeded)
		{
			// The candidate failed, not the worker. Raised as the same failure the in-process runner threw,
			// so a run is recorded exactly as it always was.
			throw new InvalidOperationException(answer.Failure);
		}

		return answer;
	}

	private IsolationFailedException Describe(IsolationFailures breach, TimeSpan deadline)
	{
		if (breach == IsolationFailures.Memory)
		{
			var megabytes = (_options.MemoryLimitBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture);

			return new(
				breach,
				$"The run was stopped after using more than {megabytes} MB. Nothing was written.");
		}

		var minutes = deadline.TotalMinutes.ToString("0.#", CultureInfo.InvariantCulture);

		return new(breach, $"The run was stopped after {minutes} minutes. Nothing was written.");
	}

	private void Retire()
	{
		_worker?.Dispose();
		_worker = null;
	}

	/// <summary>
	/// Kills the worker when it overruns its deadline or its memory, and remembers which of the two it
	/// was. Killing rather than cancelling, because cancelling a read on a pipe is not reliable and
	/// killing the process closes the pipe, which ends the read for certain.
	/// </summary>
	private sealed class Watchdog : IDisposable
	{
		private readonly CancellationTokenSource _stop = new();
		private readonly Task _watching;

		private int _breach = -1;

		public Watchdog(Worker worker, TimeSpan deadline, long memoryLimit)
		{
			_watching = Task.Run(() => WatchAsync(worker, deadline, memoryLimit, _stop.Token), CancellationToken.None);
		}

		/// <summary>Which limit was reached, or null when neither was.</summary>
		public IsolationFailures? Breach
		{
			get
			{
				var value = Volatile.Read(ref _breach);

				return value < 0 ? null : (IsolationFailures)value;
			}
		}

		public void Dispose()
		{
			_stop.Cancel();

			try
			{
				_watching.GetAwaiter().GetResult();
			}
			catch (OperationCanceledException)
			{
			}

			_stop.Dispose();
		}

		private async Task WatchAsync(Worker worker, TimeSpan deadline, long memoryLimit, CancellationToken stop)
		{
			var started = Stopwatch.StartNew();

			while (!stop.IsCancellationRequested)
			{
				try
				{
					await Task.Delay(TimeSpan.FromMilliseconds(250), stop);
				}
				catch (OperationCanceledException)
				{
					return;
				}

				if (worker.HasExited)
					return;

				if (started.Elapsed > deadline)
				{
					Volatile.Write(ref _breach, (int)IsolationFailures.Timeout);
					worker.Kill();

					return;
				}

				// Polled rather than enforced by the operating system: a job object would be exact and is
				// Windows only, and "stop before the machine does" is a question a quarter-second poll
				// answers well enough - but only of a number that stands still between two polls.
				//
				// The high-water mark rather than the working set of the moment. The working set is how
				// much of the process is resident right now, and under memory pressure - which is the
				// one condition this exists for - the operating system trims it, so a run holding
				// gigabytes can read as holding very little at the instant the poll lands. Reading that
				// number, the watchdog waits while the machine it was meant to protect runs out of
				// memory, and the run ends up recorded as a worker that died rather than as a candidate
				// stopped on its limit: refunded and blamed on this server instead of charged and
				// reported to whoever wrote the strategy. The peak only ever rises, so crossing the
				// ceiling once is enough and no poll can miss it.
				if (worker.PeakWorkingSet > memoryLimit)
				{
					Volatile.Write(ref _breach, (int)IsolationFailures.Memory);
					worker.Kill();

					return;
				}
			}
		}
	}

	/// <summary>One worker process and the two pipes it is spoken to through.</summary>
	private sealed class Worker : IDisposable
	{
		private readonly Process _process;
		private readonly Queue<string> _errors = new();
		private readonly Lock _sync = new();

		private int _served;

		private Worker(Process process)
		{
			_process = process;

			_process.ErrorDataReceived += OnError;
			_process.BeginErrorReadLine();
		}

		public Stream Input => _process.StandardInput.BaseStream;

		public Stream Output => _process.StandardOutput.BaseStream;

		public bool HasExited => _process.HasExited;

		public bool IsUsable => !_process.HasExited;

		/// <summary>
		/// The most physical memory the process has ever held, which is what the memory limit is about.
		/// </summary>
		/// <remarks>
		/// A high-water mark on both platforms this runs on - PeakWorkingSetSize on Windows, VmHWM on
		/// Linux - and neither of them lowers it when the operating system reclaims pages.
		/// </remarks>
		public long PeakWorkingSet
		{
			get
			{
				try
				{
					_process.Refresh();

					return _process.PeakWorkingSet64;
				}
				catch (InvalidOperationException)
				{
					// It exited between the check above and this read, which the caller notices anyway.
					return 0;
				}
			}
		}

		public static Worker Start(WorkerOptions options)
		{
			Process process;

			try
			{
				process = Process.Start(WorkerHost.Describe(options));
			}
			catch (Exception error) when (error is not OperationCanceledException)
			{
				// A worker that is not where the deployment says it is arrives here as a Win32Exception.
				// Untyped it reaches the service as an ordinary exception raised while a candidate ran,
				// which is what a candidate throwing looks like, and the run is charged to the strategy.
				throw new IsolationFailedException(
					IsolationFailures.Crashed,
					$"The worker at {options.WorkerPath} would not start: {error.Message}",
					error);
			}

			// Null is documented only for a reused process, which needs a shell start and this is not
			// one. Refused rather than dereferenced further in.
			if (process is null)
			{
				throw new IsolationFailedException(
					IsolationFailures.Crashed,
					$"The worker at {options.WorkerPath} started nothing and said nothing about why.");
			}

			return new(process);
		}

		public void Served() => _served++;

		public bool ShouldRetire(WorkerOptions options)
			=> _process.HasExited || _served >= options.RunsBeforeRecycle;

		public void Kill()
		{
			try
			{
				if (!_process.HasExited)
					_process.Kill(entireProcessTree: true);
			}
			catch (InvalidOperationException)
			{
				// It had already gone, which is the outcome that was wanted.
			}
		}

		public string Diagnostics()
		{
			using (_sync.EnterScope())
				return _errors.Count == 0 ? string.Empty : $"It last said: {string.Join(" / ", _errors)}";
		}

		public void Dispose()
		{
			_process.ErrorDataReceived -= OnError;

			Kill();

			_process.Dispose();
		}

		private void OnError(object sender, DataReceivedEventArgs args)
		{
			if (args.Data is null)
				return;

			using (_sync.EnterScope())
			{
				_errors.Enqueue(args.Data);

				while (_errors.Count > 10)
					_errors.Dequeue();
			}
		}
	}
}
