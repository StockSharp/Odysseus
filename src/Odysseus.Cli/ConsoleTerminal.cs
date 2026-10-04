namespace Odysseus.Cli;

using System.Threading;

using Odysseus.Application;

using Con = System.Console;

/// <summary>
/// The terminal this command was typed at, when it was typed at one.
/// </summary>
/// <remarks>
/// The same port the runner reaches a person through, for the same reason: whether there is anybody to
/// ask is a property of the process rather than something a command guesses, and a question put to a
/// redirected standard input is a question nobody answers.
/// </remarks>
internal sealed class ConsoleTerminal : IOperatorTerminal
{
	private ConsoleTerminal()
	{
	}

	/// <summary>The console this process was started with.</summary>
	public static ConsoleTerminal Instance { get; } = new();

	/// <inheritdoc />
	public bool IsInteractive => !Con.IsInputRedirected;

	/// <inheritdoc />
	/// <remarks>
	/// The read is not abandoned when the token is cancelled after it has started, and here it does not
	/// need to be: this process is the command a person is typing at, it holds no position and nothing
	/// is waiting on it, so an interrupt ends it outright. The runner is the one that cannot afford to
	/// be ended outright, and its terminal is the one that abandons the read.
	/// </remarks>
	public string Ask(string question, CancellationToken cancellationToken)
	{
		if (cancellationToken.IsCancellationRequested)
			return null;

		Con.Write(question);

		return Con.ReadLine();
	}
}
