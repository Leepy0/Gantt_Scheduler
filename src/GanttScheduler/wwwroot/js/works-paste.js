// 상세: 작업 여러 개 추가 — Excel 에서 복사한 줄을 붙여넣으면 서버가 해석·검증한 표를 보이고, 확인하면 한 번에 저장한다.
// 날짜 해석·검증·선행 판정은 모두 서버가 한다(화면은 결과를 그린다)
(function () {
    'use strict';

    var esc = App.esc, icon = App.icon;

    // 문제 있는 줄은 기존 오류 모양(form-error)으로 그 줄 아래에 적는다 — 빨강 지연 칩과 헷갈리지 않게 칩을 쓰지 않는다
    function rowHtml(r) {
        var issues = r.issues.map(function (i) {
            return '<p class="form-error row-issue">' + icon('alert') + '<span><b>' + esc(i.column) + '</b> ' + esc(i.message) + '</span></p>';
        }).join('');
        return '<tr class="' + (r.issues.length ? 'row-bad' : '') + '" data-line="' + r.line + '">' +
            '<td class="num" data-label="줄">' + r.line + '</td>' +
            '<td data-label="작업명"><span class="preview-title">' + esc(r.title || '') + '</span></td>' +
            '<td data-label="담당">' + esc(r.owner || '-') + (r.newOwner ? ' <span class="chip accent" data-new-owner>새로 등록됨</span>' : '') + '</td>' +
            '<td class="num" data-label="목표 시작">' + esc(r.planStart || '-') + '</td>' +
            '<td class="num" data-label="목표 종료">' + esc(r.planEnd || '-') + '</td>' +
            '<td class="num" data-label="근무일">' + (r.workDays != null ? r.workDays + '일' : '-') + '</td>' +
            '<td data-label="선행">' + esc(r.predecessor || '-') + '</td>' +
            '</tr>' + (issues ? '<tr class="row-bad-detail"><td colspan="7">' + issues + '</td></tr>' : '');
    }

    function previewHtml(pv) {
        var okCount = pv.rows.length - pv.errorCount;
        var head = pv.errorCount
            ? '<p class="form-error" data-paste-summary>' + icon('alert') + '<span>' + pv.rows.length + '줄 중 ' + pv.errorCount + '줄에 문제가 있습니다. 붙여넣은 글을 고친 뒤 다시 확인해 주세요.</span></p>'
            : '<p class="form-note" data-paste-summary>' + icon('check') + '<span>' + okCount + '개 작업을 \'' + esc(pv.stageName) + '\' 단계에 추가합니다.</span></p>';
        if (pv.newOwners.length) {
            head += '<p class="form-note" data-paste-new-owners>' + icon('user') + '<span>목록에 없는 담당 ' + pv.newOwners.length + '명(' + esc(pv.newOwners.join(', ')) +
                ')은 저장하면 새 사용자로 등록됩니다. 오타가 아닌지 확인해 주세요.</span></p>';
        }
        return head + '<div class="table-scroll"><table class="simple-table rows-table" data-paste-table><thead><tr>' +
            '<th class="num">줄</th><th>작업명</th><th>담당</th><th>목표 시작</th><th>목표 종료</th><th>근무일</th><th>선행</th></tr></thead><tbody>' +
            pv.rows.map(rowHtml).join('') + '</tbody></table></div>';
    }

    // ctx: { project(), stage, api(handler, body), previewUrl, limits }
    function open(ctx) {
        var stage = ctx.stage;
        var preview = null;          // 마지막 미리보기 결과
        var previewedText = null;    // 그 미리보기의 글 (글을 고치면 다시 확인한다)
        var timer = null, seq = 0;

        var html = '<div class="field" data-field="text"><span class="label">붙여넣기</span>' +
            '<textarea name="text" rows="7" spellcheck="false" placeholder="작업명&#9;담당&#9;목표 시작&#9;목표 종료&#9;선행 작업명&#10;현장 실사&#9;김물류&#9;2026-10-12&#9;2026-10-14&#10;사양 검토&#9;김물류&#9;10/15&#9;10/21&#9;현장 실사" aria-describedby="paste-hint"></textarea>' +
            '<span class="hint" id="paste-hint">Excel 에서 작업명 · 담당 · 목표 시작 · 목표 종료 · 선행 작업명(선택) 열을 복사해 붙여 넣습니다. 한 줄에 작업 하나, 머리글 줄은 건너뜁니다. ' +
            '날짜는 2026-10-12 · 2026.10.12 · 10/12(올해) 모두 됩니다. 종료를 비우면 하루짜리 작업입니다. 선행은 붙여넣은 줄이나 이 과제의 작업 이름입니다(끝나면 시작).</span></div>' +
            '<div class="inline-btns"><button type="button" class="btn small" data-paste-check>' + icon('search') + '확인</button></div>' +
            '<div data-paste-preview class="paste-preview"></div>';

        App.sheet({
            title: '작업 여러 개 추가 · ' + stage.name,
            html: html,
            wide: true,
            primary: '추가',
            draftKey: 'works.paste.' + stage.id,
            onOpen: function (form, sheet) {
                var box = form.querySelector('[data-paste-preview]');
                var submit = sheet.el.querySelector('button[type=submit]');
                function setSubmit() {
                    var n = preview && previewedText === form.elements.text.value && !preview.errorCount ? preview.rows.length : 0;
                    submit.textContent = n ? n + '개 추가' : '추가';
                }
                async function check() {
                    var text = form.elements.text.value;
                    if (!text.trim()) { box.innerHTML = ''; preview = null; previewedText = null; setSubmit(); return; }
                    var my = ++seq;
                    box.setAttribute('aria-busy', 'true');
                    try {
                        var r = await App.post(ctx.previewUrl, { stageId: stage.id, text: text });
                        if (my !== seq) return;      // 늦게 온 옛 응답은 버린다
                        preview = r.preview;
                        previewedText = text;
                        box.innerHTML = previewHtml(preview);
                        sheet.setError(null);
                    } catch (err) {
                        if (my !== seq) return;
                        preview = null;
                        previewedText = null;
                        box.innerHTML = '';
                        sheet.setError(err.message, err.field);
                    } finally {
                        if (my === seq) box.removeAttribute('aria-busy');
                        setSubmit();
                    }
                }
                sheet.checkPaste = check;
                form.querySelector('[data-paste-check]').addEventListener('click', function (e) { App.busy(e.currentTarget, check); });
                form.elements.text.addEventListener('input', function () {
                    clearTimeout(timer);
                    setSubmit();
                    timer = setTimeout(check, 500);
                });
                if (!App.isMobile()) form.elements.text.focus();
            },
            onClose: function () { clearTimeout(timer); },
            onSubmit: async function (form, sheet) {
                clearTimeout(timer);
                // 확인하지 않았거나 확인 뒤 글을 고쳤으면 먼저 다시 확인한다(시트는 열어 둔다)
                if (!preview || previewedText !== form.elements.text.value) {
                    await sheet.checkPaste();
                    return false;
                }
                if (preview.errorCount) {
                    var bad = new Error('문제 있는 줄 ' + preview.errorCount + '개를 고친 뒤 다시 확인해 주세요.');
                    bad.field = 'text';
                    throw bad;
                }
                var rows = preview.rows.map(function (r) {
                    return { title: r.title, owner: r.owner, planStart: r.planStart, planEnd: r.planEnd, predecessor: r.predecessor, line: r.line };
                });
                await ctx.api('AddWorks', { stageId: stage.id, rows: rows, stageVersion: preview.stageVersion });
                App.toast('작업 ' + rows.length + '개를 추가했습니다.');
            }
        });
    }

    window.WorksPaste = { open: open };
})();
