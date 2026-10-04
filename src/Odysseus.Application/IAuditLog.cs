namespace Odysseus.Application;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Odysseus.Domain;

/// <summary>
/// Appends and reads the permanent record of what happened in a project.
/// </summary>
/// <remarks>
/// There is deliberately no way to change or remove an entry. An audit trail that can be edited proves
/// nothing, and the trail is what a candidate report ultimately rests on.
/// </remarks>
public interface IAuditLog
{
	/// <summary>
	/// Appends an entry.
	/// </summary>
	/// <param name="project">Project the action belongs to.</param>
	/// <param name="type">What happened.</param>
	/// <param name="actor">Who caused it.</param>
	/// <param name="detail">Short human-readable description.</param>
	/// <param name="payloadHash">Hash of the payload the action carried.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The appended entry, with the sequence it was given.</returns>
	ValueTask<AuditEvent> AppendAsync(
		ProjectId project,
		AuditEventTypes type,
		Actors actor,
		string detail,
		string payloadHash,
		CancellationToken cancellationToken);

	/// <summary>
	/// Reads the trail of a project in order.
	/// </summary>
	/// <param name="project">Project to read.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The entries, oldest first.</returns>
	ValueTask<IReadOnlyList<AuditEvent>> ReadAsync(ProjectId project, CancellationToken cancellationToken);
}
