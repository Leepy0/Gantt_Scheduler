// 관리 > 사용자 정리(합치기·담당 이관 후 사용 중지), 변경 이력 탭, 사용 현황 탭.
// admin.js 가 사용자 시트·탭 전환에서 부른다. 숫자(담당 수·접속자·비율)는 서버가 센 값을 그대로 그린다
(function () {
    'use strict';

    var esc = App.esc, icon = App.icon;
    function url(handler) { return App.url('/Admin?handler=' + handler); }

    function field(name, label, inner) {
        return '<label class="field" data-field="' + name + '"><span class="label">' + label + '</span>' + inner + '</label>';
    }

    // 사용자 목록의 '과제 n · 작업 n' (진행 과제 담당·미완료 작업 담당/부담당)
    function loadText(n) {
        if (!n.openProjects && !n.openWorks) return '';
        return '과제 ' + (n.openProjects || 0) + ' · 작업 ' + (n.openWorks || 0);
    }
    function hasLoad(n) { return (n.openProjects || 0) + (n.openWorks || 0) > 0; }

    function userOptions(users, except, activeOnly) {
        return users.filter(function (u) { return u.id !== except && (!activeOnly || u.active); })
            .sort(function (a, b) { return (b.active - a.active) || a.name.localeCompare(b.name, 'ko'); })
            .map(function (u) {
                return '<option value="' + u.id + '">' + esc(u.name) + (u.org ? ' · ' + esc(u.org) : '') + (u.active ? '' : ' (사용 중지)') + '</option>';
            }).join('');
    }

    // 사유 길이 상한은 서버(Limits.Reason)가 관리 화면의 data-field-limits 로 준다
    function reasonField() {
        var max = '';
        try { max = JSON.parse(document.getElementById('admin-body').dataset.fieldLimits || '{}').reason || ''; } catch (e) { max = ''; }
        return field('reason', '사유 <span class="opt">선택</span>', '<textarea name="reason" rows="2" maxlength="' + max + '" placeholder="예: 퇴사, 부서 이동, 이름 오타"></textarea>');
    }

    // 담당 이관 후 사용 중지: 넘겨받을 사람과 범위(진행 과제 담당·미완료 작업 담당/부담당)를 고른다
    function openReassign(n, users, change) {
        var html = '<p class="form-note">' + icon('alert') + '<span><b>' + esc(n.name) + '</b> 님이 맡은 진행 과제 ' + (n.openProjects || 0) +
            '건과 미완료 작업 ' + (n.openWorks || 0) + '건을 다른 사람에게 넘기고 사용 중지합니다. 완료·중단 과제의 담당 기록은 그대로 남습니다.</span></p>' +
            field('toId', '넘겨받을 사람', '<select name="toId" required><option value="">골라 주세요</option>' + userOptions(users, n.id, true) + '</select>') +
            '<div class="field" data-field="scope"><span class="label">넘길 범위</span>' +
            '<label class="check"><input type="checkbox" name="projects"' + (n.openProjects ? ' checked' : ' disabled') + ' />진행 과제 담당 <span class="num muted">' + (n.openProjects || 0) + '건</span></label>' +
            '<label class="check"><input type="checkbox" name="works"' + (n.openWorks ? ' checked' : ' disabled') + ' />미완료 작업 담당·부담당 <span class="num muted">' + (n.openWorks || 0) + '건</span></label>' +
            '<span class="hint">넘기지 않은 것은 사용 중지된 사람 이름으로 남습니다. 바뀐 과제마다 활동이 남고 넘겨받는 사람에게 알림이 갑니다.</span></div>' +
            reasonField();
        App.sheet({
            title: '담당 이관 후 사용 중지 · ' + n.name,
            html: html,
            primary: '이관하고 사용 중지',
            onSubmit: async function (form) {
                var f = form.elements;
                var r = await change('Reassign', {
                    fromId: n.id, toId: f.toId.value ? +f.toId.value : null,
                    projects: f.projects.checked, works: f.works.checked, reason: f.reason.value
                });
                if (!r.notice) App.toast('사용 중지했습니다.');   // 서버 안내는 change() 가 띄운다
            }
        });
    }

    // 사용자 합치기: 이 사용자(from)의 모든 기록을 고른 사용자(into)로 옮기고 이 사용자를 지운다
    function openMerge(n, users, change) {
        var html = '<p class="form-note">' + icon('alert') + '<span>이름 오타 등으로 같은 사람이 둘이 된 경우에 씁니다. <b>' + esc(n.name) +
            '</b> 님의 담당·작업·활동·첨부·IP 연결·알림을 모두 고른 사람에게 옮기고 <b>' + esc(n.name) + '</b> 이름은 지웁니다. 되돌릴 수 없습니다.</span></p>' +
            field('intoId', '합칠 사람 (남길 이름)', '<select name="intoId" required><option value="">골라 주세요</option>' + userOptions(users, n.id, false) + '</select>') +
            '<span class="hint">관리자 권한은 둘 중 하나라도 관리자면 남깁니다. 메일·소속은 남길 사람에게 없을 때만 가져옵니다.</span>' +
            reasonField();
        App.sheet({
            title: '다른 사용자로 합치기 · ' + n.name,
            html: html,
            primary: '합치기',
            onSubmit: async function (form) {
                var f = form.elements;
                if (!f.intoId.value) {
                    var bad = new Error('합칠 사람을 골라 주세요.');
                    bad.field = 'intoId';
                    throw bad;
                }
                var into = users.filter(function (u) { return u.id === +f.intoId.value; })[0];
                var ok = await App.confirm({ title: '사용자 합치기', message: n.name + ' 님을 ' + (into ? into.name : '') + ' 님으로 합칩니다. ' + n.name + ' 이름은 지워지고 되돌릴 수 없습니다.', verb: '합치기' });
                if (!ok) return false;
                var r = await change('Merge', { fromId: n.id, intoId: +f.intoId.value, reason: f.reason.value });
                if (!r.notice) App.toast('합쳤습니다.');
            }
        });
    }

    // ── 변경 이력 탭 ────────────────────────────────────────────────
    var query = { kind: '', from: '', to: '', user: '', page: 1 };
    var lists = null;   // 종류·사람 고르기 목록(처음 받은 것)

    function changeItem(c) {
        var values = c.oldValue != null || c.newValue != null
            ? '<span class="feed-reason muted">' + esc(c.oldValue != null ? c.oldValue : '없음') + ' → ' + esc(c.newValue != null ? c.newValue : '없음') + '</span>'
            : '';
        return '<li><span class="history-time">' + App.timeTag(c.at) + '</span>' +
            '<span class="history-user">' + esc(c.user || '서버') + '</span>' +
            '<span class="history-body"><span class="chip">' + esc(c.kindName) + '</span><span class="history-text">' + esc(c.text) + '</span>' + values + '</span></li>';
    }

    function changesFilterHtml() {
        var kinds = (lists ? lists.kinds : []).map(function (k) {
            return '<option value="' + k.key + '"' + (query.kind === k.key ? ' selected' : '') + '>' + esc(k.label) + '</option>';
        }).join('');
        var people = (lists ? lists.people : []).map(function (p) {
            return '<option value="' + p.id + '"' + (String(query.user) === String(p.id) ? ' selected' : '') + '>' + esc(p.name) + '</option>';
        }).join('');
        return '<form class="card filter-row" data-changes-filter>' +
            '<select name="kind" aria-label="종류"><option value="">종류 전체</option>' + kinds + '</select>' +
            '<input type="date" name="from" value="' + esc(query.from) + '" aria-label="시작일" />' +
            '<span class="muted">~</span>' +
            '<input type="date" name="to" value="' + esc(query.to) + '" aria-label="종료일" />' +
            '<select name="user" aria-label="사람"><option value="">사람 전체</option><option value="0"' + (query.user === '0' ? ' selected' : '') + '>서버(자동)</option>' + people + '</select>' +
            '<button type="submit" class="btn">' + icon('search') + '조회</button></form>';
    }

    async function renderChanges(body) {
        body.innerHTML = '<div class="loading"><span class="spinner"></span>불러오는 중…</div>';
        try {
            var qs = '&page=' + query.page + (query.kind ? '&kind=' + encodeURIComponent(query.kind) : '') +
                (query.from ? '&from=' + query.from : '') + (query.to ? '&to=' + query.to : '') + (query.user !== '' ? '&user=' + query.user : '');
            var r = await App.get(url('Changes') + qs);
            lists = { kinds: r.kinds, people: r.people };
            var p = r.page;
            query.page = p.page;
            var html = '<div class="admin-changes" data-changes>' + changesFilterHtml() +
                '<p class="hint">기준 정보(계층·유형·단계 템플릿·공휴일 등)·사용자·IP 연결·업데이트 설정의 변경 기록입니다. 일정 계산을 바꾸는 변경을 언제 누가 했는지 찾을 수 있습니다.</p>';
            if (!p.items.length) html += '<div class="card"><p class="muted">조건에 맞는 변경 기록이 없습니다.</p></div>';
            else html += '<div class="card flush"><ul class="history" data-changes-list>' + p.items.map(changeItem).join('') + '</ul></div>';
            if (p.pages > 1) {
                html += '<nav class="pager" aria-label="쪽">' +
                    '<button type="button" class="btn small" data-changes-page="' + (p.page - 1) + '"' + (p.page <= 1 ? ' disabled' : '') + '>' + icon('left') + '이전</button>' +
                    '<span class="num muted">' + p.page + ' / ' + p.pages + ' (' + p.total + '건)</span>' +
                    '<button type="button" class="btn small" data-changes-page="' + (p.page + 1) + '"' + (p.page >= p.pages ? ' disabled' : '') + '>다음' + icon('right') + '</button></nav>';
            } else if (p.total) {
                html += '<p class="hint num">' + p.total + '건</p>';
            }
            body.innerHTML = html + '</div>';
            App.localizeTimes(body);
        } catch (err) {
            body.innerHTML = changesFilterHtml() + '<div class="card"><p class="form-error">' + icon('alert') + '<span>' + esc(err.message) + '</span></p></div>';
        }
    }

    // ── 사용 현황 탭 ────────────────────────────────────────────────
    function td(label, value, cls) {
        return '<td data-label="' + label + '"' + (cls ? ' class="' + cls + '"' : '') + '>' + value + '</td>';
    }

    async function renderUsage(body, isAdmin) {
        body.innerHTML = '<div class="loading"><span class="spinner"></span>불러오는 중…</div>';
        try {
            var u = (await App.get(url('Usage'))).usage;
            var t = u.total;
            var html = '<div class="admin-usage" data-usage>' +
                '<p class="hint">최근 ' + u.days + '일(' + esc(u.since) + ' 이후) 기준입니다. 사람을 살피기보다 조직별로 도구가 쓰이고 있는지 보는 화면입니다.</p>' +
                '<div class="kpis wide">' +
                '<div class="kpi"><span class="kpi-label">최근 접속자</span><span class="kpi-value num">' + t.activeUsers + '<span class="kpi-sub"> / ' + t.users + '명</span></span><span class="kpi-sub">사용 중인 사용자의 ' + t.activePercent + '%</span></div>' +
                '<div class="kpi"><span class="kpi-label">갱신 건수</span><span class="kpi-value num">' + t.updates + '</span><span class="kpi-sub">활동 기록(댓글·일정·진척 등)</span></div>' +
                '<div class="kpi' + (t.quietPercent >= 50 && t.openProjects ? ' warn' : '') + '"><span class="kpi-label">갱신 없는 진행 과제</span><span class="kpi-value num">' + t.quietPercent + '%</span><span class="kpi-sub">진행 ' + t.openProjects + '건 중 ' + t.quietProjects + '건이 ' + u.days + '일 동안 그대로</span></div>' +
                '</div>' +
                '<section class="card"><div class="card-head"><h2>조직별</h2></div><div class="table-scroll"><table class="simple-table stack-table" data-usage-orgs>' +
                '<thead><tr><th>조직</th><th class="r">사용자</th><th class="r">최근 접속</th><th class="r">갱신</th><th class="r">진행 과제</th><th class="r">갱신 없음</th></tr></thead><tbody>' +
                u.orgs.map(function (o) {
                    return '<tr><td>' + esc(o.name) + '</td>' + td('사용자', o.users, 'r') + td('최근 접속', o.activeUsers + ' <span class="muted">(' + o.activePercent + '%)</span>', 'r') +
                        td('갱신', o.updates, 'r') + td('진행 과제', o.openProjects, 'r') +
                        td('갱신 없음', o.openProjects ? o.quietProjects + ' <span class="muted">(' + o.quietPercent + '%)</span>' : '-', 'r') + '</tr>';
                }).join('') + '</tbody></table></div></section>';
            if (isAdmin && u.people) {
                html += '<section class="card"><div class="card-head"><h2>사람별 마지막 접속</h2><span class="muted">관리자만 봅니다</span></div>' +
                    '<div class="table-scroll"><table class="simple-table stack-table" data-usage-people>' +
                    '<thead><tr><th>이름</th><th>소속</th><th>마지막 접속</th><th class="r">갱신</th><th class="r">진행 과제</th><th class="r">미완료 작업</th></tr></thead><tbody>' +
                    u.people.map(function (p) {
                        return '<tr' + (p.active ? '' : ' class="muted"') + '><td>' + esc(p.name) + (p.active ? '' : ' <span class="chip">사용 중지</span>') + '</td>' +
                            td('소속', esc(p.org || '소속 없음')) + td('마지막 접속', p.lastSeen ? esc(p.lastSeen) : '<span class="muted">접속 기록 없음</span>') +
                            td('갱신', p.updates, 'r') + td('진행 과제', p.openProjects, 'r') + td('미완료 작업', p.openWorks, 'r') + '</tr>';
                    }).join('') + '</tbody></table></div></section>';
            }
            body.innerHTML = html + '</div>';
        } catch (err) {
            body.innerHTML = '<div class="card"><p class="form-error">' + icon('alert') + '<span>' + esc(err.message) + '</span></p></div>';
        }
    }

    // 탭 본문 안의 이벤트(변경 이력 조건·쪽)
    function bind(body) {
        body.addEventListener('submit', function (e) {
            var form = e.target.closest('[data-changes-filter]');
            if (!form) return;
            e.preventDefault();
            e.stopPropagation();
            var f = form.elements;
            query = { kind: f.kind.value, from: f.from.value, to: f.to.value, user: f.user.value, page: 1 };
            renderChanges(body);
        }, true);
        body.addEventListener('click', function (e) {
            var t = e.target.closest('[data-changes-page]');
            if (!t) return;
            query.page = +t.dataset.changesPage;
            renderChanges(body);
        });
    }

    window.AdminPeople = {
        loadText: loadText,
        hasLoad: hasLoad,
        openReassign: openReassign,
        openMerge: openMerge,
        renderChanges: renderChanges,
        renderUsage: renderUsage,
        bind: bind
    };
})();
