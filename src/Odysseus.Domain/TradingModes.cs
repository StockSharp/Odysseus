namespace Odysseus.Domain;

/// <summary>
/// Whether a process trades on a paper account or on a real one.
/// </summary>
/// <remarks>
/// A property of a process, fixed at its birth from configuration an operator wrote, and never a value
/// that crosses a protocol or arrives as a tool argument. Nothing reachable from an agent can move a
/// process from one of these to the other.
///
/// It lives in the domain rather than beside the mandate that produces it, because a deployment records
/// which of the two it was and the record of a deployment is domain vocabulary. The permission itself -
/// who authorised the real account, for which instruments, until when - is a use case's concern and
/// stays there.
/// </remarks>
public enum TradingModes
{
	/// <summary>A demo account. Every order is make-believe and the guard proves it before connecting.</summary>
	Paper,

	/// <summary>A real account. Every order spends money.</summary>
	Live,
}
