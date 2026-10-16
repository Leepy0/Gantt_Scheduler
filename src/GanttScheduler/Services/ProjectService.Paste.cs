using GanttScheduler.Domain;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Services;

// 등록 화면의 '단계·작업 붙여넣기'(CSV). 사람이 적은 글자 그대로 — 해석·검증은 서버가 한다
public record ProjectPasteInput(int CategoryId, DateOnly? StartDate, string? Text);

// 해석한 단계 한 줄. Line: 처음 나온 줄 번호, FromTemplate: 유형의 기본 단계와 이름이 같아 종류·체크리스트를 이어받음,
// AutoTarget: 목표일을 적지 않고 작업이 있어 작업 일정에 맞춤(규칙 1 의 예외), GapWorkDays: 앞 단계 목표일(동시 진행은 묶음 기준일)부터의 근무일
public record PasteStageView(int Line, string? Name, string Kind, bool Parallel, bool AutoTarget, DateOnly? TargetDate, int? GapWorkDays,
                             bool FromTemplate, int Checks, int WorkCount, List<RowIssue> Issues)
{
    public bool Ok => Issues.Count == 0;
}

// 해석한 작업 한 줄 (붙여넣기·가져오기의 WorkRowView 와 같은 칸에 단계 이름을 더한 것)
public record PasteWorkView(int Line, string? Stage, string? Title, string? Owner, bool NewOwner, DateOnly? PlanStart, DateOnly? PlanEnd,
                            string? Predecessor, int? WorkDays, List<RowIssue> Issues)
{
    public bool Ok => Issues.Count == 0;
}

public record ProjectPastePreview(List<PasteStageView> Stages, List<PasteWorkView> Works, int ErrorCount, List<string> NewOwners, string Note);

// 과제 등록 화면의 CSV 붙여넣기: 단계와 작업을 한 번에 만든다. 붙여 넣은 단계가 있으면 유형의 기본 단계 대신 그 단계로 등록한다
// (이름이 같은 기본 단계의 종류·체크리스트는 이어받는다). 작업 줄의 해석·검증·저장은 상세의 '작업 여러 개 추가'와 같은 코드를 쓴다.
// 칸: 단계, 작업명, 담당, 목표 시작, 목표 종료, 선행 작업명, 동시 — 작업명이 비면 단계 줄(목표 종료 칸이 단계 목표일), 있으면 그 단계의 작업
public partial class ProjectService
{
    public const int MaxPasteStages = 50;

    public static class PasteColumns
    {
        public const string Stage = "단계";
        public const string Parallel = "동시";
        public static readonly string[] All = { Stage, WorkColumns.Title, WorkColumns.Owner, WorkColumns.Start, WorkColumns.End, WorkColumns.Predecessor, Parallel };
        public static string Header => string.Join(",", All);
    }

    // 붙여넣은 글의 한 줄 (칸 순서대로, 빈 칸은 null)
    private sealed record PasteLine(int Line, string? Stage, string? Title, string? Owner, string? Start, string? End, string? Predecessor, string? Parallel);

    // 해석 중인 단계 하나
    private sealed class PasteStagePlan
    {
        public required int Line { get; init; }
        public required string Name { get; init; }
        public StageTemplate? Template { get; set; }
        public bool Parallel { get; set; }
        public string? TargetText { get; set; }
        public DateOnly? Target { get; set; }
        public List<RowIssue> Issues { get; } = new();
        public List<WorkRowInput> Works { get; } = new();
        public Stage? Stage { get; set; }
    }

    // 글을 줄로 나눈다. 첫 줄이 머리글(첫 칸이 '단계'이고 날짜 칸에 날짜가 없음)이면 건너뛴다. 칸은 TAB(Excel) 또는 쉼표(CSV)
    private List<PasteLine> ParsePasteLines(string? text)
    {
        var today = _clock.Today;
        var lines = new List<PasteLine>();
        foreach (var (line, raw) in InputText.Lines(text))
        {
            var c = InputText.Cells(raw);
            string? At(int i) => i < c.Count && c[i].Length > 0 ? c[i] : null;
            if (lines.Count == 0 && (At(0) ?? "").Contains("단계") && !InputText.TryDate(At(3), today, out _) && !InputText.TryDate(At(4), today, out _)
                && ((At(1) ?? "").Contains("작업") || At(1) == null))
                continue;
            lines.Add(new PasteLine(line, At(0), At(1), At(2), At(3), At(4), At(5), At(6)));
        }
        return lines;
    }

    // '동시' 칸: 글자가 있으면 동시 진행. 아니라고 적은 값(N·아니오·0·false·-)만 뺀다
    private static bool IsYes(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return false;
        return !new[] { "n", "no", "false", "0", "-", "x", "아니오", "아니요", "순차" }.Contains(t.ToLowerInvariant());
    }

    // 붙여넣기 미리보기: 단계·작업 줄마다 해석 결과와 문제를 돌려준다. 저장하지 않는다
    public async Task<ProjectPastePreview> PreviewProjectPasteAsync(ProjectPasteInput input)
    {
        var category = await CategoryForCreateAsync(input.CategoryId) ?? throw new RuleException("과제 유형을 먼저 골라 주세요.", "categoryId");
        var start = input.StartDate ?? _clock.Today;
        Rules.ValidateStartDate(start, _clock.Today);
        var p = new Project { StartDate = start, CategoryId = category.Id, State = ProjectState.Active };
        var (plans, workPlans) = await BuildPastedStagesAsync(p, category, input.Text, apply: false);

        var cal = await CalendarAsync();
        var stages = plans.Select(x => x.Stage!).ToList();
        var gaps = Rules.TargetGaps(start, stages.Select(s => s.TargetDate).ToList(), cal, Rules.ParallelFlags(stages));
        var stageViews = plans.Select((x, i) => new PasteStageView(x.Line, x.Name, Views.Key(x.Stage!.Kind), x.Stage.Parallel, x.Stage.AutoTarget,
            x.Stage.TargetDate, gaps[i], x.Template != null, x.Stage.Checks.Count, x.Works.Count, x.Issues)).ToList();
        var workViews = workPlans.Select(w => new PasteWorkView(w.Input.Line, w.Stage.Name, w.Title, w.Owner, w.NewOwner, w.Start, w.End,
            w.Input.Predecessor?.Trim(), w.WorkDays, w.Issues)).ToList();
        var errors = stageViews.Count(s => !s.Ok) + workViews.Count(w => !w.Ok);
        var inherited = plans.Count(x => x.Template != null);
        var note = $"유형 '{category.Name}' 의 기본 단계 대신 붙여 넣은 단계 {plans.Count}개로 등록합니다"
            + (inherited > 0 ? $" (기본 단계와 이름이 같은 {inherited}개는 종류·체크리스트를 이어받습니다)" : "") + ".";
        return new ProjectPastePreview(stageViews, workViews, errors,
            workViews.Where(w => w.NewOwner).Select(w => w.Owner!).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList(), note);
    }

    // 샘플 CSV: 고른 유형의 기본 단계(초안 목표일·동시 진행·템플릿 작업)로 만든 견본. 기본 단계가 없으면 일반적인 예
    public async Task<string> PasteSampleAsync(int categoryId, DateOnly? startDate)
    {
        var start = startDate ?? _clock.Today;
        var cal = await CalendarAsync();
        var category = await CategoryForCreateAsync(categoryId);
        var templates = category?.StageTemplates.OrderBy(t => t.SortOrder).ThenBy(t => t.Id).ToList() ?? new List<StageTemplate>();
        var rows = new List<string[]> { PasteColumns.All };
        static string Csv(params string?[] cells) => string.Join(",", cells.Select(c => (c ?? "").Contains(',') ? "\"" + c + "\"" : c ?? ""));

        if (templates.Count == 0)
        {
            var d1 = cal.AddWorkingDays(start, 5);
            var d2 = cal.AddWorkingDays(d1, 10);
            var d3 = cal.AddWorkingDays(d2, 5);
            var w1End = cal.AddWorkingDays(start, 2);
            var w2Start = cal.EarliestStartAfter(w1End, 0);
            return string.Join("\n", new[]
            {
                PasteColumns.Header,
                Csv("설계", null, null, null, Fmt(d1), null, null),
                Csv("설계", "현장 실사", null, Fmt(start), Fmt(w1End), null, null),
                Csv("설계", "사양 검토", null, Fmt(w2Start), Fmt(d1), "현장 실사", null),
                Csv("제작", null, null, null, Fmt(d2), null, null),
                Csv("설치", null, null, null, Fmt(d3), null, null),
                Csv("교육", null, null, null, Fmt(d3), null, "동시"),
            }) + "\n";
        }

        var flags = templates.Select((t, i) => i > 0 && t.Parallel).ToList();
        var targets = Rules.DraftTargets(start, templates.Select(t => t.DefaultWorkDays).ToList(), cal, null, flags);
        var starts = Rules.StageStarts(start, targets, cal, flags);
        var lines = new List<string> { PasteColumns.Header };
        var anyWorks = templates.Any(t => t.Works.Count > 0);
        for (var i = 0; i < templates.Count; i++)
        {
            var t = templates[i];
            lines.Add(Csv(t.Name, null, null, null, Fmt(targets[i]) == "미정" ? null : Fmt(targets[i]), null, flags[i] ? "동시" : null));
            var works = t.Works.OrderBy(w => w.SortOrder).ThenBy(w => w.Id).ToList();
            // 템플릿 작업이 하나도 없는 유형은 첫 단계에 작업 두 줄을 예로 넣는다(선행 쓰는 법을 보이려고)
            if (!anyWorks && i == 0)
            {
                var w1End = cal.AddWorkingDays(starts[0], 2);
                lines.Add(Csv(t.Name, "현장 실사", null, Fmt(starts[0]), Fmt(w1End), null, null));
                lines.Add(Csv(t.Name, "사양 검토", null, Fmt(cal.EarliestStartAfter(w1End, 0)), Fmt(cal.AddWorkingDays(w1End, 3)), "현장 실사", null));
            }
            var names = works.ToDictionary(w => w.Id, w => w.Title);
            foreach (var w in works)
            {
                var (ws, we) = Rules.PlaceTemplateWork(starts[i], w.OffsetWorkDays, w.DurationWorkDays, cal);
                lines.Add(Csv(t.Name, w.Title, null, Fmt(ws), Fmt(we), w.PredecessorTemplateWorkId is int pred ? names.GetValueOrDefault(pred) : null, null));
            }
        }
        return string.Join("\n", lines) + "\n";
    }

    // 붙여 넣은 글로 과제의 단계·작업을 만든다. apply=false 면 검증만(미리보기), true 면 문제가 없을 때만 작업을 붙인다(문제가 있으면 RuleException).
    // 돌려주는 값: 단계 계획(Stage 를 만들어 p.Stages 에 넣는다)과 작업 계획
    private async Task<(List<PasteStagePlan> Stages, List<WorkRowPlan> Works)> BuildPastedStagesAsync(Project p, Category category, string? text, bool apply)
    {
        var lines = ParsePasteLines(text);
        if (lines.Count == 0) throw new RuleException("붙여 넣을 내용이 없습니다. 한 줄에 단계나 작업 하나씩입니다.", "pasteText");
        var today = _clock.Today;
        var templates = category.StageTemplates.OrderBy(t => t.SortOrder).ThenBy(t => t.Id).ToList();
        var plans = new List<PasteStagePlan>();
        PasteStagePlan? last = null;

        foreach (var l in lines)
        {
            var name = l.Stage?.Trim();
            PasteStagePlan? plan;
            if (name == null)
            {
                if (last == null)
                {
                    // 첫 줄부터 단계가 없다 — 어디에 넣을지 모른다. 단계 이름 자리에 문제로 남긴다
                    plans.Add(plan = new PasteStagePlan { Line = l.Line, Name = "" });
                    plan.Issues.Add(new(PasteColumns.Stage, "단계명을 입력해 주세요. 작업 줄은 앞 줄의 단계에 들어갑니다."));
                }
                else plan = last;
            }
            else
            {
                plan = plans.FirstOrDefault(x => x.Name.Length > 0 && Same(x.Name, name));
                if (plan == null)
                {
                    plans.Add(plan = new PasteStagePlan { Line = l.Line, Name = name, Template = templates.FirstOrDefault(t => Same(t.Name, name)) });
                    if (name.Length > Limits.StageName) plan.Issues.Add(new(PasteColumns.Stage, $"단계명은 {Limits.StageName}자 이내로 입력해 주세요."));
                }
            }
            last = plan;
            if (IsYes(l.Parallel)) plan.Parallel = true;

            if (l.Title == null)
            {
                // 단계 줄: 목표 종료 칸이 목표일(선택). 시작 칸만 적었으면 그것을 목표일로 본다
                var targetText = l.End ?? l.Start;
                if (targetText != null)
                {
                    if (plan.TargetText != null && plan.TargetText != targetText)
                        plan.Issues.Add(new(WorkColumns.End, $"'{plan.Name}' 단계의 목표일이 두 번 적혀 있습니다 ({plan.TargetText}, {targetText})."));
                    plan.TargetText = targetText;
                    if (!InputText.TryDate(targetText, today, out var d))
                        plan.Issues.Add(new(WorkColumns.End, $"'{targetText}' 을(를) 날짜로 읽지 못했습니다. 2026-10-12, 2026.10.12, 10/12 처럼 적어 주세요."));
                    else
                    {
                        plan.Target = d;
                        try { Rules.ValidateDate(d, p.StartDate, $"'{plan.Name}' 목표일", "pasteText"); }
                        catch (RuleException ex) { plan.Issues.Add(new(WorkColumns.End, ex.Message)); }
                    }
                }
            }
            else
            {
                plan.Works.Add(new WorkRowInput(l.Title, l.Owner, l.Start, l.End, l.Predecessor, l.Line));
            }
        }
        if (plans.Count > MaxPasteStages) throw new RuleException($"단계는 한 번에 {MaxPasteStages}개까지 넣을 수 있습니다(지금 {plans.Count}개).", "pasteText");
        if (plans.Sum(x => x.Works.Count) > MaxPasteWorks) throw new RuleException($"작업은 한 번에 {MaxPasteWorks}줄까지 넣을 수 있습니다.", "pasteText");

        // 단계를 만든다(순서는 처음 나온 차례). 기본 단계와 이름이 같으면 종류·체크리스트를 이어받는다. 첫 단계의 동시 진행은 뜻이 없어 끈다
        for (var i = 0; i < plans.Count; i++)
        {
            var x = plans[i];
            var stage = new Stage
            {
                Name = x.Name, Kind = x.Template?.Kind ?? StageKind.Normal, SortOrder = i + 1, TargetDate = x.Target,
                Parallel = i > 0 && x.Parallel,
                // 목표일을 적지 않은 단계에 작업이 있으면 작업 일정에 맞춘다(규칙 1 의 예외 — 저장할 때 SyncAutoTargets 가 채운다)
                AutoTarget = x.Target == null && x.Works.Count > 0,
            };
            if (x.Template != null)
                foreach (var c in x.Template.CheckItems.OrderBy(c => c.SortOrder))
                    stage.Checks.Add(new CheckItem { Text = c.Text, SortOrder = c.SortOrder });
            x.Stage = stage;
            p.Stages.Add(stage);
        }

        // 작업 줄: 상세의 '작업 여러 개 추가'와 같은 검사(날짜·범위·담당·선행·순환·선행 일정)
        var rows = plans.SelectMany(x => x.Works.Select(w => (w, x.Stage!))).ToList();
        var workPlans = rows.Count == 0 ? new List<WorkRowPlan>() : await CheckWorkRowsAsync(p, rows);

        if (!apply)
        {
            // 미리보기: 작업 일정에 맞추는 단계의 목표일을 해석한 작업 줄로 미리 보인다(저장할 때 SyncAutoTargets 가 같은 값을 넣는다)
            foreach (var x in plans.Where(x => x.Stage!.AutoTarget))
            {
                var ends = workPlans.Where(w => ReferenceEquals(w.Stage, x.Stage) && w.End != null).Select(w => w.End!.Value).ToList();
                if (ends.Count > 0) x.Stage!.TargetDate = ends.Max();
            }
            return (plans, workPlans);
        }

        var badStage = plans.FirstOrDefault(x => x.Issues.Count > 0);
        if (badStage != null)
            throw new RuleException($"붙여넣기 {badStage.Line}번째 줄 ({badStage.Name}): {badStage.Issues[0].Message}", "pasteText");
        var badWork = workPlans.FirstOrDefault(x => x.Issues.Count > 0);
        if (badWork != null)
            throw new RuleException($"붙여넣기 {badWork.Input.Line}번째 줄: {badWork.Issues[0].Message}", "pasteText");
        if (workPlans.Count > 0)
        {
            try { await ApplyWorkRowsAsync(p, workPlans, Guid.NewGuid(), "CSV 붙여넣기"); }
            catch (RuleException ex) { throw new RuleException("붙여넣기: " + ex.Message, "pasteText"); }
        }
        // 목표일을 작업에 맞추는 단계는 지금 바로 채운다(등록 활동 문구에 보이게). 이후 변경은 SyncAutoTargets 가 맞춘다
        foreach (var x in plans)
            if (Rules.AutoTargetOf(x.Stage!) is { } auto) x.Stage!.TargetDate = auto;
        return (plans, workPlans);
    }
}
