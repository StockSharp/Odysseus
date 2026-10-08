namespace StockSharp.Odysseus.Application;

/// <summary>
/// How the server was started.
/// </summary>
/// <remarks>
/// The mode is fixed when the process starts and no tool can change it, which is the whole reason it
/// is a value the server carries rather than an argument a caller supplies.
///
/// One rule differs between the two, and it is the one the mode was written for: a hosted instance
/// refuses the three connector tools, all of which construct a downloaded adapter in this process.
/// Everything else is the same in both - one transport, standard input and output, and the same
/// compiler rules over every candidate whichever mode produced it.
/// </remarks>
public enum ServerModes
{
	/// <summary>
	/// Started on the user's own machine, over standard input and output. The code being researched
	/// belongs to the person running it.
	/// </summary>
	Local,

	/// <summary>
	/// Started with <c>--hosted</c>, for a server whose caller is a stranger. Nothing listens on a
	/// network. What is refused here and allowed in <see cref="Local"/> is the loading of a connector:
	/// <c>list_connectors</c>, <c>describe_connector</c> and <c>select_connector</c> all build a
	/// downloaded adapter, and a stranger does not get to choose which code this process runs.
	/// <c>describe_server</c> reports the mode and which connector, if any, was bound at start-up.
	/// </summary>
	Hosted,
}
