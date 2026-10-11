using GanttScheduler.Domain;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Services;

// 등록할 때 정하는 단계 목표일 한 줄 (단계 템플릿 Id 별)
public record StageTargetInput(int TemplateId, DateOnly? TargetDate);

// 단계 일정 표 저장: 바꾼 단계만 보낸다. Version 은 표를 연 때(또는 동시 수정 뒤 다시 맞춘 때)의 단계 버전
public record StageScheduleRow(int StageId, int? Version, DateOnly? TargetDate, DateOnly? CompletedDate);

public record StageScheduleInput(int ProjectId, List<StageScheduleRow>? Rows, string? Reason);

// 단계 일정 표 미리보기: 저장 전의 날짜로 다시 계산한 근무일 간격·추천 입고일 (화면이 계산하지 않도록 서버가 준다)
public record StagePlanRow(int StageId, DateOnly? TargetDate, DateOnly? CompletedDate);

// TargetNote: 목표일이 휴일이면 그 안내 (화면이 휴일을 판정하지 않도록 서버가 준다)
public record StagePlanView(int StageId, int? GapWorkDays, DateOnly? InboundHint, string? TargetNote = null);

// 등록 화면의 목표일 초안 한 줄. Kept: 사람이 고친 값(초안이 아님), Gap: 앞 단계 목표일(첫 단계는 착수일)부터의 근무일
public record StageDraftRow(int TemplateId, string Name, string Kind, int? DefaultWorkDays, DateOnly? TargetDate, bool Kept,
                            int? GapWorkDays, DateOnly? InboundHint);

// OceanLeadDays: 고른 법인의 해상 운송 소요일 (법인을 아직 고르지 않았으면 null — 추천 입고일을 보이지 않는다)
public record StageDraft(DateOnly StartDate, int? OceanLeadDays, List<StageDraftRow> Rows);

// 단계 일정 계획: 등록 화면의 목표일 초안, 상세의 단계 일정 표(한 번에 저장·미리보기)
public partial class ProjectService
{
    // 등록 화면에서 넘어온 단계 목표일 (같은 템플릿이 두 번 오면 나중 값, 빈 값은 '정하지 않음')
    private static Dictionary<int, DateOnly> InitialTargets(List<StageTargetInput>? rows)
    {
        var map = new Dictionary<int, DateOnly>();
        foreach (var r in rows ?? new())
        {
            if (r.TargetDate is { } d) map[r.TemplateId] = d;
            else map.Remove(r.TemplateId);
        }
        return map;
    }

    // ── 목표일 초안 (등록 화면) ─────────────────────────────────────────

    // 유형의 단계 템플릿과 착수일로 단계 목표일 초안을 만든다(Rules.DraftTargets). keep(템플릿 Id → 날짜, 빈 값 포함)은 사람이 고친 값이라
    // 그대로 두고 그 뒤 단계만 거기서 이어 센다. 법인을 골랐으면 입고 단계에 추천 입고일(검수일 + 해상 운송)을 함께 준다
    public async Task<StageDraft> DraftAsync(int categoryId, DateOnly? startDate, int? locationId, IReadOnlyDictionary<int, DateOnly?>? keep = null)
    {
        var category = await _db.Categories.AsNoTracking().Include(c => c.StageTemplates)
                           .FirstOrDefaultAsync(c => c.Id == categoryId)
            ?? throw new RuleException("과제 유형을 선택해 주세요.", "categoryId");
        var start = startDate ?? _clock.Today;
        Rules.ValidateStartDate(start, _clock.Today);
        var cal = await CalendarAsync();

        int? lead = null;
        if (locationId is int loc && loc > 0)
            lead = (await LocationsAsync()).CorporationOf(loc)?.OceanLeadDays;

        var templates = category.StageTemplates.OrderBy(t => t.SortOrder).ThenBy(t => t.Id).ToList();
        var fixedAt = new Dictionary<int, DateOnly?>();
        for (var i = 0; i < templates.Count; i++)
            if (keep != null && keep.TryGetValue(templates[i].Id, out var kept))
            {
                // 등록할 때와 같은 범위·문구로 미리 알린다 (field 가 초안 표의 그 칸을 가리킨다)
                Rules.ValidateDate(kept, start, $"'{templates[i].Name}' 목표일", $"stageTargets.{templates[i].Id}");
                fixedAt[i] = kept;
            }

        var targets = Rules.DraftTargets(start, templates.Select(t => t.DefaultWorkDays).ToList(), cal, fixedAt);
        var gaps = Rules.TargetGaps(start, targets, cal);
        var stages = templates.Select((t, i) => new Stage { Name = t.Name, Kind = t.Kind, SortOrder = i + 1, TargetDate = targets[i] }).ToList();
        var inbound = lead == null ? null : Rules.InboundRecommendation(stages, lead.Value);

        return new StageDraft(start, lead, templates.Select((t, i) => new StageDraftRow(
            t.Id, t.Name, Views.Key(t.Kind), t.DefaultWorkDays, targets[i], fixedAt.ContainsKey(i), gaps[i],
            t.Kind == StageKind.Inbound ? inbound : null)).ToList());
    }

    // ── 단계 일정 표 (상세) ─────────────────────────────────────────────

    // 저장 전의 날짜로 근무일 간격·추천 입고일을 다시 계산한다. rows 에 없는 단계는 저장된 값을 쓴다
    public async Task<List<StagePlanView>> SchedulePreviewAsync(int projectId, List<StagePlanRow>? rows)
    {
        var p = await _db.Projects.AsNoTracking().Include(x => x.Stages).FirstOrDefaultAsync(x => x.Id == projectId)
            ?? throw new RuleException("과제를 찾을 수 없습니다. 삭제되었을 수 있습니다.");
        var edits = (rows ?? new()).GroupBy(r => r.StageId).ToDictionary(g => g.Key, g => g.Last());
        // 저장할 때와 같은 범위 검사 — 바뀐 날짜만 본다(범위 밖으로 남은 옛 데이터가 미리보기를 막지 않게)
        foreach (var s in p.Stages)
            if (edits.TryGetValue(s.Id, out var e))
            {
                if (s.TargetDate != e.TargetDate) Rules.ValidateDate(e.TargetDate, p.StartDate, $"'{s.Name}' 목표일", $"targetDate.{s.Id}");
                if (s.CompletedDate != e.CompletedDate) Rules.ValidateDate(e.CompletedDate, p.StartDate, $"'{s.Name}' 완료일", $"completedDate.{s.Id}");
            }
        var stages = Rules.Ordered(p).Select(s => edits.TryGetValue(s.Id, out var e)
            ? new Stage { Id = s.Id, Name = s.Name, Kind = s.Kind, SortOrder = s.SortOrder, TargetDate = e.TargetDate, CompletedDate = e.CompletedDate }
            : s).ToList();

        var cal = await CalendarAsync();
        var lead = (await LocationsAsync()).Get(p.CorporationId)?.OceanLeadDays ?? 0;
        var gaps = Rules.TargetGaps(p.StartDate, stages.Select(s => s.TargetDate).ToList(), cal);
        var inbound = Rules.InboundRecommendation(stages, lead);
        var result = new List<StagePlanView>();
        for (var i = 0; i < stages.Count; i++)
            result.Add(new StagePlanView(stages[i].Id, gaps[i], stages[i].Kind == StageKind.Inbound && stages[i].CompletedDate == null ? inbound : null,
                await DayNoteAsync(stages[i].TargetDate, "목표일")));
        return result;
    }

    // 단계 일정 표 저장: 여러 단계의 목표일·완료일을 한 번에 바꾼다.
    // - 단계마다 화면이 본 버전을 확인하고(다른 사람이 먼저 고쳤으면 409), 바뀐 단계의 버전을 올린다.
    // - 이미 정한 목표일을 바꾸거나 지우는 단계가 하나라도 있으면 사유를 한 번 받는다. 처음 정하는 목표일은 사유 없이 '목표일 설정'.
    // - 완료는 단계 시트와 같은 규칙을 거친다(체크리스트가 남으면 거절, 완료를 취소하면 그 단계 작업을 선행에 맞춤, 과제 자동 완료·재개).
    // - 모든 변경(목표일·완료·작업 밀기)을 BatchId 하나로 묶는다. 검사를 모두 통과한 뒤에만 값을 바꾼다
    public async Task SaveStageScheduleAsync(StageScheduleInput input)
    {
        var reason = Text(input.Reason, Limits.Reason, "사유", "reason");
        var p = await RequireAsync(input.ProjectId);
        var rows = input.Rows ?? new();
        if (rows.Select(r => r.StageId).Distinct().Count() != rows.Count)
            throw new RuleException("같은 단계가 두 번 들어 있습니다. 화면을 새로고침해 주세요.");

        var changes = new List<(Stage Stage, StageScheduleRow Row)>();
        foreach (var r in rows)
        {
            // 화면을 연 뒤 다른 사람이 지운 단계 — 최신 내용으로 다시 맞춰 저장하게 한다
            var s = p.Stages.FirstOrDefault(x => x.Id == r.StageId) ?? throw new ConflictException(p.Id);
            CheckVersion(r.Version, s.Version, p.Id);
            if (s.TargetDate == r.TargetDate && s.CompletedDate == r.CompletedDate) continue;
            // 바뀐 날짜만 범위를 본다 (SaveStageAsync 와 같다). 오류는 표의 그 칸을 가리킨다
            if (s.TargetDate != r.TargetDate) Rules.ValidateDate(r.TargetDate, p.StartDate, $"'{s.Name}' 목표일", $"targetDate.{s.Id}");
            if (s.CompletedDate != r.CompletedDate) Rules.ValidateDate(r.CompletedDate, p.StartDate, $"'{s.Name}' 완료일", $"completedDate.{s.Id}");
            if (s.CompletedDate == null && r.CompletedDate != null) EnsureCanComplete(s, $"completedDate.{s.Id}", named: true);
            changes.Add((s, r));
        }
        if (changes.Count == 0) return;
        if (reason == null && changes.Any(c => c.Stage.TargetDate != null && c.Stage.TargetDate != c.Row.TargetDate))
            throw new RuleException("이미 정한 목표일을 바꾸는 단계가 있어 변경 사유가 필요합니다.", "reason");

        var batch = Guid.NewGuid();
        var reopened = new List<Stage>();
        foreach (var (s, r) in changes.OrderBy(c => c.Stage.SortOrder).ThenBy(c => c.Stage.Id))
        {
            SetTarget(p, s, r.TargetDate, reason, batch);
            if (SetCompleted(p, s, r.CompletedDate, closeWorks: false, batch)) reopened.Add(s);
            s.Version++;
        }
        if (reopened.Count > 0) await RealignReopenedAsync(p, reopened, batch, $"completedDate.{reopened[0].Id}");

        SyncState(p);
        Touch(p);
        await _db.SaveChangesAsync();
    }
}
