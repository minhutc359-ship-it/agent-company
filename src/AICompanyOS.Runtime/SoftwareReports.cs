using System.Text.Json;

namespace AICompanyOS.Runtime;

public sealed partial class FileEventStore
{
    private static DomainEvent[] SoftwareReportEvents(SoftwareRun run, List<DomainEvent> events)
    {
        var sourceId = "report-" + run.Seed.RunId;
        var revision = SourcesFrom(events).Where(s => s.SourceId == sourceId).Select(s => s.Revision).DefaultIfEmpty(0).Max() + 1;
        var body = $"Software run {run.Seed.RunId}, round {run.Round}: {run.Report.Status}. Candidate: {run.Candidate?.Sha256 ?? "none"}. Quality gate: {Node(run, "gate").Status}. This records execution observations; Owner acceptance and rights review are separate gates.";
        var text = body + "\n" + JsonSerializer.Serialize(new { run.Report.ArtifactRefs, run.Report.ReceiptIds,
            checks = run.Receipts.Where(r => run.Report.ReceiptIds.Contains(r.ReceiptId)).Select(r => new { r.ReceiptId, r.Tool, r.Status, r.ExitCode, r.InputSha256, r.LogSha256 }) }, RuntimeJson.Options);
        var source = new SourceRevision(sourceId, revision, "Software delivery report: " + run.Seed.RunId, text, RuntimeJson.TextHash(text), DateTimeOffset.UtcNow);
        var proposal = new MemoryProposal("proposal-" + run.Seed.RunId + "-" + revision, "delivery-" + run.Seed.RunId, "fact", body,
            [new(sourceId, revision, 0, body.Length, source.ContentSha256)], "proposed");
        return [NodeEvent("subpm", "passed"), DomainEvent.Create("software.report.produced", run.Report), DomainEvent.Create("source.imported", source), DomainEvent.Create("memory.proposed", proposal)];
    }
}
