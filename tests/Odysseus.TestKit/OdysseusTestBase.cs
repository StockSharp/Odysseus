namespace StockSharp.Odysseus.TestKit;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Base class for every Odysseus test class. Provides the shared assertion vocabulary and a
/// cancellation token bound to the lifetime of a single test, so no test can hang the run.
/// </summary>
public abstract class OdysseusTestBase
{
	private static string _repositoryRoot;
	private CancellationTokenSource _cts;

	/// <summary>
	/// Directory holding <c>Odysseus.slnx</c>, resolved by walking up from the test output directory.
	/// </summary>
	protected static string RepositoryRoot => _repositoryRoot ??= FindRepositoryRoot();

	/// <summary>
	/// Time a single test is allowed to run before its <see cref="CancellationToken"/> is cancelled.
	/// </summary>
	protected virtual TimeSpan Timeout => TimeSpan.FromMinutes(2);

	/// <summary>
	/// Token every asynchronous call inside a test must observe. Never pass <c>CancellationToken.None</c>.
	/// </summary>
	protected CancellationToken CancellationToken => _cts.Token;

	/// <summary>
	/// Creates the per-test cancellation source.
	/// </summary>
	[TestInitialize]
	public void InitializeTest()
	{
		_cts = new CancellationTokenSource(Timeout);
	}

	/// <summary>
	/// Releases the per-test cancellation source.
	/// </summary>
	[TestCleanup]
	public void CleanupTest()
	{
		_cts?.Dispose();
		_cts = null;
	}

	/// <summary>Asserts that two values are equal.</summary>
	protected static void AreEqual<T>(T expected, T actual, string message = null)
		=> Assert.AreEqual(expected, actual, message);

	/// <summary>Asserts that two values are not equal.</summary>
	protected static void AreNotEqual<T>(T notExpected, T actual, string message = null)
		=> Assert.AreNotEqual(notExpected, actual, message);

	/// <summary>Asserts that two references are the same object.</summary>
	protected static void AreSame(object expected, object actual, string message = null)
		=> Assert.AreSame(expected, actual, message);

	/// <summary>Asserts that a condition holds.</summary>
	protected static void IsTrue(bool condition, string message = null)
		=> Assert.IsTrue(condition, message);

	/// <summary>Asserts that a condition does not hold.</summary>
	protected static void IsFalse(bool condition, string message = null)
		=> Assert.IsFalse(condition, message);

	/// <summary>Asserts that a value is not null.</summary>
	protected static void IsNotNull(object value, string message = null)
		=> Assert.IsNotNull(value, message);

	/// <summary>Asserts that a value is null.</summary>
	protected static void IsNull(object value, string message = null)
		=> Assert.IsNull(value, message);

	/// <summary>Fails the test unconditionally.</summary>
	protected static void Fail(string message)
		=> Assert.Fail(message);

	/// <summary>Asserts that an action throws the expected exception type.</summary>
	protected static TException Throws<TException>(Action action, string message = null)
		where TException : Exception
		=> message is null
			? Assert.ThrowsExactly<TException>(action)
			: Assert.ThrowsExactly<TException>(action, message);

	/// <summary>
	/// Asserts that an asynchronous operation throws the expected exception type or a type derived from
	/// it. Cancellation in particular surfaces as different derived types depending on where inside the
	/// operation it was noticed, and a test has no business pinning that down.
	/// </summary>
	protected static Task<TException> ThrowsAsync<TException>(Func<Task> action, string message = null)
		where TException : Exception
		=> message is null
			? Assert.ThrowsAsync<TException>(action)
			: Assert.ThrowsAsync<TException>(action, message);

	private static string FindRepositoryRoot()
	{
		var dir = new DirectoryInfo(AppContext.BaseDirectory);

		while (dir is not null)
		{
			if (File.Exists(Path.Combine(dir.FullName, "Odysseus.slnx")))
				return dir.FullName;

			dir = dir.Parent;
		}

		throw new InvalidOperationException("Odysseus.slnx was not found above the test output directory.");
	}
}
