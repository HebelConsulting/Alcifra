using Microsoft.Extensions.Logging.Abstractions;
using Alcifra.Infrastructure.Intray;

namespace Alcifra.IntegrationTests;

// #2221: an item deleted while the ingest pipeline is working on it. The delete removes the item and then sweeps its
// derived files; the pipeline, still running, wrote its "already processed" marker AFTER that sweep, and the marker stayed
// behind as an orphan the intray could never clean up. The pipeline now checks for its item after writing a marker.
public class IntrayIngestDeleteRaceTests
{
    private const string Prefix = "tenants/t/users/u/inbox/";

    // Stands in for the person deleting the item while a processor reads it: the delete and its sweep happen here, in
    // the middle of the run, which is the window the full end-to-end leg hit.
    private sealed class DeletedWhileProcessing(InMemoryObjectStorage storage) : IIntrayIngestProcessor
    {
        public string Name => "deleted-meanwhile";

        public Task<IntrayProcessed?> TryProcessAsync(IntrayIngestContext context, CancellationToken cancellationToken)
        {
            foreach (var key in storage.Objects.Keys.Where(k => k.StartsWith(Prefix + context.Name, StringComparison.Ordinal)).ToList())
            {
                storage.Objects.Remove(key);
            }

            return Task.FromResult<IntrayProcessed?>(null);
        }
    }

    [Fact]
    public async Task An_item_deleted_while_it_is_processed_leaves_no_marker_behind()
    {
        var storage = new InMemoryObjectStorage();
        storage.Objects[Prefix + "report.csv"] = "name,amount\n"u8.ToArray();
        var pipeline = new IntrayIngestPipeline(storage, [new DeletedWhileProcessing(storage)], NullLogger<IntrayIngestPipeline>.Instance);

        await pipeline.RunAsync(Guid.NewGuid(), Guid.NewGuid(), Prefix, "report.csv");

        Assert.DoesNotContain(storage.Objects.Keys, k => k.StartsWith(Prefix, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_item_still_there_keeps_its_marker()
    {
        var storage = new InMemoryObjectStorage();
        storage.Objects[Prefix + "report.csv"] = "name,amount\n"u8.ToArray();
        var pipeline = new IntrayIngestPipeline(storage, [], NullLogger<IntrayIngestPipeline>.Instance);

        await pipeline.RunAsync(Guid.NewGuid(), Guid.NewGuid(), Prefix, "report.csv");

        Assert.Contains(Prefix + "report.csv" + IntrayIngestPipeline.MarkerSuffix, storage.Objects.Keys);
    }
}
