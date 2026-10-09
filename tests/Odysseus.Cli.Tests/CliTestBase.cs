namespace StockSharp.Odysseus.Cli.Tests;

using System.Threading;

using StockSharp.Odysseus.Application;
using StockSharp.Odysseus.Cli.Console;
using StockSharp.Odysseus.TestKit;

using Con = System.Console;

/// <summary>
/// What one command printed and what it exited with.
/// </summary>
/// <param name="Code">The process exit code, which is what a person scripts against.</param>
/// <param name="Out">Everything written to standard output.</param>
/// <param name="Error">Everything written to standard error, which is where a refusal goes.</param>
public sealed record Answer(int Code, string Out, string Error)
{
	/// <summary>Both streams, for an assertion that does not care which one carried the words.</summary>
	public string Everything => Out + Error;

	/// <summary>
	/// Whether some text was printed, on either stream.
	/// </summary>
	/// <param name="text">What to look for.</param>
	/// <returns>Whether it is there.</returns>
	public bool Says(string text) => Everything.Contains(text, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A person at a terminal, or the absence of one, arranged by a test.
/// </summary>
/// <remarks>
/// The command line asks a human exactly one question - the identifier of a runner it is about to kill -
/// and the answer decides whether a process holding a position is ended. A test cannot type, so the
/// terminal is a port here for the same reason it is one in the runner.
/// </remarks>
public sealed class ScriptedTerminal : IOperatorTerminal
{
	private readonly Queue<string> _answers = new();

	/// <summary>Whether there is anybody here to be asked.</summary>
	public bool IsInteractive { get; set; } = true;

	/// <summary>Every question that was put, in the order it was put.</summary>
	public List<string> Questions { get; } = [];

	/// <summary>
	/// What standard output already held when each question was put.
	/// </summary>
	/// <remarks>
	/// The order matters here rather than merely the content: a command that asks whether to end a
	/// process holding a position, and prints what that position is afterwards, asked the question of
	/// somebody who could not see the answer.
	/// </remarks>
	public List<string> SeenBeforeAsking { get; } = [];

	/// <summary>
	/// Reads what standard output holds at the moment a question is put.
	/// </summary>
	/// <remarks>
	/// The run arranges this. It cannot be read off the console, because setting standard output keeps a
	/// synchronised wrapper around the writer it was handed rather than the writer itself, and asking that
	/// wrapper for its text yields the name of its type instead of a word of what was printed.
	/// </remarks>
	public Func<string> StandardOutput { get; set; } = () => string.Empty;

	/// <summary>
	/// Arranges what the person types next.
	/// </summary>
	/// <param name="answer">The line they type.</param>
	/// <returns>The terminal, so arrangements read in one line.</returns>
	public ScriptedTerminal Types(string answer)
	{
		_answers.Enqueue(answer);

		return this;
	}

	/// <inheritdoc />
	public string Ask(string question, CancellationToken cancellationToken)
	{
		Questions.Add(question);
		SeenBeforeAsking.Add(StandardOutput());

		if (cancellationToken.IsCancellationRequested)
			return null;

		return _answers.Count > 0 ? _answers.Dequeue() : null;
	}
}

/// <summary>
/// A workspace of this test's own, and one command run against it.
/// </summary>
/// <remarks>
/// The commands are driven through <see cref="Program.Run"/> rather than by starting the executable, so
/// what a test observes is the exit code the process would return and the text it would print, without a
/// process start per assertion. Everything a command touches lives under a temporary root made for the
/// test and removed after it.
/// </remarks>
public abstract class CliTestBase : OdysseusTestBase
{
	private const string ConnectorVariable = "ODYSSEUS_BROKER_CONNECTOR";
	private const string KeysVariable = "ODYSSEUS_BROKER_KEYS";

	private string _root;
	private Workspace _workspace;

	/// <summary>The hypothesis the README walks through, as it is published in the repository.</summary>
	protected static string SampleHypothesis => Path.Combine(RepositoryRoot, "samples", "hypothesis.json");

	/// <summary>The projects root this test works in.</summary>
	protected string Root => _root;

	/// <summary>The workspace the commands are run against.</summary>
	protected Workspace Workspace => _workspace;

	/// <summary>The person the commands may ask something.</summary>
	protected ScriptedTerminal Terminal { get; private set; }

	/// <summary>Opens a projects root of this test's own.</summary>
	[TestInitialize]
	public void OpenWorkspace()
	{
		_root = Path.Combine(Path.GetTempPath(), "odysseus-cli", Guid.NewGuid().ToString("n"));

		Terminal = new();
		_workspace = OpenWithoutABroker(_root);

		// Colour is a property of the whole process and the tests beside these turn it on. What is asserted
		// here is what was said, so it is turned off where the words are read.
		Ui.UseColour(false);
	}

	/// <summary>Closes it and removes what it left behind.</summary>
	[TestCleanup]
	public void CloseWorkspace()
	{
		_workspace?.Dispose();
		_workspace = null;

		if (!Directory.Exists(_root))
			return;

		try
		{
			Directory.Delete(_root, recursive: true);
		}
		catch (IOException)
		{
			// A child process is still releasing its files. The directory is under the temporary folder and its
			// name is a fresh identifier, so nothing later depends on it having gone.
		}
	}

	/// <summary>
	/// Runs one command and collects what it printed.
	/// </summary>
	/// <param name="args">The command and its arguments, as they would be typed.</param>
	/// <returns>The exit code and the two streams.</returns>
	protected async Task<Answer> RunAsync(params string[] args)
	{
		var outBefore = Con.Out;
		var errorBefore = Con.Error;

		using var output = new StringWriter();
		using var error = new StringWriter();

		int code;

		try
		{
			Con.SetOut(output);
			Con.SetError(error);

			Terminal.StandardOutput = output.ToString;

			code = await Program.Run(args, _workspace, Terminal);
		}
		finally
		{
			Con.SetOut(outBefore);
			Con.SetError(errorBefore);
		}

		return new(code, output.ToString(), error.ToString());
	}

	/// <summary>
	/// Runs the walkthrough as far as a project with data in it.
	/// </summary>
	/// <returns>Nothing.</returns>
	/// <remarks>
	/// The generated bars, because a hypothesis is refused against a project with no data to measure it
	/// on. Nothing measured on them says anything about any market, which is what makes them the right
	/// data for a test about what the commands do.
	/// </remarks>
	protected async Task StartAProjectWithDataAsync()
	{
		AreEqual(0, (await RunAsync("new", "a", "study")).Code, "the project was not started.");
		AreEqual(0, (await RunAsync("demo")).Code, "the demo data was not imported.");
	}

	/// <summary>
	/// Opens a workspace over a root, with no broker whatever this machine has configured.
	/// </summary>
	/// <param name="root">Directory the projects live in.</param>
	/// <returns>The workspace.</returns>
	/// <remarks>
	/// A developer's own connector would have these tests download a package from a gallery, and would
	/// make them pass on one machine and fail on another. What the commands do without a broker is also
	/// what several of them are about, so the absence is arranged rather than assumed.
	/// </remarks>
	private static Workspace OpenWithoutABroker(string root)
	{
		var connector = Environment.GetEnvironmentVariable(ConnectorVariable);
		var keys = Environment.GetEnvironmentVariable(KeysVariable);

		try
		{
			Environment.SetEnvironmentVariable(ConnectorVariable, null);
			Environment.SetEnvironmentVariable(KeysVariable, null);

			return Workspace.Open(root, Path.Combine(root, "market-data"));
		}
		finally
		{
			Environment.SetEnvironmentVariable(ConnectorVariable, connector);
			Environment.SetEnvironmentVariable(KeysVariable, keys);
		}
	}
}
