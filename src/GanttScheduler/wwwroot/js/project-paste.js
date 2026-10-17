// 과제 등록: 단계·작업 CSV 붙여넣기. 글을 넣으면 서버가 해석·검증한 표(Edit?handler=PastePreview)를 보이고, 등록은 폼 제출로 한다
// (서버가 저장할 때 같은 해석을 다시 한다). 글이 있으면 기본 단계 초안·템플릿 작업 선택은 숨긴다(붙여 넣은 단계로 대신하므로).
// '샘플 CSV 복사'는 고른 유형·착수일로 서버가 만든 견본(Edit?handler=PasteSample)을 클립보드에 넣는다
(function () {
    'use strict';

    var text = document.getElementById('paste-text');
    var form = document.getElementById('edit-form');
    if (!text || !form) return;

    var esc = App.esc, icon = App.icon;
    var box = document.getElementById('paste-preview');
    var startInput = document.getElementById('Form_StartDate');
    var draftField = document.getElementById('draft-field');
    var twField = document.getElementById('template-works-field');
    var clearBtn = document.getElementById('paste-clear');
    var download = document.getElementById('paste-download');
    var preview = null, previewedText = null, timer = null, seq = 0;

    function categoryId() {
        var checked = form.querySelector('input[name="Form.CategoryId"]:checked');
        return checked ? +checked.value : 0;
    }

    function issuesHtml(issues, cols) {
        if (!issues.length) return '';
        return '<tr class="row-bad-detail"><td colspan="' + cols + '">' + issues.map(function (i) {
            return '<p class="form-error row-issue">' + icon('alert') + '<span><b>' + esc(i.column) + '</b> ' + esc(i.message) + '</span></p>';
        }).join('') + '</td></tr>';
    }

    function gapHtml(gap, parallel) {
        if (gap == null) return '<span class="muted">—</span>';
        if (gap < 0) return '<span class="warn" title="' + (parallel ? '묶음 기준일보다 이릅니다' : '앞 단계 목표일보다 이릅니다') + '">' + icon('alert') + gap + '근무일</span>';
        return '+' + gap + '근무일';
    }

    function stagesHtml(pv) {
        return '<div class="table-scroll"><table class="simple-table rows-table" data-paste-stages><thead><tr>' +
            '<th class="num">줄</th><th>단계</th><th>목표일</th><th>간격</th><th>작업</th><th>비고</th></tr></thead><tbody>' +
            pv.stages.map(function (s) {
                var notes = [];
                if (s.parallel) notes.push('동시');
                if (s.autoTarget) notes.push('작업 기준');
                if (s.fromTemplate) notes.push('기본 단계' + (s.checks ? ' · 체크 ' + s.checks : ''));
                return '<tr class="' + (s.issues.length ? 'row-bad' : '') + '" data-line="' + s.line + '">' +
                    '<td class="num" data-label="줄">' + s.line + '</td>' +
                    '<td data-label="단계"><span class="preview-title">' + esc(s.name || '') + '</span></td>' +
                    '<td class="num" data-label="목표일">' + esc(s.targetDate || (s.autoTarget ? '작업 기준' : '미정')) + '</td>' +
                    '<td class="num" data-label="간격">' + gapHtml(s.gapWorkDays, s.parallel) + '</td>' +
                    '<td class="num" data-label="작업">' + (s.workCount ? s.workCount + '개' : '-') + '</td>' +
                    '<td data-label="비고">' + esc(notes.join(' · ') || '-') + '</td></tr>' + issuesHtml(s.issues, 6);
            }).join('') + '</tbody></table></div>';
    }

    function worksHtml(pv) {
        if (!pv.works.length) return '';
        return '<div class="table-scroll"><table class="simple-table rows-table" data-paste-works><thead><tr>' +
            '<th class="num">줄</th><th>단계</th><th>작업명</th><th>담당</th><th>목표 시작</th><th>목표 종료</th><th>근무일</th><th>선행 단계</th><th>선행 작업</th></tr></thead><tbody>' +
            pv.works.map(function (r) {
                return '<tr class="' + (r.issues.length ? 'row-bad' : '') + '" data-line="' + r.line + '">' +
                    '<td class="num" data-label="줄">' + r.line + '</td>' +
                    '<td data-label="단계">' + esc(r.stage || '') + '</td>' +
                    '<td data-label="작업명"><span class="preview-title">' + esc(r.title || '') + '</span></td>' +
                    '<td data-label="담당">' + esc(r.owner || '과제 담당') + (r.newOwner ? ' <span class="chip accent" data-new-owner>새로 등록됨</span>' : '') + '</td>' +
                    '<td class="num" data-label="목표 시작">' + esc(r.planStart || '-') + '</td>' +
                    '<td class="num" data-label="목표 종료">' + esc(r.planEnd || '-') + '</td>' +
                    '<td class="num" data-label="근무일">' + (r.workDays != null ? r.workDays + '일' : '-') + '</td>' +
                    '<td data-label="선행 단계">' + (r.predecessorStage ? esc(r.predecessorStage) : r.predecessor ? '<span class="muted">같은 단계</span>' : '-') + '</td>' +
                    '<td data-label="선행 작업">' + esc(r.predecessor || '-') + '</td></tr>' + issuesHtml(r.issues, 9);
            }).join('') + '</tbody></table></div>';
    }

    function previewHtml(pv) {
        var head = pv.errorCount
            ? '<p class="form-error" data-paste-summary>' + icon('alert') + '<span>' + pv.errorCount + '줄 문제. 고친 뒤 다시 확인하세요.</span></p>'
            : '<p class="form-note" data-paste-summary>' + icon('check') + '<span>' + esc(pv.note) + ' 작업 ' + pv.works.length + '개.</span></p>';
        if (pv.newOwners.length) {
            head += '<p class="form-note" data-paste-new-owners>' + icon('user') + '<span>목록에 없는 담당 ' + pv.newOwners.length + '명(' + esc(pv.newOwners.join(', ')) +
                ')은 등록하면 새 사용자로 등록됩니다. 오타가 아닌지 확인해 주세요.</span></p>';
        }
        return head + stagesHtml(pv) + worksHtml(pv);
    }

    // 붙여 넣은 글이 있으면 기본 단계 초안·템플릿 작업은 쓰지 않는다 — 숨겨서 헷갈리지 않게
    function syncVisibility() {
        var has = text.value.trim().length > 0;
        if (draftField) draftField.hidden = has;
        if (twField && has) twField.hidden = true;
        else if (twField && !has) {
            // 템플릿 작업 칸은 edit.js 가 유형에 따라 보이므로 다시 판단하게 한다
            var checked = form.querySelector('input[name="Form.CategoryId"]:checked');
            if (checked) checked.dispatchEvent(new Event('change', { bubbles: true }));
        }
        clearBtn.hidden = !has;
        if (!has) { box.innerHTML = ''; preview = null; previewedText = null; }
    }

    async function check() {
        var value = text.value;
        if (!value.trim()) { syncVisibility(); return; }
        var cat = categoryId();
        if (!cat) {
            box.innerHTML = '<p class="form-warn">' + icon('alert') + '<span>유형을 먼저 골라 주세요. 붙여 넣은 단계가 유형의 기본 단계와 이름이 같으면 종류·체크리스트를 이어받습니다.</span></p>';
            return;
        }
        var my = ++seq;
        box.setAttribute('aria-busy', 'true');
        try {
            var r = await App.post(App.url('/Projects/Edit?handler=PastePreview'), { categoryId: cat, startDate: startInput.value || null, text: value });
            if (my !== seq) return;
            preview = r.preview;
            previewedText = value;
            box.innerHTML = previewHtml(preview);
        } catch (err) {
            if (my !== seq) return;
            preview = null;
            previewedText = null;
            box.innerHTML = '<p class="form-error" data-paste-summary>' + icon('alert') + '<span>' + esc(err.message) + '</span></p>';
        } finally {
            if (my === seq) box.removeAttribute('aria-busy');
        }
    }

    text.addEventListener('input', function () {
        syncVisibility();
        clearTimeout(timer);
        timer = setTimeout(check, 600);
    });
    document.getElementById('paste-check').addEventListener('click', function (e) { clearTimeout(timer); App.busy(e.currentTarget, check); });
    clearBtn.addEventListener('click', function () {
        text.value = '';
        syncVisibility();
        text.focus();
    });
    form.querySelectorAll('input[name="Form.CategoryId"]').forEach(function (r) { r.addEventListener('change', function () { if (text.value.trim()) check(); updateDownload(); }); });
    startInput.addEventListener('input', function () { if (text.value.trim()) { clearTimeout(timer); timer = setTimeout(check, 600); } updateDownload(); });

    // 샘플 CSV: 복사(클립보드 — 사내망 http 에서는 clipboard API 가 없을 수 있어 App.copyText 가 대신 처리) / 파일 받기
    function sampleUrl(download) {
        return App.url('/Projects/Edit?handler=PasteSample&categoryId=' + categoryId() + '&start=' + encodeURIComponent(startInput.value || '') + (download ? '&download=true' : ''));
    }
    function updateDownload() { download.href = sampleUrl(true); }
    updateDownload();
    document.getElementById('paste-sample').addEventListener('click', function (e) {
        App.busy(e.currentTarget, async function () {
            try {
                var r = await App.get(sampleUrl(false));
                var ok = await App.copyText(r.text);
                if (ok) App.toast('샘플 CSV 를 복사했습니다. 붙여넣기 칸이나 Excel 에 붙여 넣어 고쳐 쓰세요.');
                else {
                    // 복사가 막힌 환경: 글을 보여 주고 직접 복사하게 한다
                    App.sheet({
                        title: '샘플 CSV',
                        html: '<p class="hint">복사가 자동으로 되지 않았습니다. 아래 글을 선택해 복사해 주세요.</p>' +
                            '<textarea rows="12" readonly spellcheck="false" style="width:100%">' + esc(r.text) + '</textarea>',
                        onOpen: function (f) { var t = f.querySelector('textarea'); t.focus(); t.select(); }
                    });
                }
            } catch (err) { App.toast(err.message, 'error'); }
        });
    });

    // 제출: 확인한 뒤 글을 고쳤거나 문제가 남아 있으면 먼저 알린다 (서버도 같은 검사로 거절한다)
    form.addEventListener('submit', function (e) {
        if (!text.value.trim()) return;
        if (preview && previewedText === text.value && preview.errorCount) {
            e.preventDefault();
            box.scrollIntoView({ block: 'nearest' });
            App.toast('붙여넣은 글에 문제 있는 줄 ' + preview.errorCount + '개가 있습니다. 고친 뒤 등록해 주세요.', 'error');
        }
    });

    syncVisibility();
    if (text.value.trim()) check();     // 오류로 다시 그린 화면: 붙여 넣은 글이 남아 있으면 표를 다시 받는다
})();
