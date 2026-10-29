namespace Alcifra.ManualCapture;

public static class Paths
{
    // Walk up from the running binary to the repo root (the dir holding Alcifra.slnx).
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Alcifra.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repo root (Alcifra.slnx).");
    }
}
