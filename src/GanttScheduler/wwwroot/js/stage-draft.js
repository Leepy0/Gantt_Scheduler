// 과제 등록: 단계 목표일 초안 표. 유형·착수일·법인을 고르면 서버가 단계별 기본 기간으로 계산한 초안을 받아 그리고(Edit?handler=Draft),
// 사람이 고친 날짜(kept)는 그대로 두고 그 뒤 단계만 다시 계산하게 한다. 계산(근무일·간격·추천 입고일)은 모두 서버가 한다
(function () {
    'use strict';

    var host = document.getElementById('draft');
    var fields = document.getElementById('draft-fields');
    var form = document.getElementById('edit-form');
    if (!host || !form) return;

    var meta = App.data();
    var esc = App.esc, icon = App.icon;
    var KIND = { inspection: '검수', inbound: '입고' };
    var startInput = document.getElementById('Form_StartDate');
    var locationInput = document.getElementById('location-id');

    var rows = [];          // 서버가 준 초안 줄 (StageDraftRow)
    var rowsCategory = 0;   // rows 가 어느 유형의 단계인지 (유형을 바꾼 뒤 실패하면 지난 유형의 표를 남기지 않는다)
    var lead = null;        // 고른 법인의 해상 운송 소요일 (법인 전이면 null)
    var kept = {};          // 템플릿 id → 사람이 정한 날짜('' = 비움)
    var seq = 0;
    var timer = null;
    var pendingError = meta.errorField && /^stageTargets\./.test(meta.errorField) ? meta.errorField : null;

    // 오류로 다시 그린 화면: 사람이 고쳤던 줄을 그대로 살린다
    (meta.form.stageTargets || []).forEach(function (t) { if (t.kept) kept[t.templateId] = t.targetDate || ''; });

    function categoryId() {
        var checked = form.querySelector('input[name="Form.CategoryId"]:checked');
        return checked ? +checked.value : 0;
    }

    function gapHtml(gap, parallel) {
        if (gap == null) return '<span class="muted">—</span>';
        if (gap < 0) return '<span class="warn" title="' + (parallel ? '묶음 기준일보다 이릅니다' : '앞 단계 목표일보다 이릅니다') + '">' + icon('alert') + gap + '근무일</span>';
        return '+' + gap + '근무일';
    }

    function notesHtml(r) {
        var notes = [];
        if (r.kept) {
            notes.push('<span class="hint">직접 정한 날짜' +
                ' <button type="button" class="btn small ghost" data-unkeep="' + r.templateId + '">초안으로</button></span>');
        }
        if (r.inboundHint && r.inboundHint !== r.targetDate) {
            notes.push('<span class="sched-hint">추천 입고일 <b>' + esc(r.inboundHint) + '</b>' +
                '<span class="muted">검수일 + 해상 운송 ' + lead + '일</span>' +
                '<button type="button" class="btn small" data-apply-hint="' + r.templateId + '">적용</button></span>');
        }
        return notes.join('');
    }

    function rowHtml(r) {
        var label = esc(r.name);
        var sub = [];
        if (KIND[r.kind]) sub.push(KIND[r.kind]);
        if (r.parallel) sub.push('앞 단계와 동시');
        sub.push(r.defaultWorkDays != null ? '기본 ' + r.defaultWorkDays + '근무일' : '기본 기간 없음');
        return '<div class="sched-row' + (r.kept ? ' changed' : '') + '" data-template="' + r.templateId + '">' +
            '<div class="sched-name"><span class="sched-title">' + label + '</span><span class="hint">' + esc(sub.join(' · ')) + '</span></div>' +
            '<label class="field" data-field="stageTargets.' + r.templateId + '"><span class="label">목표일</span>' +
            '<input type="date" data-target="' + r.templateId + '" value="' + esc(r.targetDate || '') + '" aria-label="' + label + ' 목표일" /></label>' +
            '<div class="sched-gap" data-gap>' + gapHtml(r.gapWorkDays, r.parallel) + '</div>' +
            '<div class="sched-notes" data-notes>' + notesHtml(r) + '</div></div>';
    }

    // 같은 단계 목록이면 줄을 다시 만들지 않고 값만 바꾼다 — 날짜를 입력하는 중인 칸의 포커스·값을 지킨다
    function render() {
        var same = rows.length > 0 && rows.length === host.querySelectorAll('.sched-row').length &&
            rows.every(function (r) { return host.querySelector('.sched-row[data-template="' + r.templateId + '"]'); });
        if (!same) {
            host.innerHTML = rows.length
                ? '<div class="sched draft"><div class="sched-head" aria-hidden="true"><span>단계</span><span>목표일</span><span>간격</span></div>' +
                  rows.map(rowHtml).join('') + '</div>'
                : '<p class="hint">이 유형에는 기본 단계가 없습니다.</p>';
        } else {
            clearError();
            rows.forEach(function (r) {
                var row = host.querySelector('.sched-row[data-template="' + r.templateId + '"]');
                var input = row.querySelector('input[data-target]');
                if (input !== document.activeElement && input.value !== (r.targetDate || '')) input.value = r.targetDate || '';
                row.classList.toggle('changed', r.kept);
                row.querySelector('[data-field]').classList.remove('invalid');   // 초안을 받았으면 고친 날짜가 범위 안이다
                row.querySelector('[data-gap]').innerHTML = gapHtml(r.gapWorkDays, r.parallel);
                row.querySelector('[data-notes]').innerHTML = notesHtml(r);
            });
        }
        if (pendingError) {
            var f = host.querySelector('[data-field="' + pendingError + '"]');
            if (f) { f.classList.add('invalid'); f.querySelector('input').focus(); }
            pendingError = null;
        }
    }

    // 초안을 받지 못하면(착수일·고친 목표일이 범위 밖 등) 지난 표는 그대로 두고 위에 이유만 보인다.
    // 표를 지우면 잘못 넣은 날짜를 고칠 칸도, 등록할 때 보낼 고친 날짜도 함께 사라진다
    function showError(message, field, cat) {
        if (cat !== rowsCategory) { rows = []; host.innerHTML = ''; }
        clearError();
        host.insertAdjacentHTML('afterbegin', '<p class="form-warn" data-draft-error>' + icon('alert') + '<span>' + esc(message) + '</span></p>');
        var f = field ? host.querySelector('[data-field="' + field + '"]') : null;
        if (f) f.classList.add('invalid');
    }

    function clearError() {
        var el = host.querySelector('[data-draft-error]');
        if (el) el.remove();
    }

    // 초안을 다시 받는다 (빠르게 여러 번 바뀌면 마지막 응답만 쓴다)
    function load(delay) {
        clearTimeout(timer);
        timer = setTimeout(function () {
            var cat = categoryId();
            if (!cat) { rows = []; rowsCategory = 0; host.innerHTML = '<p class="hint">유형을 고르면 단계 목표일 초안을 보여 줍니다.</p>'; return; }
            var q = '&categoryId=' + cat + '&start=' + encodeURIComponent(startInput.value || '') +
                (+locationInput.value ? '&locationId=' + locationInput.value : '');
            Object.keys(kept).forEach(function (id) { q += '&keep=' + encodeURIComponent(id + ':' + kept[id]); });
            var my = ++seq;
            App.get(App.url('/Projects/Edit?handler=Draft' + q)).then(function (r) {
                if (my !== seq) return;
                rows = r.draft.rows;
                rowsCategory = cat;
                lead = r.draft.oceanLeadDays;
                render();
            }, function (err) {
                if (my === seq) showError(err.message, err.field, cat);
            });
        }, delay || 0);
    }

    // 사람이 날짜를 고치면 그 줄은 그대로 두고, 뒤 단계 초안만 다시 받는다
    host.addEventListener('input', function (e) {
        var input = e.target.closest('input[data-target]');
        if (!input) return;
        kept[input.dataset.target] = input.value;
        var row = input.closest('.sched-row');
        row.classList.add('changed');
        row.querySelector('[data-field]').classList.remove('invalid');
        load(300);
    });
    host.addEventListener('click', function (e) {
        var t;
        if ((t = e.target.closest('[data-apply-hint]'))) {
            var r = rows.filter(function (x) { return String(x.templateId) === t.dataset.applyHint; })[0];
            if (!r) return;
            kept[r.templateId] = r.inboundHint;
            load();
        } else if ((t = e.target.closest('[data-unkeep]'))) {
            delete kept[t.dataset.unkeep];
            load();
        }
    });

    // 유형이 바뀌면 단계가 달라지므로 고친 값을 버린다. 착수일·법인은 고친 값을 두고 나머지만 다시 계산한다
    form.querySelectorAll('input[name="Form.CategoryId"]').forEach(function (r) {
        r.addEventListener('change', function () { kept = {}; load(); });
    });
    startInput.addEventListener('input', function () { load(300); });
    document.getElementById('cascade').addEventListener('change', function () { load(); });   // edit.js 가 숨은 위치 값을 먼저 맞춘다

    // 제출: 표의 값을 서버가 받는 이름(Form.StageTargets[i].*)으로 붙인다. 빈 칸은 목표일 미정
    form.addEventListener('submit', function () {
        fields.innerHTML = '';
        var i = 0, sent = {};
        function add(id, value, isKept) {
            fields.insertAdjacentHTML('beforeend',
                '<input type="hidden" name="Form.StageTargets[' + i + '].TemplateId" value="' + esc(id) + '" />' +
                '<input type="hidden" name="Form.StageTargets[' + i + '].TargetDate" value="' + esc(value) + '" />' +
                '<input type="hidden" name="Form.StageTargets[' + i + '].Kept" value="' + (isKept ? 'true' : 'false') + '" />');
            sent[id] = true;
            i++;
        }
        host.querySelectorAll('input[data-target]').forEach(function (input) {
            var id = input.dataset.target;
            add(id, input.value, Object.prototype.hasOwnProperty.call(kept, id));
        });
        // 초안 표가 없을 때(오류로 다시 그린 화면에서 초안을 받지 못함)도 사람이 고친 날짜는 보낸다 — 다음 화면이 되살린다
        Object.keys(kept).forEach(function (id) { if (!sent[id]) add(id, kept[id], true); });
    });

    load();
})();
