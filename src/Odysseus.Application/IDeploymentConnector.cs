namespace StockSharp.Odysseus.Application;

/// <summary>
/// Which connector a deployment's runner is told to load.
/// </summary>
/// <remarks>
/// A runner binds its own connector, in its own process, from its own configuration. So what a session
/// has to supply is a name, not a binding - and that is a real change in what deploying requires: this
/// server no longer needs a connector loaded in order to start a deployment, it needs one named.
///
/// A port rather than a value because two things can name it and they are tried in order: whatever a
/// session selected, and failing that whatever the operator named at start-up. Where that precedence
/// lives is the host's business; what the use case needs is one answer or one refusal.
/// </remarks>
public interface IDeploymentConnector
{
	/// <summary>
	/// The connector a runner is to load.
	/// </summary>
	/// <returns>The choice.</returns>
	/// <exception cref="BrokerNotConfiguredException">Nothing has named one.</exception>
	ConnectorChoice Choose();
}
