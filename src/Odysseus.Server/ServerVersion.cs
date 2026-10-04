namespace Odysseus.Server;

using System.Reflection;

/// <summary>
/// Version of the running server, as reported to a connecting agent.
/// </summary>
public static class ServerVersion
{
	/// <summary>The informational version of this assembly.</summary>
	public static string Current { get; } =
		typeof(ServerVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
			?? typeof(ServerVersion).Assembly.GetName().Version?.ToString()
			?? "unknown";
}
