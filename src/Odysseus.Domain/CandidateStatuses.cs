namespace StockSharp.Odysseus.Domain;

/// <summary>
/// Stages a strategy candidate passes through, in the order it passes through them.
/// </summary>
public enum CandidateStatuses
{
	/// <summary>Created from a specification revision; nothing has been produced yet.</summary>
	Draft,

	/// <summary>The translator has emitted C# for the specification.</summary>
	SourceGenerated,

	/// <summary>The source cleared the analyzers.</summary>
	AnalysisPassed,

	/// <summary>The source compiled to an assembly.</summary>
	Compiled,

	/// <summary>Measured on the development slice.</summary>
	Backtested,

	/// <summary>Numeric parameters were searched and a set was frozen.</summary>
	Optimized,

	/// <summary>Measured on the validation slice with the frozen parameters.</summary>
	Validated,

	/// <summary>Survived the stress scenarios and the walk-forward windows.</summary>
	StressTested,

	/// <summary>Measured once on the closed final slice.</summary>
	FinalChecked,

	/// <summary>
	/// Declared finished by whoever is doing the research, and saved with everything behind it.
	/// </summary>
	/// <remarks>
	/// The server does not decide this and never did anything like it well: it measures, and the reading
	/// of what it measured belongs to the person or the model doing the work.
	/// </remarks>
	Completed,

	/// <summary>Running on the paper account.</summary>
	PaperRunning,

	/// <summary>Stopped after a paper run.</summary>
	Stopped,

	/// <summary>
	/// A technical failure: a crashed worker, a timeout, a corrupt artifact. Never used for a strategy
	/// that ran correctly and lost money, which is a result rather than a failure.
	/// </summary>
	Failed,

	/// <summary>Abandoned before it was finished with.</summary>
	Cancelled,
}
