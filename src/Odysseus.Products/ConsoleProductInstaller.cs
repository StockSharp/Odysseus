namespace Odysseus.Products;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Odysseus.Application;

/// <summary>
/// Installs StockSharp products by driving the vendor's installer console as a process.
/// </summary>
/// <remarks>
/// Five properties of that program shape everything here, and none of them are ours to change.
///
/// It waits for a keypress before returning from every failure, and the switch named after suppressing
/// errors does not turn that off - so its standard input is closed the moment it starts, and every
/// invocation carries a wall-clock deadline and is killed with its children when it runs past one.
///
/// It takes no cancellation of any kind, so a token here can only kill it, never ask it to stop.
///
/// It is a machine-wide singleton holding one named pipe and one global mutex, and a second copy asks
/// the first to close rather than queueing behind it - so one invocation happens at a time inside this
/// process, and a foreign one is looked for before each start.
///
/// It signs in from a file in the user's documents folder, or else prompts for an email address and
/// waits forever - so that file is checked before anything but the hardware-id read, because without
/// the check every product call is a hang until its deadline.
///
/// And it has no machine-readable output at all, which is what <see cref="InstallerOutput"/> is for.
/// </remarks>
public sealed class ConsoleProductInstaller : IProductInstaller, IDisposable
{
	/// <summary>How many lines of an invocation's output an answer carries.</summary>
	/// <remarks>
	/// The whole of it is on disk and the answer carries the end, which is what the worker's own
	/// diagnostics do and for the same reason: the last thing a program said before it stopped is
	/// almost always the thing worth reading.
	/// </remarks>
	public const int TailLines = 40;

	private readonly ProductInstallerOptions _options;

	// One invocation at a time. Not a lock, because what is being serialised is asynchronous and long:
	// an install is twenty minutes of somebody else's program.
	private readonly SemaphoreSlim _gate = new(1, 1);

	/// <summary>
	/// Creates the installer.
	/// </summary>
	/// <param name="options">Where the console is, what it may touch and how long it is given.</param>
	/// <exception cref="ArgumentNullException">No options were given.</exception>
	/// <remarks>
	/// Constructed whether or not the console exists. A tool whose dependency is missing from the
	/// container fails while its arguments are being bound, before any code of ours runs, and the caller
	/// is told only that something went wrong invoking a tool; constructed against a path that holds
	/// nothing, the same call reaches here and is told what is missing and what still works.
	/// </remarks>
	public ConsoleProductInstaller(ProductInstallerOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);

		_options = options;
	}

	/// <inheritdoc />
	public async ValueTask<ProductInstallerState> DescribeAsync(CancellationToken cancellationToken)
	{
		var console = _options.Locate();
		var hasAccount = _options.HasAccount;

		IReadOnlyList<long> allowed = _options.AllowedProducts ?? Array.Empty<long>();
		IReadOnlyList<string> lookedIn = _options.LookedIn ?? Array.Empty<string>();

		var blockedBy = console.Length == 0
			? ProductInstallerState.NoConsole
			: allowed.Count == 0
				? ProductInstallerState.NoneAllowed
				: hasAccount
					? ProductInstallerState.Nothing
					: ProductInstallerState.NoAccount;

		var hardwareId = string.Empty;

		// The one command that needs neither an account nor the network. Whatever goes wrong reading it
		// - a foreign installer, a console that will not start, a deadline - this method answers rather
		// than failing: being unavailable is what it is here to report.
		if (console.Length > 0)
		{
			try
			{
				var capture = await RunAsync(
					console, InstallerVerbs.HddId, InstallerCommand.HardwareId(), cancellationToken);

				hardwareId = InstallerOutput.HardwareId(capture.Output);
			}
			catch (Exception error) when (error is not OperationCanceledException)
			{
				hardwareId = string.Empty;
			}
		}

		return new(
			IsAvailable: blockedBy == ProductInstallerState.Nothing,
			ConsolePath: console,
			LookedIn: lookedIn,
			HasAccount: hasAccount,
			AccountFile: _options.AccountFile ?? string.Empty,
			HardwareId: hardwareId,
			BlockedBy: blockedBy,
			AllowedProducts: allowed,
			InstallRoot: _options.InstallRoot);
	}

	/// <inheritdoc />
	public ValueTask<ProductOutcome> ListAsync(string search, CancellationToken cancellationToken)
		=> InvokeAsync(InstallerVerbs.Products, InstallerCommand.Products(search), product: 0, cancellationToken);

	/// <inheritdoc />
	public ValueTask<ProductOutcome> InstalledAsync(string search, CancellationToken cancellationToken)
		=> InvokeAsync(InstallerVerbs.Installed, InstallerCommand.Installed(search), product: 0, cancellationToken);

	/// <inheritdoc />
	public ValueTask<ProductOutcome> InstallAsync(long product, bool reinstall, CancellationToken cancellationToken)
		=> InvokeAsync(
			InstallerVerbs.Install,
			InstallerCommand.Install(product, _options.DirectoryFor(product), reinstall),
			product,
			cancellationToken);

	/// <inheritdoc />
	public ValueTask<ProductOutcome> UpdateAsync(long product, bool backupSettings, CancellationToken cancellationToken)
		=> InvokeAsync(InstallerVerbs.Update, InstallerCommand.Update(product, backupSettings), product, cancellationToken);

	/// <inheritdoc />
	public ValueTask<ProductOutcome> RemoveAsync(long product, bool removeData, CancellationToken cancellationToken)
		=> InvokeAsync(InstallerVerbs.Remove, InstallerCommand.Remove(product, removeData), product, cancellationToken);

	/// <summary>Releases the gate that serialises invocations.</summary>
	public void Dispose()
		=> _gate.Dispose();

	/// <summary>
	/// Refuses everything the operator has not arranged, then runs the console and reads what it said.
	/// </summary>
	/// <param name="verb">Verb being invoked.</param>
	/// <param name="arguments">The whole command line, verb included.</param>
	/// <param name="product">Product the call is about, or zero when it is about none.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>What the invocation came to.</returns>
	private async ValueTask<ProductOutcome> InvokeAsync(
		InstallerVerbs verb,
		IReadOnlyList<string> arguments,
		long product,
		CancellationToken cancellationToken)
	{
		var console = EnsureAvailable(product);

		var capture = await RunAsync(console, verb, arguments, cancellationToken);

		// The refusal we check for before starting, arriving in the race between that check and the
		// start. It is a phrase match and it is allowed to be wrong; being wrong means a caller sees a
		// conflict where there was some other failure, which is not a lie worth avoiding.
		if (capture.ExitCode != 0 && InstallerOutput.NamesAForeignInstaller(capture.Output))
		{
			throw new ProductInstallerBusyException(
				"Another StockSharp installer took the machine while this one was starting. The installer " +
				"is a machine-wide singleton, so the two cannot run together. Nothing was installed; the " +
				$"output is in {capture.LogPath}.");
		}

		var reading = InstallerOutput.Read(capture.Output);

		return new(
			Succeeded: capture.ExitCode == 0,
			ExitCode: capture.ExitCode,
			Products: reading.Products,
			Unparsed: reading.Unparsed,
			LogPath: capture.LogPath,
			Took: capture.Took);
	}

	/// <summary>
	/// Refuses a call this machine is not set up to make, and says which of the three reasons it was.
	/// </summary>
	/// <param name="product">Product the call is about, or zero when it is about none.</param>
	/// <returns>Path of the console to run.</returns>
	/// <exception cref="ProductInstallerUnavailableException">Something the operator supplies is missing.</exception>
	private string EnsureAvailable(long product)
	{
		var console = _options.Locate();

		if (console.Length == 0)
		{
			throw new ProductInstallerUnavailableException(
				"The StockSharp installer console is not on this machine, so no product can be installed " +
				$"or listed. It was looked for at {Join(_options.LookedIn)}. Nothing here downloads it: " +
				"the operator supplies the program and names it in " + ProductInstallerOptions.PathVariable +
				". Everything this server does with data, candidates and measurements works without it.");
		}

		if (_options.AllowedProducts is null || _options.AllowedProducts.Count == 0)
		{
			throw new ProductInstallerUnavailableException(
				"This server may install no product at all. The list of allowed product identifiers is " +
				"empty, which is what it is unless an operator wrote one - nothing in the research loop " +
				"needs a product, so installing is off until somebody turns it on for named products.");
		}

		if (product > 0 && !_options.Allows(product))
		{
			var allowed = Join(_options.AllowedProducts.Select(id => id.ToString(CultureInfo.InvariantCulture)));

			throw new ProductInstallerUnavailableException(
				$"Product {product.ToString(CultureInfo.InvariantCulture)} is not one this server may " +
				$"touch. The operator allowed {allowed}.");
		}

		if (!_options.HasAccount)
		{
			throw new ProductInstallerUnavailableException(
				"This machine has no StockSharp account signed in. The installer reads its credentials " +
				$"from {_options.AccountFile}, which is not there, and without them it stops and waits for " +
				"an email address to be typed at a keyboard - which nothing here can answer. So the call " +
				"is refused now rather than left to run into its deadline. The account is machine-wide and " +
				"is arranged with the installer itself; there is no way to pass one from here.");
		}

		return console;
	}

	/// <summary>
	/// Runs the console once, under the gate, and captures everything it printed.
	/// </summary>
	/// <param name="console">Path of the console.</param>
	/// <param name="verb">Verb being invoked, which decides the deadline.</param>
	/// <param name="arguments">The whole command line, verb included.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>What it printed, what it exited with and where that was written down.</returns>
	private async Task<Capture> RunAsync(
		string console,
		InstallerVerbs verb,
		IReadOnlyList<string> arguments,
		CancellationToken cancellationToken)
	{
		await _gate.WaitAsync(cancellationToken);

		try
		{
			AssertNoForeignInstaller();

			Directory.CreateDirectory(_options.InstallRoot);
			Directory.CreateDirectory(_options.Invocations);

			var info = new ProcessStartInfo(console)
			{
				RedirectStandardInput = true,
				RedirectStandardOutput = true,

				// Nothing is ever written here, as far as the vendor's source shows. Redirected anyway,
				// so that if that turns out to be wrong it lands in the capture rather than on the
				// standard error this server carries its own protocol frames beside.
				RedirectStandardError = true,
				UseShellExecute = false,

				// The whole of the confinement: the console resolves the directory it installs into, its
				// own default for that directory, and the folder it writes its text log to, all against
				// the directory it was started in.
				WorkingDirectory = _options.InstallRoot,
			};

			foreach (var argument in arguments)
				info.ArgumentList.Add(argument);

			var clock = Stopwatch.StartNew();

			Process process;

			try
			{
				process = Process.Start(info);
			}
			catch (Exception error) when (error is not OperationCanceledException)
			{
				// A console that is not where it was said to be arrives here as the operating system's
				// own error. Untyped it would reach the caller as a defect of this server, which it is
				// not: it is the one thing the operator supplies, and the answer has to say so.
				throw new ProductInstallerUnavailableException(
					$"The installer console at {console} would not start: {error.Message} It is supplied by " +
					$"the operator and named in {ProductInstallerOptions.PathVariable}; nothing here " +
					"downloads it.");
			}

			if (process is null)
			{
				throw new ProductInstallerUnavailableException(
					$"The installer console at {console} did not start and gave no reason.");
			}

			using (process)
			{
				// The program reads a key before returning from every failure. Closed rather than left
				// open, so that read ends at once instead of waiting for a keypress that is never coming.
				process.StandardInput.Close();

				// Both pipes are drained while the process runs. A child that fills one and is not read
				// blocks on the write and never reaches its own exit.
				var output = DrainAsync(process.StandardOutput);
				var errors = DrainAsync(process.StandardError);

				var deadline = _options.DeadlineFor(verb);
				var stopped = false;

				using (var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
				{
					limit.CancelAfter(deadline);

					try
					{
						await process.WaitForExitAsync(limit.Token);
					}
					catch (OperationCanceledException)
					{
						// The whole tree: the console starts nothing itself, but the NuGet client it
						// downloads through and the service it stops products with both can.
						Kill(process);

						stopped = true;
					}
				}

				var text = await ReadAsync(output, errors);
				var took = clock.Elapsed;
				var log = await WriteAsync(console, verb, arguments, text, stopped ? null : (int?)process.ExitCode, took);

				if (stopped)
				{
					// A caller that cancelled is told it cancelled. Only a deadline reached on its own is
					// the installer overrunning.
					cancellationToken.ThrowIfCancellationRequested();

					throw new ProductInstallerTimedOutException(
						$"The installer was stopped after {Describe(deadline)} and killed along with " +
						$"everything it had started. What it printed up to that point is in {log}.");
				}

				return new(process.ExitCode, text, log, took);
			}
		}
		finally
		{
			_gate.Release();
		}
	}

	/// <summary>
	/// Refuses to start while another installer holds the machine.
	/// </summary>
	/// <exception cref="ProductInstallerBusyException">One of them is running.</exception>
	/// <remarks>
	/// The same check the installer makes for itself, done up front so the answer can name the conflict
	/// in a category a caller can branch on rather than leaving it to be read out of prose. Its own
	/// version does more than refuse: it asks a running window to close, which is not a thing an agent
	/// should be able to do to somebody's desktop.
	/// </remarks>
	private void AssertNoForeignInstaller()
	{
		IReadOnlyList<string> names = _options.ForeignProcesses ?? Array.Empty<string>();

		foreach (var name in names)
		{
			Process[] running;

			try
			{
				running = Process.GetProcessesByName(name);
			}
			catch (Exception error) when (error is not OperationCanceledException)
			{
				// A check that could not be made is not a reason to refuse: the console makes the same
				// one for itself, and its refusal is recognised in the output.
				continue;
			}

			try
			{
				if (running.Length == 0)
					continue;

				throw new ProductInstallerBusyException(
					$"{name} is already running on this machine. The StockSharp installer is a machine-wide " +
					"singleton: it holds one named pipe and one global mutex, and a second one would ask " +
					"the first to close rather than waiting for it. Close it and ask again.");
			}
			finally
			{
				foreach (var process in running)
					process.Dispose();
			}
		}
	}

	/// <summary>
	/// Reads a pipe to its end, and answers with what arrived rather than raising.
	/// </summary>
	/// <param name="reader">Pipe to read.</param>
	/// <returns>What was read, or an empty string when the pipe broke.</returns>
	/// <remarks>
	/// A pipe whose far end was killed mid-write is not a failure of the call: what the program managed
	/// to say before it was stopped is exactly what the caller needs to read.
	/// </remarks>
	private static async Task<string> DrainAsync(TextReader reader)
	{
		try
		{
			return await reader.ReadToEndAsync(CancellationToken.None);
		}
		catch (Exception error) when (error is not OperationCanceledException)
		{
			return string.Empty;
		}
	}

	/// <summary>
	/// Waits for both pipes, without waiting for them for ever.
	/// </summary>
	/// <param name="output">Standard output being read.</param>
	/// <param name="errors">Standard error being read.</param>
	/// <returns>Everything that arrived.</returns>
	/// <remarks>
	/// After a kill both reads end, because the handles close with the process tree. The bound is for
	/// the case where something outside that tree inherited a handle: the answer is then short rather
	/// than never.
	/// </remarks>
	private static async Task<string> ReadAsync(Task<string> output, Task<string> errors)
	{
		await Task.WhenAny(Task.WhenAll(output, errors), Task.Delay(TimeSpan.FromSeconds(5)));

		var text = output.IsCompletedSuccessfully ? output.Result : string.Empty;
		var failures = errors.IsCompletedSuccessfully ? errors.Result : string.Empty;

		return string.IsNullOrWhiteSpace(failures) ? text : text + Environment.NewLine + failures;
	}

	/// <summary>
	/// Writes the whole of an invocation down.
	/// </summary>
	/// <param name="console">Path of the console.</param>
	/// <param name="verb">Verb that was invoked.</param>
	/// <param name="arguments">The whole command line, verb included.</param>
	/// <param name="text">Everything it printed.</param>
	/// <param name="exitCode">What it exited with, or <see langword="null"/> when it was killed.</param>
	/// <param name="took">How long it ran.</param>
	/// <returns>Path of the file, or an empty string when it could not be written.</returns>
	private async Task<string> WriteAsync(
		string console,
		InstallerVerbs verb,
		IReadOnlyList<string> arguments,
		string text,
		int? exitCode,
		TimeSpan took)
	{
		var now = DateTime.UtcNow;

		// The stamp alone would collide between two invocations inside one millisecond, which nothing but
		// a test can produce - and a test that loses a capture to a name clash is a test that fails for
		// the wrong reason.
		var path = Path.Combine(
			_options.Invocations,
			verb.ToString().ToLowerInvariant() +
			"-" + now.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture) +
			"-" + Guid.NewGuid().ToString("n")[..4] + ".log");

		var ended = exitCode is null
			? "killed on its deadline"
			: exitCode.Value.ToString(CultureInfo.InvariantCulture);

		var header = string.Join(
			Environment.NewLine,
			now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "Z " + console + " " + string.Join(' ', arguments),
			"working directory: " + _options.InstallRoot,
			"exit code: " + ended + " after " + took.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s",
			string.Empty,
			string.Empty);

		try
		{
			await File.WriteAllTextAsync(path, header + text, CancellationToken.None);

			return path;
		}
		catch (Exception error) when (error is not OperationCanceledException)
		{
			// The capture is a convenience; losing it must not lose the outcome as well.
			return string.Empty;
		}
	}

	private static void Kill(Process process)
	{
		try
		{
			if (!process.HasExited)
				process.Kill(entireProcessTree: true);

			process.WaitForExit(5000);
		}
		catch (Exception error) when (error is not OperationCanceledException)
		{
			// It exited between the two calls, or the platform would not let go of it. Either way there
			// is nothing further to do, and the deadline is reported whatever the kill came to.
		}
	}

	private static string Describe(TimeSpan deadline)
		=> deadline.TotalMinutes >= 1
			? deadline.TotalMinutes.ToString("0.#", CultureInfo.InvariantCulture) + " minutes"
			: deadline.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " seconds";

	private static string Join(IEnumerable<string> values)
	{
		var joined = values is null ? string.Empty : string.Join(", ", values);

		return joined.Length == 0 ? "nowhere" : joined;
	}

	/// <summary>What one run of the console printed, and what became of it.</summary>
	/// <param name="ExitCode">What it exited with.</param>
	/// <param name="Output">Everything it printed, standard error included.</param>
	/// <param name="LogPath">File that was captured to.</param>
	/// <param name="Took">How long it ran.</param>
	private sealed record Capture(int ExitCode, string Output, string LogPath, TimeSpan Took);
}
