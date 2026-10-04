namespace Odysseus.Platform;

using System.IO;

/// <summary>
/// The assemblies a generated strategy is written against.
/// </summary>
/// <remarks>
/// Named here rather than in the compiler, so that the compiler stays a compiler and the trading engine
/// is known in one place.
///
/// The directory is passed in rather than found from a loaded type. Two things follow from that. The
/// set no longer depends on what this process happens to have loaded, so it is the same whichever host
/// asks; and a caller can name the folder a candidate will actually run in, which is what makes a
/// strategy compiled here and run somewhere else compile against the same engine that will run it.
///
/// The filter is by name because a broker connector is published under the same prefix as the platform
/// itself. Nothing keeps a connector out of this list except its absence from the folder, which is why
/// no project of this product may be compiled against one.
/// </remarks>
public static class EngineAssemblies
{
	/// <summary>
	/// Paths of the assemblies to compile a strategy against.
	/// </summary>
	/// <param name="directory">Folder holding the engine, normally the host's own.</param>
	/// <returns>The paths, in a stable order.</returns>
	/// <exception cref="DirectoryNotFoundException">The folder does not exist.</exception>
	public static IReadOnlyList<string> Paths(string directory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(directory);

		if (!Directory.Exists(directory))
			throw new DirectoryNotFoundException($"There is no engine to compile against at '{directory}'.");

		return
		[
			.. Directory
				.EnumerateFiles(directory, "*.dll")
				.Where(f => IsEngine(Path.GetFileNameWithoutExtension(f)))
				.OrderBy(f => f, StringComparer.Ordinal),
		];
	}

	private static bool IsEngine(string name)
		=> name.StartsWith("StockSharp.", StringComparison.Ordinal) ||
			name.StartsWith("Ecng.", StringComparison.Ordinal);
}
