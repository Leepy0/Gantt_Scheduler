// 상세 '단계 일정 편집': 모든 단계의 목표일·완료일을 한 시트에서 고치고 한 번에 저장한다.
// 근무일 간격·추천 입고일·휴일 안내는 서버가 계산한 값을 그린다(열 때와 고치는 동안 미리보기 요청으로 받는다). detail.js 가 ctx 를 넘겨 연다
(function () {
    'use strict';

    var esc = App.esc, icon = App.icon;
    var KIND = { inspection: '검수', inbound: '입고' };

    // 근무일 간격 글자: 앞 단계 목표일(첫 단계는 착수일)부터. 음수면 앞 단계보다 이른 일정이라 경고한다
    function gapHtml(gap) {
        if (gap == null) return '<span class="muted">—</span>';
        if (gap < 0) return '<span class="warn" title="앞 단계 목표일보다 이릅니다">' + icon('alert') + gap + '근무일</span>';
        return '+' + gap + '근무일';
    }

    // ctx: { project(), api(handler, body), previewUrl, mergeField, conflict(문구), userToday() }
    function open(ctx) {
        var P = ctx.project();
        if (!P.stages.length) { App.toast('단계가 없습니다. 단계를 먼저 추가해 주세요.', 'error'); return; }

        var base = {};          // 단계 id → 표를 연 때(동시 수정 뒤에는 최신)의 값과 버전
        var computed = {};      // 단계 id → 서버가 계산한 { gap, inboundHint, targetNote }
        function snapshot(stages) {
            base = {};
            computed = {};
            stages.forEach(function (s) {
                base[s.id] = { name: s.name, target: s.targetDate || '', completed: s.completedDate || '', version: s.version };
                computed[s.id] = { gap: s.gapWorkDays, inboundHint: s.inboundHint || null };
            });
        }
        snapshot(P.stages);

        function rowHtml(s, value) {
            var label = esc(s.name);
            var meta = [];
            if (KIND[s.kind]) meta.push(KIND[s.kind]);
            if (s.checkTotal) meta.push('체크 ' + s.checkDone + '/' + s.checkTotal);
            if (s.works.length) meta.push('작업 ' + s.works.length);
            return '<div class="sched-row" data-stage="' + s.id + '">' +
                '<div class="sched-name"><span class="sched-title">' + label + '</span>' +
                '<span class="sched-meta">' + App.chip(s.chip) + (meta.length ? '<span class="hint">' + esc(meta.join(' · ')) + '</span>' : '') + '</span></div>' +
                '<label class="field" data-field="targetDate.' + s.id + '"><span class="label">목표일</span>' +
                '<input type="date" name="target.' + s.id + '" value="' + esc(value.target) + '" aria-label="' + label + ' 목표일" /></label>' +
                '<div class="sched-gap" data-gap></div>' +
                '<label class="field" data-field="completedDate.' + s.id + '"><span class="label">완료일</span>' +
                '<input type="date" name="completed.' + s.id + '" value="' + esc(value.completed) + '" aria-label="' + label + ' 완료일" /></label>' +
                '<div class="sched-notes" data-notes></div>' +
                '</div>';
        }

        function rowsHtml(values) {
            return '<div class="sched-head" aria-hidden="true"><span>단계</span><span>목표일</span><span>간격</span><span>완료일</span></div>' +
                P.stages.map(function (s) {
                    return rowHtml(s, values[s.id] || { target: s.targetDate || '', completed: s.completedDate || '' });
                }).join('');
        }

        var html =
            '<p class="hint">모든 단계의 날짜를 한 번에 고칩니다. 간격은 앞 단계 목표일(첫 단계는 착수일 ' + esc(P.startDate) + ')부터의 근무일입니다. ' +
            '처음 정하는 목표일은 사유 없이 기록되고, 이미 정한 목표일을 바꾸면 사유를 한 번 받습니다.</p>' +
            '<div class="sched" data-rows>' + rowsHtml({}) + '</div>' +
            '<div data-reason hidden class="form">' +
            '<p class="form-warn" data-reason-note>' + icon('clock') + '<span></span></p>' +
            '<label class="field" data-field="reason"><span class="label">변경 사유</span>' +
            '<textarea name="reason" rows="2" maxlength="500" placeholder="목표일을 바꾸는 이유 (바꾼 단계 모두에 남습니다)"></textarea></label></div>' +
            '<p class="hint" data-summary></p>';

        var form = null;
        var previewSeq = 0;
        var previewTimer = null;

        // 지금 입력 값 (단계 id → { target, completed })
        function values() {
            var out = {};
            P.stages.forEach(function (s) {
                var t = form.elements['target.' + s.id];
                var c = form.elements['completed.' + s.id];
                if (t && c) out[s.id] = { target: t.value, completed: c.value };
            });
            return out;
        }

        // 표를 연 때와 다른 단계 (저장할 줄)
        function changedRows() {
            var v = values();
            return P.stages.filter(function (s) {
                var b = base[s.id];
                return b && v[s.id] && (v[s.id].target !== b.target || v[s.id].completed !== b.completed);
            }).map(function (s) {
                return { stageId: s.id, version: base[s.id].version, targetDate: v[s.id].target || null, completedDate: v[s.id].completed || null };
            });
        }

        // 줄마다 간격·안내(추천 입고일, 휴일, 체크리스트, 미래 완료일)와 바뀐 표시, 사유 칸을 다시 맞춘다
        function refresh() {
            var v = values();
            var targetMoves = [];
            P.stages.forEach(function (s) {
                var row = form.querySelector('.sched-row[data-stage="' + s.id + '"]');
                if (!row || !v[s.id]) return;
                var b = base[s.id] || { target: '', completed: '' };
                var cur = v[s.id];
                var calc = computed[s.id] || {};
                row.classList.toggle('changed', cur.target !== b.target || cur.completed !== b.completed);
                if (b.target && cur.target !== b.target) targetMoves.push(s.name);

                var gap = row.querySelector('[data-gap]');
                gap.innerHTML = '<span class="sched-gap-label">간격</span>' + gapHtml(calc.gap);

                var notes = [];
                if (cur.target && calc.targetNote) {
                    notes.push('<span class="hint">' + icon('alert') + esc(calc.targetNote) + '</span>');
                }
                if (s.kind === 'inbound' && !cur.completed && calc.inboundHint && calc.inboundHint !== cur.target) {
                    notes.push('<span class="sched-hint">추천 입고일 <b>' + esc(calc.inboundHint) + '</b>' +
                        '<span class="muted">검수일 + 해상 운송 ' + P.oceanLeadDays + '일</span>' +
                        '<button type="button" class="btn small" data-apply-hint="' + s.id + '">적용</button></span>');
                }
                if (cur.completed && !b.completed && s.checkTotal > s.checkDone) {
                    notes.push('<span class="form-warn">' + icon('alert') + '<span>체크리스트 ' + (s.checkTotal - s.checkDone) + '개 항목이 남아 있어 완료할 수 없습니다. 단계 창에서 먼저 체크해 주세요.</span></span>');
                }
                if (cur.completed && cur.completed > ctx.userToday()) {
                    notes.push('<span class="hint warn">' + icon('alert') + '완료일이 오늘보다 미래입니다.</span>');
                }
                if (cur.completed && !b.completed && s.openWorks > 0) {
                    notes.push('<span class="hint">남은 작업 ' + s.openWorks + '건은 완료(단계)로 봅니다. 작업마다 실적을 넣으려면 단계 창에서 완료해 주세요.</span>');
                }
                row.querySelector('[data-notes]').innerHTML = notes.join('');
            });

            var reasonBox = form.querySelector('[data-reason]');
            reasonBox.hidden = targetMoves.length === 0;
            form.querySelector('[data-reason-note] span').textContent =
                '이미 정한 목표일을 바꾸는 단계 ' + targetMoves.length + '개: ' + targetMoves.join(', ');
            var n = changedRows().length;
            form.querySelector('[data-summary]').textContent = n ? '바꾼 단계 ' + n + '개를 저장합니다. 한 번의 변경으로 묶어 기록합니다.' : '';
        }

        // 고친 날짜로 간격·추천 입고일을 서버에서 다시 계산한다 (빠르게 여러 번 고치면 마지막 응답만 쓴다)
        function preview() {
            clearTimeout(previewTimer);
            // 새 간격을 받기 전까지 지난 값을 흐리게 보인다 (고친 날짜와 맞지 않는 간격을 그대로 믿지 않게)
            form.querySelector('[data-rows]').setAttribute('aria-busy', 'true');
            previewTimer = setTimeout(function () {
                var seq = ++previewSeq;
                var v = values();
                var rows = P.stages.filter(function (s) { return v[s.id]; }).map(function (s) {
                    return { stageId: s.id, targetDate: v[s.id].target || null, completedDate: v[s.id].completed || null };
                });
                App.post(ctx.previewUrl, { projectId: P.id, rows: rows }).then(function (r) {
                    if (seq !== previewSeq || !form) return;
                    r.rows.forEach(function (x) { computed[x.stageId] = { gap: x.gapWorkDays, inboundHint: x.inboundHint, targetNote: x.targetNote }; });
                    form.querySelector('[data-rows]').removeAttribute('aria-busy');
                    refresh();
                }, function () {
                    // 미리보기 실패는 저장에 영향이 없다 — 지난 값을 그대로 둔다
                    if (seq === previewSeq && form) form.querySelector('[data-rows]').removeAttribute('aria-busy');
                });
            }, 250);
        }

        App.sheet({
            title: '단계 일정 편집',
            wide: true,
            html: html,
            primary: '저장',
            draftKey: 'schedule.' + P.id,
            onOpen: function (f) {
                form = f;
                form.addEventListener('input', function (e) {
                    if (e.target.type !== 'date') return;
                    refresh();
                    preview();
                });
                form.addEventListener('click', function (e) {
                    var b = e.target.closest('[data-apply-hint]');
                    if (!b) return;
                    var id = +b.dataset.applyHint;
                    var input = form.elements['target.' + id];
                    input.value = computed[id].inboundHint;
                    input.dispatchEvent(new Event('input', { bubbles: true }));
                });
                refresh();
                preview();      // 휴일 안내는 서버가 준다 — 연 때의 날짜로도 한 번 받는다
            },
            onClose: function () { form = null; clearTimeout(previewTimer); },
            // 다른 사용자가 먼저 고쳤으면: 최신 단계로 표를 다시 그리고, 내가 손대지 않은 칸은 최신 값으로, 둘 다 바꾼 칸은 내 값을 두고 옆에 알린다
            onConflict: function (f, sheet) {
                var mine = values();
                var opened = base;
                P = ctx.project();
                var gone = Object.keys(opened).filter(function (id) {
                    return !P.stages.some(function (s) { return String(s.id) === id; });
                }).map(function (id) { return opened[id].name; });
                sheet.clearConflictNotes();
                form.querySelector('[data-rows]').innerHTML = rowsHtml(mine);
                snapshot(P.stages);
                P.stages.forEach(function (s) {
                    var b = opened[s.id];
                    if (!b) { sheet.conflictNote('targetDate.' + s.id, '새로 추가된 단계'); return; }
                    ctx.mergeField(sheet, form.elements['target.' + s.id], 'targetDate.' + s.id, b.target, s.targetDate, s.targetDate || '미정');
                    ctx.mergeField(sheet, form.elements['completed.' + s.id], 'completedDate.' + s.id, b.completed, s.completedDate,
                        s.completedDate ? '완료 ' + s.completedDate : '완료 취소');
                });
                if (gone.length) sheet.conflictNote(null, '삭제된 단계: ' + gone.join(', '));
                refresh();
                preview();
                return ctx.conflict;
            },
            onSubmit: async function (f) {
                var rows = changedRows();
                if (!rows.length) { App.toast('바꾼 날짜가 없습니다.'); return; }
                await ctx.api('SaveSchedule', { projectId: P.id, rows: rows, reason: f.elements.reason.value });
                App.toast('단계 일정 ' + rows.length + '개를 저장했습니다.');
            }
        });
    }

    window.StageSchedule = { open: open };
})();
