namespace Odysseus.Products;

using System.IO;
using System.Runtime.InteropServices;

/// <summary>
/// How this server drives the installer console, and what it will let the console touch.
/// </summary>
/// <param name="LookedIn">Every place the console is looked for, in the order they are tried.</param>
/// <param name="InstallRoot">Directory every invocation runs in and every product lands under.</param>
/// <param name="AccountFile">File the installer reads its StockSharp account from.</param>
/// <param name="AllowedProducts">Product identifiers the operator allowed, which may be none.</param>
/// <param name="ForeignProcesses">Process names that mean another installer holds this machine.</param>
/// <param name="ReadDeadline">How long a listing or a hardware-id read may take.</param>
/// <param name="ChangeDeadline">How long an install or an update may take.</param>
/// <param name="RemoveDeadline">How long a removal may take.</param>
/// <remarks>
/// None of this is reachable from a tool, for the reason <c>WorkerOptions</c> gives about its own
/// limits: a deadline is a property of the machine rather than of the request, and a directory that
/// arrives as an argument is a directory that writes anywhere.
///
/// The working directory is the whole of the confinement. The console resolves both an explicit
/// directory and its own <c>products/{name}</c> default against the current directory, and writes its
/// text log to <c>Logs/</c> relative to it as well, so setting one place settles all three. What is not
/// confined is the folder the installer keeps its account, its package cache and its registry of
/// installations in: that is under the user's documents folder, has no switch, and is stated rather
/// than pretended about.
/// </remarks>
public sealed record ProductInstallerOptions(
	IReadOnlyList<string> LookedIn,
	string InstallRoot,
	string AccountFile,
	IReadOnlyList<long> AllowedProducts,
	IReadOnlyList<string> ForeignProcesses,
	TimeSpan ReadDeadline,
	TimeSpan ChangeDeadline,
	TimeSpan RemoveDeadline)
{
	/// <summary>Environment variable naming the installer console, which the operator supplies.</summary>
	public const string PathVariable = "ODYSSEUS_INSTALLER";

	/// <summary>Name of the console, without the extension the platform gives it.</summary>
	public const string ConsoleName = "StockSharp.Installer.Console";

	/// <summary>Folder beside the host the console is looked for in when the variable says nothing.</summary>
	public const string ConsoleFolder = "installer";

	/// <summary>Folder under the projects root that products are installed into.</summary>
	public const string ProductsFolder = "products";

	/// <summary>Folder under the install root each invocation's output is captured into.</summary>
	public const string InvocationsFolder = "invocations";

	/// <summary>Name of the running installer window, which will not share the machine.</summary>
	public const string InstallerUiProcess = "StockSharp.Installer.UI";

	/// <summary>Name of the running installer console, which will not share the machine either.</summary>
	public const string InstallerConsoleProcess = "StockSharp.Installer.Console";

	/// <summary>Where each invocation's captured output is written.</summary>
	public string Invocations => Path.Combine(InstallRoot, InvocationsFolder);

	/// <summary>Whether a StockSharp account is signed in on this machine.</summary>
	public bool HasAccount => !string.IsNullOrWhiteSpace(AccountFile) && File.Exists(AccountFile);

	private static string Extension
		=> RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : string.Empty;

	/// <summary>
	/// The options a server starts with.
	/// </summary>
	/// <param name="projectsRoot">Directory the projects live in.</param>
	/// <param name="allowedProducts">Product identifiers the operator allowed.</param>
	/// <returns>The options.</returns>
	/// <exception cref="ArgumentException">No projects root was given.</exception>
	public static ProductInstallerOptions Read(string projectsRoot, IEnumerable<long> allowedProducts)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectsRoot);

		var installRoot = Path.Combine(Path.GetFullPath(projectsRoot), ProductsFolder);

		return new(
			LookedIn: Candidates(),
			InstallRoot: installRoot,
			AccountFile: AccountFileOf(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), installRoot),
			AllowedProducts: Identifiers(allowedProducts),
			ForeignProcesses: [InstallerUiProcess, InstallerConsoleProcess],

			// A read forces a full reload of the product cache from the web API on every invocation -
			// there is no offline mode - so ninety seconds is the round trip and not padding.
			ReadDeadline: TimeSpan.FromSeconds(90),

			// A NuGet download that retries five times, unpacked onto a disk.
			ChangeDeadline: TimeSpan.FromMinutes(20),
			RemoveDeadline: TimeSpan.FromMinutes(5));
	}

	/// <summary>
	/// Every place the console is looked for, in the order they are tried.
	/// </summary>
	/// <returns>The paths, of which none need exist.</returns>
	/// <remarks>
	/// The same shape the worker is located with, and for the same reason: a deployment that puts the
	/// program somewhere else says so in one variable, and a deployment that puts it beside the host
	/// says nothing at all.
	/// </remarks>
	public static IReadOnlyList<string> Candidates()
	{
		var named = Environment.GetEnvironmentVariable(PathVariable);

		var beside = Path.Combine(AppContext.BaseDirectory, ConsoleFolder, ConsoleName + Extension);

		if (string.IsNullOrWhiteSpace(named))
			return [beside];

		return [Path.GetFullPath(named), beside];
	}

	/// <summary>
	/// The file the installer reads its StockSharp account from.
	/// </summary>
	/// <param name="documents">
	/// The current user's documents folder as the platform names it, or an empty string where it names
	/// none.
	/// </param>
	/// <param name="installRoot">Directory the console is started in.</param>
	/// <returns>The path, which need not exist.</returns>
	/// <remarks>
	/// Outside everything else this server confines. The installer builds it from the current user's
	/// documents folder and has neither a switch nor a variable for it, so this is the same path
	/// computed the same way rather than a place of ours the console would ignore.
	///
	/// The same way includes the machine that has no such folder, which a server usually is: the
	/// installer is then left with a bare name and settles it against the directory it was started in.
	/// </remarks>
	public static string AccountFileOf(string documents, string installRoot)
	{
		ArgumentNullException.ThrowIfNull(documents);
		ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);

		return Path.GetFullPath(Path.Combine(documents, "StockSharp", "credentials.json"), Path.GetFullPath(installRoot));
	}

	/// <summary>
	/// Finds the console, if it is there now.
	/// </summary>
	/// <returns>Its path, or an empty string when none of the places hold it.</returns>
	/// <remarks>
	/// Asked again on every call rather than settled at start-up, because an operator installing the
	/// console while the server runs is the ordinary way this stops being unavailable, and telling them
	/// to restart the server for it would be a worse product.
	/// </remarks>
	public string Locate()
		=> LookedIn?.FirstOrDefault(File.Exists) ?? string.Empty;

	/// <summary>
	/// Whether the operator allowed this product to be touched.
	/// </summary>
	/// <param name="product">Identifier to check.</param>
	/// <returns><see langword="true"/> when it is on the list.</returns>
	public bool Allows(long product)
		=> AllowedProducts is not null && AllowedProducts.Contains(product);

	/// <summary>
	/// Resolves the directory a product is installed into, and refuses one that leaves the install root.
	/// </summary>
	/// <param name="name">Folder name under the install root.</param>
	/// <returns>The absolute directory.</returns>
	/// <exception cref="ArgumentException">The name is empty, or resolves outside the install root.</exception>
	/// <remarks>
	/// Nothing an agent sends reaches this today - the folder is derived from the product identifier -
	/// and the check is here so that it stays true. A relative name climbing out with <c>..</c>, or an
	/// absolute one, resolves to somewhere this server may not write, and the console would write there
	/// perfectly happily because it resolves the directory it is given against its working directory
	/// exactly as this does.
	/// </remarks>
	public string DirectoryFor(string name)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);

		var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(InstallRoot));
		var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(root, name)));

		var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

		if (!full.StartsWith(root + Path.DirectorySeparatorChar, comparison))
		{
			throw new ArgumentException(
				$"'{name}' resolves to {full}, which is outside {root}. Products are installed under the " +
				"projects root and nowhere else.",
				nameof(name));
		}

		return full;
	}

	/// <summary>
	/// Resolves the directory a product is installed into.
	/// </summary>
	/// <param name="product">Identifier of the product.</param>
	/// <returns>The absolute directory.</returns>
	/// <remarks>
	/// Named by the identifier rather than by the product's own name, because the name is only known
	/// after a listing - a network round trip whose answer would then be deciding where this server
	/// writes.
	/// </remarks>
	public string DirectoryFor(long product)
		=> DirectoryFor(product.ToString(CultureInfo.InvariantCulture));

	/// <summary>
	/// How long one invocation of a verb may take.
	/// </summary>
	/// <param name="verb">Verb being invoked.</param>
	/// <returns>The deadline.</returns>
	public TimeSpan DeadlineFor(InstallerVerbs verb)
		=> verb switch
		{
			InstallerVerbs.Install or InstallerVerbs.Update => ChangeDeadline,
			InstallerVerbs.Remove => RemoveDeadline,
			_ => ReadDeadline,
		};

	/// <summary>
	/// Puts the operator's list into the one order it will always be reported in.
	/// </summary>
	/// <param name="allowedProducts">What the operator named, which may be nothing.</param>
	/// <returns>The identifiers, sorted, without duplicates and without anything that names no product.</returns>
	private static IReadOnlyList<long> Identifiers(IEnumerable<long> allowedProducts)
	{
		if (allowedProducts is null)
			return [];

		return [.. allowedProducts.Where(id => id > 0).Distinct().Order()];
	}
}
