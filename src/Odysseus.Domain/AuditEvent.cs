namespace StockSharp.Odysseus.Domain;

/// <summary>
/// One permanently recorded action.
/// </summary>
/// <remarks>
/// Audit is not logging. A log line explains what the software did; an audit entry is the evidence that
/// a research result was produced the way the report claims. Entries are appended and never changed,
/// and the sequence has no gaps, so a missing step is visible rather than invisible.
/// </remarks>
public sealed record AuditEvent
{
	/// <summary>Position in the project's audit trail, starting at one and never reused.</summary>
	public required long Sequence { get; init; }

	/// <summary>What happened.</summary>
	public required AuditEventTypes Type { get; init; }

	/// <summary>Who caused it.</summary>
	public required Actors Actor { get; init; }

	/// <summary>When it happened, in UTC.</summary>
	public required DateTime OccurredAt { get; init; }

	/// <summary>
	/// Hash of the payload the action carried, so the entry ties to an artifact without copying it.
	/// </summary>
	public required string PayloadHash { get; init; }

	/// <summary>Short human-readable description of the action.</summary>
	public required string Detail { get; init; }
}
