namespace Odysseus.Server;

/// <summary>
/// The tool that spends the closed part of the history, and the one that says whether it is still there.
/// </summary>
[McpServerToolType]
public static class ClosedDataTools
{
	/// <summary>
	/// Measures a candidate once against the closed part of the history.
	/// </summary>
	/// <param name="guard">Turns a failure into the published error shape.</param>
	/// <param name="service">Closed data use cases.</param>
	/// <param name="projectId">Project the candidate belongs to.</param>
	/// <param name="candidateId">Candidate to measure.</param>
	/// <param name="operationKey">Key that makes a repeated call return the first result.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The numbers from the closed slice, beside the ones from the open data.</returns>
	[McpServerTool(Name = "measure_on_closed_data")]
	[Description("Measure a candidate ONCE against the part of the history that has been closed all " +
		"along, and report what it did there beside what it did on the data it was allowed to see. Use it " +
		"on the candidate you would actually trade and on nothing else: a candidate cannot be measured " +
		"here twice, and once a second candidate of the same project has been, neither result is " +
		"independent of the slice any more — whoever chose the second one knew how the first went. " +
		"Everything the earlier numbers are worth rests on this slice not having been touched, so spend " +
		"it last. What gets measured is not yours to choose here: the instrument and the parameters come " +
		"from the run validation was taken on, because measuring some other setting spends the only slice " +
		"that could have said anything about the one you validated. It also asks the closed data the " +
		"walk-forward question, in consecutive stretches, so that answer too comes from data nobody could " +
		"have tuned to. That costs five backtests. The result is statistics and nothing else: what the " +
		"gap between the open and closed numbers means is yours to read.")]
	public static Task<object> MeasureOnClosedData(
		ToolGuard guard,
		ClosedDataService service,
		[Description("Identifier of the project.")] string projectId,
		[Description("Identifier of the candidate to measure.")] string candidateId,
		[Description("Caller-generated key identifying this request, reused on retries of the same intent.")] string operationKey,
		CancellationToken cancellationToken)
		=> guard.RunAsync(nameof(MeasureOnClosedData), async () =>
		{
			var result = await service.MeasureAsync(
				Ids.Project(projectId),
				Ids.Candidate(candidateId),
				operationKey,
				Actors.Agent,
				cancellationToken);

			return new
			{
				candidateId = result.Measurement.Candidate.Value,
				onClosedData = MeasurementView.Of(result.Measurement),
				onOpenData = MeasurementView.Of(result.OnOpenData),
				provenance = new
				{
					timesMeasuredOnOpenData = result.TimesMeasuredOnOpenData,
					earlierCandidatesThatSpentTheSlice = result.EarlierCandidates,
					independent = result.EarlierCandidates == 0,
				},
				runs = result.Runs.Select(r => r.Value).ToArray(),
				note = "Measured once against data that was closed while this candidate was being formed " +
					"and tuned. That data is now spent for this project: anything measured on it afterwards " +
					"was chosen by someone who knew what it said.",
			};
		});

	/// <summary>
	/// Says whether the closed part of the history is still untouched.
	/// </summary>
	/// <param name="guard">Turns a failure into the published error shape.</param>
	/// <param name="service">Closed data use cases.</param>
	/// <param name="projectId">Project to ask about.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>What has been spent.</returns>
	[McpServerTool(Name = "get_closed_data_state")]
	[Description("Say whether this project has spent its one measurement against the closed part of the " +
		"history, and on which candidates. Where the closed part lies is in describe_split.")]
	public static Task<object> GetClosedDataState(
		ToolGuard guard,
		ClosedDataService service,
		[Description("Identifier of the project.")] string projectId,
		CancellationToken cancellationToken)
		=> guard.RunAsync(nameof(GetClosedDataState), async () =>
		{
			var spent = await service.SpentOnAsync(Ids.Project(projectId), cancellationToken);

			return new
			{
				spent = spent.Count > 0,
				measuredCandidates = spent.Select(c => c.Value).ToArray(),
				note = spent.Count == 0
					? "The closed part of the history is untouched. It is worth one measurement; spend it on " +
						"the candidate you would actually trade."
					: "The closed part has been spent. A further measurement is still possible but no longer " +
						"independent, and the report will say so.",
			};
		});
}
