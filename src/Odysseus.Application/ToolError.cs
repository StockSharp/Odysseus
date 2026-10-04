namespace Odysseus.Application;

using System;

/// <summary>
/// A failure, in the shape the published error contract declares.
/// </summary>
/// <param name="Category">Why the call failed.</param>
/// <param name="Message">What went wrong, in the caller's own terms.</param>
/// <param name="Remediation">What to do next.</param>
/// <param name="CorrelationId">Identifier tying this failure to the server logs.</param>
public sealed record ToolError(
	ToolErrorCategories Category,
	string Message,
	string Remediation,
	string CorrelationId);

/// <summary>
/// Turns an exception into the failure an agent receives.
/// </summary>
/// <remarks>
/// The text of a failure is not a log line; it is the next prompt. An agent that is told only that
/// something went wrong has nothing to aim at and spends its remaining attempts guessing, so every
/// mapping here keeps the message that names what was wrong and adds what to do about it. The one
/// exception is an unexpected defect, where the details stay in the log: an internal message could
/// carry a path or a value the caller must not see.
/// </remarks>
public static class ToolErrors
{
	/// <summary>
	/// Maps an exception onto the error contract.
	/// </summary>
	/// <param name="error">Exception to map.</param>
	/// <param name="correlationId">Identifier tying the failure to the server logs.</param>
	/// <returns>The failure to return to the caller.</returns>
	public static ToolError Describe(Exception error, string correlationId)
	{
		ArgumentNullException.ThrowIfNull(error);
		ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

		return error switch
		{
			InvalidSpecException => new(
				ToolErrorCategories.SpecValidation,
				error.Message,
				"Every problem is listed with its place in the document and what would fix it. Correct them all " +
				"and propose again; validate_spec reports the same list without recording anything, so drafting " +
				"against it costs nothing.",
				correlationId),

			// The agent did not write this C# - the translator did - so a build failure is ours whichever
			// end it came from, and the answer says so rather than inviting a retry that cannot work.
			StrategyBuildException build => new(
				build.IsAnalyzerViolation ? ToolErrorCategories.AnalyzerViolation : ToolErrorCategories.CompilationFailed,
				(build.IsAnalyzerViolation
					? "The generated strategy broke a rule this server enforces on strategy code. "
					: "The generated strategy did not compile. ") + build.Enumerate(),
				"This is a defect in the server's translator rather than in your specification: the same " +
				"specification will emit the same source every time, so retrying will not help. Report it " +
				$"with correlation id {correlationId}. Line numbers are of the generated C#, which " +
				"get_candidate_source prints for any candidate that did build.",
				correlationId),

			BrokerNotConfiguredException => new(
				ToolErrorCategories.ConfigurationInvalid,
				error.Message,
				"A broker is a connector this server loads while it runs. Call select_connector with one " +
				"list_connectors offers, or point ODYSSEUS_BROKER_CONNECTOR at a connector file and " +
				"ODYSSEUS_BROKER_KEYS at a file holding the credentials, then restart the server. " +
				"Everything that does not touch an account works without one: import_demo_dataset brings in " +
				"bundled data, and analyze_market, the backtests and measure_on_closed_data all read history " +
				"that is already imported.",
				correlationId),

			// Placed before the InvalidOperationException arm, which would otherwise swallow it and answer
			// with "bring the project into the state this call needs". There is no state to bring it into:
			// something is alive, holding something, and not saying what.
			RunnerUnresponsiveException => new(
				ToolErrorCategories.Conflict,
				error.Message,
				"Nothing was written down, so nothing here now says anything untrue. Read the account with " +
				"get_account_state to see what is actually open, and give the process a moment - a runner " +
				"that is placing an order answers late rather than not at all. If it stays silent it has to " +
				"be dealt with by a person at the machine: no tool here can end a process, because a tool " +
				"that could would be a way to leave a position with nothing watching it.",
				correlationId),

			RunnerStopFailedException => new(
				ToolErrorCategories.Conflict,
				error.Message,
				"The deployment is still recorded as running, because it may be. Read the account with " +
				"get_account_state to see what is open, then ask stop_deployment again; a stop that keeps " +
				"failing has to be dealt with by a person at the broker.",
				correlationId),

			RunnerAlreadyRunningException => new(
				ToolErrorCategories.Conflict,
				error.Message,
				"list_deployments says which deployment holds the project and whether its process is still " +
				"there. Stop that one first; two strategies on one account trade against each other's " +
				"positions and neither result means anything afterwards.",
				correlationId),

			RunnerStartFailedException => new(
				ToolErrorCategories.WorkerFailure,
				error.Message,
				"Nothing was traded and the deployment is recorded as failed with this reason on it. The " +
				"runner's own directory is left where it is, and its log is the only thing that says why. " +
				$"If it keeps happening, quote correlation id {correlationId} when reporting it.",
				correlationId),

			// Raised in the process that was going to trade, on its own reading of a file this one never
			// sees. What it can say is that nothing was started; what it deliberately does not say is how
			// such a file would be arranged, because a correct recipe read out to a tired person is the
			// failure this whole arrangement is designed against.
			LiveMandateInvalidException => new(
				ToolErrorCategories.ConfigurationInvalid,
				error.Message,
				"Nothing was started, and nothing here can start it. Which account a process may reach is " +
				"decided where that process was configured, before it ran: there is nothing reachable from " +
				"this channel that sets it and nothing that can be given here that would. Whoever configured " +
				"the process is the one who can look at this.",
				correlationId),

			ServerModeException => new(
				ToolErrorCategories.PermissionDenied,
				error.Message,
				"describe_server reports the mode this instance runs in. It is chosen when the process " +
				"starts and nothing reachable from here can change it.",
				correlationId),

			// Safety before courtesy: a connector that cannot be proven to be on paper is a connector that
			// would trade for real, and no amount of care downstream makes that recoverable.
			ConnectorNotPaperException or ConnectorRefusedException => new(
				ToolErrorCategories.ConfigurationInvalid,
				error.Message,
				"describe_connector reports what a package declares - its adapter, the credentials it takes " +
				"and every setting it accepts - without loading it. Correct the choice against that and call " +
				"select_connector again.",
				correlationId),

			// The ordinary case rather than a broken one: almost no machine has the installer console on
			// it, and this server never fetches it. So the answer names the variable and says plainly
			// that supplying the program is the operator's job, the way the broker arm says that
			// credentials are.
			ProductInstallerUnavailableException => new(
				ToolErrorCategories.ConfigurationInvalid,
				error.Message,
				"Product installation is off unless an operator turned it on, and nothing here downloads " +
				"the installer: point ODYSSEUS_INSTALLER at StockSharp.Installer.Console on this machine, " +
				"or put it in an 'installer' folder beside the server, and list the product identifiers " +
				"that may be installed in ODYSSEUS_PRODUCT_ALLOW. The account the installer signs in with " +
				"is machine-wide and is arranged with the installer itself, not from here. " +
				"get_installer_state reports all of this without installing anything, and none of the " +
				"research tools need a product at all.",
				correlationId),

			ProductInstallerBusyException => new(
				ToolErrorCategories.Conflict,
				error.Message,
				"Wait for the other installer to finish, or close it, and ask again. Nothing was started " +
				"and nothing was changed on this machine.",
				correlationId),

			// Unlike every other timeout in this product, this one cannot promise that nothing was
			// written: the program being driven has no cancellation at all, so ending it means killing
			// it, and it may have been half way through copying a product onto the disk.
			ProductInstallerTimedOutException => new(
				ToolErrorCategories.Timeout,
				error.Message,
				"The installer was killed, so the product directory may hold a partial installation. " +
				"Read the captured output the message names, then either ask again with reinstall set - " +
				"which removes what is there before installing - or clear the directory by hand. " +
				"get_installer_state names the directory products are installed under.",
				correlationId),

			// Placed before the InvalidOperationException arm below, which would otherwise swallow these:
			// they are the three published categories that nothing could produce while a candidate ran in
			// the server's own process.
			IsolationFailedException { Kind: IsolationFailures.Timeout } => new(
				ToolErrorCategories.Timeout,
				error.Message,
				"The run was stopped and nothing was written. A shorter slice or a simpler specification is " +
				"the cheapest way to find out whether the strategy is the problem; a search costs several " +
				"runs inside one deadline.",
				correlationId),

			IsolationFailedException { Kind: IsolationFailures.Memory } => new(
				ToolErrorCategories.ResourceExhausted,
				error.Message,
				"Indicator lengths and warm-up are the usual cause, and a search evaluates several settings " +
				"at once. Reduce the lengths the specification declares, or search a smaller population.",
				correlationId),

			IsolationFailedException => new(
				ToolErrorCategories.WorkerFailure,
				error.Message,
				"This is a defect on the server side rather than a problem with the request: the process " +
				$"that runs candidates failed. Quote correlation id {correlationId} when reporting it.",
				correlationId),

			// The machinery failed, not the candidate, so there is no finding here to reason from and
			// nothing was taken for it. The one useful thing to say is: ask again.
			HarnessFailedException { Kind: HarnessFailures.Storage } => new(
				ToolErrorCategories.StorageFailure,
				error.Message,
				"Nothing was measured, the attempt was not charged and the request is unchanged, so asking " +
				"again is safe. If it keeps happening the machine this server writes to is the problem " +
				$"rather than anything in the request; quote correlation id {correlationId} when reporting it.",
				correlationId),

			HarnessFailedException => new(
				ToolErrorCategories.WorkerFailure,
				error.Message,
				"The process that runs candidates failed rather than the strategy inside it, so this says " +
				"nothing about the specification and there is nothing in it to correct. The attempt was not " +
				"charged and asking again is safe; a candidate that keeps breaking the process stops being " +
				$"free and is recorded as having failed. Quote correlation id {correlationId} if it persists.",
				correlationId),

			SpecNotFoundException or DatasetNotFoundException => new(
				ToolErrorCategories.NotFound,
				error.Message,
				"Check the identifier against what the project actually holds; list_specs and get_dataset_report " +
				"report what exists.",
				correlationId),

			CandidateNotFoundException => new(
				ToolErrorCategories.NotFound,
				error.Message,
				"list_candidates reports every candidate of the project with its identifier and the stage it " +
				"has reached. A candidate belongs to one project, so an identifier from another will not be " +
				"found here either.",
				correlationId),

			RunNotFoundException => new(
				ToolErrorCategories.NotFound,
				error.Message,
				"list_runs reports every run of the project. A run identifier comes back from run_backtest " +
				"and from the measurements, which name the runs they were computed from.",
				correlationId),

			DeploymentNotFoundException => new(
				ToolErrorCategories.NotFound,
				error.Message,
				"list_deployments reports what this project has put on the paper account, running or stopped.",
				correlationId),

			ProjectNotFoundException => new(
				ToolErrorCategories.NotFound,
				error.Message,
				"Call list_projects to see which projects exist, and pass one of the identifiers it returns.",
				correlationId),

			ArtifactNotFoundException => new(
				ToolErrorCategories.NotFound,
				error.Message,
				"The artifact was never stored, or the project it belongs to was exported without it.",
				correlationId),

			ArtifactCorruptedException => new(
				ToolErrorCategories.IntegrityViolation,
				error.Message,
				"The file was changed outside the product. Restore the project from a backup; results that " +
				"reference this artifact can no longer be reproduced.",
				correlationId),

			FormatException or ArgumentException => new(
				ToolErrorCategories.InvalidRequest,
				error.Message,
				"Correct the argument named in the message and call again. Identifiers are values the server " +
				"issued; echo them back rather than composing them.",
				correlationId),

			ResearchBudgetExhaustedException => new(
				ToolErrorCategories.BudgetExhausted,
				error.Message,
				"The allowance is a ceiling, not a suggestion, and nothing here will raise it. Read what the " +
				"project has already measured and decide from that.",
				correlationId),

			InvalidOperationException => new(
				ToolErrorCategories.PreconditionFailed,
				error.Message,
				"Bring the project into the state this call needs first, then call again.",
				correlationId),

			TimeoutException => new(
				ToolErrorCategories.Timeout,
				error.Message,
				"Nothing was written, so asking again is safe. If it keeps happening, the broker is the " +
				"problem rather than the request; a shorter range is the cheapest way to find out.",
				correlationId),

			OperationCanceledException => new(
				ToolErrorCategories.Cancelled,
				"The operation was cancelled.",
				"Nothing was left half-written. Call again when you want the work done.",
				correlationId),

			// Anything unrecognised is a defect on our side. The message stays in the log, because it can
			// carry a path or a value the caller has no business seeing.
			_ => new(
				ToolErrorCategories.Internal,
				"The server failed to complete the call.",
				$"This is a defect on the server side, not a problem with the request. Quote correlation id " +
				$"{correlationId} when reporting it.",
				correlationId),
		};
	}
}
