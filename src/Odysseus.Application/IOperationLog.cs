namespace Odysseus.Application;

using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Remembers what an operation key already produced.
/// </summary>
/// <remarks>
/// The caller is an autonomous agent over a connection that can drop mid-call. Without this, a retry
/// after a lost response creates a second project, a second candidate or a second run, and the budget
/// pays for work nobody asked for twice.
/// </remarks>
public interface IOperationLog
{
	/// <summary>
	/// Looks up the result an operation key already produced.
	/// </summary>
	/// <param name="scope">
	/// What the key belongs to — normally the project. Keys are chosen by the caller, and two projects
	/// picking the same obvious word would otherwise be handed each other's results.
	/// </param>
	/// <param name="key">Key the caller supplied.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The recorded result, or <see langword="null"/> when the key is new.</returns>
	ValueTask<string> TryGetAsync(string scope, string key, CancellationToken cancellationToken);

	/// <summary>
	/// Records the result of an operation key.
	/// </summary>
	/// <param name="scope">
	/// What the key belongs to — normally the project. Keys are chosen by the caller, and two projects
	/// picking the same obvious word would otherwise be handed each other's results.
	/// </param>
	/// <param name="key">Key the caller supplied.</param>
	/// <param name="result">Result to return if the key is used again.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The result now associated with the key, which is the earlier one if the key was taken.</returns>
	ValueTask<string> RecordAsync(string scope, string key, string result, CancellationToken cancellationToken);
}
