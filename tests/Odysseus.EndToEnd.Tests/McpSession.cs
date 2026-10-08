namespace StockSharp.Odysseus.EndToEnd.Tests;

using System.Diagnostics;
using System.Text;
using System.Threading;

/// <summary>
/// A client that speaks the protocol to a real server process.
/// </summary>
/// <remarks>
/// Deliberately hand-written rather than built on the client library: these tests exist to check what
/// actually travels over the wire, and a client from the same family as the server would hide exactly
/// the mistakes worth catching.
/// </remarks>
public sealed class McpSession : IDisposable
{
	private readonly Process _process;
	private readonly SemaphoreSlim _gate = new(1, 1);

	private int _id;

	/// <summary>The identity received in the initialization response.</summary>
	public JsonElement ServerInfo { get; private set; }

	private McpSession(Process process)
	{
		_process = process;
	}

	/// <summary>
	/// Finds the built server executable.
	/// </summary>
	/// <returns>Its path, or <see langword="null"/> when it has not been built.</returns>
	public static string FindServer()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);

		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Odysseus.slnx")))
			directory = directory.Parent;

		if (directory is null)
			return null;

		// The server is built into the same configuration and framework as the test that looks for it.
		var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
			? "Debug"
			: "Release";

		var candidate = Path.Combine(
			directory.FullName, "src", "Odysseus.Server", "bin", configuration, "net10.0",
			OperatingSystem.IsWindows() ? "Odysseus.Server.exe" : "Odysseus.Server");

		return File.Exists(candidate) ? candidate : null;
	}

	/// <summary>
	/// Starts a server and completes the handshake.
	/// </summary>
	/// <param name="executable">Path of the server executable.</param>
	/// <param name="projectsRoot">Directory the server keeps its projects in.</param>
	/// <returns>The session.</returns>
	public static McpSession Start(string executable, string projectsRoot)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(executable);
		ArgumentException.ThrowIfNullOrWhiteSpace(projectsRoot);

		var info = new ProcessStartInfo(executable)
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			StandardOutputEncoding = Encoding.UTF8,
			StandardInputEncoding = Encoding.UTF8,
		};

		info.Environment["ODYSSEUS_PROJECTS_ROOT"] = projectsRoot;

		// Whatever the machine running the suite happens to have configured, the server under test has no
		// broker. These tests are about what this product does; a developer's own keys and a connector
		// downloaded from a gallery would make them pass or fail differently on two machines, and the
		// broker paths are covered where an account is actually required.
		info.Environment.Remove("ODYSSEUS_BROKER_KEYS");
		info.Environment.Remove("ODYSSEUS_BROKER_CONNECTOR");

		// The same for the product half. A developer who has the StockSharp installer on this machine and
		// has allowed a product would otherwise have these tests install software while they run, and a
		// machine without it would run different tests than a machine with it.
		info.Environment.Remove("ODYSSEUS_INSTALLER");
		info.Environment.Remove("ODYSSEUS_PRODUCT_ALLOW");

		var session = new McpSession(Process.Start(info));

		session.HandshakeAsync().GetAwaiter().GetResult();

		return session;
	}

	/// <summary>
	/// Lists the names of the published tools.
	/// </summary>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The names.</returns>
	public async Task<IReadOnlyCollection<string>> ListToolsAsync(CancellationToken cancellationToken)
	{
		var listed = await CallAsync("tools/list", new { }, cancellationToken);

		return [.. listed.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString())];
	}

	/// <summary>
	/// Calls a tool and parses the answer it produced.
	/// </summary>
	/// <param name="name">Tool to call.</param>
	/// <param name="arguments">Arguments for the tool.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The parsed answer.</returns>
	public async Task<JsonElement> ToolAsync(string name, object arguments, CancellationToken cancellationToken)
	{
		var result = await CallAsync("tools/call", new { name, arguments }, cancellationToken);
		var text = result.GetProperty("content")[0].GetProperty("text").GetString();

		return JsonDocument.Parse(text).RootElement.Clone();
	}

	/// <summary>
	/// Sends a request and waits for its answer.
	/// </summary>
	/// <param name="method">Method to call.</param>
	/// <param name="parameters">Parameters for the method.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The result of the call.</returns>
	public async Task<JsonElement> CallAsync(string method, object parameters, CancellationToken cancellationToken)
	{
		await _gate.WaitAsync(cancellationToken);

		try
		{
			var id = ++_id;

			await WriteAsync(new { jsonrpc = "2.0", id, method, @params = parameters }, cancellationToken);

			var line = await _process.StandardOutput.ReadLineAsync(cancellationToken)
				?? throw new InvalidOperationException("The server closed its output without answering.");

			var response = JsonDocument.Parse(line).RootElement;

			if (response.TryGetProperty("error", out var error))
				throw new InvalidOperationException($"The server refused '{method}': {error}");

			return response.GetProperty("result").Clone();
		}
		finally
		{
			_gate.Release();
		}
	}

	/// <inheritdoc />
	public void Dispose()
	{
		try
		{
			_process.StandardInput.Close();

			if (!_process.WaitForExit(2000))
				_process.Kill(entireProcessTree: true);
		}
		catch (InvalidOperationException)
		{
			// The process is already gone.
		}

		_process.Dispose();
		_gate.Dispose();
	}

	private async Task HandshakeAsync()
	{
		// A whole minute for one exchange, because the process on the other side is a real server on a
		// machine that may be building something else; the number is a deadlock guard, not a performance
		// assertion, and a tight one only turns load into a false failure.
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

		var initialized = await CallAsync("initialize", new
		{
			protocolVersion = "2025-06-18",
			capabilities = new { },
			clientInfo = new { name = "odysseus-e2e", version = "1.0.0" },
		}, timeout.Token);

		ServerInfo = initialized.GetProperty("serverInfo").Clone();

		await WriteAsync(new { jsonrpc = "2.0", method = "notifications/initialized" }, timeout.Token);
	}

	private async Task WriteAsync(object message, CancellationToken cancellationToken)
	{
		await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), cancellationToken);
		await _process.StandardInput.FlushAsync(cancellationToken);
	}
}
