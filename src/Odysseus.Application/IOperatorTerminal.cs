namespace StockSharp.Odysseus.Application;

/// <summary>
/// The person who started this process, as far as it can reach them.
/// </summary>
/// <remarks>
/// A port for one question: the phrase out of a live mandate, asked of whoever is at the terminal. It
/// exists so that the asking can be driven from a test without a console, and so that a process with
/// nobody at it answers that there is nobody rather than blocking for ever on a handle no one is typing
/// into.
///
/// Nothing else in the product asks a human anything. This is the only channel by which an answer can
/// arrive that the mandate file could not have supplied by itself, which is the whole of its point.
/// </remarks>
public interface IOperatorTerminal
{
	/// <summary>Whether there is a person at a terminal here to be asked.</summary>
	bool IsInteractive { get; }

	/// <summary>
	/// Puts a question and reads one line back.
	/// </summary>
	/// <param name="question">What to ask, written as it is to be seen.</param>
	/// <param name="cancellationToken">
	/// What ends the wait when the answer is no longer wanted: an interrupt at the terminal, or this
	/// process being stopped. A question is the one thing here that can outlast the reason for asking
	/// it - the person walks away, or presses the interrupt instead of typing - so whoever asks has to
	/// be able to stop waiting, or the process waits for a line nobody is going to type.
	/// </param>
	/// <returns>
	/// The line that was typed, exactly as typed, or <see langword="null"/> when nothing was typed: the
	/// input ended, or the wait was given up on. Nothing is trimmed off it here: what the person typed
	/// is what gets compared.
	/// </returns>
	string Ask(string question, CancellationToken cancellationToken);
}
