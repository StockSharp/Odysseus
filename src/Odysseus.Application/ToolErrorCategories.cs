namespace StockSharp.Odysseus.Application;

/// <summary>
/// Why a tool call failed.
/// </summary>
/// <remarks>
/// These are the categories of the published error contract, and a test holds the two together:
/// an agent branches on the category, so a value that exists on one side only is a branch that
/// never runs or a message that cannot be understood.
/// </remarks>
public enum ToolErrorCategories
{
	/// <summary>An argument was missing, malformed or outside its allowed range.</summary>
	InvalidRequest,

	/// <summary>A strategy specification failed schema or semantic validation.</summary>
	SpecValidation,

	/// <summary>The project is not in a state where this call makes sense.</summary>
	PreconditionFailed,

	/// <summary>The thing that was asked for does not exist.</summary>
	NotFound,

	/// <summary>The call is not available in the mode this server runs in.</summary>
	PermissionDenied,

	/// <summary>The call would break a rule the server enforces, such as reusing the closed slice.</summary>
	PolicyViolation,

	/// <summary>The project has no research allowance left.</summary>
	BudgetExhausted,

	/// <summary>A configured limit was reached, such as the size of a response or a session quota.</summary>
	LimitExceeded,

	/// <summary>A deterministic risk gate refused the order before it reached the broker.</summary>
	RiskRejected,

	/// <summary>The call waits for a confirmation the user must give outside the agent channel.</summary>
	ConfirmationRequired,

	/// <summary>Generated or submitted source broke an analyzer rule.</summary>
	AnalyzerViolation,

	/// <summary>The source did not compile.</summary>
	CompilationFailed,

	/// <summary>There is not enough history to answer honestly.</summary>
	InsufficientData,

	/// <summary>A stored artifact no longer matches the hash it is filed under.</summary>
	IntegrityViolation,

	/// <summary>The server is configured in a way that cannot work.</summary>
	ConfigurationInvalid,

	/// <summary>The broker refused the credentials.</summary>
	AuthenticationFailed,

	/// <summary>The account or data plan does not offer what the call needs.</summary>
	CapabilityUnavailable,

	/// <summary>An external service could not be reached.</summary>
	UpstreamUnavailable,

	/// <summary>An external service asked us to slow down.</summary>
	UpstreamRateLimited,

	/// <summary>Another operation is already doing this.</summary>
	Conflict,

	/// <summary>The operation took longer than it is allowed to.</summary>
	Timeout,

	/// <summary>A worker ran out of memory or another resource.</summary>
	ResourceExhausted,

	/// <summary>The isolated worker process failed rather than the strategy.</summary>
	WorkerFailure,

	/// <summary>What the server had to write down could not be written.</summary>
	StorageFailure,

	/// <summary>The caller cancelled the operation.</summary>
	Cancelled,

	/// <summary>A defect in the server. The details are logged, not returned.</summary>
	Internal,
}
