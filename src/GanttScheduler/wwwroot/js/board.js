// TV 상황판(/Board) — 사람 없이 켜 두는 화면.
// 1) 화면 높이에 맞춰 목록을 쪽으로 나누고 일정 시간마다 넘긴다(스크롤 없이 모두 보이게).
// 2) 1분마다 데이터 버전(?handler=Stamp)만 묻고, 바뀌었을 때만 화면을 다시 받는다 — 과제 수백 건을 1분마다 그리지 않게.
// 계산은 하지 않는다. 서버가 그린 행을 보이고 숨길 뿐이다.
(function () {
    'use strict';

    var PAGE_MS = 12000;      // 쪽 넘김 간격
    var POLL_MS = 60000;      // 데이터 버전 확인 간격
    // 데이터가 그대로여도 이만큼 지나면 다시 받는다 — 정체·위험 판정은 서버 시각에 따라 하루 중에도 바뀌기 때문(판정은 서버가 한다)
    var REFRESH_MS = 30 * 60000;
    var lastRefresh = Date.now();
    var pageTimer = null;
    var tick = 0;

    // 목록 하나를 칸 높이에 맞는 쪽으로 나눈다. 행의 위치(offsetTop)를 보고 칸을 넘는 행에서 새 쪽을 시작한다
    function paginate(panel) {
        var list = panel.querySelector('[data-rows]');
        if (!list) return null;
        var rows = Array.prototype.slice.call(list.children);
        rows.forEach(function (r) { r.hidden = false; });
        var height = list.clientHeight;
        var pages = [[]];
        var start = rows.length ? rows[0].offsetTop : 0;
        rows.forEach(function (r) {
            var bottom = r.offsetTop + r.offsetHeight - start;
            // 한 행이 칸보다 커도 그 행 하나로 한 쪽을 만든다(빈 쪽이 생기지 않게)
            if (bottom > height + 1 && pages[pages.length - 1].length > 0) {
                pages.push([]);
                start = r.offsetTop;
            }
            pages[pages.length - 1].push(r);
        });
        return { panel: panel, rows: rows, pages: pages };
    }

    var pagers = [];

    function layout() {
        pagers = Array.prototype.map.call(document.querySelectorAll('#board [data-pager]'), paginate).filter(Boolean);
        show();
    }

    function show() {
        pagers.forEach(function (p) {
            var n = p.pages.length;
            var at = tick % n;
            p.rows.forEach(function (r) { r.hidden = true; });
            p.pages[at].forEach(function (r) { r.hidden = false; });
            var no = p.panel.querySelector('[data-page-no]');
            if (no) no.textContent = n > 1 ? (at + 1) + ' / ' + n + '쪽' : '';
        });
    }

    function startPaging() {
        if (pageTimer) clearInterval(pageTimer);
        tick = 0;
        layout();
        pageTimer = setInterval(function () { tick++; show(); }, PAGE_MS);
    }

    var resizeTimer = null;
    window.addEventListener('resize', function () {
        clearTimeout(resizeTimer);
        resizeTimer = setTimeout(layout, 200);
    });

    function setOffline(off) {
        var el = document.getElementById('board-conn');
        if (el) el.hidden = !off;
    }

    // 화면 다시 받기: 같은 주소의 HTML 을 받아 #board 만 바꾼다(깜빡임 없이). 실패하면 지금 화면을 그대로 둔다
    function refresh() {
        return fetch(location.href, { headers: { 'X-Background': '1' }, cache: 'no-store' })
            .then(function (r) { if (!r.ok) throw new Error(r.status); return r.text(); })
            .then(function (html) {
                var doc = new DOMParser().parseFromString(html, 'text/html');
                var next = doc.getElementById('board');
                var cur = document.getElementById('board');
                if (!next || !cur) throw new Error('board');
                var build = doc.documentElement.getAttribute('data-build');
                if (build && build !== document.documentElement.getAttribute('data-build')) { location.reload(); return; }
                cur.replaceWith(document.importNode(next, true));
                lastRefresh = Date.now();
                setOffline(false);
                startPaging();
            });
    }

    function poll() {
        var board = document.getElementById('board');
        if (!board) return;
        fetch(board.getAttribute('data-stamp-url'), { headers: { 'Accept': 'application/json', 'X-Background': '1' }, cache: 'no-store' })
            .then(function (r) { if (!r.ok) throw new Error(r.status); return r.json(); })
            .then(function (j) {
                setOffline(false);
                // 서버가 새 버전으로 바뀌었으면 스타일·스크립트까지 새로 받는다
                if (j.build && j.build !== document.documentElement.getAttribute('data-build')) { location.reload(); return; }
                // 서버가 준 확인 시각을 그대로 적는다(바뀐 것이 없어도 화면이 살아 있음을 보이게)
                var checked = document.querySelector('#board [data-checked]');
                if (checked && j.checkedAt) checked.textContent = j.checkedAt;
                if (j.stamp !== document.getElementById('board').getAttribute('data-stamp') || Date.now() - lastRefresh >= REFRESH_MS) return refresh();
            })
            .catch(function () { setOffline(true); });
    }

    startPaging();
    setInterval(poll, POLL_MS);
    // 화면 점검·관리용: 바로 한 번 확인한다
    window.BoardPoll = poll;
})();
