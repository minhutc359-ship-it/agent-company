using System.Text.Json;

namespace AICompanyOS.Runtime;

public sealed record TeamDiscussionRequest(string RunId, string BranchId, string Question, ConsultationRequest[] Participants);
public sealed record TeamDiscussion(string DiscussionId, string RunId, string BranchId, string QuestionId, RoleBinding[] Participants, string[] JobIds, string Status, DateTimeOffset CreatedAt, int MaxOutputTokens);

public sealed partial class FileEventStore
{
    private static TeamDiscussion[] DiscussionsFrom(IEnumerable<DomainEvent> events) => events.Where(e => e.Type == "team.discussion.changed").Select(e => e.Data.Deserialize<TeamDiscussion>(JsonOptions)!).GroupBy(d => d.DiscussionId).Select(g => g.Last()).ToArray();
    public async Task<TeamDiscussion> StartTeamDiscussionAsync(string company, TeamDiscussionRequest request, LocalModel[] models, string key, CancellationToken ct)
    {
        if (!Identifiers.IsValid(request.RunId) || !Identifiers.IsValid(request.BranchId) || request.Participants is not { Length: >= 2 and <= 4 } || request.Participants.Any(p => p is null) || request.Participants.Select(p => p.AgentId).Distinct().Count() != request.Participants.Length ||
            string.IsNullOrWhiteSpace(request.Question) || request.Question.Length > 2000 || SourceSafety.ContainsKnownSecret(request.Question)) throw new ArgumentException("Chọn 2–4 nhân viên khác nhau và câu hỏi công khai tối đa 2000 ký tự.");
        await _gate.WaitAsync(ct);
        try
        {
            var events = await ReadEventsUnsafeAsync(company, 0, ct); RequireConversationRun(company, request.RunId, events); _ = BranchHistory(events, request.RunId, request.BranchId);
            var hash = RuntimeJson.Hash(request); var replay = events.FirstOrDefault(e => e.Type == "team.discussion.command" && ReadString(e.Data, "key") == key);
            if (replay is not null) return ReadString(replay.Data, "hash") == hash ? DiscussionsFrom(events).Single(d => d.DiscussionId == ReadString(replay.Data, "id")) : throw new WorkspaceConflictException("Discussion key belongs to another request.");
            if (DiscussionsFrom(events).Count(d => d.Status == "active") >= 2) throw new WorkspaceConflictException("Tối đa 2 hội ý tự động đang chạy trong company.");
            var catalog = await ReadEventsUnsafeAsync(CatalogCompany, 0, ct);
            var bindings = request.Participants.Select(p => ResolveConsultationBinding(company, p with { MeetingId = null, BranchId = request.BranchId }, models, catalog, events)).ToArray();
            var message = new PublicInteraction("message-" + Guid.NewGuid().ToString("N"), request.RunId, "consult", "owner", bindings.Select(b => b.Agent.AgentId).ToArray(), request.Question, [], [], null, "recorded", "team-" + key, DateTimeOffset.UtcNow, BranchId: request.BranchId);
            var pending = new List<DomainEvent> { DomainEvent.Create("interaction.created", message) };
            var previewEvents = new List<DomainEvent>(events) { DomainEvent.Create("interaction.created", message) };
            var snapshot = BuildConsultationSnapshot(company, previewEvents, message, null, bindings[0].Configuration.MaxInputBytes, text => System.Text.Encoding.UTF8.GetByteCount(BuildConsultationInput(bindings[0], text)));
            var job = BuildConsultationJob(company, message.InteractionId, null, bindings[0], snapshot);
            var discussion = new TeamDiscussion("team-" + Guid.NewGuid().ToString("N"), request.RunId, request.BranchId, message.InteractionId, bindings, [job.JobId], "active", DateTimeOffset.UtcNow, bindings.Sum(b => b.Configuration.MaxOutputTokens) * 2);
            pending.Add(DomainEvent.Create("consultation.changed", job)); pending.Add(DomainEvent.Create("team.discussion.changed", discussion)); pending.Add(DomainEvent.Create("team.discussion.command", new { key, hash, id = discussion.DiscussionId }));
            await AppendUnsafeAsync(company, request.RunId, pending, events, ct); return discussion;
        }
        finally { _gate.Release(); }
    }
    private async Task AdvanceTeamDiscussionUnsafeAsync(string company, string jobId, List<DomainEvent> events, CancellationToken ct)
    {
        var discussion = DiscussionsFrom(events).FirstOrDefault(d => d.Status == "active" && d.JobIds.LastOrDefault() == jobId); if (discussion is null) return;
        var job = ConsultationsFrom(events).Single(j => j.JobId == jobId); if (job.Status != "completed") return;
        if (discussion.JobIds.Length >= discussion.Participants.Length)
        { await AppendUnsafeAsync(company, discussion.RunId, [DomainEvent.Create("team.discussion.changed", discussion with { Status = "completed" })], events, ct); return; }
        var binding = discussion.Participants[discussion.JobIds.Length];
        try
        {
            var question = InteractionsFrom(events).Single(m => m.InteractionId == discussion.QuestionId);
            var priorReply = InteractionsFrom(events).Last(m => m.DedupeKey == "reply-" + jobId);
            var snapshot = BuildConsultationSnapshot(company, events, question, null, binding.Configuration.MaxInputBytes, text => System.Text.Encoding.UTF8.GetByteCount(BuildConsultationInput(binding, text)));
            var next = BuildConsultationJob(company, priorReply.InteractionId, null, binding, snapshot);
            await AppendUnsafeAsync(company, discussion.RunId, [DomainEvent.Create("consultation.changed", next), DomainEvent.Create("team.discussion.changed", discussion with { JobIds = [.. discussion.JobIds, next.JobId] })], events, ct);
        }
        catch (Exception error) when (error is ArgumentException or WorkspaceConflictException)
        { await AppendUnsafeAsync(company, discussion.RunId, [DomainEvent.Create("team.discussion.changed", discussion with { Status = "blocked" })], events, ct); }
    }
    public async Task<TeamDiscussion> CancelTeamDiscussionAsync(string company, string id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var events = await ReadEventsUnsafeAsync(company, 0, ct); var session = DiscussionsFrom(events).FirstOrDefault(d => d.DiscussionId == id) ?? throw new KeyNotFoundException();
            if (session.Status is "completed" or "cancelled") return session;
            var changed = session with { Status = "cancelled" }; var additions = new List<DomainEvent> { DomainEvent.Create("team.discussion.changed", changed) };
            foreach (var job in ConsultationsFrom(events).Where(j => session.JobIds.Contains(j.JobId) && j.Status is "queued" or "running")) additions.Add(DomainEvent.Create("consultation.changed", job with { Status = "cancelled", LeaseToken = null, LeaseUntil = null }));
            await AppendUnsafeAsync(company, session.RunId, additions, events, ct); return changed;
        }
        finally { _gate.Release(); }
    }
}
