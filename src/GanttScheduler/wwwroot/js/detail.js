// 과제 상세: 요약, 단계 진행, 일정(간트/목록), 정보, 활동 + 단계·작업 편집 시트
(function () {
    'use strict';

    var data = App.data();
    var P = data.project;
    var today = data.today;
    var D = App.d, esc = App.esc, icon = App.icon;

    var view = App.pref.get('detail.view', '') || (App.isMobile() ? 'list' : 'gantt');
    var zoom = App.pref.get('detail.zoom', 'week');
    var collapsed = {};
    try { JSON.parse(App.pref.get('detail.collapsed.' + P.id, '[]')).forEach(function (id) { collapsed[id] = true; }); } catch (e) { /* 무시 */ }
    var gantt = null;
    var tab = 'schedule';

    var $ = function (id) { return document.getElementById(id); };

    // 변경 요청 → 서버가 돌려준 최신 과제로 화면 전체를 다시 그린다
    function api(handler, body) {
        return App.post(App.url('/Projects/Detail?handler=' + handler), body).then(function (r) {
            if (r.project) { P = r.project; renderAll(true); }
            return r;
        });
    }

    // ── 표시용 조각 ─────────────────────────────────────────────────
    function chip(cls, iconName, text) {
        return '<span class="chip ' + cls + '">' + (iconName ? icon(iconName) : '') + esc(text) + '</span>';
    }

    function stateChip(p) {
        if (p.delayDays > 0) return chip('alarm', 'alert', '지연 D+' + p.delayDays);
        if (p.state === 'active') return chip('accent', null, '진행');
        if (p.state === 'done') return chip('ok', 'check', '완료');
        if (p.state === 'hold') return chip('', 'pause', '보류');
        return chip('', 'ban', '중단');
    }

    function stageChip(s) {
        if (s.status === 'done') return chip('ok', 'check', '완료');
        if (s.status === 'late') return chip('alarm', 'alert', '지연 D+' + s.lateDays);
        var out = s.status === 'current' ? chip('accent', null, '진행 중') : chip('', null, '예정');
        if (s.targetDate) {
            var dday = D.diff(s.targetDate, today);
            if (dday <= 7) out += chip('warn', 'clock', dday === 0 ? '오늘' : 'D-' + dday);
        }
        return out;
    }

    function workChip(w) {
        if (w.late) return chip('alarm', 'alert', '지연');
        if (w.status === 'done') return w.doneLate ? chip('warn', 'clock', '초과 완료') : chip('ok', 'check', '완료');
        if (w.status === 'inprogress') return chip('accent', null, '진행 중');
        return chip('', null, '예정');
    }

    function owners(w) {
        return [w.owner, w.coOwner].filter(Boolean).join(', ');
    }

    function stageById(id) {
        for (var i = 0; i < P.stages.length; i++) if (P.stages[i].id === id) return P.stages[i];
        return null;
    }

    function workById(id) {
        for (var i = 0; i < P.stages.length; i++) {
            var ws = P.stages[i].works;
            for (var j = 0; j < ws.length; j++) if (ws[j].id === id) return ws[j];
        }
        return null;
    }

    // ── 요약 ────────────────────────────────────────────────────────
    function renderSummary() {
        $('p-title').textContent = P.title;
        document.title = P.title + ' · 과제 스케줄러';

        var period = P.startDate + ' ~ ' + (P.endTarget || '미정');
        var current = P.currentStage
            ? P.currentStage + (P.currentTarget ? ' (목표 ' + D.md(P.currentTarget) + ')' : ' (목표일 미정)')
            : '-';
        function meta(k, v) {
            return '<div class="meta-item"><span class="k">' + k + '</span><span class="v" title="' + esc(v) + '">' + esc(v) + '</span></div>';
        }

        $('summary').innerHTML =
            '<div class="summary-top">' +
            '<div class="summary-chips">' + chip('', null, P.category) + stateChip(P) + '</div>' +
            '<div class="summary-actions">' +
            '<a class="btn small" href="' + App.url('/Projects/Edit?id=' + P.id) + '">' + icon('edit') + '수정</a>' +
            '<a class="btn small" href="' + App.url('/Projects/Detail?handler=Excel&id=' + P.id) + '">' + icon('download') + 'Excel</a>' +
            (P.state === 'done' ? '' : '<button type="button" class="btn small" id="btn-state">' + icon('flag') + '상태 변경</button>') +
            '</div></div>' +
            '<div class="summary-meta">' +
            meta('위치', P.corporation + (P.locationPath ? ' · ' + P.locationPath : '')) +
            meta('담당', P.owner) +
            meta('기간', period) +
            meta('현재 단계', current) +
            '</div>' +
            '<div class="summary-progress"><span class="progress" aria-hidden="true"><i style="width:' + P.progress + '%"></i></span>' +
            '<span class="num">' + P.progress + '%</span></div>';
    }

    // ── 단계 진행 표시 ──────────────────────────────────────────────
    function renderStepper() {
        $('stepper').innerHTML = P.stages.map(function (s) {
            var ic = s.status === 'done' ? icon('check') : s.status === 'late' ? icon('alert') : s.status === 'current' ? icon('dot') : '';
            var date = s.completedDate ? '완료 ' + D.md(s.completedDate) : s.targetDate ? '목표 ' + D.md(s.targetDate) : '목표일 미정';
            return '<button type="button" class="step ' + s.status + '" data-stage="' + s.id + '">' +
                '<span class="step-name">' + ic + esc(s.name) + '</span><span class="step-date">' + date + '</span></button>';
        }).join('');
    }

    // ── 일정: 간트 ──────────────────────────────────────────────────
    var stageLane = { planTop: 9, planH: 12, actTop: 25, actH: 12 };

    function workBars(w) {
        var bars = [{ from: w.planStart, to: w.planEnd, top: 9, h: 6, cls: 'plan', title: '목표 ' + w.planStart + ' ~ ' + w.planEnd }];
        var top = 19, h = 12;
        if (w.actualStart) {
            if (w.actualEnd) {
                bars.push({
                    from: w.actualStart, to: w.actualEnd, top: top, h: h, cls: w.doneLate ? 'over' : 'done',
                    title: '실적 ' + w.actualStart + ' ~ ' + w.actualEnd + (w.doneLate ? ' (목표 초과)' : '')
                });
            } else {
                var end = D.max(today, w.actualStart);
                if (w.late && w.planEnd >= w.actualStart) {
                    bars.push({ from: w.actualStart, to: w.planEnd, top: top, h: h, cls: 'cur', title: '진행 중 ' + w.progress + '%' });
                    bars.push({ from: D.add(w.planEnd, 1), to: end, top: top, h: h, cls: 'late', title: '지연 ' + D.diff(today, w.planEnd) + '일' });
                } else {
                    bars.push({ from: w.actualStart, to: end, top: top, h: h, cls: w.late ? 'late' : 'cur', title: '진행 중 ' + w.progress + '%' });
                }
            }
        } else {
            // 아직 시작 전: 목표 기간을 점선으로, 이미 목표 종료를 넘겼으면 지연 구간 표시
            bars.push({ from: w.planStart, to: w.planEnd, top: top, h: h, cls: 'ghost', title: '시작 전' });
            if (w.late) bars.push({ from: D.add(w.planEnd, 1), to: today, top: top, h: h, cls: 'late', title: '지연 ' + D.diff(today, w.planEnd) + '일' });
        }
        return bars;
    }

    function ganttRows() {
        var rows = [];
        P.stages.forEach(function (s, i) {
            var sideIcon = s.status === 'late' ? '<span class="alarm">' + icon('alert') + '</span>'
                : s.status === 'done' ? '<span class="ok">' + icon('check') + '</span>'
                : s.forecastOver > 0 ? '<span class="warn">' + icon('clock') + '</span>' : '';
            var sub = s.completedDate ? '완료 ' + D.md(s.completedDate) : s.targetDate ? '목표 ' + D.md(s.targetDate) : '목표일 미정';
            if (s.status === 'late') sub += ' · 지연 ' + s.lateDays + '일';
            var marks = [];
            if (s.forecast) {
                marks.push({
                    date: s.forecast, top: 10, cls: s.forecastOver > 0 ? 'over' : '',
                    title: '작업 기준 예상 종료 ' + s.forecast + (s.forecastOver > 0 ? ' (목표일 +' + s.forecastOver + '일)' : '')
                });
            }
            rows.push({
                key: 's' + s.id, cls: 'stage', title: s.name, sub: sub, height: 48,
                side: sideIcon + '<span>' + s.progress + '%</span>',
                toggle: s.works.length ? { expanded: !collapsed[s.id] } : null,
                bars: Gantt.stageBars(s, i, stageLane, false), marks: marks,
                data: { stage: s.id }
            });
            if (collapsed[s.id]) return;
            s.works.forEach(function (w) {
                rows.push({
                    key: 'w' + w.id, cls: 'work', title: w.title, sub: owners(w), height: 40,
                    side: (w.late ? '<span class="alarm">' + icon('alert') + '</span>' : '') + '<span>' + w.progress + '%</span>',
                    bars: workBars(w), data: { work: w.id }
                });
            });
        });
        return rows;
    }

    function renderGantt(keepScroll) {
        gantt = Gantt.render($('gantt'), {
            rows: ganttRows(), today: today, holidays: data.holidays, zoom: zoom,
            corner: '단계 · 작업', keepScroll: keepScroll, empty: '단계가 없습니다. 단계를 추가해 주세요.',
            onRowClick: function (r) {
                if (r.data.stage) openStageSheet(stageById(r.data.stage));
                else if (r.data.work) openWorkSheet(workById(r.data.work));
            },
            onToggle: function (r) {
                var id = r.data.stage;
                if (collapsed[id]) delete collapsed[id]; else collapsed[id] = true;
                App.pref.set('detail.collapsed.' + P.id, JSON.stringify(Object.keys(collapsed).map(Number)));
                renderGantt(true);
            }
        });
    }

    // ── 일정: 목록 ──────────────────────────────────────────────────
    function renderList() {
        var html = P.stages.map(function (s) {
            var dates = (s.targetDate ? '목표 ' + D.md(s.targetDate) : '목표일 미정') +
                (s.completedDate ? '<br>완료 ' + D.md(s.completedDate) : '');
            var warn = s.forecastOver > 0
                ? '<div class="stage-warn">' + icon('clock') + '작업 기준 예상 종료 ' + D.md(s.forecast) + ' · 목표일보다 ' + s.forecastOver + '일 늦음</div>'
                : '';
            var works = s.works.map(function (w) {
                var range = '목표 ' + D.md(w.planStart) + '~' + D.md(w.planEnd) +
                    (w.actualStart ? ' · 실적 ' + D.md(w.actualStart) + '~' + (w.actualEnd ? D.md(w.actualEnd) : '') : '');
                var who = owners(w);
                return '<li><button type="button" class="work-item" data-work="' + w.id + '">' +
                    '<span class="work-title">' + esc(w.title) + '</span>' + workChip(w) +
                    '<span class="work-meta">' + (who ? esc(who) + ' · ' : '') + range + '</span>' +
                    '<span class="work-progress"><span class="progress" aria-hidden="true"><i style="width:' + w.progress + '%"></i></span>' +
                    '<span class="num">' + w.progress + '%</span></span></button></li>';
            }).join('');
            return '<section class="stage-card ' + s.status + '">' +
                '<button type="button" class="stage-head" data-stage="' + s.id + '"><h3>' + esc(s.name) + '</h3>' + stageChip(s) +
                '<span class="stage-dates">' + dates + '</span></button>' + warn +
                (works ? '<ul class="work-list">' + works + '</ul>' : '') +
                '<div class="stage-foot"><button type="button" class="btn small" data-add-work="' + s.id + '">' + icon('plus') + '작업</button></div>' +
                '</section>';
        }).join('');
        $('schedule-list').innerHTML = html || '<div class="card"><p class="empty">단계가 없습니다. 단계를 추가해 주세요.</p></div>';
    }

    function renderSchedule(keepScroll) {
        var isGantt = view === 'gantt';
        $('schedule-gantt').hidden = !isGantt;
        $('schedule-list').hidden = isGantt;
        $('zoom').hidden = !isGantt;
        $('go-today').hidden = !isGantt;
        document.querySelectorAll('#view-mode [data-view]').forEach(function (b) {
            b.classList.toggle('on', b.dataset.view === view);
            b.setAttribute('aria-pressed', b.dataset.view === view ? 'true' : 'false');
        });
        document.querySelectorAll('#zoom [data-zoom]').forEach(function (b) {
            b.classList.toggle('on', b.dataset.zoom === zoom);
            b.setAttribute('aria-pressed', b.dataset.zoom === zoom ? 'true' : 'false');
        });
        if (tab !== 'schedule') return;      // 숨겨진 상태에서는 폭을 잴 수 없다
        if (isGantt) renderGantt(keepScroll); else renderList();
    }

    // ── 정보 ────────────────────────────────────────────────────────
    function renderInfo() {
        function row(k, v) { return '<dt>' + k + '</dt><dd>' + (v ? esc(v) : '<span class="muted">-</span>') + '</dd>'; }
        var items = P.categoryKind === 'container' ? P.containers : P.equipments;
        var itemTitle = P.categoryKind === 'container' ? '용기 내역' : '기기 내역';
        var itemHtml = '';
        if (P.categoryKind !== 'general') {
            var total = (items || []).reduce(function (a, b) { return a + b.quantity; }, 0);
            itemHtml = '<section class="card"><header class="card-head"><h2>' + itemTitle + '</h2><span class="count">' + total + '</span></header>' +
                (items && items.length
                    ? '<table class="simple-table"><thead><tr><th>종류</th><th class="r">수량</th><th>비고</th></tr></thead><tbody>' +
                    items.map(function (it) {
                        return '<tr><td>' + esc(it.name) + '</td><td class="r">' + it.quantity + '</td><td>' + esc(it.note || '') + '</td></tr>';
                    }).join('') + '</tbody></table>'
                    : '<p class="empty">등록된 내역이 없습니다.</p>') +
                '</section>';
        }
        $('tab-info').innerHTML =
            '<div class="info-grid"><section class="card"><header class="card-head"><h2>기본 정보</h2></header><dl class="dl">' +
            row('유형', P.category) +
            row('위치', P.corporation + (P.locationPath ? ' · ' + P.locationPath : '')) +
            row('담당', P.owner) +
            row('착수일', P.startDate) +
            row('목표 종료', P.endTarget) +
            row('협력사', P.vendor) +
            row('등록', (P.createdBy || '') + (P.createdAt ? ' · ' + App.time(P.createdAt) : '')) +
            row('설명', P.description) +
            '</dl></section>' + itemHtml + '</div>' +
            '<section class="card danger-zone"><div><h3>과제 삭제</h3>' +
            '<p class="muted">단계·작업·활동 이력이 모두 삭제되며 되돌릴 수 없습니다.</p></div>' +
            '<span class="spacer"></span><button type="button" class="btn danger" id="btn-delete">' + icon('trash') + '과제 삭제</button></section>';
    }

    // ── 활동 ────────────────────────────────────────────────────────
    function feedIcon(type) {
        if (type === 'comment') return ['comment', 'comment'];
        if (type === 'targetChanged' || type === 'workPlanChanged') return ['clock', 'target'];
        if (type === 'stageCompleted') return ['check', 'done'];
        if (type === 'stateChanged') return ['flag', ''];
        if (type === 'created' || type === 'stageAdded' || type === 'workAdded') return ['plus', ''];
        if (type === 'stageRemoved' || type === 'workRemoved') return ['trash', ''];
        return ['edit', ''];
    }

    function feedItem(a) {
        var ic = feedIcon(a.type);
        return '<li><span class="feed-ic ' + ic[1] + '">' + icon(ic[0]) + '</span><div>' +
            '<div class="feed-text">' + esc(a.text) + '</div>' +
            (a.reason ? '<div class="feed-reason">' + esc(a.reason) + '</div>' : '') +
            '<div class="feed-meta">' + esc(a.user) + ' · ' + App.time(a.at) + '</div></div></li>';
    }

    function renderFeed() {
        var list = P.activities || [];
        $('activity-count').textContent = list.length ? String(list.length) : '';
        $('feed').innerHTML = list.length ? list.map(feedItem).join('') : '<li><span></span><p class="muted">아직 활동이 없습니다.</p></li>';
    }

    function renderAll(keepScroll) {
        renderSummary();
        renderStepper();
        renderSchedule(keepScroll);
        renderInfo();
        renderFeed();
    }

    // ── 시트: 단계 ──────────────────────────────────────────────────
    function field(name, label, inner, optional) {
        return '<label class="field" data-field="' + name + '"><span class="label">' + label +
            (optional ? ' <span class="opt">선택</span>' : '') + '</span>' + inner + '</label>';
    }

    function openStageSheet(s) {
        var isNew = !s;
        var index = isNew ? -1 : P.stages.indexOf(s);
        var later = isNew ? 0 : P.stages.filter(function (x) { return x.sort > s.sort && !x.completedDate && x.targetDate; }).length;

        var html = field('name', '단계명', '<input name="name" maxlength="50" required value="' + esc(isNew ? '' : s.name) + '" />');
        if (!isNew && s.forecastOver > 0) {
            html += '<p class="form-warn">' + icon('clock') + '<span>작업 기준 예상 종료 ' + s.forecast + ' · 목표일보다 ' + s.forecastOver + '일 늦습니다.</span></p>';
        }
        html += field('targetDate', '목표일', '<input type="date" name="targetDate" value="' + (isNew ? '' : s.targetDate || '') + '" />', true);
        if (!isNew && s.inboundHint && s.inboundHint !== s.targetDate) {
            html += '<div class="form-note"><span>추천 입고일 <b>' + s.inboundHint + '</b></span>' +
                '<span class="muted">검수일 + 해상 운송 ' + P.oceanLeadDays + '일</span>' +
                '<button type="button" class="btn small" data-apply-hint>적용</button></div>';
        }
        if (!isNew) {
            html += '<div data-reason hidden class="form">' +
                field('reason', '변경 사유', '<textarea name="reason" rows="2" maxlength="500" placeholder="목표일을 바꾸는 이유"></textarea>') +
                (later > 0 ? '<label class="check"><input type="checkbox" name="cascade" />이후 미완료 단계 ' + later + '개도 <span data-delta></span> 이동</label>' : '') +
                '</div>';
            html += '<div class="field" data-field="completedDate"><span class="label">완료일</span>' +
                '<input type="date" name="completedDate" value="' + (s.completedDate || '') + '" />' +
                '<div class="inline-btns"><button type="button" class="btn small" data-done-today>' + icon('check') + '오늘 완료</button>' +
                '<button type="button" class="btn small" data-clear-done>완료 취소</button></div></div>' +
                '<p class="form-warn" data-future hidden>' + icon('alert') + '<span>완료일이 오늘보다 미래입니다.</span></p>';
            html += '<div class="field"><span class="label">순서</span><div class="inline-btns">' +
                '<button type="button" class="btn small" data-move="-1"' + (index <= 0 ? ' disabled' : '') + '>' + icon('up') + '앞으로</button>' +
                '<button type="button" class="btn small" data-move="1"' + (index >= P.stages.length - 1 ? ' disabled' : '') + '>' + icon('down') + '뒤로</button></div></div>';
        }

        App.sheet({
            title: isNew ? '단계 추가' : '단계 · ' + s.name,
            html: html,
            primary: isNew ? '추가' : '저장',
            danger: isNew ? null : {
                label: '단계 삭제',
                onClick: async function (sheet) {
                    var ok = await App.confirm({
                        title: '단계 삭제',
                        message: '"' + s.name + '" 단계' + (s.works.length ? '와 작업 ' + s.works.length + '건' : '') + '을 삭제합니다. 되돌릴 수 없습니다.',
                        verb: '단계 삭제'
                    });
                    if (!ok) return;
                    try {
                        await api('DeleteStage', { id: s.id });
                        sheet.close();
                        App.toast('단계를 삭제했습니다.');
                    } catch (err) { sheet.setError(err.message, err.field); }
                }
            },
            onOpen: function (form, sheet) {
                var target = form.elements.targetDate;
                var reasonBox = form.querySelector('[data-reason]');
                var delta = form.querySelector('[data-delta]');
                var done = form.elements.completedDate;
                var future = form.querySelector('[data-future]');

                function onTarget() {
                    if (!reasonBox) return;
                    var changed = s.targetDate && target.value !== s.targetDate;
                    reasonBox.hidden = !changed;
                    if (delta && changed && target.value) {
                        var d = D.diff(target.value, s.targetDate);
                        delta.textContent = (d > 0 ? '+' : '') + d + '일';
                    }
                }
                function onDone() { if (future) future.hidden = !(done.value && done.value > today); }

                target.addEventListener('input', onTarget);
                if (done) done.addEventListener('input', onDone);

                var hintBtn = form.querySelector('[data-apply-hint]');
                if (hintBtn) hintBtn.addEventListener('click', function () { target.value = s.inboundHint; onTarget(); });
                var todayBtn = form.querySelector('[data-done-today]');
                if (todayBtn) todayBtn.addEventListener('click', function () { done.value = today; onDone(); });
                var clearBtn = form.querySelector('[data-clear-done]');
                if (clearBtn) clearBtn.addEventListener('click', function () { done.value = ''; onDone(); });

                form.querySelectorAll('[data-move]').forEach(function (b) {
                    b.addEventListener('click', async function () {
                        try {
                            await api('MoveStage', { id: s.id, dir: +b.dataset.move });
                            sheet.close();
                        } catch (err) { sheet.setError(err.message, err.field); }
                    });
                });
            },
            onSubmit: async function (form) {
                var f = form.elements;
                await api('SaveStage', {
                    projectId: P.id,
                    id: isNew ? null : s.id,
                    name: f.name.value,
                    targetDate: f.targetDate.value || null,
                    completedDate: isNew ? null : (f.completedDate.value || null),
                    reason: f.reason ? f.reason.value : null,
                    cascade: f.cascade ? f.cascade.checked : false
                });
                App.toast(isNew ? '단계를 추가했습니다.' : '단계를 저장했습니다.');
            }
        });
    }

    // ── 시트: 작업 ──────────────────────────────────────────────────
    function workFeedHtml(workId) {
        var list = (P.activities || []).filter(function (a) { return a.workId === workId; });
        if (!list.length) return '<li class="muted">아직 기록이 없습니다.</li>';
        return list.map(function (a) {
            return '<li><div class="feed-text">' + esc(a.text) + '</div>' +
                (a.reason ? '<div class="feed-reason">' + esc(a.reason) + '</div>' : '') +
                '<div class="feed-meta">' + esc(a.user) + ' · ' + App.time(a.at) + '</div></li>';
        }).join('');
    }

    function openWorkSheet(w, stageId) {
        var isNew = !w;
        if (isNew && !P.stages.length) { App.toast('단계를 먼저 추가해 주세요.', 'error'); return; }

        var sid = isNew ? stageId : w.stageId;
        var stage = stageById(sid) || P.stages[0];
        var planStart = isNew ? today : w.planStart;
        var planEnd = isNew ? (stage.targetDate && stage.targetDate >= today ? stage.targetDate : D.add(today, 7)) : w.planEnd;

        var stageOptions = P.stages.map(function (s) {
            return '<option value="' + s.id + '"' + (s.id === stage.id ? ' selected' : '') + '>' + esc(s.name) + '</option>';
        }).join('');

        var html =
            field('title', '작업명', '<input name="title" maxlength="200" required value="' + esc(isNew ? '' : w.title) + '" />') +
            field('stageId', '단계', '<select name="stageId">' + stageOptions + '</select>') +
            '<div class="field-row">' +
            field('owner', '담당', '<input name="owner" maxlength="50" list="dl-users" autocomplete="off" value="' + esc(isNew ? data.me.name : w.owner || '') + '" />') +
            field('coOwner', '부담당', '<input name="coOwner" maxlength="50" list="dl-users" autocomplete="off" value="' + esc(isNew ? '' : w.coOwner || '') + '" />', true) +
            '</div><div class="field-row">' +
            field('planStart', '목표 시작', '<input type="date" name="planStart" required value="' + planStart + '" />') +
            field('planEnd', '목표 종료', '<input type="date" name="planEnd" required value="' + planEnd + '" />') +
            '</div>' +
            (isNew ? '' : '<div data-reason hidden>' +
                field('reason', '변경 사유', '<textarea name="reason" rows="2" maxlength="500" placeholder="목표 일정을 바꾸는 이유"></textarea>') + '</div>') +
            '<div class="field-row">' +
            field('actualStart', '실적 시작', '<input type="date" name="actualStart" value="' + (isNew ? '' : w.actualStart || '') + '" />', true) +
            field('actualEnd', '실적 종료', '<input type="date" name="actualEnd" value="' + (isNew ? '' : w.actualEnd || '') + '" />', true) +
            '</div>' +
            '<div class="inline-btns"><button type="button" class="btn small" data-start-today>오늘 시작</button>' +
            '<button type="button" class="btn small" data-end-today>' + icon('check') + '오늘 완료</button></div>' +
            '<p class="form-warn" data-future hidden>' + icon('alert') + '<span>실적 일자가 오늘보다 미래입니다.</span></p>' +
            '<div class="field" data-field="progress"><span class="label">진행률</span><div class="range-row">' +
            '<input type="range" name="progress" min="0" max="100" step="5" value="' + (isNew ? 0 : w.progress) + '" aria-label="진행률" />' +
            '<output></output></div></div>' +
            field('note', '메모', '<textarea name="note" rows="2" maxlength="1000">' + esc(isNew ? '' : w.note || '') + '</textarea>', true);

        if (!isNew) {
            html += '<div class="sheet-section"><h3>진행 내용</h3><ul class="mini-feed" data-feed>' + workFeedHtml(w.id) + '</ul>' +
                '<textarea data-comment rows="2" maxlength="1000" placeholder="이 작업의 진행 내용 남기기" aria-label="진행 내용"></textarea>' +
                '<div class="inline-btns"><button type="button" class="btn small" data-add-comment>' + icon('comment') + '등록</button></div></div>';
        }

        App.sheet({
            title: isNew ? '작업 추가' : '작업 · ' + w.title,
            html: html,
            primary: isNew ? '추가' : '저장',
            danger: isNew ? null : {
                label: '작업 삭제',
                onClick: async function (sheet) {
                    var ok = await App.confirm({ title: '작업 삭제', message: '"' + w.title + '" 작업을 삭제합니다. 되돌릴 수 없습니다.', verb: '작업 삭제' });
                    if (!ok) return;
                    try {
                        await api('DeleteWork', { id: w.id });
                        sheet.close();
                        App.toast('작업을 삭제했습니다.');
                    } catch (err) { sheet.setError(err.message, err.field); }
                }
            },
            onOpen: function (form, sheet) {
                var f = form.elements;
                var out = form.querySelector('output');
                var reasonBox = form.querySelector('[data-reason]');
                var future = form.querySelector('[data-future]');

                function sync() {
                    // 종료일이 있으면 100% 로 고정
                    if (f.actualEnd.value) { f.progress.value = 100; f.progress.disabled = true; }
                    else f.progress.disabled = false;
                    out.textContent = f.progress.value + '%';
                    future.hidden = !((f.actualStart.value && f.actualStart.value > today) || (f.actualEnd.value && f.actualEnd.value > today));
                    if (reasonBox) reasonBox.hidden = !(f.planStart.value !== w.planStart || f.planEnd.value !== w.planEnd);
                }
                ['planStart', 'planEnd', 'actualStart', 'actualEnd', 'progress'].forEach(function (n) { f[n].addEventListener('input', sync); });
                form.querySelector('[data-start-today]').addEventListener('click', function () { f.actualStart.value = today; sync(); });
                form.querySelector('[data-end-today]').addEventListener('click', function () {
                    if (!f.actualStart.value) f.actualStart.value = today;
                    f.actualEnd.value = today;
                    sync();
                });
                sync();

                var addComment = form.querySelector('[data-add-comment]');
                if (addComment) {
                    addComment.addEventListener('click', async function () {
                        var box = form.querySelector('[data-comment]');
                        try {
                            await api('Comment', { projectId: P.id, stageId: w.stageId, workId: w.id, text: box.value });
                            box.value = '';
                            form.querySelector('[data-feed]').innerHTML = workFeedHtml(w.id);
                            sheet.setError(null);
                        } catch (err) { sheet.setError(err.message, err.field); }
                    });
                }
            },
            onSubmit: async function (form) {
                var f = form.elements;
                await api('SaveWork', {
                    id: isNew ? null : w.id,
                    stageId: +f.stageId.value,
                    title: f.title.value,
                    owner: f.owner.value,
                    coOwner: f.coOwner.value,
                    planStart: f.planStart.value || null,
                    planEnd: f.planEnd.value || null,
                    actualStart: f.actualStart.value || null,
                    actualEnd: f.actualEnd.value || null,
                    progress: +f.progress.value,
                    note: f.note.value,
                    reason: f.reason ? f.reason.value : null
                });
                App.toast(isNew ? '작업을 추가했습니다.' : '작업을 저장했습니다.');
            }
        });
    }

    // ── 시트: 과제 상태 ─────────────────────────────────────────────
    function openStateSheet() {
        var options = [];
        if (P.state !== 'active') options.push(['active', '진행으로 되돌리기']);
        if (P.state === 'active') options.push(['hold', '보류']);
        if (P.state !== 'dropped') options.push(['dropped', '중단']);

        var html = '<div class="field" data-field="state"><span class="label">바꿀 상태</span><div class="choice-row">' +
            options.map(function (o, i) {
                return '<label class="choice"><input type="radio" name="state" value="' + o[0] + '"' + (i === 0 ? ' checked' : '') + ' /><span>' + o[1] + '</span></label>';
            }).join('') + '</div></div>' +
            field('reason', '사유', '<textarea name="reason" rows="3" maxlength="500" placeholder="보류·중단은 사유가 필요합니다"></textarea>');

        App.sheet({
            title: '상태 변경',
            html: html,
            primary: '변경',
            onSubmit: async function (form) {
                await api('State', { projectId: P.id, state: form.elements.state.value, reason: form.elements.reason.value });
                App.toast('상태를 변경했습니다.');
            }
        });
    }

    async function deleteProject() {
        var ok = await App.confirm({
            title: '과제 삭제',
            message: '"' + P.title + '" 과제와 모든 단계·작업·활동 이력을 삭제합니다. 되돌릴 수 없습니다.',
            verb: '과제 삭제'
        });
        if (!ok) return;
        try {
            var r = await App.post(App.url('/Projects/Detail?handler=Delete'), { id: P.id });
            window.location.href = r.redirect || App.url('/Projects');
        } catch (err) { App.toast(err.message, 'error'); }
    }

    // ── 이벤트 ──────────────────────────────────────────────────────
    document.querySelectorAll('.tabs [data-tab]').forEach(function (b) {
        b.addEventListener('click', function () {
            tab = b.dataset.tab;
            document.querySelectorAll('.tabs [data-tab]').forEach(function (x) {
                x.classList.toggle('on', x === b);
                x.setAttribute('aria-selected', x === b ? 'true' : 'false');
            });
            ['schedule', 'info', 'activity'].forEach(function (t) { $('tab-' + t).hidden = t !== tab; });
            if (tab === 'schedule') renderSchedule(true);
        });
    });

    document.querySelectorAll('#view-mode [data-view]').forEach(function (b) {
        b.addEventListener('click', function () {
            view = b.dataset.view;
            App.pref.set('detail.view', view);
            renderSchedule(false);
        });
    });

    document.querySelectorAll('#zoom [data-zoom]').forEach(function (b) {
        b.addEventListener('click', function () {
            zoom = b.dataset.zoom;
            App.pref.set('detail.zoom', zoom);
            renderSchedule(false);
        });
    });

    $('go-today').addEventListener('click', function () { if (gantt) gantt.scrollToToday(); });
    $('add-stage').addEventListener('click', function () { openStageSheet(null); });
    $('add-work').addEventListener('click', function () {
        var current = P.stages.filter(function (s) { return !s.completedDate; })[0] || P.stages[P.stages.length - 1];
        openWorkSheet(null, current ? current.id : null);
    });

    document.getElementById('detail').addEventListener('click', function (e) {
        var t;
        if ((t = e.target.closest('#stepper [data-stage], #schedule-list [data-stage]'))) openStageSheet(stageById(+t.dataset.stage));
        else if ((t = e.target.closest('#schedule-list [data-work]'))) openWorkSheet(workById(+t.dataset.work));
        else if ((t = e.target.closest('[data-add-work]'))) openWorkSheet(null, +t.dataset.addWork);
        else if (e.target.closest('#btn-state')) openStateSheet();
        else if (e.target.closest('#btn-delete')) deleteProject();
    });

    $('comment-form').addEventListener('submit', async function (e) {
        e.preventDefault();
        var box = $('comment-text');
        try {
            await api('Comment', { projectId: P.id, text: box.value });
            box.value = '';
            App.toast('등록했습니다.');
        } catch (err) { App.toast(err.message, 'error'); }
    });

    // 담당자 자동완성 목록
    var dl = document.createElement('datalist');
    dl.id = 'dl-users';
    dl.innerHTML = (data.users || []).map(function (n) { return '<option value="' + esc(n) + '"></option>'; }).join('');
    document.body.appendChild(dl);

    var timer = null;
    var lastWidth = window.innerWidth;
    window.addEventListener('resize', function () {
        if (window.innerWidth === lastWidth) return;
        lastWidth = window.innerWidth;
        clearTimeout(timer);
        timer = setTimeout(function () { if (view === 'gantt') renderSchedule(true); }, 150);
    });

    renderAll(false);
})();
