namespace Odysseus.Runner.Tests;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Odysseus.Application;
using Odysseus.Domain;
using Odysseus.Engine;
using Odysseus.TestKit;

/// <summary>
/// The runner as a process, asked for the phrase of a live mandate by a test that cannot type.
/// </summary>
/// <remarks>
/// Everything else about the confirmation is settled without a console: whether a phrase matches, what a
/// refusal says, what gets written down. What cannot be settled that way is the question this process
/// asks about its own console - is there anybody here - because the answer comes from the handles the
/// operating system gave it, and those exist only in a process somebody actually started.
///
/// Both ways of being wrong about that are here, and they are not symmetrical. Deciding there is nobody
/// when there is means a person at a terminal is never asked and a mandate they meant to confirm is
/// refused, which is annoying. Deciding there is somebody when there is not means a runner nobody can
/// see stops on a question and waits for ever, holding its project and answering nothing - which from
/// outside is indistinguishable from a runner that is trading, and is the failure worth a suite.
///
/// The runner is reached by path, the way a launcher reaches it, rather than by being called into: what
/// is under test is a process, and a process is the one thing a reference cannot give you. Each one is
/// given a live mandate whose phrase nothing supplies, so all of them end in a refusal - what differs is
/// which refusal, how long it took, and what was left behind.
/// </remarks>
[TestClass]
public class RunnerConsoleTests : OdysseusTestBase
{
	/// <summary>The sentence the mandate is written around, which nothing here ever supplies.</summary>
	private const string Phrase = "trade real money on U1234567 until the thirtieth";

	/// <summary>Environment variable naming the file the broker credentials are in, which none of these has.</summary>
	private const string KeysVariable = "ODYSSEUS_BROKER_KEYS";

	/// <summary>The interrupt this suite sends. See <see cref="StartInItsOwnGroup"/> for why it is Break.</summary>
	private const int CtrlBreakEvent = 1;

	private const int CreateNewProcessGroup = 0x00000200;
	private const int CreateUnicodeEnvironment = 0x00000400;
	private const int WaitObject0 = 0;

	private readonly List<Process> _started = [];

	private string _root;
	private string _deploymentId;
	private string _mandate;

	/// <summary>Makes a home of its own, under an identifier no other test shares.</summary>
	/// <remarks>
	/// A runner listens on a pipe named after its deployment and a named pipe is machine-wide, so two
	/// tests holding one identifier would be two processes fighting over one name.
	/// </remarks>
	[TestInitialize]
	public void CreateHome()
	{
		_deploymentId = $"dep_console_{Guid.NewGuid():n}";
		_root = Path.Combine(Path.GetTempPath(), "odysseus-runner-console", Guid.NewGuid().ToString("n"));
		_mandate = null;
	}

	/// <summary>Ends whatever is still running, and removes what it was run out of.</summary>
	[TestCleanup]
	public void DeleteHome()
	{
		foreach (var process in _started)
		{
			try
			{
				if (!process.HasExited)
					process.Kill(entireProcessTree: true);
			}
			catch (Exception error) when (error is InvalidOperationException or NotSupportedException)
			{
				// It had already gone, which is the outcome that was wanted.
			}

			process.Dispose();
		}

		if (Directory.Exists(_root))
		{
			try
			{
				Directory.Delete(_root, recursive: true);
			}
			catch (IOException)
			{
				// A runner still letting go of its own log. The directory is under the temporary folder
				// and its name is a fresh identifier, so nothing later depends on it having gone.
			}
		}
	}

	/// <summary>
	/// A runner whose standard input is a pipe refuses rather than waiting. This is every runner a host
	/// starts, and the wait it must not do is the one nobody can end: there is no person on the other
	/// side of that handle to type anything, and a runner standing there would hold its project and
	/// answer nothing until somebody noticed and killed it.
	/// </summary>
	[TestMethod]
	public async Task ARunnerWhoseInputIsAPipeRefusesInsteadOfWaiting()
	{
		var home = Home();
		var process = Start(home, redirectInput: true, detached: false);
		var said = process.StandardOutput.ReadToEndAsync(CancellationToken);

		AreEqual(3, await Ended(process), "a runner that never started reported that it had.");

		NothingWasAsked(await said);
		Refused(home);
		Says(home, "not a terminal");
		Says(home, LiveMandateConfirmation.PhraseVariable);
	}

	/// <summary>
	/// Standard input that ended is not a person either. A closed handle is what a parent that went away
	/// leaves behind, and it has to be the same refusal rather than a read that comes back empty and is
	/// taken for somebody pressing return.
	/// </summary>
	[TestMethod]
	public async Task AnInputThatEndedIsNotAPersonEither()
	{
		var home = Home();
		var process = Start(home, redirectInput: true, detached: false);
		var said = process.StandardOutput.ReadToEndAsync(CancellationToken);

		process.StandardInput.Close();

		AreEqual(3, await Ended(process), "a runner that never started reported that it had.");

		NothingWasAsked(await said);
		Refused(home);
		Says(home, "not a terminal");
	}

	/// <summary>
	/// A runner a host detached is never asked, whatever it inherited. Its standard input is not
	/// redirected here - it is this test host's own, whatever that happens to be - and detachment alone
	/// settles it, because the console a detached runner shares belongs to the host, and the person at
	/// it, if there is one, is not sitting there for this process.
	/// </summary>
	[TestMethod]
	public async Task ADetachedRunnerIsNeverAskedWhateverItInherited()
	{
		var home = Home();
		var process = Start(home, redirectInput: false, detached: true);
		var said = process.StandardOutput.ReadToEndAsync(CancellationToken);

		AreEqual(3, await Ended(process), "a runner that never started reported that it had.");

		NothingWasAsked(await said);
		Refused(home);
		Says(home, LiveMandateConfirmation.PhraseVariable);
	}

	/// <summary>
	/// What a runner makes of the standard input it inherited is what that input is. The child is given
	/// this test host's own, so what to expect comes from the environment rather than from the code being
	/// tested: under a test run it is a pipe and the runner must refuse; run from a terminal it is a
	/// terminal, and then the runner must ask - and must still be standing there waiting, rather than
	/// having read an answer off a stream nobody typed into.
	/// </summary>
	[TestMethod]
	public async Task WhatARunnerMakesOfItsInheritedInputIsWhatThatInputIs()
	{
		var home = Home();
		var process = Start(home, redirectInput: false, detached: false);
		var said = process.StandardOutput.ReadToEndAsync(CancellationToken);

		if (Console.IsInputRedirected)
		{
			AreEqual(3, await Ended(process), "a runner that inherited a pipe found somebody to ask.");

			NothingWasAsked(await said);
			Refused(home);

			return;
		}

		// A terminal was inherited, so the runner is at the question - and has to still be there. This is
		// the branch where a wrong answer would have been read off the stream instead of off a person.
		// Still being there is asked first: a runner that refused removes its record on the way out, so
		// waiting for the record would report the refusal as a missing file half a minute later.
		IsFalse(await Exited(process, TimeSpan.FromSeconds(5)),
			$"the runner refused a terminal it was actually started at: {Log(home)}");

		await Recorded(home);

		process.Kill(entireProcessTree: true);

		var question = await said;

		IsTrue(question.Contains("U1234567", StringComparison.Ordinal),
			$"the question does not say which account is about to be traded: {question}");

		IsFalse(question.Contains(Phrase, StringComparison.OrdinalIgnoreCase),
			$"the question printed the phrase it was asking for: {question}");
	}

	/// <summary>
	/// An interrupt at the terminal, while the runner stands at the phrase, starts nothing and says so.
	/// The person pressing it is answering the question - with no - and what has to come of that is an
	/// ordinary refusal: no strategy, an exit code that says nothing started, a journal entry saying
	/// nobody confirmed the mandate, and no stack trace anywhere near it.
	/// </summary>
	/// <remarks>
	/// The runner is started in a console group of its own so the interrupt reaches it and not this test
	/// host, and inheriting no handles so its standard input is the console's own rather than the pipes
	/// a test run is wrapped in - which is what puts it at a real question in the first place. Break
	/// rather than C because Windows disables Ctrl+C for a group created this way; both arrive as the
	/// same interrupt at the same handler, generated by the operating system rather than simulated here.
	/// </remarks>
	[TestMethod]
	public async Task AnInterruptWhileTheRunnerStandsAtThePhraseStartsNothing()
	{
		if (!OperatingSystem.IsWindows())
		{
			Assert.Inconclusive(
				"A console interrupt is sent to a child by process group here, which is a Windows call. The " +
				"same event on Unix is SIGINT and is easy enough to send - but putting the runner at the " +
				"question first needs a pseudo-terminal, because a child inherits this test host's standard " +
				"input and under a test run that is a pipe. Nothing is put in its place.");
		}

		if (!HasConsole())
		{
			Assert.Inconclusive(
				"This test host has no console, so there is no console for an interrupt to happen on and none " +
				"for a child to inherit a terminal from. Run this suite from a terminal to exercise it.");
		}

		var home = Home();

		using var runner = StartInItsOwnGroup(home);

		// Whether it is still there is what says it got a terminal, and it is asked first. The record is
		// not the thing to wait for here: a runner writes it before it asks anybody anything and removes
		// it again on the way out, so a refused one writes and removes it too - and waiting for it races
		// the removal, which is a test that reports a defect on whichever side of that race it lands.
		if (await Exited(runner, TimeSpan.FromSeconds(5)))
		{
			Assert.Inconclusive(
				"The runner did not get a terminal from this test host, so it refused before there was a " +
				$"question for an interrupt to interrupt: {Log(home)}");
		}

		// Still there, so it is standing at the question - and it wrote down how to find it on the way.
		await Recorded(home);

		IsTrue(GenerateConsoleCtrlEvent(CtrlBreakEvent, runner.Id),
			$"the console interrupt could not be sent to the runner: {Marshal.GetLastWin32Error()}.");

		IsTrue(await Exited(runner, TimeSpan.FromSeconds(60)),
			"the runner was still waiting for a phrase a minute after the interrupt at its terminal.");

		AreEqual(3, runner.ExitCode, "a runner nobody confirmed reported that it had started.");

		Refused(home);
		Says(home, "Nobody answered");
	}

	/// <summary>
	/// What a refusal leaves behind, whichever way the runner was refused.
	/// </summary>
	/// <param name="home">The runner's home.</param>
	private static void Refused(RunnerHome home)
	{
		var log = Log(home);

		IsFalse(log.Contains("   at ", StringComparison.Ordinal),
			$"the runner left a stack trace where a person was to be told what to do: {log}");

		IsFalse(log.Contains(nameof(LiveMandateInvalidException), StringComparison.Ordinal),
			$"the runner reported a decision it had made as though it had fallen over: {log}");

		var last = home.LastEntry();

		IsNotNull(last, $"nothing was written down about a runner that was refused: {log}");

		AreEqual(RunnerPhases.Failed, last.Phase,
			$"a runner that never started did not write down that it had failed: {last.What}");

		AreEqual(TradingModes.Live, last.Mode, "a refused live runner wrote itself down as a paper one.");
		AreEqual(0m, last.Position, "a runner that never started wrote down a position.");
		AreEqual(0, last.Trades, "a runner that never started wrote down trades.");

		IsNull(home.ReadRecord(), "a runner that exited left behind the record saying how to reach it.");
	}

	/// <summary>
	/// Asserts that the runner's log carries a sentence.
	/// </summary>
	/// <param name="home">The runner's home.</param>
	/// <param name="text">What it has to say.</param>
	private static void Says(RunnerHome home, string text)
	{
		var log = Log(home);

		IsTrue(log.Contains(text, StringComparison.OrdinalIgnoreCase),
			$"the runner's log does not say '{text}': {log}");
	}

	/// <summary>
	/// Asserts that nobody was asked anything.
	/// </summary>
	/// <param name="said">Everything the runner wrote to standard output.</param>
	private static void NothingWasAsked(string said)
		=> IsFalse(said.Contains("Type the phrase", StringComparison.OrdinalIgnoreCase),
			$"a question was put to a standard input with nobody at it: {said}");

	/// <summary>Everything the runner wrote for a person to read.</summary>
	/// <param name="home">The runner's home.</param>
	/// <returns>The log, or a note saying why there is none.</returns>
	private static string Log(RunnerHome home)
	{
		try
		{
			return File.Exists(home.LogFile)
				? File.ReadAllText(home.LogFile)
				: $"there is no log at {home.LogFile}";
		}
		catch (IOException error)
		{
			return $"the log at {home.LogFile} could not be read: {error.Message}";
		}
	}

	/// <summary>Waits until the runner has written down how to find it, which it does before it asks.</summary>
	/// <param name="home">The runner's home.</param>
	private async Task Recorded(RunnerHome home)
	{
		for (var waited = 0; waited < 300; waited++)
		{
			if (home.ReadRecord() is not null)
				return;

			await Task.Delay(TimeSpan.FromMilliseconds(100), CancellationToken);
		}

		Fail($"The runner never wrote a record into {home.Directory}: {Log(home)}");
	}

	/// <summary>
	/// Waits for the runner to exit, and gives up well before this test's own deadline.
	/// </summary>
	/// <param name="process">The runner.</param>
	/// <returns>Its exit code.</returns>
	private async Task<int> Ended(Process process)
	{
		if (!await Exited(process, TimeSpan.FromSeconds(60)))
		{
			Fail(
				"The runner was still running a minute after it was started, so it is waiting for a phrase " +
				"that nobody can type into it.");
		}

		return process.ExitCode;
	}

	/// <summary>
	/// Whether the runner is gone within a deadline.
	/// </summary>
	/// <param name="process">The runner.</param>
	/// <param name="deadline">How long to wait.</param>
	/// <returns><see langword="true"/> when it exited inside the deadline.</returns>
	private async Task<bool> Exited(Process process, TimeSpan deadline)
	{
		using var waiting = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);

		waiting.CancelAfter(deadline);

		try
		{
			await process.WaitForExitAsync(waiting.Token);

			return true;
		}
		catch (OperationCanceledException)
		{
			return false;
		}
	}

	/// <summary>
	/// Whether the runner started in a console group of its own is gone within a deadline.
	/// </summary>
	/// <param name="runner">The runner.</param>
	/// <param name="deadline">How long to wait.</param>
	/// <returns><see langword="true"/> when it exited inside the deadline.</returns>
	private static Task<bool> Exited(ConsoleChild runner, TimeSpan deadline)
		=> Task.Run(() => runner.Wait(deadline));

	/// <summary>
	/// Writes a home the runner can be started against, with a plan the mandate covers.
	/// </summary>
	/// <returns>The home.</returns>
	private RunnerHome Home()
	{
		var home = new RunnerHome(Path.Combine(_root, _deploymentId), _deploymentId);

		home.WritePlan(new(
			RunnerHome.Schema,
			_deploymentId,
			"prj_console",
			"cnd_console",
			"Generated",
			"strategy.dll",
			"AAPL",
			TimeSpan.FromMinutes(5),
			10m,
			new Dictionary<string, decimal>(StringComparer.Ordinal),
			new("StockSharp.Example", "1.2.3", "StockSharp.Example.ExampleMessageAdapter", null),
			Path.Combine(_root, "connectors"),
			["https://example.invalid"],
			["StockSharp."],
			RunnerProtocol.PipeOf(_deploymentId),
			"0123456789abcdef0123456789abcdef",
			TimeSpan.FromSeconds(15)));

		// Read only after the phrase has been confirmed, and nothing here ever gets that far. Written
		// anyway, so that what refuses these runners is the confirmation rather than a missing file
		// underneath it.
		File.WriteAllBytes(home.AssemblyFile, [1, 2, 3]);

		return home;
	}

	/// <summary>
	/// The live mandate these runners are started with, written once per test.
	/// </summary>
	/// <returns>Path of the file, as the variable names it.</returns>
	/// <remarks>
	/// It covers the plan above - the same connector package, the same instrument - and expires a month
	/// out, so that what refuses a runner here is always the phrase and never one of the checks in front
	/// of it.
	/// </remarks>
	private string Mandate()
	{
		if (_mandate is not null)
			return _mandate;

		Directory.CreateDirectory(_root);

		var path = Path.Combine(_root, "mandate.json");

		File.WriteAllText(
			path,
			$$"""
			{
			  "schema": 1,
			  "phrase": "{{Phrase}}",
			  "account": "U1234567",
			  "connector": {
			    "packageId": "StockSharp.Example",
			    "packageVersion": "1.2.3",
			    "adapter": "StockSharp.Example.ExampleMessageAdapter"
			  },
			  "symbols": ["AAPL"],
			  "maxPositionNotional": 5000,
			  "expiresAt": "{{DateTime.UtcNow.AddDays(30):O}}"
			}
			""");

		_mandate = path;

		return path;
	}

	/// <summary>
	/// Where the runner was built. Reached by path rather than by reference, which is the whole point of
	/// a runner being a process of its own.
	/// </summary>
	/// <returns>The executable, or <see langword="null"/> when it has not been built.</returns>
	private static string Built()
	{
		var configuration = AppContext.BaseDirectory.Contains(
			$"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}",
			StringComparison.OrdinalIgnoreCase)
			? "Debug"
			: "Release";

		var candidate = Path.Combine(
			RepositoryRoot, "src", "Odysseus.Runner", "bin", configuration, "net10.0",
			OperatingSystem.IsWindows() ? "Odysseus.Runner.exe" : "Odysseus.Runner");

		return File.Exists(candidate) ? candidate : null;
	}

	/// <summary>
	/// Starts the runner over a home, with a live mandate nothing here can confirm.
	/// </summary>
	/// <param name="home">Its home.</param>
	/// <param name="redirectInput">Whether to give it a pipe for standard input instead of this process's own.</param>
	/// <param name="detached">Whether to start it the way a host does.</param>
	/// <returns>The process.</returns>
	/// <remarks>
	/// Standard output is redirected in every case and standard input only when the test says so: the two
	/// are independent, and a question this test could not read would be a question it could not say had
	/// gone unasked. Standard error is left alone, so that anything the runner fails with before it has a
	/// log to write into lands in the test run's own output.
	/// </remarks>
	private Process Start(RunnerHome home, bool redirectInput, bool detached)
	{
		var runner = Built();

		if (runner is null)
			Fail("The runner was not found where the build puts it. This project builds it, so its absence is a broken build or a wrong path.");

		var info = new ProcessStartInfo(runner)
		{
			UseShellExecute = false,
			RedirectStandardInput = redirectInput,
			RedirectStandardOutput = true,
		};

		info.ArgumentList.Add(home.Directory);

		if (detached)
			info.ArgumentList.Add(RunnerLauncher.DetachedArgument);

		info.Environment[LiveMandateFile.PathVariable] = Mandate();
		info.Environment.Remove(LiveMandateConfirmation.PhraseVariable);
		info.Environment.Remove(KeysVariable);

		var process = Process.Start(info);

		_started.Add(process);

		return process;
	}

	/// <summary>
	/// Starts the runner in a console group of its own, inheriting no handles.
	/// </summary>
	/// <param name="home">Its home.</param>
	/// <returns>The process, which whoever started it has to end.</returns>
	/// <remarks>
	/// Two things are wanted that an ordinary start cannot give. The group is what makes the interrupt
	/// reachable: a console control event goes to a process group, and a child started the ordinary way
	/// is in this test host's group, so sending one would interrupt the test run itself. Inheriting no
	/// handles is what gives the child the console's own standard input rather than the pipes a test run
	/// is wrapped in - and that is the only way it is a terminal at all, so the only way there is a
	/// question here to interrupt.
	///
	/// The cost is that nothing it says can be read out of a pipe. What it did is read where a session
	/// would read it: out of its own home.
	/// </remarks>
	private ConsoleChild StartInItsOwnGroup(RunnerHome home)
	{
		var runner = Built();

		if (runner is null)
			Fail("The runner was not found where the build puts it. This project builds it, so its absence is a broken build or a wrong path.");

		var line = $"\"{runner}\" \"{home.Directory}\"";
		var environment = Marshal.StringToHGlobalUni(Block());
		var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };

		try
		{
			var started = CreateProcess(
				runner,
				new StringBuilder(line, line.Length + 32),
				IntPtr.Zero,
				IntPtr.Zero,
				inheritHandles: false,
				CreateNewProcessGroup | CreateUnicodeEnvironment,
				environment,
				null,
				ref startup,
				out var information);

			if (!started)
				Fail($"The runner could not be started in a console group of its own: {Marshal.GetLastWin32Error()}.");

			CloseHandle(information.Thread);

			return new(information.Process, information.ProcessId);
		}
		finally
		{
			Marshal.FreeHGlobal(environment);
		}
	}

	/// <summary>
	/// Whether this process is attached to a console at all.
	/// </summary>
	/// <returns><see langword="true"/> when it has one.</returns>
	/// <remarks>
	/// Asked by counting who is attached rather than by looking for a window, because a test host is
	/// usually started without one: a console with no window is still a console, and is still both a
	/// terminal a child can inherit and something an interrupt can happen on.
	/// </remarks>
	private static bool HasConsole()
		=> GetConsoleProcessList(new int[1], 1) > 0;

	/// <summary>
	/// The environment such a runner is given, as Windows wants it: this process's own plus the mandate,
	/// minus every way of confirming it, sorted, name=value, and null-separated.
	/// </summary>
	/// <returns>The block.</returns>
	private string Block()
	{
		var values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		foreach (DictionaryEntry variable in Environment.GetEnvironmentVariables())
		{
			if (variable.Key is string name && name.Length > 0 && variable.Value is string value)
				values[name] = value;
		}

		values[LiveMandateFile.PathVariable] = Mandate();
		values.Remove(LiveMandateConfirmation.PhraseVariable);
		values.Remove(KeysVariable);

		var block = new StringBuilder();

		foreach (var (name, value) in values)
			block.Append(name).Append('=').Append(value).Append('\0');

		return block.Append('\0').ToString();
	}

	/// <summary>A runner started outside <see cref="Process"/>, so that it could be given a console group.</summary>
	/// <param name="handle">Its process handle, which this owns.</param>
	/// <param name="id">Its process number, which is also its process group.</param>
	private sealed class ConsoleChild(IntPtr handle, int id) : IDisposable
	{
		/// <summary>Its process number, which is also the group an interrupt is sent to.</summary>
		public int Id => id;

		/// <summary>What it exited with.</summary>
		public int ExitCode
			=> GetExitCodeProcess(handle, out var code)
				? code
				: throw new InvalidOperationException(
					$"the runner's exit code could not be read: {Marshal.GetLastWin32Error()}");

		/// <summary>
		/// Waits for it to go.
		/// </summary>
		/// <param name="deadline">How long to wait.</param>
		/// <returns><see langword="true"/> when it exited inside the deadline.</returns>
		public bool Wait(TimeSpan deadline)
			=> WaitForSingleObject(handle, (int)deadline.TotalMilliseconds) == WaitObject0;

		/// <inheritdoc />
		public void Dispose()
		{
			// Ended rather than asked, because this is cleanup and whatever it is doing it is not holding
			// a position: nothing here ever gets past the phrase. Both calls fail harmlessly on a process
			// that has already gone, which is the ordinary case.
			TerminateProcess(handle, 1);
			CloseHandle(handle);
		}
	}

	/// <summary>How a process is to be started, as the operating system's own call takes it.</summary>
	/// <remarks>
	/// Two things about it matter here: its size, and that no standard-handle flag is set in it - which
	/// is what leaves the child taking its handles from the console rather than from this process. The
	/// rest are here because the layout is the contract, and are set to nothing explicitly because a
	/// field the call reads has to exist even when there is nothing to say in it.
	/// </remarks>
	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct StartupInfo
	{
		public int Size = 0;
		public IntPtr Reserved = IntPtr.Zero;
		public IntPtr Desktop = IntPtr.Zero;
		public IntPtr Title = IntPtr.Zero;
		public int X = 0;
		public int Y = 0;
		public int XSize = 0;
		public int YSize = 0;
		public int XCountChars = 0;
		public int YCountChars = 0;
		public int FillAttribute = 0;
		public int Flags = 0;
		public short ShowWindow = 0;
		public short Reserved2Size = 0;
		public IntPtr Reserved2 = IntPtr.Zero;
		public IntPtr StandardInput = IntPtr.Zero;
		public IntPtr StandardOutput = IntPtr.Zero;
		public IntPtr StandardError = IntPtr.Zero;

		/// <summary>Makes one with nothing in it, which is what every field above says.</summary>
		public StartupInfo()
		{
		}
	}

	/// <summary>What the operating system says about the process it started.</summary>
	/// <remarks>Filled in by the call rather than by anything here.</remarks>
	[StructLayout(LayoutKind.Sequential)]
	private struct ProcessInformation
	{
		public IntPtr Process = IntPtr.Zero;
		public IntPtr Thread = IntPtr.Zero;
		public int ProcessId = 0;
		public int ThreadId = 0;

		/// <summary>Makes an empty one, which the call then fills in.</summary>
		public ProcessInformation()
		{
		}
	}

	[DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CreateProcess(
		string applicationName,
		StringBuilder commandLine,
		IntPtr processAttributes,
		IntPtr threadAttributes,
		[MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
		int creationFlags,
		IntPtr environment,
		string currentDirectory,
		ref StartupInfo startupInfo,
		out ProcessInformation information);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GenerateConsoleCtrlEvent(int controlEvent, int processGroupId);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern int GetConsoleProcessList(int[] processList, int count);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern int WaitForSingleObject(IntPtr handle, int milliseconds);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetExitCodeProcess(IntPtr handle, out int exitCode);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool TerminateProcess(IntPtr handle, int exitCode);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CloseHandle(IntPtr handle);
}
