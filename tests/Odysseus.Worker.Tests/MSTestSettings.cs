[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace StockSharp.Odysseus.Worker.Tests;

/// <summary>
/// What the assembly leaves behind.
/// </summary>
/// <remarks>
/// A run reads its bars out of a folder, so the tests here write folders. They go under one root and
/// that root is removed when the assembly is done, so a test run does not fill the temporary directory
/// with market data nobody deletes.
/// </remarks>
[TestClass]
public static class RunLifetime
{
	/// <summary>Removes the bars the tests froze.</summary>
	[AssemblyCleanup]
	public static void Cleanup()
		=> StoredBars.Clear();
}
