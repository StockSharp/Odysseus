namespace Odysseus.Server;

using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using ModelContextProtocol.Server;

using Odysseus.Application;
using Odysseus.Domain;
using Odysseus.Spec;

/// <summary>
/// The specification tools an agent sees over MCP.
/// </summary>
[McpServerToolType]
public static class SpecTools
{
	/// <summary>
	/// Lists the indicators a specification may use.
	/// </summary>
	/// <param name="guard">Turns a failure into the published error shape.</param>
	/// <returns>The catalog.</returns>
	[McpServerTool(Name = "get_indicator_catalog")]
	[Description("List every indicator a specification may refer to, with what it reads and the window " +
		"lengths it accepts. This is the only vocabulary that exists: a specification naming anything else " +
		"is refused before any code is generated, so read this before writing rules.")]
	public static Task<object> GetIndicatorCatalog(ToolGuard guard)
		=> guard.Run(nameof(GetIndicatorCatalog), () => (object)new
		{
			indicators = IndicatorCatalog.All.Select(i => new
			{
				name = i.Name,
				title = i.Title,
				description = i.Description,
				reads = i.Source switch
				{
					IndicatorSources.ChosenField => $"a candle field you choose; {i.Field} if you name none",
					IndicatorSources.FixedField => $"{i.Field}, and nothing else",
					_ => "the whole candle",
				},
				acceptsSource = i.Source == IndicatorSources.ChosenField,
				minimumLength = i.MinimumLength,
				maximumLength = i.MaximumLength,
			}).ToArray(),
		});

	/// <summary>
	/// Checks a specification without recording it.
	/// </summary>
	/// <param name="guard">Turns a failure into the published error shape.</param>
	/// <param name="service">Specification use cases.</param>
	/// <param name="spec">The specification as text.</param>
	/// <returns>Everything found wrong.</returns>
	[McpServerTool(Name = "validate_spec")]
	[Description("Check a specification and report everything wrong with it at once, each with the place in " +
		"the document, what is wrong and what would fix it. Nothing is recorded, so use this freely while " +
		"drafting. Every problem it reports would otherwise become a refusal from propose_spec.")]
	public static Task<object> ValidateSpec(
		ToolGuard guard,
		SpecService service,
		[Description("The strategy specification, as a JSON document.")] string spec)
		=> guard.Run(nameof(ValidateSpec), () => Describe(service.Check(spec)));

	/// <summary>
	/// Records a specification as the project's next revision.
	/// </summary>
	/// <param name="guard">Turns a failure into the published error shape.</param>
	/// <param name="service">Specification use cases.</param>
	/// <param name="projectId">Project the specification belongs to.</param>
	/// <param name="spec">The specification as text.</param>
	/// <param name="operationKey">Key that makes a repeated call return the first result.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The recorded revision.</returns>
	[McpServerTool(Name = "propose_spec")]
	[Description("Record a specification as this project's next revision. It is checked first and refused " +
		"if it would not survive translation, so an invalid one never becomes part of the history. " +
		"Specifications are never edited: a change is a new revision, and every candidate points at the " +
		"exact words it was built from.")]
	public static Task<object> ProposeSpec(
		ToolGuard guard,
		SpecService service,
		[Description("Identifier of the project.")] string projectId,
		[Description("The strategy specification, as a JSON document.")] string spec,
		[Description("Caller-generated key identifying this request, reused on retries of the same intent.")] string operationKey,
		CancellationToken cancellationToken)
		=> guard.RunAsync(nameof(ProposeSpec), async () =>
		{
			var revision = await service.ProposeAsync(Ids.Project(projectId), spec, operationKey, Actors.Agent, cancellationToken);

			return new
			{
				specId = revision.Id.Value,
				revision = revision.Revision,
				hash = revision.Hash,
				author = revision.Author.ToString(),
				createdAt = revision.CreatedAt,
			};
		});

	/// <summary>
	/// Lists the specifications recorded for a project.
	/// </summary>
	/// <param name="guard">Turns a failure into the published error shape.</param>
	/// <param name="service">Specification use cases.</param>
	/// <param name="projectId">Project to list.</param>
	/// <param name="offset">How many to skip.</param>
	/// <param name="limit">How many to return.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The revisions, oldest first.</returns>
	[McpServerTool(Name = "list_specs")]
	[Description("List the specifications recorded for a project, oldest first, so the history of what was " +
		"tried can be read back. Answers a page at a time and says how to ask for the next.")]
	public static Task<object> ListSpecs(
		ToolGuard guard,
		SpecService service,
		[Description("Identifier of the project.")] string projectId,
		CancellationToken cancellationToken,
		[Description(Page.OffsetDescription)] int offset = 0,
		[Description(Page.LimitDescription)] int limit = 0)
		=> guard.RunAsync(nameof(ListSpecs), async () =>
		{
			var all = await service.ListAsync(Ids.Project(projectId), cancellationToken);

			var (specs, window) = Page.Of(all, offset, limit, r => new
			{
				specId = r.Id.Value,
				revision = r.Revision,
				hash = r.Hash,
				author = r.Author.ToString(),
				createdAt = r.CreatedAt,
			});

			return new { specs, window };
		});

	private static object Describe(SpecCheck check)
		=> new
		{
			isValid = check.IsValid,
			requiredWarmupBars = check.RequiredWarmup,
			problems = check.Problems.Select(p => new
			{
				path = p.Path,
				message = p.Message,
				remedy = p.Remedy,
			}).ToArray(),
		};
}
