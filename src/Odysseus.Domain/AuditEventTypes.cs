namespace StockSharp.Odysseus.Domain;

/// <summary>
/// Actions worth recording permanently, as opposed to logging.
/// </summary>
public enum AuditEventTypes
{
	/// <summary>A project was created.</summary>
	ProjectCreated,

	/// <summary>A project was renamed.</summary>
	ProjectRenamed,

	/// <summary>A project changed state.</summary>
	ProjectStatusChanged,

	/// <summary>A dataset was imported for the project.</summary>
	DatasetImported,

	/// <summary>A revision of the strategy specification was recorded.</summary>
	SpecRevised,

	/// <summary>A candidate was translated and compiled.</summary>
	CandidateBuilt,

	/// <summary>A run was started.</summary>
	RunStarted,

	/// <summary>A run finished, in whatever way.</summary>
	RunCompleted,

	/// <summary>A candidate was put through the fixed set of runs and the numbers were recorded.</summary>
	CandidateEvaluated,

	/// <summary>The closed slice of the history was used, which can happen once.</summary>
	FinalHoldoutConsumed,

	/// <summary>
	/// A strategy was declared finished and kept. The only conclusion in the record, and it arrived from
	/// outside: the server measures and never decides whether a result is worth trading.
	/// </summary>
	StrategyCompleted,

	/// <summary>A paper deployment started.</summary>
	PaperStarted,

	/// <summary>A paper deployment stopped.</summary>
	PaperStopped,

	/// <summary>The project was exported.</summary>
	ProjectExported,
}
