// 과제 가져오기: 파일 올리기 → 서버 미리보기(행·열 문제, 새 담당자) → 가져오기. 검사는 모두 서버가 한다
(function () {
    'use strict';

    var esc = App.esc, icon = App.icon;
    var form = document.getElementById('import-form');
    var fileInput = document.getElementById('import-file');
    var errorEl = document.getElementById('import-error');
    var resultCard = document.getElementById('import-result');
    var previewEl = document.getElementById('import-preview');
    var actions = document.getElementById('import-actions');
    var confirmRow = document.getElementById('import-confirm-row');
    var confirmBox = document.getElementById('import-confirm');
    var runBtn = document.getElementById('import-run');
    var checkBtn = document.getElementById('import-check');
    var last = null;          // 마지막 미리보기 { file, preview }

    function url(handler) { return App.url('/Projects/Import?handler=' + handler); }

    function setError(message) {
        if (!message) { errorEl.hidden = true; form.querySelector('[data-field=file]').classList.remove('invalid'); return; }
        errorEl.innerHTML = icon('alert') + '<span>' + esc(message) + '</span>';
        errorEl.hidden = false;
        form.querySelector('[data-field=file]').classList.add('invalid');
    }

    function body(file, extra) {
        var fd = new FormData();
        fd.append('file', file);
        Object.keys(extra || {}).forEach(function (k) { fd.append(k, extra[k]); });
        return fd;
    }

    // 행의 문제(같은 시트·행)를 그 행 아래에 기존 오류 모양으로 적는다 — 지연 빨강 칩과 헷갈리지 않게 칩을 쓰지 않는다
    function issuesOf(issues, sheet, row) {
        return issues.filter(function (i) { return i.sheet === sheet && i.row === row; });
    }
    function issueHtml(list) {
        return list.map(function (i) {
            return '<p class="form-error row-issue">' + icon('alert') + '<span>' + (i.column ? '<b>' + esc(i.column) + '</b> ' : '') + esc(i.message) + '</span></p>';
        }).join('');
    }
    function owner(name, isNew) {
        return esc(name || '-') + (isNew ? ' <span class="chip accent">새로 등록됨</span>' : '');
    }

    function render(pv) {
        var html = '';
        var bad = pv.issues.length;
        var sheetIssues = pv.issues.filter(function (i) { return i.row <= 1; });
        if (bad) {
            html += '<p class="form-error" data-import-summary>' + icon('alert') + '<span>문제 ' + bad + '건이 있어 가져올 수 없습니다. 파일을 고쳐 저장한 뒤 다시 올려 주세요. 문제가 하나라도 있으면 아무것도 등록하지 않습니다.</span></p>';
            if (sheetIssues.length) html += issueHtml(sheetIssues);
        } else {
            html += '<p class="form-note" data-import-summary>' + icon('check') + '<span>과제 ' + pv.projects.length + '건' +
                (pv.works.length ? ', 작업 ' + pv.works.length + '건' : '') + '을 가져올 수 있습니다.</span></p>';
        }
        if (pv.newUsers.length) {
            html += '<p class="form-warn" data-import-new-users>' + icon('user') + '<span>새로 만들 담당자 ' + pv.newUsers.length + '명: ' + esc(pv.newUsers.join(', ')) +
                ' — 사용자 목록에 없는 이름입니다. 오타가 아닌지 확인해 주세요.</span></p>';
        }

        if (pv.projects.length) {
            html += '<h3 class="import-sub">과제 시트</h3><div class="table-scroll"><table class="simple-table rows-table" data-import-projects><thead><tr>' +
                '<th class="num">행</th><th>과제명</th><th>유형</th><th>위치</th><th>담당</th><th>착수일</th><th class="num">목표일</th><th class="num">작업</th></tr></thead><tbody>' +
                pv.projects.map(function (p) {
                    var list = issuesOf(pv.issues, '과제', p.row);
                    return '<tr class="' + (list.length ? 'row-bad' : '') + '">' +
                        '<td class="num" data-label="행">' + p.row + '</td>' +
                        '<td data-label="과제명"><span class="preview-title">' + esc(p.title || '') + '</span></td>' +
                        '<td data-label="유형">' + esc(p.category || '-') + '</td>' +
                        '<td data-label="위치">' + esc(p.location || '-') + '</td>' +
                        '<td data-label="담당">' + owner(p.owner, p.newOwner) + '</td>' +
                        '<td class="num" data-label="착수일">' + esc(p.startDate || '-') + '</td>' +
                        '<td class="num" data-label="목표일">' + (p.stages ? p.targets + '/' + p.stages : '-') + '</td>' +
                        '<td class="num" data-label="작업">' + p.works + '</td></tr>' +
                        (list.length ? '<tr class="row-bad-detail"><td colspan="8">' + issueHtml(list) + '</td></tr>' : '');
                }).join('') + '</tbody></table></div>';
        }
        if (pv.works.length) {
            html += '<h3 class="import-sub">작업 시트</h3><div class="table-scroll"><table class="simple-table rows-table" data-import-works><thead><tr>' +
                '<th class="num">행</th><th>과제</th><th>단계</th><th>작업명</th><th>담당</th><th>목표 시작</th><th>목표 종료</th><th>선행</th></tr></thead><tbody>' +
                pv.works.map(function (w) {
                    var list = issuesOf(pv.issues, '작업', w.row);
                    return '<tr class="' + (list.length ? 'row-bad' : '') + '">' +
                        '<td class="num" data-label="행">' + w.row + '</td>' +
                        '<td data-label="과제">' + esc(w.project || '-') + '</td>' +
                        '<td data-label="단계">' + esc(w.stage || '-') + '</td>' +
                        '<td data-label="작업명"><span class="preview-title">' + esc(w.title || '') + '</span></td>' +
                        '<td data-label="담당">' + owner(w.owner, w.newOwner) + '</td>' +
                        '<td class="num" data-label="목표 시작">' + esc(w.planStart || '-') + '</td>' +
                        '<td class="num" data-label="목표 종료">' + esc(w.planEnd || '-') + '</td>' +
                        '<td data-label="선행">' + esc(w.predecessor || '-') + '</td></tr>' +
                        (list.length ? '<tr class="row-bad-detail"><td colspan="8">' + issueHtml(list) + '</td></tr>' : '');
                }).join('') + '</tbody></table></div>';
        }
        previewEl.innerHTML = html;
        resultCard.hidden = false;

        var can = bad === 0 && pv.projects.length > 0;
        actions.hidden = !can;
        confirmRow.hidden = !pv.newUsers.length;
        confirmBox.checked = false;
        document.getElementById('import-confirm-label').textContent = '새 담당자 ' + pv.newUsers.length + '명을 사용자로 등록하고 가져옵니다';
        runBtn.disabled = can && pv.newUsers.length > 0;
        runBtn.innerHTML = icon('upload') + '과제 ' + pv.projects.length + '건 가져오기';
    }

    form.addEventListener('submit', function (e) {
        e.preventDefault();
        var file = fileInput.files[0];
        if (!file) { setError('가져올 Excel 파일을 선택해 주세요.'); return; }
        App.busy(checkBtn, async function () {
            setError(null);
            try {
                var r = await App.upload(url('Preview'), body(file));
                last = { file: file, preview: r.preview };
                render(r.preview);
                resultCard.scrollIntoView({ block: 'start', behavior: 'smooth' });
            } catch (err) {
                last = null;
                resultCard.hidden = true;
                setError(err.message);
            }
        });
    });

    // 파일을 바꾸면 이전 결과는 맞지 않으므로 지운다
    fileInput.addEventListener('change', function () {
        last = null;
        resultCard.hidden = true;
        setError(null);
    });

    confirmBox.addEventListener('change', function () { runBtn.disabled = !confirmBox.checked; });

    runBtn.addEventListener('click', function () {
        if (!last) return;
        App.busy(runBtn, async function () {
            try {
                var r = await App.upload(url('Import'), body(last.file, { confirmNewUsers: confirmBox.checked ? 'true' : 'false' }));
                App.toast('과제 ' + r.result.projects + '건' + (r.result.works ? ', 작업 ' + r.result.works + '건' : '') + '을 가져왔습니다.');
                window.location.href = r.redirect;
            } catch (err) {
                previewEl.insertAdjacentHTML('afterbegin', '<p class="form-error" role="alert">' + icon('alert') + '<span>' + esc(err.message) + '</span></p>');
            }
        });
    });
})();
