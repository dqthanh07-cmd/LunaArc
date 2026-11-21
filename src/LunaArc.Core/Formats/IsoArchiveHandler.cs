using System.Buffers;
using DiscUtils;
using DiscUtils.Iso9660;
using DiscUtils.Udf;
using LunaArc.Core.Contracts;
using LunaArc.Core.Pipeline;
using LunaArc.Core.Security;
using LunaArc.Core.VfsIndex;

namespace LunaArc.Core.Formats;

/// <summary>Reads ISO 9660/Joliet and UDF optical-disc images.</summary>
internal static class IsoArchiveHandler
{
    private const int MaxEntries = 1_000_000;

    public static bool Handles(string path) => path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase);

    public static Task<FlatVfsArena> ReadCatalogAsync(string path, CancellationToken ct, LunaJobController? job = null) => Task.Run(() =>
    {
        using var image = Open(path);
        var arena = new FlatVfsArena();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<DiscDirectoryInfo>();
        pending.Push(image.FileSystem.Root);
        job?.Report("Reading ISO metadata");

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            job?.WaitIfPaused();
            DiscDirectoryInfo directory = pending.Pop();
            foreach (DiscDirectoryInfo subdirectory in directory.GetDirectories())
            {
                ct.ThrowIfCancellationRequested();
                RejectReparsePoint(subdirectory.Attributes, subdirectory.FullName);
                string relativePath = GetRelativePath(subdirectory.FullName);
                ValidateRelativePath(relativePath);
                if (!visited.Add(relativePath)) continue;
                EnsureEntryLimit(visited.Count, arena.Count);
                arena.AddEntry(relativePath, 0, 0, 0, subdirectory.LastWriteTimeUtc, isDir: true, isEncrypted: false);
                pending.Push(subdirectory);
            }

            foreach (DiscFileInfo file in directory.GetFiles())
            {
                ct.ThrowIfCancellationRequested();
                RejectReparsePoint(file.Attributes, file.FullName);
                string relativePath = GetRelativePath(file.FullName);
                ValidateRelativePath(relativePath);
                if (!visited.Add(relativePath)) throw new InvalidDataException($"Duplicate ISO path: {relativePath}");
                EnsureEntryLimit(visited.Count, arena.Count);
                if (file.Length < 0) throw new InvalidDataException($"Invalid ISO file length: {relativePath}");
                arena.AddEntry(relativePath, file.Length, 0, 0, file.LastWriteTimeUtc, isDir: false, isEncrypted: false);
            }
        }

        return arena;
    }, ct);

    public static async Task ExtractAsync(
        string path,
        FlatVfsArena catalog,
        ExtractOptions options,
        LunaJobController job,
        Action<long, long, string>? progress)
    {
        using var image = Open(path);
        long total = catalog.TotalUncompressedBytes;
        long processed = 0;
        var directories = new List<string>();
        for (int i = 0; i < catalog.Count; i++)
        {
            var node = catalog.GetNode(i);
            string key = catalog.GetFullPath(i);
            if (node.IsDirectory)
            {
                if (options.SelectedEntries is not { Count: > 0 } selected || selected.Contains(key)) directories.Add(key);
            }
        }

        foreach (string key in directories)
        {
            job.WaitIfPaused();
            job.Token.ThrowIfCancellationRequested();
            string target = ZipSlipGuard.ValidateAndResolvePath(options.DestinationDirectory, key);
            if (options.UpdateMode != ExtractionUpdateMode.FreshenExisting) Directory.CreateDirectory(target);
        }

        for (int i = 0; i < catalog.Count; i++)
        {
            job.WaitIfPaused();
            job.Token.ThrowIfCancellationRequested();
            var node = catalog.GetNode(i);
            if (node.IsDirectory) continue;

            string key = catalog.GetFullPath(i);
            if (options.SelectedEntries is { Count: > 0 } selected && !selected.Contains(key)) continue;
            string target = ZipSlipGuard.ValidateAndResolvePath(options.DestinationDirectory, key);
            DateTime? modified = node.LastWriteUnixTime > 0
                ? DateTimeOffset.FromUnixTimeSeconds(node.LastWriteUnixTime).UtcDateTime
                : null;
            if (!ExtractionUpdatePolicy.ShouldExtract(target, modified, options.UpdateMode)) continue;

            if (File.Exists(target))
            {
                OverwriteMode mode = options.OverwriteMode == OverwriteMode.Ask && options.OnFileConflictCallback != null
                    ? options.OnFileConflictCallback(key)
                    : options.OverwriteMode;
                if (mode == OverwriteMode.Ask) throw new IOException($"Overwrite confirmation required: {key}");
                if (mode == OverwriteMode.SkipExisting) continue;
                if (mode == OverwriteMode.UpdateNewer && modified.HasValue && File.GetLastWriteTimeUtc(target) >= modified.Value) continue;
                if (mode == OverwriteMode.AutoRename) target = ZipSlipGuard.GetAutoRenamedPath(target);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string imagePath = ToImagePath(key);
            await using var input = image.FileSystem.OpenFile(imagePath, FileMode.Open, FileAccess.Read);
            await AtomicExtractFile.WriteAsync(target, options.KeepBrokenFiles, job.Token, async output =>
            {
                await CopyExactAsync(input, output, node.UncompressedSize, job,
                    current => progress?.Invoke(processed + current, Math.Max(1, total), key));
            });

            if (modified.HasValue) File.SetLastWriteTimeUtc(target, modified.Value);
            processed = checked(processed + node.UncompressedSize);
            progress?.Invoke(processed, Math.Max(1, total), key);
        }
    }

    public static async Task<ArchiveTestResult> TestAsync(
        string path,
        LunaJobController job,
        Action<int, int, string>? progress)
    {
        int total = 0;
        int passed = 0;
        var broken = new List<string>();
        try
        {
            using var image = Open(path);
            var files = EnumerateFiles(image.FileSystem, job.Token, job);
            total = files.Count;
            for (int i = 0; i < files.Count; i++)
            {
                job.WaitIfPaused();
                job.Token.ThrowIfCancellationRequested();
                var file = files[i];
                string key = GetRelativePath(file.FullName);
                try
                {
                    await using var input = image.FileSystem.OpenFile(file.FullName, FileMode.Open, FileAccess.Read);
                    using var output = new VerificationWriteStream(job, file.Length);
                    await LunaStreamPipeline.CopyAndHashChunkedAsync(input, output, file.Length, job);
                    if (output.Written != file.Length) throw new InvalidDataException($"Size mismatch in ISO file '{key}'.");
                    passed++;
                }
                catch (OperationCanceledException) { throw; }
                catch { broken.Add(key); }
                progress?.Invoke(i + 1, Math.Max(1, total), key);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            broken.Add(ex.Message);
            return new ArchiveTestResult(Math.Max(1, total), passed, broken.Count, broken);
        }

        return new ArchiveTestResult(total, passed, broken.Count, broken);
    }

    private static List<DiscFileInfo> EnumerateFiles(IFileSystem fileSystem, CancellationToken ct, LunaJobController job)
    {
        var files = new List<DiscFileInfo>();
        var pending = new Stack<DiscDirectoryInfo>();
        pending.Push(fileSystem.Root);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            job.WaitIfPaused();
            DiscDirectoryInfo directory = pending.Pop();
            foreach (DiscDirectoryInfo subdirectory in directory.GetDirectories())
            {
                RejectReparsePoint(subdirectory.Attributes, subdirectory.FullName);
                string key = GetRelativePath(subdirectory.FullName);
                ValidateRelativePath(key);
                if (visited.Add(key)) pending.Push(subdirectory);
            }
            foreach (DiscFileInfo file in directory.GetFiles())
            {
                RejectReparsePoint(file.Attributes, file.FullName);
                string key = GetRelativePath(file.FullName);
                ValidateRelativePath(key);
                if (visited.Add(key)) files.Add(file);
            }
            EnsureEntryLimit(visited.Count, files.Count);
        }
        return files;
    }

    private static IsoImage Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            stream.Position = 0;
            if (UdfReader.Detect(stream))
            {
                stream.Position = 0;
                return new IsoImage(stream, new UdfReader(stream));
            }

            stream.Position = 0;
            return new IsoImage(stream, new CDReader(stream, joliet: true, hideVersions: true));
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static string GetRelativePath(string path) => path.Trim('/', '\\').Replace('\\', '/');

    private static string ToImagePath(string path) => "\\" + path.Replace('/', '\\');

    private static async Task CopyExactAsync(
        Stream source,
        Stream destination,
        long expectedLength,
        LunaJobController job,
        Action<long>? onProgress)
    {
        if (expectedLength < 0) throw new InvalidDataException("Invalid ISO file length.");
        byte[] buffer = ArrayPool<byte>.Shared.Rent(LunaStreamPipeline.ChunkSize);
        long copied = 0;
        try
        {
            while (true)
            {
                job.WaitIfPaused();
                job.Token.ThrowIfCancellationRequested();
                int read = await source.ReadAsync(buffer.AsMemory(0, LunaStreamPipeline.ChunkSize), job.Token);
                if (read == 0) break;
                if (read > expectedLength - copied)
                    throw new InvalidDataException("ISO file exceeds its declared size.");
                await destination.WriteAsync(buffer.AsMemory(0, read), job.Token);
                copied += read;
                onProgress?.Invoke(copied);
            }
            if (copied != expectedLength) throw new InvalidDataException("ISO file is shorter than its declared size.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void RejectReparsePoint(FileAttributes attributes, string path)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new NotSupportedException($"ISO symbolic links are not extracted: {path}");
    }

    private static void ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.Contains(':') || path.Contains('\0') ||
            path.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException($"Unsafe ISO entry path: {path}");
    }

    private static void EnsureEntryLimit(int visitedCount, int catalogCount)
    {
        if (visitedCount > MaxEntries || catalogCount > MaxEntries)
            throw new InvalidDataException($"ISO image contains more than {MaxEntries:N0} entries.");
    }

    private sealed class IsoImage(FileStream stream, IFileSystem fileSystem) : IDisposable
    {
        public IFileSystem FileSystem { get; } = fileSystem;

        public void Dispose()
        {
            (FileSystem as IDisposable)?.Dispose();
            stream.Dispose();
        }
    }
}
