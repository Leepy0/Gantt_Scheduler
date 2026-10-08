// 대시보드: 진행 과제 간트
(function () {
    'use strict';

    var data = App.data();
    var host = document.getElementById('gantt');
    var zoom = App.pref.get('dash.zoom', 'week');
    var gantt = null;

    // 과제 한 행: 위쪽이 목표(단계별 구간), 아래쪽이 실행
    var lane = { planTop: 8, planH: 18, actTop: 30, actH: 12 };

    function rows() {
        return (data.timeline || []).map(function (p) {
            var bars = [];
            p.stages.forEach(function (s, i) {
                bars = bars.concat(Gantt.stageBars(s, i, lane, true));
            });
            var side = p.delayDays > 0
                ? '<span class="alarm">' + App.icon('alert') + '</span><span class="alarm">D+' + p.delayDays + '</span>'
                : '<span>' + p.progress + '%</span>';
            return {
                key: 'p' + p.id,
                cls: 'project',
                title: p.title,
                sub: p.corporation + (p.currentStage ? ' · ' + p.currentStage : ''),
                side: side,
                href: App.url('/Projects/Detail?id=' + p.id),
                height: 50,
                bars: bars
            };
        });
    }

    function render(keepScroll) {
        if (!host) return;
        gantt = Gantt.render(host, {
            rows: rows(), today: data.today, holidays: data.holidays, zoom: zoom,
            corner: '과제', keepScroll: keepScroll
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

    render(false);
})();
