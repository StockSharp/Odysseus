namespace StockSharp.Odysseus.Server;

using StockSharp.Odysseus.Products;

/// <summary>
/// Installing StockSharp products on the machine this server runs on.
/// </summary>
/// <remarks>
/// These tools drive another vendor's installer as a process, and the two things worth knowing about
/// that program run through every answer here.
///
/// It has no machine-readable output, so a listing carries the entries it could read and the lines it
/// could not, as text. Dropping what it could not parse would be a shorter answer and a dishonest one.
///
/// And almost everything it can fail at fails in one way, so a failed invocation comes back as an
/// answer with <c>succeeded</c> false, its exit code and the end of what it printed - not as a
/// refusal - because the explanation is in that output and nowhere else. The failures that do have an
/// answer of their own are refusals with a category to branch on: nothing to install with, no account,
/// a product the operator did not allow, another installer holding the machine, and a deadline.
///
/// Two gates, both of them start-up decisions no tool can reach: a hosted instance refuses all six, and
/// an instance whose operator named no product identifiers may touch none.
/// </remarks>
[McpServerToolType]
public static class ProductTools
{
	/// <summary>
	/// Reports whether products can be installed at all.
	/// </summary>
	/// <param name="guard">Turns a failure into the published error shape.</param>
	/// <param name="installer">Drives the installer console.</param>
	/// <param name="options">How this instance was started.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>What is available, and what is in the way when nothing is.</returns>
	[McpServerTool(Name = "get_installer_state")]
	[Description("Report whether this server can install StockSharp products at all: whether the " +
		"installer console is where it expects it, whether this machine has a StockSharp account signed " +
		"in, which product ids the operator allowed, and where products would be installed. Call this " +
		"before the other product tools - installing is off by default and most machines do not have the " +
		"installer on them, so being refused mid-plan is a worse way to find that out. This touches no " +
		"network and installs nothing; it does briefly run the installer to read this machine's hardware " +
		"id, which is the one thing it can answer offline.")]
	public static Task<object> GetInstallerState(
		ToolGuard guard,
		IProductInstaller installer,
		ServerOptions options,
		CancellationToken cancellationToken)
		=> guard.RunAsync(nameof(GetInstallerState), async () =>
		{
			AssertNotHosted(options, "Reading the installer state runs the installer on this machine");

			var state = await installer.DescribeAsync(cancellationToken);

			return new
			{
				schemaVersion = 1,
				available = state.IsAvailable,
				blockedBy = Text(state.BlockedBy),
				consolePath = Text(state.ConsolePath),
				lookedIn = state.LookedIn,
				hasAccount = state.HasAccount,
				accountFile = state.AccountFile,
				hardwareId = Text(state.HardwareId),
				allowedProducts = state.AllowedProducts,
				installRoot = state.InstallRoot,
				whatThisMeans = Explain(state),
			};
		});

	/// <summary>
	/// Lists the products the StockSharp store offers.
	/// </summary>
	/// <param name="guard">Turns a failure into the published error shape.</param>
	/// <param name="installer">Drives the installer console.</param>
	/// <param name="options">How this instance was started.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <param name="search">Phrase a product name must contain.</param>
	/// <returns>The products, and every line that could not be read as one.</returns>
	[McpServerTool(Name = "list_products")]
	[Description("List the StockSharp products the store offers, with the numeric id each one is " +
		"installed by. Needs the network and a StockSharp account on this machine even though it sounds " +
		"local: the installer reloads its whole product catalogue from the web on every call. The " +
		"program behind this has no machine-readable output, so an entry it printed in a shape this " +
		"server does not recognise comes back in 'unparsed' as raw text rather than being dropped.")]
	public static Task<object> ListProducts(
		ToolGuard guard,
		IProductInstaller installer,
		ServerOptions options,
		CancellationToken cancellationToken,
		[Description("Phrase a product name must contain. Leave empty for all of them.")] string search = "")
		=> guard.RunAsync(nameof(ListProducts), async () =>
		{
			AssertNotHosted(options, "Listing products runs the installer on this machine");

			return Listing(await installer.ListAsync(search ?? string.Empty, cancellationToken));
		});

	/// <summary>
	/// Lists the products already installed on this machine.
	/// </summary>
	/// <param name="guard">Turns a failure into the published error shape.</param>
	/// <param name="installer">Drives the installer console.</param>
	/// <param name="options">How this instance was started.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <param name="search">Phrase a product name must contain.</param>
	/// <returns>The products, and every line that could not be read as one.</returns>
	[McpServerTool(Name = "list_installed_products")]
	[Description("List the StockSharp products already installed on this machine, with the directory " +
		"each one is in. Reads the installer's own registry of installations, which covers products this " +
		"server never installed. Needs the network and a StockSharp account for the same reason " +
		"list_products does, and reports the same 'unparsed' lines for anything it could not read.")]
	public static Task<object> ListInstalledProducts(
		ToolGuard guard,
		IProductInstaller installer,
		ServerOptions options,
		CancellationToken cancellationToken,
		[Description("Phrase a product name must contain. Leave empty for all of them.")] string search = "")
		=> guard.RunAsync(nameof(ListInstalledProducts), async () =>
		{
			AssertNotHosted(options, "Listing installed products runs the installer on this machine");

			return Listing(await installer.InstalledAsync(search ?? string.Empty, cancellationToken));
		});

	/// <summary>
	/// Installs one product.
	/// </summary>
	/// <param name="guard">Turns a failure into the published error shape.</param>
	/// <param name="installer">Drives the installer console.</param>
	/// <param name="options">How this instance was started.</param>
	/// <param name="productId">Numeric identifier of the product.</param>
	/// <param name="reinstall">Whether an installed product is removed first.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>What the invocation came to.</returns>
	[McpServerTool(Name = "install_product")]
	[Description("Download and install one StockSharp product on this machine. Takes the numeric product " +
		"id list_products reports and nothing else: where it lands is derived from this server's own " +
		"projects root and is not something a caller chooses. Only the product ids the operator allowed " +
		"can be installed, and the refusal names the ones that can. Set reinstall true to remove an " +
		"already installed copy first and put it back - the two behaviours differ enough to be asked " +
		"about rather than defaulted. This downloads packages and can take many minutes; it does not " +
		"start the product afterwards.")]
	public static Task<object> InstallProduct(
		ToolGuard guard,
		IProductInstaller installer,
		ServerOptions options,
		[Description("Numeric product id, as list_products reports it.")] long productId,
		[Description("True to remove an already installed copy first, false to install alongside what is there.")] bool reinstall,
		CancellationToken cancellationToken)
		=> guard.RunAsync(nameof(InstallProduct), async () =>
		{
			AssertNotHosted(options, "Installing a product puts code on the machine this server runs on");

			return Change(productId, await installer.InstallAsync(productId, reinstall, cancellationToken));
		});

	/// <summary>
	/// Updates one installed product.
	/// </summary>
	/// <param name="guard">Turns a failure into the published error shape.</param>
	/// <param name="installer">Drives the installer console.</param>
	/// <param name="options">How this instance was started.</param>
	/// <param name="productId">Numeric identifier of the product.</param>
	/// <param name="backupSettings">Whether the product's settings are copied aside first.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>What the invocation came to.</returns>
	[McpServerTool(Name = "update_product")]
	[Description("Update one installed StockSharp product to the newest version the store offers. Note " +
		"the side effect before calling it: the installer closes a running copy of the product first, so " +
		"updating something a person has open in front of them will shut it. Set backupSettings true to " +
		"copy that product's settings aside before the update. Only the product ids the operator allowed " +
		"can be updated.")]
	public static Task<object> UpdateProduct(
		ToolGuard guard,
		IProductInstaller installer,
		ServerOptions options,
		[Description("Numeric product id, as list_installed_products reports it.")] long productId,
		[Description("True to copy the product's settings aside before updating it.")] bool backupSettings,
		CancellationToken cancellationToken)
		=> guard.RunAsync(nameof(UpdateProduct), async () =>
		{
			AssertNotHosted(options, "Updating a product changes code on the machine this server runs on");

			return Change(productId, await installer.UpdateAsync(productId, backupSettings, cancellationToken));
		});

	/// <summary>
	/// Removes one installed product.
	/// </summary>
	/// <param name="guard">Turns a failure into the published error shape.</param>
	/// <param name="installer">Drives the installer console.</param>
	/// <param name="options">How this instance was started.</param>
	/// <param name="productId">Numeric identifier of the product.</param>
	/// <param name="removeData">Whether what the product wrote goes with it.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>What the invocation came to.</returns>
	[McpServerTool(Name = "remove_product")]
	[Description("Remove one installed StockSharp product from this machine. removeData decides what " +
		"happens to that product's own application data - the settings, schemas and logs it wrote, which " +
		"live outside its install directory and are not recoverable once removed. There is no safe " +
		"default for that, which is why the tool asks rather than choosing. Only the product ids the " +
		"operator allowed can be removed.")]
	public static Task<object> RemoveProduct(
		ToolGuard guard,
		IProductInstaller installer,
		ServerOptions options,
		[Description("Numeric product id, as list_installed_products reports it.")] long productId,
		[Description("True to delete the settings, schemas and logs the product wrote. This cannot be undone.")] bool removeData,
		CancellationToken cancellationToken)
		=> guard.RunAsync(nameof(RemoveProduct), async () =>
		{
			AssertNotHosted(options, "Removing a product changes what is on the machine this server runs on");

			return Change(productId, await installer.RemoveAsync(productId, removeData, cancellationToken));
		});

	/// <summary>
	/// Refuses a call that would run the installer in a server that answers a stranger.
	/// </summary>
	/// <param name="options">How this instance was started.</param>
	/// <param name="what">What the caller asked for, and what doing it amounts to.</param>
	/// <exception cref="ServerModeException">This instance is hosted.</exception>
	private static void AssertNotHosted(ServerOptions options, string what)
	{
		if (options.Mode != ServerModes.Hosted)
			return;

		throw new ServerModeException(
			$"{what}, which a hosted instance does not allow. It is the same rule that refuses the " +
			"connector tools there, one word wider: a stranger does not get to choose which code this " +
			"process runs, and still less which code lands on the operator's disk.");
	}

	private static object Listing(ProductOutcome outcome)
		=> new
		{
			schemaVersion = 1,
			succeeded = outcome.Succeeded,
			exitCode = outcome.ExitCode,
			products = outcome.Products.Select(Describe).ToArray(),

			// Everything the program printed that was not a product entry. For a listing that worked
			// this is progress chatter; for one that failed it is the whole of the explanation, which is
			// why it comes back rather than being swallowed.
			unparsed = InstallerOutput.Tail(outcome.Unparsed, ConsoleProductInstaller.TailLines),
			unparsedLines = outcome.Unparsed.Count,
			logPath = Text(outcome.LogPath),
			tookSeconds = Math.Round(outcome.Took.TotalSeconds, 1),
		};

	private static object Change(long productId, ProductOutcome outcome)
		=> new
		{
			schemaVersion = 1,
			succeeded = outcome.Succeeded,
			productId,
			exitCode = outcome.ExitCode,

			// The installer's own account of what it did, ending with whatever it said last. The whole
			// of it is in the file logPath names; this is the end, which is where a failure explains
			// itself.
			output = InstallerOutput.Tail(outcome.Unparsed, ConsoleProductInstaller.TailLines),
			outputLines = outcome.Unparsed.Count,
			logPath = Text(outcome.LogPath),
			tookSeconds = Math.Round(outcome.Took.TotalSeconds, 1),
			whatThisMeans = outcome.Succeeded
				? "The installer reported success. Nothing was started: this server installs software and " +
					"does not run it."
				: "The installer failed. It exits through one code for almost everything that can go " +
					"wrong, so the exit code says little and the output above is the explanation. Nothing " +
					"here can tell you whether the product directory was left partly written; " +
					"list_installed_products reports what the installer believes is installed.",
		};

	private static object Describe(Product product)
		=> new
		{
			productId = product.Id,
			packageId = product.PackageId,
			name = product.Name,
			contentType = product.ContentType,
			installed = product.IsInstalled,
			installedIn = Text(product.InstalledIn),
			updateAvailable = Text(product.Updates),
		};

	private static string Explain(ProductInstallerState state)
		=> state.BlockedBy switch
		{
			ProductInstallerState.NoConsole =>
				"The StockSharp installer console is not on this machine, which is the ordinary case " +
				"rather than a fault: this server never downloads it. No product can be listed or " +
				"installed until an operator puts it there. Everything else this server does - datasets, " +
				"candidates, backtests, the closed-data measurement - is unaffected.",

			ProductInstallerState.NoneAllowed =>
				"The installer is here, but the operator allowed no product identifiers, so nothing may " +
				"be listed or installed. This is the default: no part of the research loop needs a " +
				"product, so the surface is off until somebody turns it on for named products.",

			ProductInstallerState.NoAccount =>
				"The installer is here and products are allowed, but this machine has no StockSharp " +
				"account signed in. The installer reads it from the file named above and there is no way " +
				"to pass one from here; without it every product call would stop and wait for an email " +
				"address to be typed, so they are refused instead.",

			_ =>
				"Products can be installed. Only the identifiers listed above may be touched, and each " +
				"one lands under the install root named above. Every call needs the network, including " +
				"the two listings.",
		};

	private static string Text(string value)
		=> string.IsNullOrEmpty(value) ? null : value;
}
