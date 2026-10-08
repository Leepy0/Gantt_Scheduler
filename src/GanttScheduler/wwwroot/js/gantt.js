// 간트 렌더러 — 대시보드(과제 단위)와 과제 상세(단계·작업 단위)가 함께 쓴다.
// 호출하는 쪽이 행과 막대(날짜·세로 위치·종류)를 만들어 넘기고, 여기서는 시간축과 DOM 만 담당한다.
//
// Gantt.render(host, {
//   rows: [{ key, cls, title, sub, side(html), href, height, toggle: {expanded}, data,
//            bars:  [{ from, to, top, h, cls, label, title, fill }],
//            marks: [{ date, top, cls, title }] }],
//   today, holidays: [{date, name}], zoom: 'day'|'week'|'month', corner,
//   onRowClick(row), onToggle(row), keepScroll
// })
window.Gantt = (function () {
    'use strict';

    var D = App.d;
    var PPD = { day: 28, week: 10, month: 4 };   // 하루당 픽셀
    var esc = App.esc;

    function el(tag, cls, html) {
        var e = document.createElement(tag);
        if (cls) e.className = cls;
        if (html != null) e.innerHTML = html;
        return e;
    }

    function render(host, opts) {
        var rows = opts.rows || [];
        var zoom = PPD[opts.zoom] ? opts.zoom : 'week';
        var ppd = PPD[zoom];
        var today = D.num(opts.today);

        // 이전 스크롤 위치(재렌더 시 유지)
        var old = host.querySelector('.g-scroll');
        var keep = opts.keepScroll && old ? { left: old.scrollLeft, top: old.scrollTop, zoom: host.dataset.zoom } : null;

        // 표시 범위: 모든 막대와 오늘을 포함하고 앞뒤로 여유를 둔다
        var min = today, max = today;
        rows.forEach(function (r) {
            (r.bars || []).forEach(function (b) {
                min = Math.min(min, D.num(b.from));
                max = Math.max(max, D.num(b.to));
            });
            (r.marks || []).forEach(function (m) {
                min = Math.min(min, D.num(m.date));
                max = Math.max(max, D.num(m.date));
            });
        });
        var start = min - 7;
        start -= (D.dow(start) + 6) % 7;               // 월요일로 맞춤
        var end = max + 14;

        var leftW = parseInt(getComputedStyle(host).getPropertyValue('--g-left'), 10) || 300;
        var avail = Math.max(0, host.clientWidth - leftW);
        var days = Math.max(end - start + 1, Math.ceil(avail / ppd));
        end = start + days - 1;
        var width = days * ppd;

        function x(dayNum) { return (dayNum - start) * ppd; }

        var holidays = {};
        (opts.holidays || []).forEach(function (h) { holidays[D.num(h.date)] = h.name; });
        function isOff(n) { var w = D.dow(n); return w === 0 || w === 6 || holidays[n] != null; }

        host.style.setProperty('--g-width', width + 'px');
        host.dataset.zoom = zoom;

        var scroll = el('div', 'g-scroll');
        var grid = el('div', 'g-grid');
        scroll.appendChild(grid);

        // ── 머리글(월 / 일·주) ──
        var head = el('div', 'g-head');
        head.appendChild(el('div', 'g-corner', esc(opts.corner || '')));
        var scale = el('div', 'g-scale');
        var html = '';
        var n = start;
        while (n <= end) {
            var dt = new Date(n * 86400000);
            var y = dt.getUTCFullYear(), m = dt.getUTCMonth();
            var next = Math.round(Date.UTC(y, m + 1, 1) / 86400000);
            var w = (Math.min(next, end + 1) - n) * ppd;
            var label = w >= 64 ? y + '.' + (m < 9 ? '0' : '') + (m + 1) : (w >= 24 ? String(m + 1) : '');
            html += '<div class="g-month" style="left:' + x(n) + 'px;width:' + w + 'px">' + label + '</div>';
            n = next;
        }
        if (zoom === 'day') {
            for (n = start; n <= end; n++) {
                var cls = 'g-tick' + (n === today ? ' today' : isOff(n) ? ' off' : '');
                var tip = holidays[n] ? ' title="' + esc(holidays[n]) + '"' : '';
                html += '<div class="' + cls + '"' + tip + ' style="left:' + x(n) + 'px;width:' + ppd + 'px">' + new Date(n * 86400000).getUTCDate() + '</div>';
            }
        } else if (zoom === 'week') {
            for (n = start; n <= end; n += 7) {
                var wd = new Date(n * 86400000);
                html += '<div class="g-tick week" style="left:' + x(n) + 'px;width:' + (7 * ppd) + 'px">' + (wd.getUTCMonth() + 1) + '/' + wd.getUTCDate() + '</div>';
            }
        }
        scale.innerHTML = html;
        head.appendChild(scale);
        grid.appendChild(head);

        // ── 본문 배경(휴일 음영·월 경계) ──
        var body = el('div', 'g-body');
        var bg = el('div', 'g-bg');
        html = '';
        if (zoom !== 'month') {
            var runStart = null;
            for (n = start; n <= end + 1; n++) {
                var off = n <= end && isOff(n);
                if (off && runStart == null) runStart = n;
                if (!off && runStart != null) {
                    html += '<div class="g-band" style="left:' + x(runStart) + 'px;width:' + ((n - runStart) * ppd) + 'px"></div>';
                    runStart = null;
                }
            }
        }
        n = start;
        while (n <= end) {
            var d2 = new Date(n * 86400000);
            var nx = Math.round(Date.UTC(d2.getUTCFullYear(), d2.getUTCMonth() + 1, 1) / 86400000);
            if (nx <= end) html += '<div class="g-mline" style="left:' + x(nx) + 'px"></div>';
            n = nx;
        }
        bg.innerHTML = html;
        body.appendChild(bg);

        // 오늘 선 — 하루 칸의 가운데
        var todayX = leftW + x(today) + ppd / 2;
        var line = el('div', 'g-today');
        line.style.left = (todayX - 1) + 'px';
        body.appendChild(line);
        var todayLabel = el('div', 'g-today-label', '오늘');
        todayLabel.style.left = todayX + 'px';
        body.appendChild(todayLabel);

        // 맨 위 여백 — '오늘' 표시가 첫 행의 막대를 가리지 않도록
        body.appendChild(el('div', 'g-pad'));

        // ── 행 ──
        if (rows.length === 0) body.appendChild(el('div', 'g-empty', esc(opts.empty || '표시할 일정이 없습니다.')));

        rows.forEach(function (r) {
            var row = el('div', 'g-row ' + (r.cls || '') + (opts.onRowClick || r.href ? ' clickable' : ''));
            row.style.height = (r.height || 48) + 'px';

            var name = el(r.href ? 'a' : 'div', 'g-name');
            if (r.href) name.href = r.href;
            var nameHtml = '';
            if (r.toggle) {
                nameHtml += '<button type="button" class="g-toggle" aria-expanded="' + (r.toggle.expanded ? 'true' : 'false') +
                    '" aria-label="작업 펼치기/접기">' + App.icon('down') + '</button>';
            }
            nameHtml += '<span class="g-name-main"><span class="g-name-title">' + esc(r.title) + '</span>' +
                (r.sub ? '<span class="g-name-sub">' + esc(r.sub) + '</span>' : '') + '</span>';
            if (r.side) nameHtml += '<span class="g-name-side">' + r.side + '</span>';
            name.innerHTML = nameHtml;
            name.title = r.title + (r.sub ? ' · ' + r.sub : '');
            row.appendChild(name);

            var track = el('div', 'g-track');
            var t = '';
            (r.bars || []).forEach(function (b) {
                var from = D.num(b.from), to = D.num(b.to);
                if (to < from) to = from;
                var bw = (to - from + 1) * ppd - 1;
                var inner = '';
                if (b.fill != null) inner = '<i class="g-fill" style="width:' + Math.max(0, Math.min(100, b.fill)) + '%"></i>';
                else if (b.label && bw >= 40) inner = esc(b.label);
                t += '<div class="g-bar ' + b.cls + '" style="left:' + x(from) + 'px;width:' + bw + 'px;top:' + b.top + 'px;height:' + b.h +
                    'px;line-height:' + b.h + 'px"' + (b.title ? ' title="' + esc(b.title) + '"' : '') + '>' + inner + '</div>';
            });
            (r.marks || []).forEach(function (mk) {
                t += '<div class="g-mark ' + (mk.cls || '') + '" style="left:' + (x(D.num(mk.date)) + ppd) + 'px;top:' + mk.top + 'px"' +
                    (mk.title ? ' title="' + esc(mk.title) + '"' : '') + '></div>';
            });
            track.innerHTML = t;
            row.appendChild(track);

            row.addEventListener('click', function (e) {
                var tg = e.target.closest('.g-toggle');
                if (tg) {
                    e.preventDefault();
                    e.stopPropagation();
                    if (opts.onToggle) opts.onToggle(r);
                    return;
                }
                if (r.href) {
                    if (!e.target.closest('a')) window.location.href = r.href;
                    return;
                }
                if (opts.onRowClick) opts.onRowClick(r);
            });

            body.appendChild(row);
        });

        grid.appendChild(body);
        host.innerHTML = '';
        host.appendChild(scroll);

        function scrollToToday() {
            scroll.scrollLeft = Math.max(0, x(today) - Math.max(40, (scroll.clientWidth - leftW) * 0.3));
        }

        if (keep && keep.zoom === zoom) {
            scroll.scrollLeft = keep.left;
            scroll.scrollTop = keep.top;
        } else {
            scrollToToday();
        }

        return { scrollToToday: scrollToToday };
    }

    // 단계의 목표/실행 막대 계산 (대시보드·상세 공통)
    // lane: { planTop, planH, actTop, actH }
    function stageBars(stage, index, lane, withLabel) {
        var bars = [];
        if (stage.targetDate && stage.planFrom) {
            var pf = index > 0 && stage.planFrom < stage.targetDate ? D.add(stage.planFrom, 1) : stage.planFrom;
            bars.push({
                from: pf, to: stage.targetDate, top: lane.planTop, h: lane.planH,
                cls: 'plan' + (index % 2 ? ' alt' : ''), label: withLabel ? stage.name : null,
                title: stage.name + ' 목표 ' + stage.targetDate
            });
        }
        if (stage.actualFrom && stage.actualTo) {
            var af = index > 0 && stage.actualFrom < stage.actualTo ? D.add(stage.actualFrom, 1) : stage.actualFrom;
            if (stage.completedDate) {
                var lateDone = stage.targetDate && stage.completedDate > stage.targetDate;
                bars.push({
                    from: af, to: stage.actualTo, top: lane.actTop, h: lane.actH, cls: lateDone ? 'over' : 'done',
                    title: stage.name + ' 완료 ' + stage.completedDate + (lateDone ? ' (목표 ' + stage.targetDate + ' 초과)' : '')
                });
            } else if (stage.status === 'late' && stage.targetDate) {
                // 목표일까지는 진행, 그 이후는 지연 구간
                if (af <= stage.targetDate) {
                    bars.push({ from: af, to: stage.targetDate, top: lane.actTop, h: lane.actH, cls: 'cur', title: stage.name + ' 진행 중' });
                }
                bars.push({
                    from: D.max(af, D.add(stage.targetDate, 1)), to: stage.actualTo, top: lane.actTop, h: lane.actH, cls: 'late',
                    title: stage.name + ' 지연 ' + stage.lateDays + '일'
                });
            } else {
                bars.push({ from: af, to: stage.actualTo, top: lane.actTop, h: lane.actH, cls: 'cur', title: stage.name + ' 진행 중' });
            }
        }
        return bars;
    }

    return { render: render, stageBars: stageBars };
})();
