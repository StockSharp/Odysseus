namespace Odysseus.Platform;

using System.IO;

using Ecng.IO;

/// <summary>
/// A file system that reads what is there and refuses to change it.
/// </summary>
/// <remarks>
/// A market-data drive is a directory the engine is designed to append to: saving into one adds a day
/// beside the days already in it. A run only reads, and the storage it reads is shared by every project.
///
/// Every run opens the storage through this, so a run cannot change what the next one reads, rather than
/// that being left to whoever remembers not to write. The refusal is an
/// <see cref="UnauthorizedAccessException"/> because that is what a read-only data folder raises, which
/// is a case the engine already expects and handles when it keeps its own caches.
/// </remarks>
internal sealed class ReadOnlyFileSystem : IFileSystem
{
	private readonly IFileSystem _inner;

	/// <summary>
	/// Creates the wrapper.
	/// </summary>
	/// <param name="inner">File system to read through.</param>
	public ReadOnlyFileSystem(IFileSystem inner)
	{
		_inner = inner ?? throw new ArgumentNullException(nameof(inner));
	}

	/// <inheritdoc />
	public long MaxSize
	{
		get => _inner.MaxSize;
		set => throw Refused(nameof(MaxSize));
	}

	/// <inheritdoc />
	public FileSystemOverflowBehavior OverflowBehavior
	{
		get => _inner.OverflowBehavior;
		set => throw Refused(nameof(OverflowBehavior));
	}

	/// <inheritdoc />
	public long TotalSize => _inner.TotalSize;

	/// <inheritdoc />
	public bool FileExists(string path) => _inner.FileExists(path);

	/// <inheritdoc />
	public bool DirectoryExists(string path) => _inner.DirectoryExists(path);

	/// <inheritdoc />
	public Stream Open(string path, FileMode mode, FileAccess access = FileAccess.ReadWrite, FileShare share = FileShare.None)
	{
		if (mode != FileMode.Open || access != FileAccess.Read)
			throw Refused(path);

		return _inner.Open(path, mode, access, share);
	}

	/// <inheritdoc />
	public void CreateDirectory(string path) => throw Refused(path);

	/// <inheritdoc />
	public void DeleteDirectory(string path, bool recursive = false) => throw Refused(path);

	/// <inheritdoc />
	public void DeleteFile(string path) => throw Refused(path);

	/// <inheritdoc />
	public void MoveFile(string sourceFileName, string destFileName, bool overwrite = false) => throw Refused(destFileName);

	/// <inheritdoc />
	public void MoveDirectory(string sourceDirName, string destDirName) => throw Refused(destDirName);

	/// <inheritdoc />
	public void CopyFile(string sourceFileName, string destFileName, bool overwrite = false) => throw Refused(destFileName);

	/// <inheritdoc />
	public IEnumerable<string> EnumerateFiles(string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly)
		=> _inner.EnumerateFiles(path, searchPattern, searchOption);

	/// <inheritdoc />
	public IEnumerable<string> EnumerateDirectories(string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly)
		=> _inner.EnumerateDirectories(path, searchPattern, searchOption);

	/// <inheritdoc />
	public DateTime GetCreationTimeUtc(string path) => _inner.GetCreationTimeUtc(path);

	/// <inheritdoc />
	public DateTime GetLastWriteTimeUtc(string path) => _inner.GetLastWriteTimeUtc(path);

	/// <inheritdoc />
	public long GetFileLength(string path) => _inner.GetFileLength(path);

	/// <inheritdoc />
	public void SetReadOnly(string path, bool isReadOnly) => throw Refused(path);

	/// <inheritdoc />
	public FileAttributes GetAttributes(string path) => _inner.GetAttributes(path);

	private static UnauthorizedAccessException Refused(string what)
		=> new($"'{what}' belongs to the market-data storage, which a run opens for reading and never for writing.");
}
