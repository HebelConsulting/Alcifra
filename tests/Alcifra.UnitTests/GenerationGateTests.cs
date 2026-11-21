using System.Diagnostics;
using Alcifra.BrandIcons;

namespace Alcifra.UnitTests;

// The brand-icon generation gate (#2392): one generation per output directory, and the completed-marker that lets
// the loser of the race do nothing. A clean mirror build reddened when two inner MSBuild calls generated into the same
// folder in parallel; these tests pin the guarantee without the rendering stack.
public sealed class GenerationGateTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"brand-icons-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_second_acquirer_waits_until_the_first_releases()
    {
        using var first = GenerationGate.Acquire(_directory);

        var started = new Stopwatch();
        var second = Task.Run(() =>
        {
            started.Start();
            using var gate = GenerationGate.Acquire(_directory, TimeSpan.FromSeconds(20));
            started.Stop();
            return gate.IsCurrent([]);
        });

        // Long enough that a broken gate would have returned already, short enough not to slow the suite.
        await Task.Delay(1200);
        Assert.False(second.IsCompleted, "the second acquirer must block while the first holds the gate");

        first.Dispose();
        await second.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(started.Elapsed >= TimeSpan.FromSeconds(1),
            $"the second acquirer returned after only {started.Elapsed} — it did not wait for the release");
    }

    [Fact]
    public void Acquiring_a_gate_another_process_holds_times_out_with_a_named_refusal()
    {
        using var held = GenerationGate.Acquire(_directory);
        var started = Stopwatch.StartNew();

        var refused = Assert.Throws<TimeoutException>(
            () => GenerationGate.Acquire(_directory, TimeSpan.FromMilliseconds(300)));

        Assert.Contains("generation lock", refused.Message, StringComparison.Ordinal);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(30), $"the refusal took {started.Elapsed}");
    }

    [Fact]
    public void A_generation_is_current_only_after_its_marker_and_only_while_no_input_is_newer()
    {
        var input = Path.Combine(_directory, "assets");
        Directory.CreateDirectory(input);
        var asset = Path.Combine(input, "icon.svg");
        File.WriteAllText(asset, "<svg/>");

        using (var gate = GenerationGate.Acquire(_directory))
        {
            Assert.False(gate.IsCurrent([asset]), "without a marker nothing is current");

            gate.MarkComplete();
            Assert.True(gate.IsCurrent([asset]));
            Assert.True(gate.IsCurrent([asset, Path.Combine(_directory, "absent-input.dll")]),
                "a missing input cannot be newer than the marker");

            File.SetLastWriteTimeUtc(asset, DateTime.UtcNow.AddMinutes(1));
            Assert.False(gate.IsCurrent([asset]), "an input newer than the marker makes the generation stale");
        }
    }

    [Fact]
    public void The_marker_lives_in_the_output_directory_and_survives_a_reacquire()
    {
        using (var gate = GenerationGate.Acquire(_directory))
        {
            gate.MarkComplete();
        }

        Assert.True(File.Exists(GenerationGate.MarkerPathOf(_directory)));
        using var again = GenerationGate.Acquire(_directory);
        Assert.True(again.IsCurrent([]), "a released gate is taken again and the completed marker still counts");
    }

    // #2401: "current" has TWO halves — new-enough (the marker against the inputs) and WHOLE. The marker alone was
    // trusted once, and a build skipped on it while icon.png was absent; the writer now asks the file set too.
    [Fact]
    public void FirstMissing_names_the_file_that_makes_a_directory_incomplete()
    {
        Directory.CreateDirectory(_directory);
        var present = Path.Combine(_directory, "icon.png");
        var absent = Path.Combine(_directory, "web", "favicon.ico");
        File.WriteAllBytes(present, []);

        Assert.Equal(absent, GenerationGate.FirstMissing([present, absent]));
        Assert.Null(GenerationGate.FirstMissing([present]));

        Directory.CreateDirectory(Path.GetDirectoryName(absent)!);
        File.WriteAllBytes(absent, []);
        Assert.Null(GenerationGate.FirstMissing([present, absent]));
    }

    [Fact]
    public void The_writer_expects_every_file_a_generation_writes()
    {
        var expected = IconWriter.ExpectedOutputs(_directory).Select(Path.GetFileName).ToList();
        Assert.Contains("icon.png", expected);
        Assert.Contains("icon-1024.png", expected);
        Assert.Contains("app.ico", expected);
        Assert.Contains("app.icns", expected);
        Assert.Contains("favicon.png", expected);
        Assert.Contains("favicon.ico", expected);

        // Nothing is expected on a fresh directory.
        Assert.NotNull(GenerationGate.FirstMissing(IconWriter.ExpectedOutputs(_directory)));
    }
}
