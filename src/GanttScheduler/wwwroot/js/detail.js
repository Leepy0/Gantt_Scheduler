// 과제 상세: 요약, 단계 진행, 일정(간트/목록), 정보, 자료, 활동 + 단계·작업 편집 시트
(function () {
    'use strict';

    var data = App.data();
    var P = data.project;
    var today = data.today;
    var D = App.d, esc = App.esc, icon = App.icon;

    var view = App.pref.get('detail.view', '') || (App.isMobile() ? 'list' : 'gantt');
    var zoom = App.pref.get('detail.zoom', 'week');
    var showBaseline = App.pref.get('detail.baseline', '1') === '1';
    var showLinks = App.pref.get('detail.links', '1') === '1';
    var collapsed = {};
    try { JSON.parse(App.pref.get('detail.collapsed.' + P.id, '[]')).forEach(function (id) { collapsed[id] = true; }); } catch (e) { /* 무시 */ }
    var gantt = null;
    var tab = 'schedule';

    var $ = function (id) { return document.getElementById(id); };
    var detailUrl = function (handler) { return App.url('/Projects/Detail?handler=' + handler); };

    // 휴일 표시용(입력 안내). 일정 계산은 서버가 한다
    var holidays = {};
    (data.holidays || []).forEach(function (h) { holidays[h.date] = h.name; });
    function isOff(date) {
        var w = D.dow(D.num(date));
        return w === 0 || w === 6 || holidays[date] != null;
    }
    function workDays(from, to) {
        var n = 0;
        for (var d = D.num(from), end = D.num(to); d <= end; d++) if (!isOff(D.str(d))) n++;
        return n;
    }

    // 변경 요청 → 서버가 돌려준 최신 과제로 화면 전체를 다시 그린다.
    // 다른 사람이 먼저 고쳤으면(409) 최신 내용을 다시 읽어 보여 주고, 오류는 그대로 올려 시트에 표시한다
    function api(handler, body) {
        return App.post(detailUrl(handler), body).then(function (r) {
            if (r.project) { P = r.project; renderAll(true); }
            return r;
        }, function (err) {
            if (err.data && err.data.conflict) {
                return App.get(detailUrl('Data') + '&id=' + P.id).then(function (r) {
                    P = r.project;
                    renderAll(true);
                    throw err;
                }, function () { throw err; });
            }
            throw err;
        });
    }

    // ── 표시용 조각 ─────────────────────────────────────────────────
    function chip(cls, iconName, text) {
        return '<span class="chip ' + cls + '">' + (iconName ? icon(iconName) : '') + esc(text) + '</span>';
    }

    function signed(n) { return (n > 0 ? '+' : '') + n; }

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

    function slipChip(days) {
        if (days == null) return '';
        if (days > 0) return chip('alarm', 'pin', '기준선 ' + signed(days) + '일');
        if (days < 0) return chip('ok', 'pin', '기준선 ' + signed(days) + '일');
        return chip('', 'pin', '기준선 유지');
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

    function allWorks() {
        var list = [];
        P.stages.forEach(function (s) { s.works.forEach(function (w) { list.push({ work: w, stage: s }); }); });
        return list;
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

        var chips = chip('', null, P.category) + stateChip(P);
        if (P.baselineAt) chips += slipChip(P.slipDays);
        if (P.criticalCount > 0) chips += chip('warn', 'bolt', '임계 작업 ' + P.criticalCount);

        $('summary').innerHTML =
            '<div class="summary-top">' +
            '<div class="summary-chips">' + chips + '</div>' +
            '<div class="summary-actions">' +
            '<a class="btn small" href="' + App.url('/Projects/Edit?id=' + P.id) + '">' + icon('edit') + '수정</a>' +
            '<button type="button" class="btn small" id="btn-clone">' + icon('copy') + '복제</button>' +
            '<button type="button" class="btn small" id="btn-baseline">' + icon('pin') + (P.baselineAt ? '기준선 다시 확정' : '기준선 확정') + '</button>' +
            '<a class="btn small" href="' + detailUrl('Excel') + '&id=' + P.id + '">' + icon('download') + 'Excel</a>' +
            '<a class="btn small" href="' + detailUrl('Pdf') + '&id=' + P.id + '">' + icon('download') + 'PDF</a>' +
            (P.state === 'done' ? '' : '<button type="button" class="btn small" id="btn-state">' + icon('flag') + '상태 변경</button>') +
            '</div></div>' +
            '<div class="summary-meta">' +
            meta('위치', P.corporation + (P.locationPath ? ' · ' + P.locationPath : '')) +
            meta('담당', P.owner + (P.ownerOrg ? ' (' + P.ownerOrg + ')' : '')) +
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
            var extra = [];
            if (s.slipDays) extra.push('기준선 ' + signed(s.slipDays) + '일');
            if (s.checkTotal) extra.push('체크 ' + s.checkDone + '/' + s.checkTotal);
            return '<button type="button" class="step ' + s.status + '" data-stage="' + s.id + '">' +
                '<span class="step-name">' + ic + esc(s.name) + '</span><span class="step-date">' + date + '</span>' +
                (extra.length ? '<span class="step-date">' + extra.join(' · ') + '</span>' : '') + '</button>';
        }).join('');
    }

    // ── 일정: 간트 ──────────────────────────────────────────────────
    var stageLane = { planTop: 11, planH: 12, actTop: 27, actH: 12 };

    function workBars(w) {
        var bars = [];
        if (showBaseline && w.baselineStart && w.baselineEnd) {
            bars.push({ from: w.baselineStart, to: w.baselineEnd, top: 4, h: 3, cls: 'base', title: '기준선 ' + w.baselineStart + ' ~ ' + w.baselineEnd });
        }
        // 목표 막대: 끝나지 않은 작업은 끌어서 옮기거나 양 끝을 늘릴 수 있다
        bars.push({
            from: w.planStart, to: w.planEnd, top: 10, h: 8, cls: 'plan' + (w.critical ? ' crit' : ''), anchor: true,
            drag: w.actualEnd ? null : 'move', kind: 'workPlan',
            title: '목표 ' + w.planStart + ' ~ ' + w.planEnd + ' (근무일 ' + w.planWorkDays + '일)' + (w.critical ? ' · 여유 없음' : '')
        });
        var top = 22, h = 12;
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
            if (s.checkTotal) sub += ' · 체크 ' + s.checkDone + '/' + s.checkTotal;

            var bars = Gantt.stageBars(s, i, stageLane, false);
            // 미완료 단계의 목표 막대는 오른쪽 끝을 끌어 목표일을 바꿀 수 있다
            bars.forEach(function (b) { if (b.kind === 'plan' && !s.completedDate) b.drag = 'end'; });

            var marks = [];
            if (s.forecast) {
                marks.push({
                    date: s.forecast, top: 12, cls: s.forecastOver > 0 ? 'over' : '',
                    title: '작업 기준 예상 종료 ' + s.forecast + (s.forecastOver > 0 ? ' (목표일 +' + s.forecastOver + '일)' : '')
                });
            }
            if (showBaseline && s.baselineDate) {
                marks.push({ date: s.baselineDate, top: 4, cls: 'tick', title: '기준선 ' + s.baselineDate + (s.slipDays ? ' (' + signed(s.slipDays) + '일)' : '') });
            }
            rows.push({
                key: 's' + s.id, cls: 'stage', title: s.name, sub: sub, height: 50,
                side: sideIcon + '<span>' + s.progress + '%</span>',
                toggle: s.works.length ? { expanded: !collapsed[s.id] } : null,
                bars: bars, marks: marks,
                data: { stage: s.id }
            });
            if (collapsed[s.id]) return;
            s.works.forEach(function (w) {
                var side = '';
                if (w.depViolation) side += '<span class="alarm" title="선행 작업보다 먼저 시작">' + icon('link') + '</span>';
                else if (w.critical) side += '<span class="warn" title="여유 없음">' + icon('bolt') + '</span>';
                if (w.late) side += '<span class="alarm">' + icon('alert') + '</span>';
                rows.push({
                    key: 'w' + w.id, cls: 'work', title: w.title, sub: owners(w), height: 42,
                    side: side + '<span>' + w.progress + '%</span>',
                    bars: workBars(w), data: { work: w.id }
                });
            });
        });
        return rows;
    }

    function ganttLinks() {
        if (!showLinks) return [];
        return (P.links || []).map(function (l) {
            var to = workById(l.to);
            var from = workById(l.from);
            var cls = to && to.depViolation ? 'bad' : (to && from && to.critical && from.critical ? 'crit' : '');
            return { from: 'w' + l.from, to: 'w' + l.to, cls: cls };
        });
    }

    function renderGantt(keepScroll) {
        gantt = Gantt.render($('gantt'), {
            rows: ganttRows(), links: ganttLinks(), today: today, holidays: data.holidays, zoom: zoom,
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
            },
            onBarDrag: function (r, bar, range) {
                if (r.data.work) openPlanChange(workById(r.data.work), range.from, range.to);
                else if (r.data.stage) openTargetChange(stageById(r.data.stage), range.to);
            }
        });
    }

    // ── 일정: 목록 ──────────────────────────────────────────────────
    function renderList() {
        var titles = {};
        allWorks().forEach(function (x) { titles[x.work.id] = x.work.title; });

        var html = P.stages.map(function (s) {
            var dates = (s.targetDate ? '목표 ' + D.md(s.targetDate) : '목표일 미정') +
                (s.completedDate ? '<br>완료 ' + D.md(s.completedDate) : '') +
                (s.slipDays ? '<br>기준선 ' + signed(s.slipDays) + '일' : '');
            var warn = s.forecastOver > 0
                ? '<div class="stage-warn">' + icon('clock') + '작업 기준 예상 종료 ' + D.md(s.forecast) + ' · 목표일보다 ' + s.forecastOver + '일 늦음</div>'
                : '';
            var works = s.works.map(function (w) {
                var range = '목표 ' + D.md(w.planStart) + '~' + D.md(w.planEnd) + ' (' + w.planWorkDays + '일)' +
                    (w.actualStart ? ' · 실적 ' + D.md(w.actualStart) + '~' + (w.actualEnd ? D.md(w.actualEnd) : '') : '');
                var who = owners(w);
                var pre = w.predecessors.map(function (id) { return titles[id]; }).filter(Boolean);
                var chips = workChip(w);
                if (w.depViolation) chips += chip('alarm', 'link', '선행보다 이름');
                else if (w.critical) chips += chip('warn', 'bolt', '임계');
                return '<li><button type="button" class="work-item" data-work="' + w.id + '">' +
                    '<span class="work-title">' + esc(w.title) + '</span><span class="work-chips">' + chips + '</span>' +
                    '<span class="work-meta">' + (who ? esc(who) + ' · ' : '') + range +
                    (pre.length ? ' · 선행: ' + esc(pre.join(', ')) : '') + '</span>' +
                    '<span class="work-progress"><span class="progress" aria-hidden="true"><i style="width:' + w.progress + '%"></i></span>' +
                    '<span class="num">' + w.progress + '%</span></span></button></li>';
            }).join('');
            return '<section class="stage-card ' + s.status + '">' +
                '<button type="button" class="stage-head" data-stage="' + s.id + '"><h3>' + esc(s.name) + '</h3>' + stageChip(s) +
                (s.checkTotal ? chip(s.checkDone === s.checkTotal ? 'ok' : '', 'tasks', '체크 ' + s.checkDone + '/' + s.checkTotal) : '') +
                '<span class="stage-dates">' + dates + '</span></button>' + warn +
                (works ? '<ul class="work-list">' + works + '</ul>' : '') +
                '<div class="stage-foot"><button type="button" class="btn small" data-add-work="' + s.id + '">' + icon('plus') + '작업</button></div>' +
                '</section>';
        }).join('');
        $('schedule-list').innerHTML = html || '<div class="card"><p class="empty">단계가 없습니다. 단계를 추가해 주세요.</p></div>';
    }

    function setPressed(id, on) {
        $(id).classList.toggle('on', on);
        $(id).setAttribute('aria-pressed', on ? 'true' : 'false');
    }

    function renderSchedule(keepScroll) {
        var isGantt = view === 'gantt';
        $('schedule-gantt').hidden = !isGantt;
        $('schedule-list').hidden = isGantt;
        ['zoom', 'go-today', 'show-baseline', 'show-links'].forEach(function (id) { $(id).hidden = !isGantt; });
        document.querySelectorAll('#view-mode [data-view]').forEach(function (b) {
            b.classList.toggle('on', b.dataset.view === view);
            b.setAttribute('aria-pressed', b.dataset.view === view ? 'true' : 'false');
        });
        document.querySelectorAll('#zoom [data-zoom]').forEach(function (b) {
            b.classList.toggle('on', b.dataset.zoom === zoom);
            b.setAttribute('aria-pressed', b.dataset.zoom === zoom ? 'true' : 'false');
        });
        setPressed('show-baseline', showBaseline);
        setPressed('show-links', showLinks);
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
        var baseline = P.baselineAt
            ? App.time(P.baselineAt) + ' 확정' + (P.baselineEnd ? ' · 종료 ' + P.baselineEnd : '') + (P.slipDays != null ? ' · 현재 ' + signed(P.slipDays) + '일' : '')
            : null;
        $('tab-info').innerHTML =
            '<div class="info-grid"><section class="card"><header class="card-head"><h2>기본 정보</h2></header><dl class="dl">' +
            row('유형', P.category) +
            row('위치', P.corporation + (P.locationPath ? ' · ' + P.locationPath : '')) +
            row('담당', P.owner + (P.ownerOrg ? ' (' + P.ownerOrg + ')' : '')) +
            row('착수일', P.startDate) +
            row('목표 종료', P.endTarget) +
            row('기준선', baseline) +
            row('목표일 변경', P.targetChanges ? P.targetChanges + '회' : null) +
            row('협력사', P.vendor) +
            row('등록', (P.createdBy || '') + (P.createdAt ? ' · ' + App.time(P.createdAt) : '')) +
            row('설명', P.description) +
            '</dl></section>' + itemHtml + '</div>' +
            '<section class="card danger-zone"><div><h3>과제 삭제</h3>' +
            '<p class="muted">단계·작업·첨부·활동 이력이 모두 삭제되며 되돌릴 수 없습니다.</p></div>' +
            '<span class="spacer"></span><button type="button" class="btn danger" id="btn-delete">' + icon('trash') + '과제 삭제</button></section>';
    }

    // ── 자료(첨부) ──────────────────────────────────────────────────
    function fileUrl(id, inline) {
        return App.url('/Projects/Files?handler=Download&id=' + id + (inline ? '&inline=true' : ''));
    }

    function renderFiles() {
        var list = P.attachments || [];
        $('files-count').textContent = list.length ? String(list.length) : '';
        $('file-stage').innerHTML = '<option value="">과제 전체</option>' + P.stages.map(function (s) {
            return '<option value="' + s.id + '">' + esc(s.name) + '</option>';
        }).join('');
        if (!list.length) {
            $('files').innerHTML = '<li class="files-empty muted">올린 자료가 없습니다.</li>';
            return;
        }
        $('files').innerHTML = list.map(function (a) {
            var stage = a.stageId ? stageById(a.stageId) : null;
            return '<li><span class="feed-ic">' + icon('file') + '</span>' +
                '<span class="row-main"><a class="row-title" href="' + fileUrl(a.id) + '">' + esc(a.fileName) + '</a>' +
                '<span class="row-sub">' + App.size(a.size) + ' · ' + esc(a.uploadedBy) + ' · ' + App.time(a.uploadedAt) +
                (stage ? ' · ' + esc(stage.name) : '') + '</span></span>' +
                (a.previewable ? '<button type="button" class="btn icon small" data-preview="' + a.id + '" aria-label="미리보기" title="미리보기">' + icon('eye') + '</button>' : '') +
                '<a class="btn icon small" href="' + fileUrl(a.id) + '" aria-label="내려받기" title="내려받기">' + icon('download') + '</a>' +
                '<button type="button" class="btn icon small" data-delete-file="' + a.id + '" aria-label="삭제" title="삭제">' + icon('trash') + '</button></li>';
        }).join('');
    }

    function previewFile(id) {
        var a = (P.attachments || []).filter(function (x) { return x.id === id; })[0];
        if (!a) return;
        var src = fileUrl(id, true);
        App.sheet({
            title: a.fileName,
            wide: true,
            html: a.contentType === 'application/pdf'
                ? '<iframe class="preview-frame" src="' + src + '" title="' + esc(a.fileName) + '"></iframe>'
                : '<img class="preview-img" src="' + src + '" alt="' + esc(a.fileName) + '" />'
        });
    }

    async function deleteFile(id) {
        var a = (P.attachments || []).filter(function (x) { return x.id === id; })[0];
        if (!a) return;
        var ok = await App.confirm({ title: '첨부 삭제', message: '"' + a.fileName + '" 파일을 삭제합니다. 되돌릴 수 없습니다.', verb: '삭제' });
        if (!ok) return;
        try {
            await api('DeleteFile', { id: id });
            App.toast('삭제했습니다.');
        } catch (err) { App.toast(err.message, 'error'); }
    }

    // ── 활동 ────────────────────────────────────────────────────────
    function feedIcon(type) {
        if (type === 'comment') return ['comment', 'comment'];
        if (type === 'targetChanged' || type === 'workPlanChanged') return ['clock', 'target'];
        if (type === 'baselineSet') return ['pin', ''];
        if (type === 'stageCompleted') return ['check', 'done'];
        if (type === 'checkChanged') return ['tasks', ''];
        if (type === 'stateChanged') return ['flag', ''];
        if (type === 'attachmentAdded' || type === 'attachmentRemoved') return ['clip', ''];
        if (type === 'cloned') return ['copy', ''];
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
        renderFiles();
        renderFeed();
    }

    // ── 시트 공통 ───────────────────────────────────────────────────
    function field(name, label, inner, optional) {
        return '<label class="field" data-field="' + name + '"><span class="label">' + label +
            (optional ? ' <span class="opt">선택</span>' : '') + '</span>' + inner + '</label>';
    }

    // 날짜 입력 아래의 안내: 휴일이면 알려 준다
    function offNote(date, what) {
        if (!date || !isOff(date)) return '';
        return what + '이 휴일' + (holidays[date] ? '(' + holidays[date] + ')' : '') + '입니다. ';
    }

    // ── 시트: 단계 ──────────────────────────────────────────────────
    function checksHtml(s) {
        if (!s.checks.length) return '<li class="muted">체크 항목이 없습니다.</li>';
        return s.checks.map(function (c) {
            return '<li><label class="check"><input type="checkbox" data-check="' + c.id + '"' + (c.done ? ' checked' : '') + ' />' +
                '<span>' + esc(c.text) + '</span></label>' +
                (c.done && c.doneBy ? '<span class="muted check-by">' + esc(c.doneBy) + '</span>' : '') +
                '<button type="button" class="btn icon small ghost" data-del-check="' + c.id + '" aria-label="항목 삭제">' + icon('x') + '</button></li>';
        }).join('');
    }

    function openStageSheet(s) {
        var isNew = !s;
        var index = isNew ? -1 : P.stages.indexOf(s);
        var later = isNew ? 0 : P.stages.filter(function (x) { return x.sort > s.sort && !x.completedDate && x.targetDate; }).length;
        var version = isNew ? null : s.version;
        var sid = isNew ? null : s.id;

        var html = field('name', '단계명', '<input name="name" maxlength="50" required value="' + esc(isNew ? '' : s.name) + '" />');
        if (!isNew && s.forecastOver > 0) {
            html += '<p class="form-warn">' + icon('clock') + '<span>작업 기준 예상 종료 ' + s.forecast + ' · 목표일보다 ' + s.forecastOver + '일 늦습니다.</span></p>';
        }
        html += field('targetDate', '목표일', '<input type="date" name="targetDate" value="' + (isNew ? '' : s.targetDate || '') + '" />', true) +
            '<span class="hint" data-target-hint></span>';
        if (!isNew && s.inboundHint && s.inboundHint !== s.targetDate) {
            html += '<div class="form-note"><span>추천 입고일 <b>' + s.inboundHint + '</b></span>' +
                '<span class="muted">검수일 + 해상 운송 ' + P.oceanLeadDays + '일</span>' +
                '<button type="button" class="btn small" data-apply-hint>적용</button></div>';
        }
        if (!isNew) {
            html += '<div data-reason hidden class="form">' +
                field('reason', '변경 사유', '<textarea name="reason" rows="2" maxlength="500" placeholder="목표일을 바꾸는 이유"></textarea>') +
                (later > 0 ? '<label class="check"><input type="checkbox" name="cascade" />이후 미완료 단계 ' + later + '개도 같은 근무일 수만큼 이동</label>' : '') +
                '</div>';
            html += '<div class="sheet-section" data-field="check"><h3>체크리스트 <span class="count" data-check-count></span></h3>' +
                '<ul class="check-list" data-checks></ul>' +
                '<div class="add-inline"><input data-new-check maxlength="200" placeholder="완료 조건 추가" aria-label="체크 항목" />' +
                '<button type="button" class="btn small" data-add-check>' + icon('plus') + '추가</button></div></div>';
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
                        await api('DeleteStage', { id: sid });
                        sheet.close();
                        App.toast('단계를 삭제했습니다.');
                    } catch (err) { sheet.setError(err.message, err.field); }
                }
            },
            onOpen: function (form, sheet) {
                var target = form.elements.targetDate;
                var reasonBox = form.querySelector('[data-reason]');
                var done = form.elements.completedDate;
                var future = form.querySelector('[data-future]');
                var hint = form.querySelector('[data-target-hint]');

                function onTarget() {
                    var notes = offNote(target.value, '목표일');
                    if (!isNew && s.baselineDate) {
                        var slip = target.value ? D.diff(target.value, s.baselineDate) : null;
                        notes += '기준선 ' + s.baselineDate + (slip == null ? '' : slip === 0 ? ' (그대로)' : ' (' + signed(slip) + '일)');
                    }
                    hint.textContent = notes;
                    if (reasonBox) reasonBox.hidden = !(s.targetDate && target.value !== s.targetDate);
                }
                function onDone() { if (future) future.hidden = !(done.value && done.value > today); }

                target.addEventListener('input', onTarget);
                onTarget();
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
                            await api('MoveStage', { id: sid, dir: +b.dataset.move });
                            sheet.close();
                        } catch (err) { sheet.setError(err.message, err.field); }
                    });
                });

                // 체크리스트: 누르는 즉시 저장하고 목록만 다시 그린다
                var checksEl = form.querySelector('[data-checks]');
                if (checksEl) {
                    var refresh = function () {
                        var cur = stageById(sid);
                        if (!cur) return;
                        checksEl.innerHTML = checksHtml(cur);
                        form.querySelector('[data-check-count]').textContent = cur.checkTotal ? cur.checkDone + '/' + cur.checkTotal : '';
                    };
                    var run = async function (handler, body) {
                        try {
                            await api(handler, body);
                            sheet.setError(null);
                        } catch (err) { sheet.setError(err.message, err.field); }
                        refresh();
                    };
                    refresh();
                    checksEl.addEventListener('change', function (e) {
                        var box = e.target.closest('[data-check]');
                        if (box) run('ToggleCheck', { id: +box.dataset.check, done: box.checked });
                    });
                    checksEl.addEventListener('click', function (e) {
                        var del = e.target.closest('[data-del-check]');
                        if (del) run('DeleteCheck', { id: +del.dataset.delCheck });
                    });
                    var input = form.querySelector('[data-new-check]');
                    var add = function () {
                        if (!input.value.trim()) return;
                        run('SaveCheck', { stageId: sid, id: null, text: input.value }).then(function () { input.value = ''; });
                    };
                    form.querySelector('[data-add-check]').addEventListener('click', add);
                    input.addEventListener('keydown', function (e) {
                        if (e.key === 'Enter') { e.preventDefault(); add(); }
                    });
                }
            },
            onSubmit: async function (form) {
                var f = form.elements;
                await api('SaveStage', {
                    projectId: P.id,
                    id: sid,
                    name: f.name.value,
                    targetDate: f.targetDate.value || null,
                    completedDate: isNew ? null : (f.completedDate.value || null),
                    reason: f.reason ? f.reason.value : null,
                    cascade: f.cascade ? f.cascade.checked : false,
                    version: version
                });
                App.toast(isNew ? '단계를 추가했습니다.' : '단계를 저장했습니다.');
            }
        });
    }

    // 간트에서 단계 목표 막대의 끝을 끌었을 때: 사유를 받아 저장하고, 취소하면 원래대로 되돌린다
    function openTargetChange(s, newDate) {
        if (!s || newDate === s.targetDate) { renderSchedule(true); return; }
        var later = P.stages.filter(function (x) { return x.sort > s.sort && !x.completedDate && x.targetDate; }).length;
        App.sheet({
            title: '목표일 변경 · ' + s.name,
            html: '<div class="form-note"><b>' + s.targetDate + ' → ' + newDate + '</b><span class="muted">' + signed(D.diff(newDate, s.targetDate)) + '일</span></div>' +
                (offNote(newDate, '목표일') ? '<p class="form-warn">' + icon('alert') + '<span>' + offNote(newDate, '목표일') + '</span></p>' : '') +
                field('reason', '변경 사유', '<textarea name="reason" rows="2" maxlength="500" placeholder="목표일을 바꾸는 이유" required></textarea>') +
                (later > 0 ? '<label class="check"><input type="checkbox" name="cascade" />이후 미완료 단계 ' + later + '개도 같은 근무일 수만큼 이동</label>' : ''),
            primary: '변경',
            onOpen: function (form) { form.elements.reason.focus(); },
            onClose: function (saved) { if (!saved) renderSchedule(true); },
            onSubmit: async function (form) {
                await api('SaveStage', {
                    projectId: P.id, id: s.id, name: s.name, targetDate: newDate, completedDate: s.completedDate,
                    reason: form.elements.reason.value, cascade: form.elements.cascade ? form.elements.cascade.checked : false, version: s.version
                });
                App.toast('목표일을 변경했습니다.');
            }
        });
    }

    // ── 시트: 작업 ──────────────────────────────────────────────────
    function workPayload(w, changes) {
        var body = {
            id: w.id, stageId: w.stageId, title: w.title, owner: w.owner || '', coOwner: w.coOwner || '',
            planStart: w.planStart, planEnd: w.planEnd, actualStart: w.actualStart, actualEnd: w.actualEnd,
            progress: w.progress, note: w.note || '', reason: null, version: w.version, predecessors: null
        };
        Object.keys(changes).forEach(function (k) { body[k] = changes[k]; });
        return body;
    }

    // 간트에서 작업 목표 막대를 끌었을 때
    function openPlanChange(w, from, to) {
        if (!w || (from === w.planStart && to === w.planEnd)) { renderSchedule(true); return; }
        var notes = offNote(from, '시작일') + offNote(to, '종료일');
        App.sheet({
            title: '목표 일정 변경 · ' + w.title,
            html: '<div class="form-note"><b>' + D.md(w.planStart) + ' ~ ' + D.md(w.planEnd) + ' → ' + D.md(from) + ' ~ ' + D.md(to) + '</b>' +
                '<span class="muted">근무일 ' + w.planWorkDays + '일 → ' + workDays(from, to) + '일</span></div>' +
                (notes ? '<p class="form-warn">' + icon('alert') + '<span>' + notes + '</span></p>' : '') +
                field('reason', '변경 사유', '<textarea name="reason" rows="2" maxlength="500" placeholder="목표 일정을 바꾸는 이유" required></textarea>') +
                '<span class="hint">후행 작업이 있으면 아직 시작하지 않은 작업은 함께 밀립니다.</span>',
            primary: '변경',
            onOpen: function (form) { form.elements.reason.focus(); },
            onClose: function (saved) { if (!saved) renderSchedule(true); },
            onSubmit: async function (form) {
                await api('SaveWork', workPayload(w, { planStart: from, planEnd: to, reason: form.elements.reason.value }));
                App.toast('목표 일정을 변경했습니다.');
            }
        });
    }

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

        // 선행 작업 후보: 같은 과제의 다른 작업
        var others = allWorks().filter(function (x) { return isNew || x.work.id !== w.id; });
        var preHtml = others.length
            ? '<div class="pick-list">' + others.map(function (x) {
                var on = !isNew && w.predecessors.indexOf(x.work.id) >= 0;
                return '<label class="check"><input type="checkbox" name="pre" value="' + x.work.id + '"' + (on ? ' checked' : '') + ' />' +
                    '<span>' + esc(x.work.title) + ' <span class="muted">' + esc(x.stage.name) + ' · ~' + D.md(x.work.planEnd) + '</span></span></label>';
            }).join('') + '</div>'
            : '<span class="hint">다른 작업이 없습니다.</span>';

        var status = '';
        if (!isNew) {
            if (w.depViolation) status += '<p class="form-error">' + icon('link') + '<span>선행 작업이 끝나기 전에 시작하도록 잡혀 있습니다. 목표 일정을 조정해 주세요.</span></p>';
            var bits = [];
            if (w.float != null) bits.push(w.float <= 0 ? '여유 없음 — 밀리면 단계 목표일이 밀립니다' : '여유 ' + w.float + '일');
            if (w.baselineStart) bits.push('기준선 ' + D.md(w.baselineStart) + ' ~ ' + D.md(w.baselineEnd));
            if (bits.length) status += '<div class="form-note">' + (w.critical ? icon('bolt') : icon('pin')) + '<span>' + bits.join(' · ') + '</span></div>';
        }

        var html =
            field('title', '작업명', '<input name="title" maxlength="200" required value="' + esc(isNew ? '' : w.title) + '" />') +
            field('stageId', '단계', '<select name="stageId">' + stageOptions + '</select>') +
            '<div class="field-row">' +
            field('owner', '담당', '<input name="owner" maxlength="50" list="dl-users" autocomplete="off" value="' + esc(isNew ? data.me.name : w.owner || '') + '" />') +
            field('coOwner', '부담당', '<input name="coOwner" maxlength="50" list="dl-users" autocomplete="off" value="' + esc(isNew ? '' : w.coOwner || '') + '" />', true) +
            '</div>' + status + '<div class="field-row">' +
            field('planStart', '목표 시작', '<input type="date" name="planStart" required value="' + planStart + '" />') +
            field('planEnd', '목표 종료', '<input type="date" name="planEnd" required value="' + planEnd + '" />') +
            '</div><span class="hint" data-plan-hint></span>' +
            (isNew ? '' : '<div data-reason hidden>' +
                field('reason', '변경 사유', '<textarea name="reason" rows="2" maxlength="500" placeholder="목표 일정을 바꾸는 이유"></textarea>') + '</div>') +
            '<div class="field" data-field="predecessors"><span class="label">선행 작업 <span class="opt">선택 — 끝난 다음 근무일부터 시작</span></span>' + preHtml + '</div>' +
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
                var planHint = form.querySelector('[data-plan-hint]');

                function sync() {
                    // 종료일이 있으면 100% 로 고정
                    if (f.actualEnd.value) { f.progress.value = 100; f.progress.disabled = true; }
                    else f.progress.disabled = false;
                    out.textContent = f.progress.value + '%';
                    future.hidden = !((f.actualStart.value && f.actualStart.value > today) || (f.actualEnd.value && f.actualEnd.value > today));
                    if (reasonBox) reasonBox.hidden = !(f.planStart.value !== w.planStart || f.planEnd.value !== w.planEnd);
                    var hint = '';
                    if (f.planStart.value && f.planEnd.value && f.planStart.value <= f.planEnd.value) {
                        hint = '근무일 ' + workDays(f.planStart.value, f.planEnd.value) + '일. ';
                    }
                    planHint.textContent = hint + offNote(f.planStart.value, '시작일') + offNote(f.planEnd.value, '종료일');
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
                var pre = Array.prototype.slice.call(form.querySelectorAll('input[name=pre]:checked')).map(function (x) { return +x.value; });
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
                    reason: f.reason ? f.reason.value : null,
                    version: isNew ? null : w.version,
                    predecessors: pre
                });
                App.toast(isNew ? '작업을 추가했습니다.' : '작업을 저장했습니다.');
            }
        });
    }

    // ── 시트: 과제 상태·복제·기준선 ─────────────────────────────────
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
                await api('State', { projectId: P.id, state: form.elements.state.value, reason: form.elements.reason.value, version: P.version });
                App.toast('상태를 변경했습니다.');
            }
        });
    }

    function openCloneSheet() {
        App.sheet({
            title: '과제 복제',
            html: field('title', '새 과제명', '<input name="title" maxlength="200" required value="' + esc(P.title + ' (복제)') + '" />') +
                field('startDate', '착수일', '<input type="date" name="startDate" required value="' + today + '" />') +
                '<label class="check"><input type="checkbox" name="works" checked />작업과 의존 관계도 복사</label>' +
                '<span class="hint">단계·체크리스트·내역을 복사하고, 날짜는 새 착수일에 맞춰 통째로 옮깁니다. 실적·활동·첨부·기준선은 복사하지 않습니다.</span>',
            primary: '복제',
            onSubmit: async function (form) {
                var f = form.elements;
                var r = await App.post(detailUrl('Clone'), {
                    projectId: P.id, title: f.title.value, startDate: f.startDate.value || null, includeWorks: f.works.checked
                });
                window.location.href = r.redirect;
                return false;
            }
        });
    }

    async function setBaseline() {
        if (P.baselineAt) {
            var ok = await App.confirm({
                title: '기준선 다시 확정',
                message: '지금의 목표 일정으로 기준선을 덮어씁니다. 이전 기준선은 되돌릴 수 없습니다.',
                verb: '다시 확정'
            });
            if (!ok) return;
        }
        try {
            await api('Baseline', { id: P.id });
            App.toast('지금의 목표 일정을 기준선으로 확정했습니다.');
        } catch (err) { App.toast(err.message, 'error'); }
    }

    async function deleteProject() {
        var ok = await App.confirm({
            title: '과제 삭제',
            message: '"' + P.title + '" 과제와 모든 단계·작업·첨부·활동 이력을 삭제합니다. 되돌릴 수 없습니다.',
            verb: '과제 삭제'
        });
        if (!ok) return;
        try {
            var r = await App.post(detailUrl('Delete'), { id: P.id });
            window.location.href = r.redirect || App.url('/Projects');
        } catch (err) { App.toast(err.message, 'error'); }
    }

    // ── 이벤트 ──────────────────────────────────────────────────────
    var TABS = ['schedule', 'info', 'files', 'activity'];
    document.querySelectorAll('.tabs [data-tab]').forEach(function (b) {
        b.addEventListener('click', function () {
            tab = b.dataset.tab;
            document.querySelectorAll('.tabs [data-tab]').forEach(function (x) {
                x.classList.toggle('on', x === b);
                x.setAttribute('aria-selected', x === b ? 'true' : 'false');
            });
            TABS.forEach(function (t) { $('tab-' + t).hidden = t !== tab; });
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

    $('show-baseline').addEventListener('click', function () {
        showBaseline = !showBaseline;
        App.pref.set('detail.baseline', showBaseline ? '1' : '0');
        renderSchedule(true);
    });
    $('show-links').addEventListener('click', function () {
        showLinks = !showLinks;
        App.pref.set('detail.links', showLinks ? '1' : '0');
        renderSchedule(true);
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
        else if ((t = e.target.closest('[data-preview]'))) previewFile(+t.dataset.preview);
        else if ((t = e.target.closest('[data-delete-file]'))) deleteFile(+t.dataset.deleteFile);
        else if (e.target.closest('#btn-state')) openStateSheet();
        else if (e.target.closest('#btn-clone')) openCloneSheet();
        else if (e.target.closest('#btn-baseline')) setBaseline();
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

    $('upload-form').addEventListener('submit', async function (e) {
        e.preventDefault();
        var input = $('file-input');
        var file = input.files && input.files[0];
        if (!file) { App.toast('파일을 선택해 주세요.', 'error'); return; }
        // 서버에 보내기 전에 걸러 헛걸음을 줄인다 (서버도 같은 검사를 한다)
        var dot = file.name.lastIndexOf('.');
        var ext = dot >= 0 ? file.name.slice(dot).toLowerCase() : '';
        if (data.upload.extensions.indexOf(ext) < 0) { App.toast('올릴 수 없는 형식입니다(' + (ext || '확장자 없음') + ').', 'error'); return; }
        if (file.size > data.upload.maxBytes) { App.toast('파일은 30MB 이하만 올릴 수 있습니다.', 'error'); return; }

        var btn = $('upload-btn');
        var label = btn.innerHTML;
        btn.disabled = true;
        btn.innerHTML = '<span class="spinner"></span>올리는 중…';
        try {
            var form = new FormData();
            form.append('projectId', P.id);
            if ($('file-stage').value) form.append('stageId', $('file-stage').value);
            form.append('file', file);
            var r = await App.upload(App.url('/Projects/Files?handler=Upload'), form);
            P = r.project;
            input.value = '';
            renderAll(true);
            App.toast('올렸습니다.');
        } catch (err) {
            App.toast(err.message, 'error');
        } finally {
            btn.disabled = false;
            btn.innerHTML = label;
        }
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

    // '내 할 일' 등에서 #w123 으로 들어오면 그 작업의 편집 창을 바로 연다
    var hash = /^#w(\d+)$/.exec(window.location.hash || '');
    if (hash) {
        var target = workById(+hash[1]);
        if (target) openWorkSheet(target);
    }
})();
