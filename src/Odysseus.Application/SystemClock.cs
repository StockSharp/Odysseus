namespace Odysseus.Application;

/// <summary>
/// The clock of the machine the server runs on.
/// </summary>
public sealed class SystemClock : IClock
{
	/// <inheritdoc />
	public DateTime UtcNow => DateTime.UtcNow;
}
