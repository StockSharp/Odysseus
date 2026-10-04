namespace Odysseus.Runner.Tests;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Odysseus.Runner;
using Odysseus.TestKit;

/// <summary>
/// The terminal a person starts a runner at, driven from a test without one.
/// </summary>
/// <remarks>
/// It holds the streams the process was born with rather than reaching for the console when it is asked
/// something, because a runner points its own console at a log file moments after starting - and the one
/// question it ever asks a person has to arrive somewhere a person is looking.
///
/// The other half of what is pinned here is the ending of a question. A console read cannot be taken
/// back once it has started, so every one of these is put from a thread of its own and given a deadline:
/// a test that waited for ever on an answer that never comes would hang the suite instead of failing it,
/// which is the same failure the runner itself must not have.
/// </remarks>
[TestClass]
public class ConsoleTerminalTests : OdysseusTestBase
{
	private const string Question = "  Type the phrase: ";

	/// <summary>The question goes to the writer, and the answer comes back exactly as it was typed.</summary>
	[TestMethod]
	public void TheAnswerComesBackExactlyAsItWasTyped()
	{
		var written = new StringWriter();

		var terminal = new ConsoleTerminal(
			written, new StringReader("  the phrase as typed  \r\nand a line nobody asked for"), interactive: true);

		var answer = Answered(terminal, CancellationToken);

		AreEqual(Question, written.ToString(), "the question did not reach the person being asked.");

		AreEqual("  the phrase as typed  ", answer,
			"the answer came back trimmed, and a trimmed answer is a different phrase than the one typed.");
	}

	/// <summary>
	/// Standard input that ended answers nothing rather than waiting for a line that is never coming.
	/// Nothing is confirmed by it, which is what the confirmation makes of a null.
	/// </summary>
	[TestMethod]
	public void AnInputThatEndedAnswersNothing()
		=> IsNull(Answered(
			new ConsoleTerminal(new StringWriter(), new StringReader(string.Empty), interactive: true),
			CancellationToken));

	/// <summary>
	/// Whether there is anybody here is decided once, at start-up, and reported as it was decided: a
	/// runner a host detached shares whatever console the host had and has nobody sitting at it.
	/// </summary>
	[TestMethod]
	public void ATerminalSaysWhetherThereIsAnybodyAtIt()
	{
		IsTrue(new ConsoleTerminal(new StringWriter(), new StringReader(string.Empty), interactive: true).IsInteractive);

		IsFalse(new ConsoleTerminal(new StringWriter(), new StringReader(string.Empty), interactive: false).IsInteractive);
	}

	/// <summary>
	/// There is somebody here only when nothing says there is not. Each half of that answers a different
	/// mistake: taking a pipe for a person leaves a runner nobody can see waiting for ever on a line
	/// nobody will type, and taking a detached runner's inherited terminal for its own does the same on
	/// a console whose person is not sitting there for this process.
	/// </summary>
	[TestMethod]
	public void ThereIsSomebodyHereOnlyWhenNothingSaysThereIsNot()
	{
		IsTrue(ConsoleTerminal.IsAttended(detached: false, inputRedirected: false),
			"a person at a terminal was never going to be asked anything.");

		IsFalse(ConsoleTerminal.IsAttended(detached: true, inputRedirected: false),
			"a runner a host detached would have stopped to ask a person who is not sitting at it.");

		IsFalse(ConsoleTerminal.IsAttended(detached: false, inputRedirected: true),
			"a redirected standard input was taken for a person.");

		IsFalse(ConsoleTerminal.IsAttended(detached: true, inputRedirected: true),
			"a detached runner reading from a pipe found somebody to ask.");
	}

	/// <summary>
	/// What this process decides about its own console is what its console actually is. A runner a host
	/// detached is never attended whatever it inherited; one nobody detached agrees with its own
	/// standard input, and this test host's input is whatever the run was started with.
	/// </summary>
	[TestMethod]
	public void TheTerminalThisProcessWasStartedAtIsTheOneItReports()
	{
		IsFalse(ConsoleTerminal.Attach(detached: true).IsInteractive,
			"a runner a host detached decided there was somebody at its console after all.");

		AreEqual(!Console.IsInputRedirected, ConsoleTerminal.Attach(detached: false).IsInteractive,
			"a runner nobody detached disagreed with its own standard input about whether there is a terminal here.");
	}

	/// <summary>
	/// A question that is interrupted while it stands comes back with nothing, and comes back at once.
	/// This is Ctrl+C at the phrase of a live mandate: the runner is being told nobody is going to answer,
	/// and a runner that went on waiting there would hold a project and a strategy that never started,
	/// answering nothing, until somebody killed the process.
	/// </summary>
	[TestMethod]
	public void AQuestionThatWasInterruptedComesBackWithNothing()
	{
		var reader = new SilentReader();
		var terminal = new ConsoleTerminal(new StringWriter(), reader, interactive: true);

		using var interrupted = new CancellationTokenSource();

		try
		{
			var asking = Task.Run(() => terminal.Ask(Question, interrupted.Token), CancellationToken);

			IsTrue(reader.Reading.Wait(TimeSpan.FromSeconds(20)),
				"the question was never put to anybody, so there was nothing to interrupt.");

			interrupted.Cancel();

			IsTrue(asking.Wait(TimeSpan.FromSeconds(20)),
				"the question was still standing after the interrupt; the runner asking it would never exit.");

			IsNull(asking.Result, "an interrupted question came back as though somebody had answered it.");
		}
		finally
		{
			// Lets the read that is still standing finish, so the thread it is on goes away with the test
			// rather than with the process.
			reader.LetGo();
		}
	}

	/// <summary>
	/// A question nobody is waiting for the answer to any more is not put at all. It is the interrupt
	/// that arrives a moment before the question rather than a moment after, and printing "type the
	/// phrase" at somebody who has already stopped this runner would be asking for an answer that could
	/// no longer be used.
	/// </summary>
	[TestMethod]
	public void AQuestionAlreadyGivenUpOnIsNotPutAtAll()
	{
		var written = new StringWriter();
		var reader = new SilentReader();

		using var interrupted = new CancellationTokenSource();

		interrupted.Cancel();

		var terminal = new ConsoleTerminal(written, reader, interactive: true);

		try
		{
			IsNull(Answered(terminal, interrupted.Token),
				"a question nobody was waiting on came back with an answer.");

			AreEqual(string.Empty, written.ToString(),
				"the question was put to somebody after the runner had stopped waiting for their answer.");

			IsFalse(reader.Reading.IsCompleted, "standard input was read after the question had been given up on.");
		}
		finally
		{
			reader.LetGo();
		}
	}

	/// <summary>
	/// Standard input that failed answers nothing, rather than taking the process down. An interrupt
	/// aborts the console read that was standing, and the platform reports that as a read that failed
	/// rather than as an input that ended - and a runner that crashed there would leave a stack trace
	/// where a person was expecting to be told that nothing had started.
	/// </summary>
	[TestMethod]
	public void AnInputThatFailedAnswersNothingRatherThanEndingTheProcess()
		=> IsNull(
			Answered(new ConsoleTerminal(new StringWriter(), new AbortedReader(), interactive: true), CancellationToken),
			"a standard input that failed was reported as an answer.");

	/// <summary>
	/// Puts the question from a thread of its own and gives up on it after twenty seconds.
	/// </summary>
	/// <param name="terminal">The terminal to ask.</param>
	/// <param name="interrupted">What ends the wait, as the runner would pass it.</param>
	/// <returns>What came back.</returns>
	private string Answered(ConsoleTerminal terminal, CancellationToken interrupted)
	{
		var asking = Task.Run(() => terminal.Ask(Question, interrupted), CancellationToken);

		IsTrue(asking.Wait(TimeSpan.FromSeconds(20)),
			"the question never came back; whatever asked it would have waited for ever.");

		return asking.Result;
	}

	/// <summary>Standard input with nobody typing into it, which is what a person who walked away is.</summary>
	private sealed class SilentReader : TextReader
	{
		private readonly TaskCompletionSource _reading = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly TaskCompletionSource _typed = new(TaskCreationOptions.RunContinuationsAsynchronously);

		/// <summary>Completes once somebody is being waited on.</summary>
		public Task Reading => _reading.Task;

		/// <summary>Ends the wait, so the thread it is on is not left standing after the test.</summary>
		public void LetGo() => _typed.TrySetResult();

		/// <inheritdoc />
		public override string ReadLine()
		{
			_reading.TrySetResult();
			_typed.Task.GetAwaiter().GetResult();

			return null;
		}
	}

	/// <summary>A console read that the operating system took away, which is what an interrupt does to one.</summary>
	private sealed class AbortedReader : TextReader
	{
		/// <inheritdoc />
		public override string ReadLine()
			=> throw new IOException("The I/O operation has been aborted because of an application request.");
	}
}
