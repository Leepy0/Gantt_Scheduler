// 대시보드: 진행 과제 간트
(function () {
    'use strict';

    // 마지막으로 본 조건(유형·법인·내 과제·간트 범위·정렬)을 기억했다가, 조건 없이 대시보드를 열면 그 조건으로 연다.
    // '조건 해제'(reset=1)는 기억도 지운다. 저장소를 못 쓰면 App.pref 가 조용히 건너뛴다
    var search = location.search.replace(/^\?/, '');
    if (/(^|&)reset=1(&|$)/.test(search)) {
        App.pref.set('dash.query', '');
        history.replaceState(null, '', location.pathname);
    } else if (!search) {
        var saved = App.pref.get('dash.query', '');
        if (saved) { location.replace(location.pathname + '?' + saved); return; }
    } else {
        App.pref.set('dash.query', search);
    }

    var D = App.d;
    var data = App.data();
    var host = document.getElementById('gantt');
    var zoom = App.pref.get('dash.zoom', 'week');
    var gantt = null;

    // 과제 한 행: 위쪽이 목표(단계별 구간), 아래쪽이 실행.
    // 단계 구간이 겹치면(동시 진행 등) 겹치지 않는 줄로 나눠 최대 MAX_LANES 줄까지 쌓는다 — 한 줄이면 예전 모양 그대로
    var MAX_LANES = 5;
    var lane = { planTop: 8, planH: 18, actTop: 30, actH: 12 };
    var LANE_H = 26;

    function laneAt(k, count) {
        if (count <= 1) return lane;
        var top = 4 + k * LANE_H;
        return { planTop: top, planH: 15, actTop: top + 16, actH: 6 };
    }

    // 단계 하나의 목표 막대·실행 막대가 차지하는 날짜 범위(일 번호). 목표와 실행은 세로로 따로 놓이므로 서로는 겹쳐도 된다
    function spans(bars) {
        var out = { plan: null, act: null };
        bars.forEach(function (b) {
            var key = b.cls.indexOf('plan') === 0 ? 'plan' : 'act';
            var f = D.num(b.from), t = D.num(b.to);
            var r = out[key];
            if (!r) out[key] = { from: f, to: t };
            else { r.from = Math.min(r.from, f); r.to = Math.max(r.to, t); }
        });
        return out;
    }

    // 단계 순서대로, 앞 단계와 목표끼리·실행끼리 겹치지 않는 첫 줄에 놓는다. 5줄이 모두 겹치면 가장 일찍 끝나는 줄에 겹쳐 놓는다
    function packLanes(stages) {
        var lanes = [];
        return stages.map(function (s, i) {
            var r = spans(Gantt.stageBars(s, i, lane, true));
            var fits = function (l) {
                return (!r.plan || l.plan == null || l.plan < r.plan.from) && (!r.act || l.act == null || l.act < r.act.from);
            };
            var k = -1;
            for (var j = 0; j < lanes.length && k < 0; j++) { if (fits(lanes[j])) k = j; }
            if (k < 0 && lanes.length < MAX_LANES) { k = lanes.length; lanes.push({ plan: null, act: null }); }
            if (k < 0) {
                k = 0;
                lanes.forEach(function (l, j) { if (Math.max(l.plan || 0, l.act || 0) < Math.max(lanes[k].plan || 0, lanes[k].act || 0)) k = j; });
            }
            if (r.plan) lanes[k].plan = Math.max(lanes[k].plan || 0, r.plan.to);
            if (r.act) lanes[k].act = Math.max(lanes[k].act || 0, r.act.to);
            return k;
        });
    }

    function rows() {
        return (data.timeline || []).map(function (p) {
            var bars = [];
            var lanes = packLanes(p.stages);
            var count = lanes.reduce(function (m, k) { return Math.max(m, k + 1); }, 1);
            p.stages.forEach(function (s, i) {
                bars = bars.concat(Gantt.stageBars(s, i, laneAt(lanes[i], count), true));
            });
            // 지연(빨강) → 지연 위험(노랑, 예상 초과 일수) → 진행률. 정체는 글자로 덧붙인다 (판정은 서버 값)
            var side = p.delayDays > 0
                ? '<span class="alarm">' + App.icon('alert') + '</span><span class="alarm">D+' + p.delayDays + '</span>'
                : p.atRisk
                    ? '<span class="warn" title="' + App.esc(p.risk || '지연 위험') + '">' + App.icon('clock') + (p.riskDays > 0 ? '+' + p.riskDays : '위험') + '</span>'
                    : '<span>' + p.progress + '%</span>';
            if (p.stale) side += '<span class="muted" title="최근 변경 없음">정체</span>';
            return {
                key: 'p' + p.id,
                cls: 'project',
                title: p.title,
                sub: p.corporation + (p.currentStage ? ' · ' + p.currentStage : ''),
                side: side,
                href: App.url('/Projects/Detail?id=' + p.id),
                height: count > 1 ? 8 + count * LANE_H : 50,
                bars: bars
            };
        });
    }

    // print: 인쇄할 때는 월 배율로 전체 일정을 종이 한 폭에 담는다 (화면처럼 가로 스크롤 안의 일부만 잘려 나오지 않게)
    function render(keepScroll, print) {
        if (!host) return;
        gantt = Gantt.render(host, {
            rows: rows(), today: data.today, holidays: data.holidays, zoom: print ? 'month' : zoom,
            fit: print ? 1000 : null,
            corner: '과제', keepScroll: keepScroll && !print
        });
        document.querySelectorAll('#zoom [data-zoom]').forEach(function (b) {
            b.classList.toggle('on', b.dataset.zoom === zoom);
            b.setAttribute('aria-pressed', b.dataset.zoom === zoom ? 'true' : 'false');
        });
    }

    document.querySelectorAll('#zoom [data-zoom]').forEach(function (b) {
        b.addEventListener('click', function () {
            zoom = b.dataset.zoom;
            App.pref.set('dash.zoom', zoom);
            render(false);
        });
    });

    // 정렬: 고르면 그 조건의 주소로 간다(서버가 같은 순서로 그린다)
    var sortPick = document.getElementById('gantt-sort');
    if (sortPick) sortPick.addEventListener('change', function () { location.href = sortPick.value; });
    // 유형·법인은 바꾸는 즉시 조회
    document.querySelectorAll('#dash-filters select').forEach(function (el) {
        el.addEventListener('change', function () { document.getElementById('dash-filters').submit(); });
    });

    var todayBtn = document.getElementById('go-today');
    if (todayBtn) todayBtn.addEventListener('click', function () { if (gantt) gantt.scrollToToday(); });

    var more = document.getElementById('corp-more');
    if (more) {
        more.addEventListener('click', function () {
            var all = document.getElementById('corp-bars').classList.toggle('all');
            more.textContent = all ? '상위 5개만' : '전체 보기';
            more.setAttribute('aria-expanded', all ? 'true' : 'false');
        });
    }

    // 화면 폭이 바뀌면 다시 그린다(회전·창 크기 조절)
    var timer = null;
    var lastWidth = window.innerWidth;
    window.addEventListener('resize', function () {
        if (window.innerWidth === lastWidth) return;
        lastWidth = window.innerWidth;
        clearTimeout(timer);
        timer = setTimeout(function () { render(true); }, 150);
    });

    App.onPrint(function () { render(false, true); }, function () { render(false); });

    render(false);
})();
