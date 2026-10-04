namespace Odysseus.Application;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Odysseus.Domain;

/// <summary>
/// Remembers which stretches of closed history have been spent, across every project on this machine.
/// </summary>
/// <remarks>
/// Every other store belongs to a project, so that a project can be zipped and carried elsewhere. This
/// one deliberately does not: a record kept inside a project could be escaped by starting another one,
/// and escaping it is exactly what has to be prevented. The closed slice is the only measurement in the
/// product that cannot be repeated, and a second project over the same dates is a repeat of it by
/// someone who has already seen the answer.
/// </remarks>
public interface IClosedHistoryLedger
{
	/// <summary>
	/// Finds stretches already spent that share time with the one asked about.
	/// </summary>
	/// <param name="symbol">Symbol to be measured.</param>
	/// <param name="from">Start of the closed stretch, in UTC.</param>
	/// <param name="to">End of the closed stretch, exclusive, in UTC.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>What has already been spent over that stretch, newest first.</returns>
	ValueTask<IReadOnlyList<SpentWindow>> FindOverlappingAsync(
		string symbol,
		DateTime from,
		DateTime to,
		CancellationToken cancellationToken);

	/// <summary>
	/// Records a stretch as spent, unless somebody has already spent any of it.
	/// </summary>
	/// <param name="window">What is about to be spent.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>
	/// The entries that stand in the way, newest first, in which case nothing was written; or nothing,
	/// in which case the stretch is now this candidate's. An entry of the same candidate for exactly the
	/// same stretch is the same spending and does not stand in the way.
	/// </returns>
	/// <remarks>Checked and written as one step, so two claims made at once cannot both be granted.</remarks>
	ValueTask<IReadOnlyList<SpentWindow>> ClaimAsync(SpentWindow window, CancellationToken cancellationToken);
}
