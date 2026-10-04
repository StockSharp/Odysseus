namespace Odysseus.Engine;

using System.Runtime.InteropServices;

/// <summary>
/// Where the runner is and how long it is given to answer.
/// </summary>
/// <param name="RunnerPath">Path of the runner executable, or of the assembly to run with the runtime host.</param>
/// <param name="Arguments">Arguments every runner is started with, before its home, which are normally none.</param>
/// <param name="HandshakeDeadline">How long a freshly started runner is given to write its record and greet.</param>
/// <param name="CallDeadline">How long a runner is given to answer a read.</param>
/// <param name="StopDeadline">How long a runner is given to wind a strategy down and say what it came to.</param>
/// <param name="Heartbeat">How often a runner writes what it is holding into its journal.</param>
/// <remarks>
/// None of this belongs on a request, for the reason <see cref="WorkerOptions"/> gives: a limit is a
/// property of the machine, and a request is the experiment.
///
/// The deadlines are not the worker's. A backtest either finishes or is killed, and killing it loses
/// nothing; a runner holds a position, so the deadline on a read is short - a report is worth having
/// quickly and is not worth waiting on - while the deadline on a stop is long, because a stop that gives
/// up early leaves a strategy winding down with nobody recording what it came to.
/// </remarks>
public sealed record RunnerOptions(
	string RunnerPath,
	IReadOnlyList<string> Arguments,
	TimeSpan HandshakeDeadline,
	TimeSpan CallDeadline,
	TimeSpan StopDeadline,
	TimeSpan Heartbeat)
{
	/// <summary>Environment variable naming the runner, for a deployment that does not put it beside the host.</summary>
	public const string PathVariable = "ODYSSEUS_RUNNER";

	/// <summary>Name of the runner, without the extension the platform gives it.</summary>
	public const string RunnerName = "Odysseus.Runner";

	/// <summary>
	/// Folder under the host's own the runner is shipped in.
	/// </summary>
	/// <remarks>
	/// A folder of its own rather than the host's, for the reason <see cref="WorkerOptions.WorkerFolder"/>
	/// gives - and the reason applies harder here, because the runner's closure reaches NuGet through the
	/// package layer and the worker's deliberately does not.
	/// </remarks>
	public const string RunnerFolder = "runner";

	private static string Extension
		=> RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : string.Empty;

	/// <summary>The options a host starts with, with the runner looked for beside it.</summary>
	/// <returns>The options.</returns>
	public static RunnerOptions Read()
		=> new(
			Locate(),
			Arguments: [],
			HandshakeDeadline: TimeSpan.FromSeconds(60),
			CallDeadline: TimeSpan.FromSeconds(15),
			StopDeadline: TimeSpan.FromMinutes(2),
			Heartbeat: TimeSpan.FromSeconds(15));

	/// <summary>
	/// Where the runner is, as named by the environment or as shipped beside the host.
	/// </summary>
	/// <returns>The path, which need not exist yet.</returns>
	public static string Locate()
	{
		var named = Environment.GetEnvironmentVariable(PathVariable);

		if (!string.IsNullOrWhiteSpace(named))
			return Path.GetFullPath(named);

		var folder = Path.Combine(AppContext.BaseDirectory, RunnerFolder);
		var beside = Path.Combine(folder, RunnerName + Extension);

		if (File.Exists(beside))
			return beside;

		// A framework-dependent build without an apphost, which is what a container image often carries.
		return Path.Combine(folder, RunnerName + ".dll");
	}
}
