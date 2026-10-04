namespace Odysseus.Runner;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Odysseus.Application;

/// <summary>
/// The terminal this process was started at, when it was started at one.
/// </summary>
/// <remarks>
/// It holds the streams the process was born with rather than reaching for <see cref="Console"/> when it
/// is asked something. A runner points its own console at a log file within a few lines of starting - it
/// may have inherited standard output from a parent that speaks a protocol over it, and one stray line
/// there corrupts that session for good - so by the time the phrase is wanted, <c>Console.Out</c> is a
/// file. A question written into a log file is a question nobody answers.
///
/// Whether there is anybody to ask is decided once, at start-up, by <see cref="IsAttended"/>. Standard
/// input has to be a terminal rather than a pipe, and this process has to be one a person started rather
/// than one a host detached: a detached runner shares whatever console the host had, and a process
/// sitting on that handle waiting for somebody to type would be waiting for a person who is not there
/// and does not know it is waiting.
/// </remarks>
/// <param name="writer">Where the question is written, captured before the console was redirected.</param>
/// <param name="reader">Where the answer is read from.</param>
/// <param name="interactive">Whether there is a person here at all.</param>
internal sealed class ConsoleTerminal(TextWriter writer, TextReader reader, bool interactive) : IOperatorTerminal
{
	/// <inheritdoc />
	public bool IsInteractive => interactive;

	/// <summary>
	/// Whether this process has somebody at it, out of the two things that decide it.
	/// </summary>
	/// <param name="detached">Whether a host started this rather than a person at a terminal.</param>
	/// <param name="inputRedirected">Whether standard input is something other than a terminal.</param>
	/// <returns><see langword="true"/> when there is a person here who could be asked something.</returns>
	/// <remarks>
	/// Both halves are needed, and getting either one wrong fails in a different direction. Deciding
	/// there is nobody when there is means a person at a terminal is never asked, and a live mandate
	/// they meant to confirm is refused; deciding there is somebody when there is not means a runner no
	/// human can see stops on a question and waits for ever, holding a project and answering nothing,
	/// which reads from outside exactly like a runner that is working.
	///
	/// A redirected standard input is never a person: a pipe, a file and a closed handle all answer
	/// nothing, and one that answers something is a script that was never asked to confirm anything.
	/// Detachment is asked separately because a detached runner's standard input can be a perfectly
	/// good terminal - the host's own - and the person at it is not sitting there for this process.
	/// </remarks>
	public static bool IsAttended(bool detached, bool inputRedirected)
		=> !detached && !inputRedirected;

	/// <summary>
	/// Takes the streams this process was born with.
	/// </summary>
	/// <param name="detached">Whether a host started this rather than a person at a terminal.</param>
	/// <returns>The terminal.</returns>
	/// <remarks>
	/// Called before the console is pointed at the runner's log, because what it captures is the console
	/// as the process was started with it. Standard input is not opened at all when there is nobody
	/// there, so nothing later reads from a handle this process has already decided nobody is typing
	/// into.
	/// </remarks>
	public static ConsoleTerminal Attach(bool detached)
	{
		var attended = IsAttended(detached, Console.IsInputRedirected);

		return new(Console.Out, attended ? Console.In : TextReader.Null, attended);
	}

	/// <inheritdoc />
	public string Ask(string question, CancellationToken cancellationToken)
	{
		if (cancellationToken.IsCancellationRequested)
			return null;

		try
		{
			writer.Write(question);
			writer.Flush();
		}
		catch (Exception error) when (error is IOException or ObjectDisposedException)
		{
			// A question that could not be put is one nobody can answer. Reported as such rather than
			// thrown, because a console that went away is not a defect in the runner and the caller's
			// answer to an unanswered question is already the right one: it refuses.
			return null;
		}

		var typed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

		// The read runs on a thread of its own, and a background one, because a read that has started on
		// a console cannot be taken back. An interrupt aborts it on some platforms and leaves it standing
		// on others, so the thread that made it is a thread that may never come back; as a background one
		// it cannot hold the process open, and nothing waits for it once the answer has been given up on.
		new Thread(() => Read(typed))
		{
			IsBackground = true,
			Name = "operator terminal",
		}.Start();

		try
		{
			return typed.Task.WaitAsync(cancellationToken).GetAwaiter().GetResult();
		}
		catch (OperationCanceledException)
		{
			// Interrupted at the terminal, or the process is stopping. Nobody typed anything, and that is
			// all this reports: what an unanswered question means belongs to whoever asked it.
			return null;
		}
	}

	/// <summary>
	/// Reads one line into the answer, treating an input that failed as one that ended.
	/// </summary>
	/// <param name="typed">Where the line is put when there is one.</param>
	private void Read(TaskCompletionSource<string> typed)
	{
		try
		{
			typed.TrySetResult(reader.ReadLine());
		}
		catch (Exception)
		{
			// An interrupt aborts a console read that was standing, and the platform reports that as a
			// failed read rather than as an end of input. Whatever it was, nobody typed anything - and
			// this thread has no caller to tell, so letting it off the end here would end the process in
			// the middle of deciding a refusal.
			typed.TrySetResult(null);
		}
	}
}
