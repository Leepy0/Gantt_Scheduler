// 관리 > 업데이트: 서버가 준 상태 하나로 화면을 그리고, 동작할 때마다 최신 상태를 받아 다시 그린다
(function () {
    'use strict';

    var data = App.data();
    var st = data.status;
    var admin = !!data.admin;
    var root = document.getElementById('update-body');
    var url = App.url('/Admin/Update');
    var failure = null;        // 마지막 동작의 실패 { message, fix }
    var uploading = null;      // 올리는 중이면 진행률(0~100)
    var waiting = null;        // 서버가 다시 뜨기를 기다리는 중이면 { text, since }
    var pollTimer = null;
    var esc = App.esc, icon = App.icon;
    var backup = data.backup || null;   // 백업 설정·목록 (관리자에게만 온다)
    var backupBusy = false;    // '지금 백업'을 누르고 기다리는 중
    var backupFailure = null;  // 마지막 '지금 백업'의 실패 { message, fix }
    var jsonRestoreBusy = false;
    var jsonRestoreFailure = null;
    var pendingApply = null;   // 적용 전 백업이 끝나면 교체가 시작된다 — 그때 보일 문구
    var diag = null;           // 진단 정보 (열었을 때만 받아 온다)
    var diagOpen = false;

    function size(bytes) { return bytes > 0 ? App.size(bytes) : ''; }
    function hours(h) { return (h < 10 ? '0' : '') + h + '시'; }

    function fixBlock(cls, message, fix) {
        return '<div class="' + cls + '">' + icon('alert') + '<div><p>' + esc(message) + '</p>' +
            (fix ? '<p class="fix">' + esc(fix) + '</p>' : '') + '</div></div>';
    }

    function notes(text) {
        return text ? '<div class="field"><span class="label">변경 내용</span><pre class="notes">' + esc(text) + '</pre></div>' : '';
    }

    function schemaWarn(changed) {
        if (!changed) return '';
        // 서버가 적용 전에 백업하도록 되어 있으면(Backup:BeforeSchemaUpdate) 그렇게 알린다 — 실패하면 적용하지 않는다
        var auto = backup && backup.beforeSchemaUpdate;
        return '<div class="form-note">' + icon('alert') + '<div><p>DB 구조가 바뀌는 업데이트입니다. 적용한 뒤에는 프로그램만 되돌릴 수 없습니다. ' +
            (auto ? '적용을 누르면 먼저 DB 를 백업하고, 백업에 실패하면 적용하지 않습니다.' : '먼저 DB 를 백업해 주세요.') + ' 자동으로는 적용되지 않습니다.</p>' +
            (st.sqliteData ? '<p class="fix">SQLite DB를 사용 중입니다. 개발 환경에서는 구조 변경 시 DB가 새로 만들어질 수 있으며, 운영 환경에서는 데이터 손실 방지를 위해 스키마 변경 업데이트를 차단합니다.</p>' : '') + '</div></div>';
    }

    function autoText(version) {
        var s = st.settings;
        if (!s.autoApply) return '자동 적용이 꺼져 있습니다. 직접 눌러 적용합니다.';
        if (version && st.startFailure && version === st.startFailure.version) return '시작되지 않아 되돌렸던 버전이라 자동으로는 다시 적용하지 않습니다. 직접 눌러 적용합니다.';
        if (version && version === st.skipAuto) return '되돌렸던 버전이라 자동으로는 다시 적용하지 않습니다. 직접 눌러 적용합니다.';
        var when = s.windowFrom === s.windowTo ? '시간대 제한 없이' : hours(s.windowFrom) + '~' + hours(s.windowTo) + '(한국 시간) 사이에';
        return when + ' ' + s.idleMinutes + '분 동안 아무도 쓰지 않으면 자동으로 적용합니다.';
    }

    // ── 카드: 현재 버전 ──────────────────────────────────────────────
    function currentCard() {
        var busy = st.phase === 'checking';
        var h = '<section class="card"><div class="card-head"><h2>현재 버전</h2><span class="spacer"></span>' +
            (admin ? '<button type="button" class="btn small" data-act="check"' + (st.phase !== 'idle' ? ' disabled' : '') + '>' +
                (busy ? '<span class="spinner"></span>확인 중…' : icon('refresh') + '지금 확인') + '</button>' : '') + '</div>';
        h += '<div class="update-now"><span class="update-version num">v' + esc(st.version) + '</span>' +
            '<span class="chip' + (st.channel === 'release' ? ' accent' : '') + '">' + esc(st.channelName) + '</span></div>';
        h += '<dl class="dl">' +
            (st.commit ? '<dt>커밋</dt><dd class="num">' + esc(st.commit) + '</dd>' : '') +
            '<dt>실행 방식</dt><dd>' + esc(st.host) + '</dd>' +
            '<dt>받아 오는 곳</dt><dd>' + esc(st.source) + '</dd>' +
            '<dt>서명 검증</dt><dd>' + (st.signatureCheck
                ? '<span class="chip ok">' + icon('check') + '켜짐</span> <span class="muted num">공개키 ' + esc(st.keyId || '') + '</span>'
                : '<span class="chip">서명 검증 꺼짐</span> <span class="muted">이 빌드에는 공개키가 없어 배포 파일의 출처는 확인하지 않고 SHA-256 으로 손상만 확인합니다.</span>') + '</dd>' +
            '<dt>마지막 확인</dt><dd class="num">' + (st.checkedAt ? esc(App.time(st.checkedAt)) : '아직 확인하지 않음') + '</dd></dl>';
        if (st.checkError) h += fixBlock('form-note', st.checkError, st.checkFix);
        if (!st.canApply && st.blockReason) h += fixBlock('form-note', st.blockReason, st.blockFix);
        return h + '</section>';
    }

    // ── 카드: 새 버전 (없음 / 있음 / 받는 중 / 적용 준비 / 적용 중) ──
    function nextCard() {
        var h = '<section class="card"><div class="card-head"><h2>새 버전</h2></div>';
        if (waiting) {
            h += '<div class="update-state"><span class="spinner"></span><div><p class="update-title">' + esc(waiting.text) + '</p>' +
                '<p class="muted">서버가 다시 시작되면 이 화면이 자동으로 바뀝니다. 보통 1분 안에 끝납니다. 새 버전이 시작되지 않으면 몇 분 안에 이전 버전으로 자동으로 되돌립니다.</p></div></div>';
            if (waiting.slow) h += fixBlock('form-note', '아직 서버가 돌아오지 않았습니다.', '서버의 App_Data/updates/apply.log 를 확인하거나 서버를 직접 다시 시작해 주세요.');
            return h + '</section>';
        }
        if (failure) h += fixBlock('form-error', failure.message, failure.fix);
        else if (st.error) h += fixBlock('form-error', st.error, st.errorFix);
        h += startFailureBlock();

        if (st.phase === 'backing-up') {
            h += '<div class="update-state" id="backing-up"><span class="spinner"></span><div class="grow"><p class="update-title">적용 전 DB 백업 중</p>' +
                '<p class="muted">백업이 끝나면 이어서 새 버전으로 바꿉니다. 실패하면 적용하지 않고 이유를 보여 줍니다.</p></div></div>';
        } else if (st.phase === 'downloading' || uploading != null) {
            var pct = uploading != null ? uploading : st.progress;
            h += '<div class="update-state"><span class="spinner"></span><div class="grow"><p class="update-title">' +
                (uploading != null ? '배포 파일 올리는 중' : '새 버전 받는 중') + ' <span class="num">' + pct + '%</span></p>' +
                '<span class="progress" role="progressbar" aria-valuemin="0" aria-valuemax="100" aria-valuenow="' + pct + '"><i style="width:' + pct + '%"></i></span></div></div>';
        } else if (st.staged) {
            var s = st.staged;
            h += '<div class="update-state">' + icon('download') + '<div><p class="update-title">v' + esc(s.version) + ' 적용 준비됨</p>' +
                '<p class="muted num">' + esc(App.time(s.at)) + ' · ' + (s.origin === 'upload' ? '파일로 올림' : '서버가 받음') + ' · ' + esc(s.by) +
                ' · SHA-256 ' + esc(s.sha256.slice(0, 12)) + '…</p></div></div>';
            h += schemaWarn(s.schemaChange) + notes(s.notes);
            h += '<p class="hint">' + esc(s.schemaChange ? '적용하면 서버가 잠깐(보통 1분 이내) 멈춥니다. 열려 있는 화면은 자동으로 다시 연결됩니다.'
                : autoText(s.version) + ' 적용하면 서버가 잠깐(보통 1분 이내) 멈춥니다.') + '</p>';
            if (admin) {
                h += '<div class="form-actions"><button type="button" class="btn ghost" data-act="discard">받은 파일 지우기</button>' +
                    '<button type="button" class="btn primary" data-act="apply"' + (st.canApply ? '' : ' disabled') + '>' + icon('bolt') + '지금 적용</button></div>';
            }
        } else if (st.latest) {
            var l = st.latest;
            h += '<div class="update-state">' + icon('flag') + '<div><p class="update-title">v' + esc(l.version) + ' 을 받을 수 있습니다</p>' +
                '<p class="muted num">' + [l.published ? App.time(l.published) + ' 배포' : '', size(l.size)].filter(Boolean).map(esc).join(' · ') + '</p></div></div>';
            h += schemaWarn(l.schemaChange) + notes(l.notes);
            if (l.version === st.skipAuto) h += '<p class="hint">되돌렸던 버전이라 자동으로는 다시 받지 않습니다.</p>';
            if (admin) h += '<div class="form-actions"><button type="button" class="btn primary" data-act="download">' + icon('download') + '받기</button></div>';
        } else {
            h += '<div class="empty">' + icon('check') + (st.checkedAt && !st.checkError ? '최신 버전을 쓰고 있습니다.' : '확인된 새 버전이 없습니다.') + '</div>';
        }
        return h + '</section>';
    }

    // 적용했지만 시작되지 않은 버전 (교체 도우미가 기록). 되돌렸으면 그 버전은 자동으로 다시 적용하지 않는다
    function startFailureBlock() {
        var f = st.startFailure;
        if (!f) return '';
        var h = '<div class="form-warn start-failure">' + icon('alert') + '<div class="grow"><p>' + esc(f.error) + '</p>' +
            '<p class="muted num">' + esc(App.time(f.at)) + '</p>' +
            (f.detail ? '<pre class="notes">' + esc(f.detail) + '</pre>' : '') +
            '<p class="fix">' + esc(f.rolledBack
                ? 'v' + f.version + ' 은 자동으로 다시 받거나 적용하지 않습니다. 원인을 확인한 뒤 다시 시도하거나, 고친 새 버전을 기다려 주세요.'
                : '서버의 App_Data/startup-error.log 와 App_Data/updates/apply.log 에 자세한 내용이 있습니다.') + '</p>';
        if (admin) {
            h += '<div class="form-actions">' +
                '<button type="button" class="btn ghost small" data-act="dismiss-failure">안내 닫기</button>' +
                // 받아 둔 파일로 다시 적용하는 것은 '지금 적용'과 같은 조건(쓰기 권한·로컬 빌드 등)에 막힌다. 다시 받기는 그대로 된다
                (f.retry ? '<button type="button" class="btn small" data-act="retry"' +
                    (st.phase !== 'idle' || (f.retry === 'apply' && !st.canApply) ? ' disabled' : '') + '>' + icon('refresh') + '다시 시도</button>' : '') +
                '</div>';
        }
        return h + '</div></div>';
    }

    // ── 카드: 파일로 올려 적용 ───────────────────────────────────────
    function uploadCard() {
        if (!admin) return '';
        var h = '<section class="card"><div class="card-head"><h2>파일로 업데이트</h2></div>' +
            '<p class="muted">서버가 인터넷에 나갈 수 없을 때 씁니다. 릴리스의 <b>GanttScheduler-win-x64.zip</b> 을 받아 그대로 올리면, 서버가 내용을 확인한 뒤 적용 준비 상태로 둡니다.' +
            (st.signatureCheck ? ' 이 서버는 서명을 확인하므로 <b>GanttScheduler.json</b> 과 <b>GanttScheduler.json.sig</b> 도 함께 골라 세 파일을 올립니다.'
                : ' <b>GanttScheduler.json</b> 을 함께 고르면 zip 이 릴리스와 같은 파일인지도 확인합니다.') +
            (st.browserPage ? ' <a href="' + esc(st.browserPage) + '" target="_blank" rel="noopener">릴리스 페이지 열기</a>' : '') + '</p>' +
            '<form class="upload-row" id="upload-form"><input type="file" name="file" accept=".zip,.json,.sig" multiple aria-label="배포 파일(zip)과 배포 정보·서명 파일" required />' +
            '<button type="submit" class="btn"' + (st.phase !== 'idle' || uploading != null ? ' disabled' : '') + '>' + icon('upload') + '올리기</button></form>';
        return h + '</section>';
    }

    // ── 카드: 자동 적용 설정 ─────────────────────────────────────────
    function settingsCard() {
        var s = st.settings, dis = admin ? '' : ' disabled';
        function hourSelect(name, value, max) {
            var o = '';
            for (var i = 0; i <= max; i++) o += '<option value="' + i + '"' + (i === value ? ' selected' : '') + '>' + hours(i) + '</option>';
            return '<select name="' + name + '"' + dis + '>' + o + '</select>';
        }
        return '<section class="card"><div class="card-head"><h2>자동 업데이트</h2></div>' +
            '<form class="form" id="settings-form">' +
            '<label class="check"><input type="checkbox" name="autoCheck"' + (s.autoCheck ? ' checked' : '') + dis + ' />새 버전을 주기적으로 확인</label>' +
            '<label class="check"><input type="checkbox" name="autoApply"' + (s.autoApply ? ' checked' : '') + dis + ' />아무도 쓰지 않을 때 자동으로 받아서 적용</label>' +
            '<div class="field-row"><div class="field" data-field="windowFrom"><span class="label">적용 시간대 (한국 시간)</span>' +
            '<div class="range-row">' + hourSelect('windowFrom', s.windowFrom, 23) + '<span class="muted">~</span>' + hourSelect('windowTo', s.windowTo, 24) + '</div>' +
            '<span class="hint">시작과 끝이 같으면 시간대를 가리지 않습니다.</span></div>' +
            '<div class="field" data-field="idleMinutes"><label class="label" for="idle-min">사용이 없어야 하는 시간 (분)</label>' +
            '<input type="number" id="idle-min" name="idleMinutes" min="5" max="720" value="' + s.idleMinutes + '"' + dis + ' />' +
            '<span class="hint">해외 법인이 쓰는 시간일 수 있어, 이만큼 요청이 없을 때만 적용합니다.</span></div></div>' +
            '<p class="hint">DB 구조가 바뀌는 업데이트는 자동으로 적용하지 않고 관리자가 직접 누릅니다.</p>' +
            (admin ? '<div class="form-actions"><button type="submit" class="btn">설정 저장</button></div>' : '') +
            '</form></section>';
    }

    // ── 카드: 되돌리기·이력 ──────────────────────────────────────────
    function rollbackCard() {
        var p = st.previous;
        if (!p) return '';
        var h = '<section class="card danger-zone"><div class="grow"><h2>이전 버전으로 되돌리기</h2>' +
            '<p class="muted">v' + esc(p.version) + ' <span class="num">(' + esc(App.time(p.at)) + ' 까지 사용)</span> 파일을 보관하고 있습니다. 업데이트 뒤 문제가 있을 때 씁니다.</p>' +
            (!p.canRollback && p.blockReason ? '<p class="hint">' + esc(p.blockReason) + '</p>' : '') + '</div>';
        if (admin) h += '<button type="button" class="btn danger" data-act="rollback"' + (p.canRollback ? '' : ' disabled') + '>v' + esc(p.version) + ' 로 되돌리기</button>';
        return h + '</section>';
    }

    function historyCard() {
        if (!st.history.length) return '';
        var rows = st.history.map(function (x) {
            return '<tr><td class="num">' + esc(App.time(x.at)) + '</td><td>' + (x.kind === 'rollback' ? '되돌리기' : '업데이트') + '</td>' +
                '<td class="num">v' + esc(x.from) + ' → v' + esc(x.to) + '</td><td>' + esc(x.by) + '</td>' +
                '<td>' + (x.ok ? '<span class="chip ok">' + icon('check') + '성공</span>'
                    : '<span class="chip alarm">' + icon('alert') + '실패</span> <span class="muted">' + esc(x.error || '') + '</span>' +
                        (admin ? ' <button type="button" class="btn ghost small" data-act="diag-open">진단 정보</button>' : '')) +
                (x.backup ? '<p class="hint num">적용 전 백업 ' + esc(x.backup) + '</p>' : '') + '</td></tr>';
        }).join('');
        return '<section class="card"><div class="card-head"><h2>적용 이력</h2></div><div class="table-scroll"><table class="simple-table">' +
            '<thead><tr><th>일시</th><th>종류</th><th>버전</th><th>실행</th><th>결과</th></tr></thead><tbody>' + rows + '</tbody></table></div></section>';
    }

    // ── 카드: DB 백업 (지금 백업·목록) ──────────────────────────────
    var KINDS = { manual: '지금 백업', nightly: '야간', update: '업데이트 전' };

    function backupCard() {
        if (!backup) return '';
        var b = backup;
        var busy = backupBusy || b.running || st.phase === 'backing-up';
        var h = '<section class="card" id="backup"><div class="card-head"><h2>DB 백업</h2><span class="spacer"></span>' +
            (admin ? '<button type="button" class="btn small" data-act="backup"' + (busy || waiting ? ' disabled' : '') + '>' +
                (busy ? '<span class="spinner"></span>백업 중…' : icon('download') + '지금 백업') + '</button>' : '') + '</div>';
        h += '<dl class="dl">' +
            '<dt>DB</dt><dd>' + (b.provider === 'SqlServer' ? 'SQL Server' : 'SQLite') + ' · <span class="path">' + esc(b.dbLocation) + '</span></dd>' +
            '<dt>파일 묶음</dt><dd><span class="path">' + esc(b.filesPath) + '</span> <span class="muted">(첨부·암호화 키·운영 설정)</span></dd>' +
            '<dt>보관</dt><dd>성공한 백업 최근 ' + b.keep + '개</dd>' +
            '<dt>자동</dt><dd>야간 백업 ' + (b.nightly ? '켜짐 · 매일 ' + esc(b.nightlyAt) + ' (한국 시간)' : '꺼짐') +
            ' · 구조가 바뀌는 업데이트 전 ' + (b.beforeSchemaUpdate ? '켜짐' : '꺼짐') + '</dd></dl>';
        if (backupFailure) h += fixBlock('form-error', backupFailure.message, backupFailure.fix);
        if (b.guide) h += fixBlock('form-note', b.guide, '');
        if (b.list.length) {
            var rows = b.list.map(function (e) {
                var sizes = [e.dbSize != null ? 'DB ' + App.size(e.dbSize) : (e.ok ? 'DB 크기 모름' : ''), e.filesSize != null ? '묶음 ' + App.size(e.filesSize) : '']
                    .filter(Boolean).join(' · ');
                return '<tr data-backup="' + esc(e.name) + '"><td class="num">' + esc(App.time(e.at)) + '</td><td>' + esc(KINDS[e.kind] || e.kind) + '<p class="hint">' + esc(e.by) + '</p></td>' +
                    '<td class="num backup-name">' + esc(e.name) + (e.note ? '<p class="hint">' + esc(e.note) + '</p>' : '') + '</td>' +
                    '<td class="num">' + esc(sizes) + '</td>' +
                    '<td>' + (e.ok ? '<span class="chip ok">' + icon('check') + '성공</span>'
                        : '<span class="chip warn">' + icon('alert') + '실패</span><p class="hint">' + esc(e.error || '') + '</p>') + '</td></tr>';
            }).join('');
            h += '<div class="table-scroll"><table class="simple-table backup-table"><thead><tr><th>일시</th><th>종류</th><th>이름</th><th>크기</th><th>결과</th></tr></thead>' +
                '<tbody>' + rows + '</tbody></table></div>';
        } else {
            h += '<div class="empty">' + icon('download') + '아직 이 서버에서 만든 백업이 없습니다.</div>';
        }
        h += '<p class="hint">설정은 서버의 appsettings.Production.json 의 Backup:* 항목입니다. 복원 절차는 운영 문서(OPERATIONS.md)에 있습니다 — DB 백업과 같은 이름의 파일 묶음을 함께 복원해야 첨부가 열립니다.</p>';
        return h + '</section>';
    }


    // ── 카드: 테이블별 JSON 내보내기·복원 ───────────────────────────
    function jsonBackupCard() {
        if (!admin) return '';
        var h = '<section class="card" id="json-backup"><div class="card-head"><h2>테이블별 JSON 백업</h2><span class="spacer"></span>' +
            '<a class="btn small" href="' + esc(url + '?handler=JsonExport') + '">' + icon('download') + 'JSON 백업 내려받기</a></div>' +
            '<p class="muted">SQLite DB의 각 테이블을 개별 JSON 파일로 내보내 ZIP으로 묶습니다. 일반 DB 백업과 별도로 데이터 확인·이관에 사용할 수 있습니다.</p>' +
            '<form id="json-restore-form" class="upload-row"><input type="file" name="file" accept=".zip" aria-label="테이블별 JSON 백업 ZIP" required />' +
            '<button type="submit" class="btn ghost"' + (jsonRestoreBusy ? ' disabled' : '') + '>' +
            (jsonRestoreBusy ? '<span class="spinner"></span>복원 중…' : icon('upload') + 'JSON 백업 복원') + '</button></form>' +
            '<p class="hint">복원하면 현재 DB의 모든 테이블 데이터가 백업 내용으로 교체됩니다. 현재 앱 버전과 스키마가 일치하는 백업만 허용되며, 실패하면 전체 복원이 취소됩니다.</p>';
        if (jsonRestoreFailure) h += fixBlock('form-error', jsonRestoreFailure.message, jsonRestoreFailure.fix);
        return h + '</section>';
    }

    // ── 카드: 진단 정보 (apply.log 끝부분, startup-error.log, 상태 요약) ──
    function diagCard() {
        if (!admin) return '';
        var h = '<section class="card" id="diagnostics"><div class="card-head"><h2>진단 정보</h2><span class="spacer"></span>' +
            '<button type="button" class="btn small" data-act="diag" aria-expanded="' + diagOpen + '">' + icon(diagOpen ? 'up' : 'eye') + (diagOpen ? '접기' : '보기') + '</button>' +
            '<a class="btn small" id="diag-download" href="' + esc(url + '?handler=DiagnosticsFile') + '">' + icon('download') + '내려받기</a></div>' +
            '<p class="muted">업데이트가 실패했거나 새 버전이 시작되지 않았을 때 원인을 봅니다. 교체 기록(apply.log) 끝 60줄, 시작 오류(startup-error.log), 상태 요약입니다. 연결 문자열의 계정·비밀번호는 가립니다.</p>';
        if (diagOpen) {
            if (!diag) h += '<div class="loading"><span class="spinner"></span>불러오는 중…</div>';
            else {
                h += '<div class="field"><span class="label">상태 요약</span><pre class="notes diag" id="diag-state">' + esc(diag.state) + '</pre></div>' +
                    '<div class="field"><span class="label">교체 기록 apply.log <span class="opt">(끝 60줄' + (diag.applyLogSize ? ' · 전체 ' + App.size(diag.applyLogSize) : '') + ')</span></span>' +
                    '<pre class="notes diag" id="diag-apply">' + esc(diag.applyLog || '(기록 없음)') + '</pre></div>' +
                    '<div class="field"><span class="label">시작 오류 startup-error.log' + (diag.startupErrorAt ? ' <span class="opt">(' + esc(App.time(diag.startupErrorAt)) + ')</span>' : '') + '</span>' +
                    '<pre class="notes diag" id="diag-startup">' + esc(diag.startupError || '(기록 없음)') + '</pre></div>';
                if (diag.logs.length) {
                    h += '<div class="field"><span class="label">최근 업데이트·백업 경고</span><pre class="notes diag">' +
                        esc(diag.logs.map(function (e) { return App.time(e.at) + ' ' + e.category + ': ' + e.message; }).join('\n')) + '</pre></div>';
                }
            }
        }
        return h + '</section>';
    }

    function loadDiag() {
        diag = null;
        render();
        App.get(url + '?handler=Diagnostics').then(function (r) { diag = r.diagnostics; render(); })
            .catch(function (err) { diagOpen = false; App.toast(err.message, 'error'); render(); });
    }

    function render() {
        // 입력 중인 설정 값이 다시 그리면서 사라지지 않게, 설정 폼에 초점이 있으면 건너뛴다
        var active = document.activeElement;
        if (active && active.closest && active.closest('#settings-form') && !waiting) return;
        root.innerHTML = '<div class="grid-2">' + currentCard() + nextCard() + '</div>' +
            (waiting ? '' : '<div class="grid-2">' + uploadCard() + settingsCard() + '</div>' + rollbackCard() + backupCard() + jsonBackupCard()) + historyCard() +
            (waiting ? '' : diagCard());
        schedulePoll();
    }

    // 응답의 최신 상태 반영 (백업 정보는 관리자 응답에만 있다)
    function take(r) {
        st = r.status;
        if (r.backup !== undefined) backup = r.backup;
    }

    // 확인·받기처럼 서버에서 진행 중인 일이 있을 때만 상태를 다시 묻는다 (평소에는 요청을 만들지 않는다)
    function schedulePoll() {
        clearTimeout(pollTimer);
        if (waiting || uploading != null || (st.phase === 'idle' && !(backup && backup.running))) return;
        pollTimer = setTimeout(function () {
            App.poll(url + '?handler=Status').then(function (r) {
                take(r);
                if (pendingApply && st.phase === 'applying') { var text = pendingApply; pendingApply = null; waitRestart(text); return; }
                if (pendingApply && st.phase === 'idle') pendingApply = null;   // 백업 실패 — 이유는 st.error 로 보인다
                render();
            }).catch(function () {
                // 백업 뒤 교체가 시작돼 서버가 내려갔다
                if (pendingApply) { var text = pendingApply; pendingApply = null; waitRestart(text); return; }
                schedulePoll();
            });
        }, 1000);
    }

    function fail(err) {
        failure = { message: err.message, fix: err.data && err.data.fix };
        if (err.field) App.toast(err.message, 'error');
        render();
    }

    function act(name, body) {
        failure = null;
        return App.post(url + '?handler=' + name, body).then(function (r) { take(r); render(); return r; });
    }

    // 적용·되돌리기 뒤: 서버가 다른 빌드로 다시 뜰 때까지 기다렸다가 화면을 새로 연다
    function waitRestart(text) {
        var before = st.build;
        waiting = { text: text, since: Date.now(), slow: false };
        render();
        var timer = setInterval(function () {
            fetch(App.url('/version'), { cache: 'no-store', headers: { 'Accept': 'application/json' } })
                .then(function (r) { return r.ok ? r.json() : null; })
                .then(function (v) {
                    if (v && !v.updating && v.build !== before) { clearInterval(timer); location.reload(); return; }
                    // 서버가 같은 빌드로 돌아왔으면 교체가 실패한 것이다 → 이력에 원인이 남는다
                    if (v && !v.updating && Date.now() - waiting.since > 20000) { clearInterval(timer); location.reload(); }
                })
                .catch(function () { /* 내려가 있는 동안은 실패가 정상 */ });
            // 새 버전이 뜨지 않으면 도우미가 기다렸다가(기본 2분) 되돌리므로 그보다 넉넉히 기다린 뒤 안내한다
            if (!waiting.slow && Date.now() - waiting.since > 300000) { waiting.slow = true; render(); }
        }, 3000);
    }

    function applyStaged() {
        var s = st.staged;
        if (!s) return;
        App.confirm({
            title: 'v' + s.version + ' 적용',
            message: '서버가 잠깐(보통 1분 이내) 멈췄다가 새 버전으로 다시 시작합니다. 그동안 저장하지 않은 입력은 저장되지 않을 수 있습니다.' +
                (s.schemaChange ? ' DB 구조가 바뀌므로 DB 백업을 먼저 확인해 주세요.' : ''),
            verb: '적용'
        }).then(function (yes) {
            if (!yes) return;
            // 구조가 바뀌는 업데이트는 서버가 먼저 백업한다(단계 backing-up) — 끝나고 교체가 시작되면 폴링이 재시작을 기다린다
            act('Apply').then(function () {
                if (st.phase === 'backing-up') pendingApply = 'v' + s.version + ' 적용 중';
                else waitRestart('v' + s.version + ' 적용 중');
            }).catch(fail);
        });
    }

    root.addEventListener('click', function (e) {
        var btn = e.target.closest('[data-act]');
        if (!btn || btn.disabled) return;
        var name = btn.getAttribute('data-act');
        if (name === 'check') {
            st.phase = 'checking'; render();
            act('Check').then(function () { if (!st.checkError && !st.latest && !st.staged) App.toast('최신 버전입니다.'); }).catch(fail);
        } else if (name === 'download') {
            act('Download').then(function () { st.phase = 'downloading'; render(); }).catch(fail);
        } else if (name === 'discard') {
            act('Discard').catch(fail);
        } else if (name === 'dismiss-failure') {
            act('DismissFailure').catch(fail);
        } else if (name === 'retry') {
            // 받아 둔 파일이 있으면 그것으로 다시 적용하고, 없으면 다시 받는다
            if (st.startFailure && st.startFailure.retry === 'download') {
                act('Download').then(function () { st.phase = 'downloading'; render(); }).catch(fail);
            } else {
                applyStaged();
            }
        } else if (name === 'apply') {
            applyStaged();
        } else if (name === 'backup') {
            backupBusy = true;
            backupFailure = null;
            render();
            App.post(url + '?handler=Backup').then(function (r) {
                take(r);
                App.toast('백업했습니다: ' + (backup && backup.list.length ? backup.list[0].name : ''));
            }).catch(function (err) {
                backupFailure = { message: err.message, fix: err.data && err.data.fix };
                // 실패 기록이 목록에 남으므로 다시 읽는다
                return App.get(url + '?handler=Status').then(take).catch(function () { /* 그대로 */ });
            }).then(function () { backupBusy = false; render(); });
        } else if (name === 'diag') {
            diagOpen = !diagOpen;
            if (diagOpen) loadDiag(); else render();
        } else if (name === 'diag-open') {
            diagOpen = true;
            loadDiag();
            var el = document.getElementById('diagnostics');
            if (el) el.scrollIntoView({ block: 'start' });
        } else if (name === 'rollback') {
            var p = st.previous;
            App.confirm({
                title: 'v' + p.version + ' 로 되돌리기',
                message: '지금 버전(v' + st.version + ')을 내리고 이전 버전으로 다시 시작합니다. 서버가 잠깐 멈춥니다.',
                verb: '되돌리기'
            }).then(function (yes) {
                if (!yes) return;
                act('Rollback').then(function () { waitRestart('v' + p.version + ' 로 되돌리는 중'); }).catch(fail);
            });
        }
    });

    root.addEventListener('submit', function (e) {
        e.preventDefault();
        var form = e.target;
        if (form.id === 'settings-form') {
            if (document.activeElement) document.activeElement.blur();
            act('Settings', {
                autoCheck: form.autoCheck.checked, autoApply: form.autoApply.checked,
                windowFrom: +form.windowFrom.value, windowTo: +form.windowTo.value, idleMinutes: +form.idleMinutes.value
            }).then(function () { App.toast('설정을 저장했습니다.'); }).catch(fail);
        } else if (form.id === 'upload-form') {
            upload(form.file.files);
        } else if (form.id === 'json-restore-form') {
            var file = form.file.files && form.file.files[0];
            if (!file) return;
            App.confirm({
                title: 'JSON 백업 복원',
                message: '현재 DB의 모든 테이블 데이터를 선택한 백업으로 교체합니다. 되돌리려면 별도의 백업이 필요합니다. 계속할까요?',
                verb: '복원'
            }).then(function (yes) {
                if (!yes) return;
                jsonRestoreBusy = true;
                jsonRestoreFailure = null;
                render();
                var fd = new FormData();
                fd.append('file', file);
                var xhr = new XMLHttpRequest();
                xhr.open('POST', url + '?handler=JsonRestore');
                xhr.setRequestHeader('Accept', 'application/json');
                xhr.setRequestHeader('X-CSRF-TOKEN', document.querySelector('input[name="__RequestVerificationToken"]').value);
                xhr.onload = function () {
                    jsonRestoreBusy = false;
                    var body = null;
                    try { body = JSON.parse(xhr.responseText); } catch (err) { /* JSON 응답이 아닐 수 있음 */ }
                    if (xhr.status === 200 && body && body.ok) {
                        take(body);
                        App.toast('JSON 백업 복원을 마쳤습니다. 화면을 새로고침합니다.');
                        location.reload();
                    } else {
                        jsonRestoreFailure = { message: body && body.error ? body.error : 'JSON 백업을 복원하지 못했습니다(' + xhr.status + ').', fix: '' };
                    }
                    render();
                };
                xhr.onerror = function () {
                    jsonRestoreBusy = false;
                    jsonRestoreFailure = { message: '서버에 연결하지 못했습니다.', fix: 'DB 복원 결과가 불분명하면 서버 상태와 로그를 확인해 주세요.' };
                    render();
                };
                xhr.send(fd);
            });
        }
    });

    // 큰 파일이라 진행률을 보여 준다 (fetch 는 올리기 진행률을 알 수 없어 XMLHttpRequest 사용)
    function upload(files) {
        if (!files || !files.length) return;
        failure = null;
        uploading = 0;
        render();
        var fd = new FormData();
        for (var i = 0; i < files.length; i++) fd.append('file', files[i]);
        var xhr = new XMLHttpRequest();
        xhr.open('POST', url + '?handler=Upload');
        xhr.setRequestHeader('Accept', 'application/json');
        xhr.setRequestHeader('X-CSRF-TOKEN', document.querySelector('input[name="__RequestVerificationToken"]').value);
        xhr.upload.onprogress = function (e) {
            if (!e.lengthComputable) return;
            var pct = Math.round(e.loaded * 100 / e.total);
            if (pct !== uploading) { uploading = pct; render(); }
        };
        xhr.onload = function () {
            uploading = null;
            var body = null;
            try { body = JSON.parse(xhr.responseText); } catch (err) { /* JSON 이 아닌 응답 */ }
            if (xhr.status === 200 && body && body.ok) {
                take(body);
                App.toast('v' + st.staged.version + ' 을 올렸습니다. 내용을 확인한 뒤 적용해 주세요.');
            } else {
                failure = body && body.error ? { message: body.error, fix: body.fix }
                    : { message: '파일을 올리지 못했습니다(' + xhr.status + ').', fix: xhr.status === 413 || xhr.status === 404 ? '웹 서버의 요청 크기 한도를 넘었습니다. web.config 의 maxAllowedContentLength 를 확인하세요.' : '' };
            }
            render();
        };
        xhr.onerror = function () {
            uploading = null;
            failure = { message: '서버에 연결하지 못했습니다.', fix: '네트워크를 확인한 뒤 다시 올려 주세요.' };
            render();
        };
        xhr.send(fd);
    }

    render();
    // 다른 화면(서버 상태·관리자 칩)에서 #backup 으로 들어오면 그 카드로 (화면을 스크립트가 그려 브라우저가 찾지 못한다)
    if (location.hash) {
        var target = document.getElementById(location.hash.slice(1));
        if (target) target.scrollIntoView({ block: 'start' });
    }
})();
