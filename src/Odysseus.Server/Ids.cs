namespace Odysseus.Server;

/// <summary>
/// Turns the text an agent sends into a typed identifier.
/// </summary>
/// <remarks>
/// A refusal here is read by a model deciding what to do next, so it says what a correct value looks
/// like and which tool produces one. Told only that the value was wrong, an agent has nothing to aim at
/// and spends its remaining attempts guessing.
/// </remarks>
public static class Ids
{
	/// <summary>
	/// Parses a project identifier.
	/// </summary>
	/// <param name="value">Text the agent sent.</param>
	/// <returns>The identifier.</returns>
	/// <exception cref="ArgumentException">The text is not a project identifier.</exception>
	public static ProjectId Project(string value)
	{
		if (!ProjectId.TryParse(value, out var id))
		{
			throw new ArgumentException(
				$"'{value}' is not a project identifier. Identifiers start with '{ProjectId.Prefix}_' and are " +
				"returned by create_project and list_projects; pass one of those back rather than composing one.",
				nameof(value));
		}

		return id;
	}

	/// <summary>
	/// Parses a specification identifier.
	/// </summary>
	/// <param name="value">Text the agent sent.</param>
	/// <returns>The identifier.</returns>
	/// <exception cref="ArgumentException">The text is not a specification identifier.</exception>
	public static SpecId Spec(string value)
	{
		if (!SpecId.TryParse(value, out var id))
		{
			throw new ArgumentException(
				$"'{value}' is not a specification identifier. Identifiers start with '{SpecId.Prefix}_' and are " +
				"returned by propose_spec and list_specs; pass one of those back rather than composing one.",
				nameof(value));
		}

		return id;
	}

	/// <summary>
	/// Parses a candidate identifier.
	/// </summary>
	/// <param name="value">Text the agent sent.</param>
	/// <returns>The identifier.</returns>
	/// <exception cref="ArgumentException">The text is not a candidate identifier.</exception>
	public static CandidateId Candidate(string value)
	{
		if (!CandidateId.TryParse(value, out var id))
		{
			throw new ArgumentException(
				$"'{value}' is not a candidate identifier. Identifiers start with '{CandidateId.Prefix}_' and are " +
				"returned by build_candidate and list_candidates; pass one of those back rather than composing one.",
				nameof(value));
		}

		return id;
	}

	/// <summary>
	/// Parses a run identifier.
	/// </summary>
	/// <param name="value">Text the agent sent.</param>
	/// <returns>The identifier.</returns>
	/// <exception cref="ArgumentException">The text is not a run identifier.</exception>
	public static RunId Run(string value)
	{
		if (!RunId.TryParse(value, out var id))
		{
			throw new ArgumentException(
				$"'{value}' is not a run identifier. Identifiers start with '{RunId.Prefix}_' and are " +
				"returned by run_backtest and list_runs; pass one of those back rather than composing one.",
				nameof(value));
		}

		return id;
	}

	/// <summary>
	/// Reads a deployment identifier.
	/// </summary>
	/// <param name="value">Text the agent sent.</param>
	/// <returns>The identifier.</returns>
	/// <exception cref="ArgumentException">The text is not a deployment identifier.</exception>
	public static DeploymentId Deployment(string value)
	{
		if (!DeploymentId.TryParse(value, out var id))
		{
			throw new ArgumentException(
				$"'{value}' is not a deployment identifier. Identifiers start with '{DeploymentId.Prefix}_' " +
				"and are returned by deploy_candidate and list_deployments; pass one of those back rather " +
				"than composing one.",
				nameof(value));
		}

		return id;
	}
}
