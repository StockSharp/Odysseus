namespace StockSharp.Odysseus.Engine;

using System.Runtime.InteropServices;

/// <summary>
/// How the host runs candidates somewhere other than in itself.
/// </summary>
/// <param name="WorkerPath">Path of the worker executable, or of the assembly to run with the runtime host.</param>
/// <param name="Arguments">Arguments the worker is started with, which a deployment may need and which are normally none.</param>
/// <param name="HandshakeDeadline">How long a freshly started worker is given to greet.</param>
/// <param name="RunDeadline">How long one backtest may take before the worker is stopped.</param>
/// <param name="SearchDeadline">How long one parameter search may take, which is several backtests.</param>
/// <param name="MemoryLimitBytes">Working set the worker may reach before it is stopped.</param>
/// <param name="RunsBeforeRecycle">Requests one worker serves before it is replaced.</param>
/// <param name="BatchSize">Settings a parameter search evaluates at once.</param>
/// <remarks>
/// None of this belongs on a request. A limit is a property of the machine the server runs on, and a
/// request is the experiment: if a timeout lived next to the parameters, two identical measurements on
/// two machines would stop being the same measurement.
///
/// The batch size is pinned for the same reason. Left alone the platform's optimizer runs twice the
/// processor count of backtests at once, so the memory a search needs - and, if the completion order can
/// reach the search at all, what it finds - would depend on how many cores the machine happens to have.
/// </remarks>
public sealed record WorkerOptions(
	string WorkerPath,
	IReadOnlyList<string> Arguments,
	TimeSpan HandshakeDeadline,
	TimeSpan RunDeadline,
	TimeSpan SearchDeadline,
	long MemoryLimitBytes,
	int RunsBeforeRecycle,
	int BatchSize)
{
	/// <summary>Environment variable naming the worker, for a deployment that does not put it beside the host.</summary>
	public const string PathVariable = "ODYSSEUS_WORKER";

	/// <summary>Name of the worker, without the extension the platform gives it.</summary>
	public const string WorkerName = "Odysseus.Worker";

	/// <summary>
	/// Folder under the host's own the worker is shipped in.
	/// </summary>
	/// <remarks>
	/// A folder of its own rather than the host's, because the two are separate applications whose
	/// dependency closures differ: shared one folder, every assembly they both carry is resolved to
	/// whichever copy the build wrote last, and the one whose manifest asks for the other version fails
	/// to load it.
	/// </remarks>
	public const string WorkerFolder = "worker";

	private static string Extension
		=> RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : string.Empty;

	/// <summary>
	/// The options a server starts with, with the worker looked for beside the host.
	/// </summary>
	/// <returns>The options.</returns>
	public static WorkerOptions Read()
		=> new(
			Locate(),
			Arguments: [],
			HandshakeDeadline: TimeSpan.FromSeconds(30),
			RunDeadline: TimeSpan.FromMinutes(5),
			SearchDeadline: TimeSpan.FromMinutes(20),
			MemoryLimitBytes: 2L * 1024 * 1024 * 1024,
			RunsBeforeRecycle: 50,
			BatchSize: 4);

	/// <summary>
	/// Where the worker is, as named by the environment or as shipped beside the host.
	/// </summary>
	/// <returns>The path, which need not exist yet.</returns>
	public static string Locate()
	{
		var named = Environment.GetEnvironmentVariable(PathVariable);

		if (!string.IsNullOrWhiteSpace(named))
			return Path.GetFullPath(named);

		var folder = Path.Combine(AppContext.BaseDirectory, WorkerFolder);
		var beside = Path.Combine(folder, WorkerName + Extension);

		if (File.Exists(beside))
			return beside;

		// A framework-dependent build without an apphost, which is what a container image often carries.
		return Path.Combine(folder, WorkerName + ".dll");
	}
}
