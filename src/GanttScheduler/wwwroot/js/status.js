// 관리 > 서버 상태: 서버가 준 점검 보고서 하나로 그린다 (다시 점검·고아 파일 정리 뒤에는 새 보고서로 다시 그린다)
(function () {
    'use strict';

    var data = App.data();
    var report = data.report;
    var root = document.getElementById('status-body');
    var url = App.url('/Admin/Status');
    var esc = App.esc, icon = App.icon;
    var onlyErrors = false;

    // 점검 수준 → 칩. 빨강은 지연 전용이라 문제는 노랑(위험)으로 보이고, 실패·확인은 글자와 아이콘으로 가른다
    var LEVELS = {
        ok: { cls: 'ok', icon: 'check', text: '정상' },
        warn: { cls: 'warn', icon: 'alert', text: '확인 필요' },
        fail: { cls: 'warn', icon: 'ban', text: '실패' }
    };

    function chip(level) {
        var l = LEVELS[level] || LEVELS.warn;
        return '<span class="chip ' + l.cls + '">' + icon(l.icon) + l.text + '</span>';
    }

    function link(href, text) {
        return href ? '<a href="' + esc(App.url(href)) + '">' + esc(text) + '</a>' : '';
    }

    // ── 첫 설정 점검표: 덜 된 것이 있을 때만 맨 위에 ──────────────────
    function setupCard() {
        if (report.setupDone) return '';
        var left = report.setup.filter(function (s) { return !s.done; }).length;
        var rows = report.setup.map(function (s) {
            return '<li class="setup-item' + (s.done ? ' done' : '') + '" data-setup="' + esc(s.key) + '">' +
                '<span class="setup-mark">' + icon(s.done ? 'check' : 'dot') + '</span>' +
                '<div class="grow"><p class="setup-name">' + esc(s.name) + ' <span class="muted num">' + esc(s.detail) + '</span></p>' +
                (s.done ? '' : '<p class="hint">' + esc(s.hint) + '</p>') + '</div>' +
                (s.done ? '<span class="chip ok">' + icon('check') + '완료</span>'
                    : (s.link ? '<a class="btn small" href="' + esc(App.url(s.link)) + '">설정하기</a>' : '<span class="chip warn">' + icon('alert') + '설정 필요</span>')) +
                '</li>';
        }).join('');
        return '<section class="card setup-card" id="setup"><div class="card-head"><h2>첫 설정 점검표</h2>' +
            '<span class="chip warn">' + icon('alert') + '남은 항목 ' + left + '개</span></div>' +
            '<p class="muted">처음 설치한 서버에서 해 둘 일입니다. 법인이 없으면 과제를 등록할 수 없고, 공휴일이 없으면 근무일 계산이 주말만 뺍니다.</p>' +
            '<ul class="setup-list">' + rows + '</ul></section>';
    }

    // ── 서버 정보와 점검 요약 ─────────────────────────────────────────
    function serverCard() {
        var s = report.server;
        var count = { ok: 0, warn: 0, fail: 0 };
        report.checks.forEach(function (c) { count[c.level] = (count[c.level] || 0) + 1; });
        return '<section class="card"><div class="card-head"><h2>서버</h2><span class="spacer"></span>' +
            '<span class="muted num">점검 ' + esc(App.time(report.checkedAt)) + '</span></div>' +
            '<div class="ops-summary">' +
            '<span class="chip ok">' + icon('check') + '정상 ' + count.ok + '</span>' +
            (count.warn ? '<span class="chip warn">' + icon('alert') + '확인 필요 ' + count.warn + '</span>' : '') +
            (count.fail ? '<span class="chip warn">' + icon('ban') + '실패 ' + count.fail + '</span>' : '') + '</div>' +
            '<dl class="dl ops-dl">' +
            '<dt>버전</dt><dd class="num">v' + esc(s.version) + ' · ' + esc(s.channel) + '</dd>' +
            '<dt>실행 방식</dt><dd>' + esc(s.host) + ' · ' + esc(s.environment) + '</dd>' +
            '<dt>시작</dt><dd class="num">' + esc(App.time(s.startedAt)) + '</dd>' +
            '<dt>DB</dt><dd>' + esc(s.db) + '</dd>' +
            '<dt>프로그램</dt><dd class="path">' + esc(s.appDir) + '</dd>' +
            '<dt>데이터</dt><dd class="path">' + esc(s.dataDir) + '</dd>' +
            '<dt>첨부</dt><dd class="path">' + esc(s.storageDir) + '</dd>' +
            '<dt>백업</dt><dd class="path">' + esc(s.backupDir) + '</dd>' +
            // 경로만 아무 데서나 줄을 바꾸고, 링크 글자는 단어 중간에서 끊기지 않게 따로 둔다
            '<dt>로그</dt><dd><span class="path">' + esc(s.logDir) + '</span> <a class="ops-link" href="' + esc(url + '?handler=Log') + '" id="log-download">오늘 로그 내려받기</a></dd>' +
            '</dl></section>';
    }

    function checksCard() {
        var rows = report.checks.map(function (c) {
            return '<tr data-check="' + esc(c.key) + '" class="lv-' + esc(c.level) + '"><td class="ops-name">' + esc(c.name) + '</td><td>' + chip(c.level) + '</td>' +
                '<td><p>' + esc(c.message) + '</p>' + (c.fix ? '<p class="fix muted">' + esc(c.fix) + '</p>' : '') +
                (c.link ? '<p>' + link(c.link, '바로 가기') + '</p>' : '') + '</td></tr>';
        }).join('');
        return '<section class="card"><div class="card-head"><h2>점검 결과</h2></div><div class="table-scroll"><table class="simple-table ops-table" id="checks">' +
            '<thead><tr><th>항목</th><th>상태</th><th>내용</th></tr></thead><tbody>' + rows + '</tbody></table></div></section>';
    }

    // ── 첨부 파일: 총량, DB↔디스크 대조, 고아 파일 정리 ────────────────
    function attachmentsCard() {
        var a = report.attachments;
        var h = '<section class="card" id="attachments"><div class="card-head"><h2>첨부 파일</h2><span class="spacer"></span>' +
            '<button type="button" class="btn small" data-act="clean"' + (a.orphanCount ? '' : ' disabled') + '>' + icon('trash') + '고아 파일 정리</button></div>' +
            '<dl class="dl"><dt>기록</dt><dd class="num">' + a.count + '개 · ' + esc(App.size(a.dbBytes)) + '</dd>' +
            '<dt>디스크</dt><dd class="num">' + a.diskFiles + '개 · ' + esc(App.size(a.diskBytes)) + '</dd>' +
            '<dt>파일 없음</dt><dd class="num">' + a.missingCount + '개</dd>' +
            '<dt>기록 없음</dt><dd class="num">' + a.orphanCount + '개' + (a.orphanCount ? ' · ' + esc(App.size(a.orphanBytes)) : '') + '</dd></dl>';
        if (a.missing.length) h += '<div class="field"><span class="label">파일이 없는 첨부 (과제 번호/저장 이름)</span><pre class="notes">' + esc(a.missing.join('\n')) + (a.missingCount > a.missing.length ? '\n…' : '') + '</pre></div>';
        if (a.orphans.length) h += '<div class="field"><span class="label">기록이 없는 파일</span><pre class="notes">' + esc(a.orphans.join('\n')) + (a.orphanCount > a.orphans.length ? '\n…' : '') + '</pre></div>';
        h += '<p class="hint">파일이 없는 첨부는 DB 만 복원했거나 저장소를 옮긴 경우에 생깁니다. 막 올라온 파일(10분 이내)은 고아로 보지 않습니다.</p>';
        return h + '</section>';
    }

    // ── 최근 경고·오류 (서버 메모리의 최근 200건) ─────────────────────
    function logsCard() {
        var list = report.logs.filter(function (e) { return !onlyErrors || e.level === 'error'; });
        var rows = list.map(function (e) {
            return '<li class="log-row"><div class="log-meta"><span class="num muted">' + esc(App.time(e.at)) + '</span>' +
                (e.level === 'error' ? '<span class="chip warn">' + icon('ban') + '오류</span>' : '<span class="chip">' + icon('alert') + '경고</span>') +
                '<span class="muted log-cat">' + esc(e.category) + '</span></div>' +
                '<p class="log-msg">' + esc(e.message) + '</p>' +
                (e.requestId ? '<p class="hint num">오류 번호 ' + esc(e.requestId) + (e.path ? ' · ' + esc(e.path) : '') + '</p>' : '') + '</li>';
        }).join('');
        return '<section class="card" id="logs"><div class="card-head"><h2>최근 경고·오류</h2><span class="count">' + list.length + '</span><span class="spacer"></span>' +
            '<div class="seg small" role="group" aria-label="보기">' +
            '<button type="button" data-logs="all" class="' + (onlyErrors ? '' : 'on') + '" aria-pressed="' + !onlyErrors + '">전체</button>' +
            '<button type="button" data-logs="error" class="' + (onlyErrors ? 'on' : '') + '" aria-pressed="' + onlyErrors + '">오류만</button></div></div>' +
            (rows ? '<ul class="log-list">' + rows + '</ul>' : '<div class="empty">' + icon('check') + '서버가 시작된 뒤 기록된 ' + (onlyErrors ? '오류가' : '경고·오류가') + ' 없습니다.</div>') +
            '<p class="hint">서버를 다시 시작하면 이 목록은 비워집니다. 지난 기록은 로그 파일(일자별, 보존 기간 Logs:KeepDays)에 있습니다.</p></section>';
    }

    function render() {
        root.innerHTML = setupCard() + '<div class="grid-2">' + serverCard() + attachmentsCard() + '</div>' + checksCard() + logsCard();
    }

    function reload(btn) {
        return App.busy(btn, function () {
            return App.get(url + '?handler=Data').then(function (r) { report = r.report; render(); })
                .catch(function (err) { App.toast(err.message, 'error'); });
        });
    }

    document.getElementById('status-refresh').addEventListener('click', function (e) { reload(e.currentTarget); });

    root.addEventListener('click', function (e) {
        var t = e.target.closest('[data-logs]');
        if (t) {
            onlyErrors = t.getAttribute('data-logs') === 'error';
            render();
            return;
        }
        t = e.target.closest('[data-act=clean]');
        if (t && !t.disabled) {
            var a = report.attachments;
            App.confirm({
                title: '고아 파일 정리',
                message: '첨부 기록이 없는 파일 ' + a.orphanCount + '개(' + App.size(a.orphanBytes) + ')를 지웁니다. 되돌릴 수 없습니다.',
                verb: '지우기'
            }).then(function (yes) {
                if (!yes) return;
                App.post(url + '?handler=CleanOrphans').then(function (r) {
                    report = r.report;
                    render();
                    App.toast('파일 ' + r.removed + '개(' + r.freed + ')를 지웠습니다.');
                }).catch(function (err) { App.toast(err.message, 'error'); });
            });
        }
    });

    render();
})();
