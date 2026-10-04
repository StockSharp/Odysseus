namespace Odysseus.Engine;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Odysseus.Application;

/// <summary>What a worker was asked to do.</summary>
public enum WorkerCommands
{
	/// <summary>Run one backtest.</summary>
	Backtest,

	/// <summary>Search a candidate's declared numbers.</summary>
	Optimize,

	/// <summary>Search and test a candidate's declared numbers window by window.</summary>
	WalkForward,
}

/// <summary>
/// The first thing a worker says, before it is asked anything.
/// </summary>
/// <param name="Protocol">Version of this framing and of these records.</param>
/// <param name="Engine">Which build of the trading platform the worker carries.</param>
/// <param name="ProcessId">The worker's process, so a stuck one can be found from a log.</param>
public sealed record WorkerHello(int Protocol, string Engine, int ProcessId);

/// <summary>
/// One thing for the worker to do.
/// </summary>
/// <param name="Id">Identifier the answer carries back.</param>
/// <param name="Command">Which of the two it is.</param>
/// <param name="Backtest">The backtest, when that is what was asked for.</param>
/// <param name="Optimization">The search, when that is what was asked for.</param>
/// <param name="WalkForward">The walk-forward, when that is what was asked for.</param>
/// <param name="BatchSize">Settings a search evaluates at once, which is the host's policy and not the experiment's.</param>
public sealed record WorkerRequest(
	string Id,
	WorkerCommands Command,
	BacktestRequest Backtest,
	OptimizationRequest Optimization,
	WalkForwardRequest WalkForward,
	int BatchSize);

/// <summary>
/// What the worker made of it.
/// </summary>
/// <param name="Id">Identifier of the request this answers.</param>
/// <param name="Succeeded">Whether the candidate ran.</param>
/// <param name="Outcome">What the backtest did, when it did.</param>
/// <param name="Trials">Every setting the search evaluated, when that is what was asked for.</param>
/// <param name="WalkForward">What each window came to, when a walk-forward is what was asked for.</param>
/// <param name="Failure">Why the candidate failed, when it did.</param>
/// <remarks>
/// A failure here is the candidate's, not the worker's. The two are different answers and are carried
/// differently: this one is re-raised in the host as the same failure the in-process runner used to
/// throw, so a run is recorded exactly as it always was, while a worker that died or overran its limits
/// never gets as far as sending anything.
/// </remarks>
public sealed record WorkerAnswer(
	string Id,
	bool Succeeded,
	BacktestOutcome Outcome,
	IReadOnlyList<OptimizationTrial> Trials,
	IReadOnlyList<WalkForwardWindowResult> WalkForward,
	string Failure);

/// <summary>
/// How the host and the worker speak to each other.
/// </summary>
/// <remarks>
/// Length-prefixed JSON over the worker's own standard input and output. Not a named pipe: this is the
/// same shape the end-to-end tests already drive the server itself with, and stopping a worker is
/// killing its process, which also closes both pipes and ends the read the host is waiting on.
///
/// Everything that crosses is already data. The requests carry an assembly, some numbers and the bars of
/// one slice - about six hundred kilobytes for ninety days of five-minute candles - and the answers carry
/// trades and an equity curve, which this product already serialises with the same serializer. There is
/// no case here for shared memory.
/// </remarks>
public static class WorkerProtocol
{
	/// <summary>Version of the framing and of the records above.</summary>
	public const int Version = 2;

	/// <summary>
	/// Largest frame either side will accept, so a corrupt length cannot ask for an unbounded allocation.
	/// </summary>
	public const int MaximumFrameBytes = 64 * 1024 * 1024;

	/// <summary>How the records are written. Kept in one place so both ends cannot disagree about it.</summary>
	public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.General)
	{
		IncludeFields = false,
		PropertyNameCaseInsensitive = true,
	};

	/// <summary>
	/// Writes one frame.
	/// </summary>
	/// <typeparam name="T">Type of the message.</typeparam>
	/// <param name="stream">Where to write it.</param>
	/// <param name="message">The message.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(stream);

		var body = JsonSerializer.SerializeToUtf8Bytes(message, Json);

		if (body.Length > MaximumFrameBytes)
			throw new InvalidOperationException($"A frame of {body.Length} bytes is larger than this protocol carries.");

		var header = new byte[4];

		BinaryPrimitives.WriteInt32BigEndian(header, body.Length);

		await stream.WriteAsync(header, cancellationToken);
		await stream.WriteAsync(body, cancellationToken);
		await stream.FlushAsync(cancellationToken);
	}

	/// <summary>
	/// Reads one frame.
	/// </summary>
	/// <typeparam name="T">Type of the message.</typeparam>
	/// <param name="stream">Where to read it from.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The message, or <see langword="null"/> when the other end closed the pipe.</returns>
	public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
		where T : class
	{
		ArgumentNullException.ThrowIfNull(stream);

		var header = new byte[4];

		if (!await FillAsync(stream, header, cancellationToken))
			return null;

		var length = BinaryPrimitives.ReadInt32BigEndian(header);

		if (length < 0 || length > MaximumFrameBytes)
			throw new InvalidOperationException($"A frame announced {length} bytes, which this protocol does not carry.");

		var body = new byte[length];

		if (!await FillAsync(stream, body, cancellationToken))
			return null;

		return JsonSerializer.Deserialize<T>(body, Json);
	}

	private static async Task<bool> FillAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
	{
		var read = 0;

		while (read < buffer.Length)
		{
			var got = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken);

			if (got == 0)
				return false;

			read += got;
		}

		return true;
	}
}
