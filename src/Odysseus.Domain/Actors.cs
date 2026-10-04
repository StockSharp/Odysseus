namespace Odysseus.Domain;

/// <summary>
/// Who caused an audited action.
/// </summary>
/// <remarks>
/// The distinction is the point of the audit here: the research loop is driven by the user's own AI
/// agent, so a reader has to be able to tell a hypothesis the agent proposed from one the user wrote,
/// and both from a step the server took on its own.
/// </remarks>
public enum Actors
{
	/// <summary>The person operating the product.</summary>
	User,

	/// <summary>The AI agent connected over MCP.</summary>
	Agent,

	/// <summary>The server itself, acting without being asked.</summary>
	System,
}
