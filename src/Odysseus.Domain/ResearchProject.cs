namespace Odysseus.Domain;

/// <summary>
/// A research project: one question, one dataset, one budget, and everything produced while
/// answering it.
/// </summary>
public sealed record ResearchProject
{
	/// <summary>Longest name a project may carry.</summary>
	public const int MaxNameLength = 120;

	/// <summary>Identity of the project.</summary>
	public required ProjectId Id { get; init; }

	/// <summary>Name the user gave the project.</summary>
	public required string Name { get; init; }

	/// <summary>Current state.</summary>
	public required ProjectStatuses Status { get; init; }

	/// <summary>When the project was created, in UTC.</summary>
	public required DateTime CreatedAt { get; init; }

	/// <summary>When the project last changed, in UTC.</summary>
	public required DateTime UpdatedAt { get; init; }

	/// <summary>Dataset the research runs against, once one has been imported.</summary>
	public DatasetId Dataset { get; init; }

	/// <summary>Remaining allowance of the project.</summary>
	public required ResearchBudgetState Budget { get; init; }

	/// <summary>
	/// Creates a project.
	/// </summary>
	/// <param name="name">Name for the project.</param>
	/// <param name="budget">Allowance the project starts with.</param>
	/// <param name="createdAt">Creation moment, in UTC.</param>
	/// <returns>The new project.</returns>
	/// <exception cref="ArgumentException">The name is empty or too long, or the moment is not UTC.</exception>
	public static ResearchProject Create(string name, ResearchBudgetState budget, DateTime createdAt)
	{
		ArgumentNullException.ThrowIfNull(budget);

		var trimmed = Normalize(name);
		var moment = EnsureUtc(createdAt, nameof(createdAt));

		return new()
		{
			Id = ProjectId.New(),
			Name = trimmed,
			Status = ProjectStatuses.Draft,
			CreatedAt = moment,
			UpdatedAt = moment,
			Budget = budget,
		};
	}

	/// <summary>
	/// Renames the project.
	/// </summary>
	/// <param name="name">New name.</param>
	/// <param name="now">Moment of the change, in UTC.</param>
	/// <returns>The renamed project.</returns>
	/// <exception cref="InvalidOperationException">The project is archived.</exception>
	public ResearchProject Rename(string name, DateTime now)
	{
		EnsureWritable();

		return this with { Name = Normalize(name), UpdatedAt = EnsureUtc(now, nameof(now)) };
	}

	/// <summary>
	/// Records that a dataset was imported for this project.
	/// </summary>
	/// <param name="dataset">Dataset that was imported.</param>
	/// <param name="now">Moment of the change, in UTC.</param>
	/// <returns>The updated project.</returns>
	/// <exception cref="InvalidOperationException">The project is archived.</exception>
	public ResearchProject WithDataset(DatasetId dataset, DateTime now)
	{
		EnsureWritable();

		if (dataset.IsEmpty)
			throw new ArgumentException("A dataset must be identified.", nameof(dataset));

		return this with
		{
			Dataset = dataset,
			Status = Status == ProjectStatuses.Draft ? ProjectStatuses.Ready : Status,
			UpdatedAt = EnsureUtc(now, nameof(now)),
		};
	}

	/// <summary>
	/// Moves the project to another state.
	/// </summary>
	/// <param name="status">State to move to.</param>
	/// <param name="now">Moment of the change, in UTC.</param>
	/// <returns>The updated project.</returns>
	/// <exception cref="InvalidOperationException">The project is archived, or research was asked for without a dataset.</exception>
	public ResearchProject WithStatus(ProjectStatuses status, DateTime now)
	{
		EnsureWritable();

		if (status == ProjectStatuses.Researching && Dataset.IsEmpty)
			throw new InvalidOperationException("Research cannot start before a dataset is imported.");

		return this with { Status = status, UpdatedAt = EnsureUtc(now, nameof(now)) };
	}

	private static string Normalize(string name)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);

		var trimmed = name.Trim();

		if (trimmed.Length > MaxNameLength)
			throw new ArgumentException($"A project name is at most {MaxNameLength} characters.", nameof(name));

		return trimmed;
	}

	private static DateTime EnsureUtc(DateTime moment, string parameterName)
	{
		// A local timestamp reaching storage would silently shift every project opened on another
		// machine, so the kind is checked rather than converted.
		if (moment.Kind != DateTimeKind.Utc)
			throw new ArgumentException("Moments are recorded in UTC.", parameterName);

		return moment;
	}

	private void EnsureWritable()
	{
		if (Status == ProjectStatuses.Archived)
			throw new InvalidOperationException($"Project {Id} is archived and cannot be changed.");
	}
}
