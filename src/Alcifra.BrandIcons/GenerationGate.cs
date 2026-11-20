namespace Alcifra.BrandIcons;

/// <summary>
/// One generation at a time per output directory (#2392): an exclusive lock held for the whole write, and the
/// completed-marker that lets the loser of a race do nothing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> MSBuild runs a multi-targeting consumer's <c>BeforeBuild</c> once per target framework, in
/// parallel, and each call runs the icon generator against the SAME <c>artifacts/brand-icons</c>. A clean mirror build
/// lost the race: one build's resource task read <c>icon.png</c> while the other's generation had not written it yet.
/// Under this gate the second caller WAITS until the first has finished every file, then finds the generation current
/// and returns.
/// </para>
/// <para>
/// A lock FILE with <see cref="FileShare.None"/> rather than a mutex: the contenders are separate processes (one per
/// inner MSBuild call), and a crashed process releases the lock with it — after which the marker is absent (it is
/// written LAST) and the next caller regenerates.
/// </para>
/// <para>
/// Deliberately free of anything from the rendering stack, so the unit tests can exercise the race without loading
/// the native rasteriser.
/// </para>
/// </remarks>
public sealed class GenerationGate : IDisposable
{
    /// <summary>The file a completed generation leaves; its timestamp is compared against the inputs.</summary>
    public const string MarkerName = ".complete";

    private readonly FileStream _lock;

    private GenerationGate(FileStream lockFile) => _lock = lockFile;

    /// <summary>The marker path for an output directory.</summary>
    public static string MarkerPathOf(string outputDirectory) => Path.Combine(outputDirectory, MarkerName);

    /// <summary>
    /// Takes the directory's exclusive generation lock, waiting for any generation in flight (up to
    /// <paramref name="timeout"/>, five minutes by default); throws a <see cref="TimeoutException"/> after that.
    /// </summary>
    public static GenerationGate Acquire(string outputDirectory, TimeSpan? timeout = null)
    {
        Directory.CreateDirectory(outputDirectory);
        var gate = Path.Combine(outputDirectory, ".generation.lock");
        var wait = timeout ?? TimeSpan.FromMinutes(5);
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            try
            {
                return new GenerationGate(new FileStream(gate, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            catch (IOException exception)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    throw new TimeoutException(
                        $"The brand-icon generation lock '{gate}' stayed held for more than {wait}, so no icons were "
                        + "written. Another generator is still running, or one died without releasing its lock file.",
                        exception);
                }

                Thread.Sleep(100);
            }
        }
    }

    /// <summary>
    /// Whether a completed generation in this directory is newer than every input path (missing inputs are ignored, a
    /// missing marker means no).
    /// </summary>
    public bool IsCurrent(IEnumerable<string> inputs)
    {
        var marker = MarkerPathOf(Path.GetDirectoryName(_lock.Name)!);
        if (!File.Exists(marker))
        {
            return false;
        }

        var done = File.GetLastWriteTimeUtc(marker);
        return inputs
            .Where(path => path.Length > 0 && File.Exists(path))
            .All(path => File.GetLastWriteTimeUtc(path) <= done);
    }

    /// <summary>Records the generation complete — written after EVERY file, so its presence means "the directory is whole".</summary>
    public void MarkComplete()
    {
        var directory = Path.GetDirectoryName(_lock.Name)!;
        var marker = MarkerPathOf(directory);
        var temp = Path.Combine(directory, $".{MarkerName}.{Environment.ProcessId}.tmp");
        try
        {
            File.WriteAllBytes(temp, []);
            File.Move(temp, marker, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try
                {
                    File.Delete(temp);
                }
                catch (IOException)
                {
                    // A leftover temp file is harmless; the next generation replaces the marker anyway.
                }
            }
        }
    }

    public void Dispose() => _lock.Dispose();
}
