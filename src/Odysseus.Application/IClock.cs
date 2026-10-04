namespace Odysseus.Application;

using System;

/// <summary>
/// Source of the current moment.
/// </summary>
/// <remarks>
/// Reading the clock directly would make every timestamp in a test depend on when the test ran, and
/// research results are supposed to be reproducible down to the manifest.
/// </remarks>
public interface IClock
{
	/// <summary>The current moment, in UTC.</summary>
	DateTime UtcNow { get; }
}
