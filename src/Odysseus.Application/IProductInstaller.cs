namespace Odysseus.Application;

/// <summary>
/// One product the StockSharp store offers.
/// </summary>
/// <param name="Id">Numeric identifier, which is what every call names a product by.</param>
/// <param name="PackageId">Package the product is delivered as.</param>
/// <param name="Name">Name a person would recognise.</param>
/// <param name="ContentType">What kind of thing it is, as the installer classifies it.</param>
/// <param name="InstalledIn">Directory it is installed in, or an empty string when it is not installed.</param>
/// <param name="Updates">Version offered as an update, or an empty string when none was reported.</param>
public sealed record Product(
	long Id,
	string PackageId,
	string Name,
	string ContentType,
	string InstalledIn,
	string Updates)
{
	/// <summary>Whether this product is installed on this machine.</summary>
	public bool IsInstalled => !string.IsNullOrEmpty(InstalledIn);
}

/// <summary>
/// Whether products can be installed at all, and what is in the way when they cannot.
/// </summary>
/// <param name="IsAvailable">Whether an install could be attempted right now.</param>
/// <param name="ConsolePath">Where the installer console was found, or an empty string when it was not.</param>
/// <param name="LookedIn">Every place it was looked for, in the order they were tried.</param>
/// <param name="HasAccount">Whether this machine has a StockSharp account signed in.</param>
/// <param name="AccountFile">File the account is read from, whether or not it exists.</param>
/// <param name="HardwareId">
/// Identifier the store licences products against, or an empty string when it could not be read.
/// </param>
/// <param name="BlockedBy">Which of the three things is missing, or an empty string when none is.</param>
/// <param name="AllowedProducts">Product identifiers the operator allowed, which may be none.</param>
/// <param name="InstallRoot">Directory products are installed under.</param>
/// <remarks>
/// Every field is answered even when nothing is available, which is the point of the record: a server
/// that cannot install anything is a server whose caller has to learn that before it plans around it,
/// and the ordinary case is a machine that simply does not have the console on it.
/// </remarks>
public sealed record ProductInstallerState(
	bool IsAvailable,
	string ConsolePath,
	IReadOnlyList<string> LookedIn,
	bool HasAccount,
	string AccountFile,
	string HardwareId,
	string BlockedBy,
	IReadOnlyList<long> AllowedProducts,
	string InstallRoot)
{
	/// <summary>What <see cref="BlockedBy"/> holds when the installer console is not on this machine.</summary>
	public const string NoConsole = "no console";

	/// <summary>What <see cref="BlockedBy"/> holds when no StockSharp account is signed in on this machine.</summary>
	public const string NoAccount = "no account";

	/// <summary>What <see cref="BlockedBy"/> holds when the operator allowed no product at all.</summary>
	public const string NoneAllowed = "no product is allowed";

	/// <summary>What <see cref="BlockedBy"/> holds when nothing is in the way.</summary>
	public const string Nothing = "";
}

/// <summary>
/// What one invocation of the installer came to.
/// </summary>
/// <param name="Succeeded">Whether the console reported success.</param>
/// <param name="ExitCode">
/// What the console exited with. A diagnostic and nothing more: the console spends almost every failure
/// through one code, so nothing branches on this value.
/// </param>
/// <param name="Products">The products its output named, which is none for anything but a listing.</param>
/// <param name="Unparsed">Every line that was not a product, verbatim and in order.</param>
/// <param name="LogPath">File the whole of the output was captured to.</param>
/// <param name="Took">How long the invocation ran for.</param>
public sealed record ProductOutcome(
	bool Succeeded,
	int ExitCode,
	IReadOnlyList<Product> Products,
	IReadOnlyList<string> Unparsed,
	string LogPath,
	TimeSpan Took);

/// <summary>
/// Installing StockSharp products on the machine this server runs on.
/// </summary>
/// <remarks>
/// The thing behind this port is another vendor's program, driven as a process. It is not linked: doing
/// so would pull a cross-repository reference, a StockSharp account, a licence and a private feed into a
/// server that fetches public packages anonymously, and none of those are things a research server
/// should acquire in order to copy an application onto a disk.
///
/// Two consequences run through every method. The program has no machine-readable output, so what could
/// not be understood comes back as text rather than being dropped. And it is a machine-wide singleton
/// that will try to close a running installer, so one invocation happens at a time and a foreign one is
/// refused rather than raced.
/// </remarks>
public interface IProductInstaller
{
	/// <summary>
	/// Reports whether products can be installed at all, and what is in the way when they cannot.
	/// </summary>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The state, which is answered whether or not anything is available.</returns>
	/// <remarks>This never throws: being unavailable is the answer rather than a failure.</remarks>
	ValueTask<ProductInstallerState> DescribeAsync(CancellationToken cancellationToken);

	/// <summary>
	/// Lists the products the store offers.
	/// </summary>
	/// <param name="search">Phrase a product name must contain, or an empty string for all of them.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>What the invocation came to.</returns>
	ValueTask<ProductOutcome> ListAsync(string search, CancellationToken cancellationToken);

	/// <summary>
	/// Lists the products already installed on this machine.
	/// </summary>
	/// <param name="search">Phrase a product name must contain, or an empty string for all of them.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>What the invocation came to.</returns>
	ValueTask<ProductOutcome> InstalledAsync(string search, CancellationToken cancellationToken);

	/// <summary>
	/// Installs one product.
	/// </summary>
	/// <param name="product">Identifier of the product to install.</param>
	/// <param name="reinstall">Whether an installed product is removed first rather than refused.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>What the invocation came to.</returns>
	/// <remarks>
	/// Where it lands is derived from the install root and never named by the caller: a path that
	/// arrives as an argument is a path that writes anywhere.
	/// </remarks>
	ValueTask<ProductOutcome> InstallAsync(long product, bool reinstall, CancellationToken cancellationToken);

	/// <summary>
	/// Updates one installed product.
	/// </summary>
	/// <param name="product">Identifier of the product to update.</param>
	/// <param name="backupSettings">Whether the product's settings are copied aside first.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>What the invocation came to.</returns>
	/// <remarks>The installer closes a running copy of the product before it updates it.</remarks>
	ValueTask<ProductOutcome> UpdateAsync(long product, bool backupSettings, CancellationToken cancellationToken);

	/// <summary>
	/// Removes one installed product.
	/// </summary>
	/// <param name="product">Identifier of the product to remove.</param>
	/// <param name="removeData">Whether the settings, schemas and logs the product wrote go with it.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>What the invocation came to.</returns>
	ValueTask<ProductOutcome> RemoveAsync(long product, bool removeData, CancellationToken cancellationToken);
}
