// 간트 렌더러 — 대시보드(과제 단위)와 과제 상세(단계·작업 단위)가 함께 쓴다.
// 호출하는 쪽이 행과 막대(날짜·세로 위치·종류)를 만들어 넘기고, 여기서는 시간축과 DOM, 드래그, 의존선만 담당한다.
//
// Gantt.render(host, {
//   rows: [{ key, cls, title, sub, side(html), href, height, toggle: {expanded}, data,
//            bars:  [{ from, to, top, h, cls, label, title, fill, drag: 'move'|'end', anchor: true }],
//            marks: [{ date, top, cls, title }] }],
//   links: [{ from: rowKey, to: rowKey, cls, fromSide, toSide }],   // 선행 행의 anchor 막대 → 후행 행의 anchor 막대.
//          fromSide/toSide: 'start'|'end' (기본 끝 → 시작 = FS. 같이 시작 SS 는 시작 → 시작, 같이 끝 FF 는 끝 → 끝)
//   today, holidays: [{date, name}], zoom: 'day'|'week'|'month', corner,
//   onRowClick(row), onToggle(row), onBarDrag(row, bar, {from, to}), keepScroll,
//   onReorder(row, target, after): 행(r.reorder = true)의 손잡이를 끌어 다른 reorder 행의 앞(after=false)·뒤(true)에 놓았을 때,
//   fit: 전체 폭(px) — 인쇄처럼 가로 스크롤 없이 모든 일정을 한 폭에 담아야 할 때. 하루 폭을 배율 값 이하로 줄여 맞춘다
// })
window.Gantt = (function () {
    'use strict';

    var D = App.d;
    var PPD = { day: 28, week: 10, month: 4 };   // 하루당 픽셀
    var PAD = 20;                                // 본문 맨 위 여백('오늘' 표시 자리)
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
        var end = max + 21;                            // 뒤쪽은 드래그로 밀 공간을 넉넉히

        var leftW = parseInt(getComputedStyle(host).getPropertyValue('--g-left'), 10) || 300;
        var avail = Math.max(0, (opts.fit || host.clientWidth) - leftW);
        if (opts.fit && avail > 0) ppd = Math.min(ppd, Math.max(0.5, avail / (end - start + 1)));
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
        var pad = el('div', 'g-pad');
        pad.style.height = PAD + 'px';
        body.appendChild(pad);

        // ── 행 ──
        if (rows.length === 0) body.appendChild(el('div', 'g-empty', esc(opts.empty || '표시할 일정이 없습니다.')));

        var suppressClick = false;     // 드래그 직후의 클릭은 행 클릭으로 치지 않는다
        var geometry = {};             // 행 key → { top, anchor: {x1, x2, y} } (의존선용)
        var top = PAD;
        var dragRow = null;            // 순서를 바꾸려고 끌고 있는 행
        var rowEls = [];
        function clearDrop() {
            rowEls.forEach(function (e) { e.classList.remove('drop-before', 'drop-after', 'reorder-src'); });
        }

        rows.forEach(function (r, ri) {
            var height = r.height || 48;
            var row = el('div', 'g-row ' + (r.cls || '') + (opts.onRowClick || r.href ? ' clickable' : ''));
            row.style.height = height + 'px';

            var name = el(r.href ? 'a' : 'div', 'g-name');
            if (r.href) name.href = r.href;
            var nameHtml = '';
            var reorder = !!(r.reorder && opts.onReorder);
            if (reorder) nameHtml += '<span class="g-grip" draggable="true" title="끌어서 순서 변경" aria-hidden="true">' + App.icon('grip') + '</span>';
            if (r.toggle) {
                nameHtml += '<button type="button" class="g-toggle" aria-expanded="' + (r.toggle.expanded ? 'true' : 'false') +
                    '" aria-label="작업 펼치기/접기">' + App.icon('down') + '</button>';
            }
            var mainHtml = '<span class="g-name-main"><span class="g-name-title">' + esc(r.title) + '</span>' +
                (r.sub ? '<span class="g-name-sub">' + esc(r.sub) + '</span>' : '') + '</span>';
            // 눌러서 여는 행(상세의 단계·작업)은 이름을 단추로 둔다 — 키보드(Tab·Enter)로도 편집 창을 열 수 있게
            nameHtml += !r.href && opts.onRowClick ? '<button type="button" class="g-name-btn">' + mainHtml + '</button>' : mainHtml;
            if (r.side) nameHtml += '<span class="g-name-side">' + r.side + '</span>';
            name.innerHTML = nameHtml;
            name.title = r.title + (r.sub ? ' · ' + r.sub : '');
            row.appendChild(name);
            rowEls.push(row);
            if (reorder) {
                var grip = name.querySelector('.g-grip');
                grip.addEventListener('dragstart', function (e) {
                    dragRow = r;
                    e.dataTransfer.effectAllowed = 'move';
                    e.dataTransfer.setData('text/plain', r.key);
                    try { e.dataTransfer.setDragImage(row, 16, 16); } catch (err) { /* 무시 */ }
                    row.classList.add('reorder-src');
                });
                grip.addEventListener('dragend', function () { dragRow = null; clearDrop(); });
                row.addEventListener('dragover', function (e) {
                    if (!dragRow || dragRow === r) return;
                    e.preventDefault();
                    var rc = row.getBoundingClientRect();
                    var after = e.clientY > rc.top + rc.height / 2;
                    row.classList.toggle('drop-before', !after);
                    row.classList.toggle('drop-after', after);
                });
                row.addEventListener('dragleave', function () { row.classList.remove('drop-before', 'drop-after'); });
                row.addEventListener('drop', function (e) {
                    if (!dragRow || dragRow === r) return;
                    e.preventDefault();
                    var rc = row.getBoundingClientRect();
                    var from = dragRow;
                    dragRow = null;
                    clearDrop();
                    opts.onReorder(from, r, e.clientY > rc.top + rc.height / 2);
                });
            }

            var track = el('div', 'g-track');
            var t = '';
            (r.bars || []).forEach(function (b, bi) {
                var from = D.num(b.from), to = D.num(b.to);
                if (to < from) to = from;
                var bw = (to - from + 1) * ppd - 1;
                var inner = '';
                if (b.fill != null) inner = '<i class="g-fill" style="width:' + Math.max(0, Math.min(100, b.fill)) + '%"></i>';
                else if (b.label && bw >= 40) inner = esc(b.label);
                var drag = '';
                if (b.drag && opts.onBarDrag) {
                    drag = ' data-drag="' + b.drag + '" data-row="' + ri + '" data-bar="' + bi + '"';
                    inner += (b.drag === 'move' ? '<i class="g-rz l"></i>' : '') + '<i class="g-rz r"></i>';
                }
                t += '<div class="g-bar ' + b.cls + (drag ? ' draggable' : '') + '"' + drag + ' style="left:' + x(from) + 'px;width:' + bw + 'px;top:' + b.top +
                    'px;height:' + b.h + 'px;line-height:' + b.h + 'px"' + (b.title ? ' title="' + esc(b.title) + '"' : '') + '>' + inner + '</div>';
                if (b.anchor) geometry[r.key] = { x1: x(from), x2: x(to) + ppd - 1, y: top + b.top + b.h / 2, rowTop: top, rowBottom: top + height };
            });
            (r.marks || []).forEach(function (mk) {
                var left = x(D.num(mk.date)) + ppd;
                t += '<div class="g-mark ' + (mk.cls || '') + '" style="left:' + left + 'px;top:' + mk.top + 'px"' +
                    (mk.title ? ' title="' + esc(mk.title) + '"' : '') + '></div>';
            });
            track.innerHTML = t;
            row.appendChild(track);

            row.addEventListener('click', function (e) {
                if (suppressClick) { suppressClick = false; return; }
                if (e.target.closest('.g-grip')) return;
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
            top += height;
        });

        // ── 의존선: 선행 막대에서 후행 막대로 꺾은선. 유형에 따라 잇는 쪽이 다르다 ──
        var paths = '';
        (opts.links || []).forEach(function (l) {
            var a = geometry[l.from], b = geometry[l.to];
            if (!a || !b) return;      // 접힌 단계의 작업은 행이 없다
            var y1 = a.y, y2 = b.y;
            var d;
            if (l.fromSide === 'start' && l.toSide !== 'end') {
                // 같이 시작(SS): 두 막대의 시작 중 앞쪽보다 조금 왼쪽으로 나가 내려온 뒤 후행 시작으로
                var left = Math.min(a.x1, b.x1) - 8;
                d = 'M' + a.x1 + ' ' + y1 + 'H' + left + 'V' + y2 + 'H' + (b.x1 - 1);
            } else if (l.toSide === 'end') {
                // 같이 끝(FF): 두 막대의 끝 중 뒤쪽보다 조금 오른쪽으로 나가 내려온 뒤 후행 끝으로(화살표는 왼쪽을 향한다)
                var right = Math.max(a.x2, b.x2) + 8;
                d = 'M' + a.x2 + ' ' + y1 + 'H' + right + 'V' + y2 + 'H' + (b.x2 + 1);
            } else {
                var x1 = a.x2, x2 = b.x1;
                if (x2 >= x1 + 14) {
                    d = 'M' + x1 + ' ' + y1 + 'H' + (x1 + 7) + 'V' + y2 + 'H' + (x2 - 1);
                } else {
                    // 후행이 선행 끝보다 앞에서 시작하면 행 사이로 돌아 들어간다
                    var mid = y2 > y1 ? a.rowBottom : a.rowTop;
                    d = 'M' + x1 + ' ' + y1 + 'H' + (x1 + 7) + 'V' + mid + 'H' + (x2 - 9) + 'V' + y2 + 'H' + (x2 - 1);
                }
            }
            var cls = l.cls || '';
            paths += '<path class="' + cls + '" data-link-type="' + (l.type || 'fs') + '" d="' + d + '" marker-end="url(#g-arrow' + (cls ? '-' + cls : '') + ')"/>';
        });
        if (paths) {
            var svg = el('div', 'g-links');
            svg.innerHTML = '<svg width="' + width + '" height="' + top + '" aria-hidden="true"><defs>' +
                ['', 'crit', 'bad'].map(function (c) {
                    return '<marker id="g-arrow' + (c ? '-' + c : '') + '" class="' + c + '" viewBox="0 0 8 8" refX="7" refY="4" markerWidth="7" markerHeight="7" orient="auto">' +
                        '<path d="M0 0L8 4L0 8z"/></marker>';
                }).join('') + '</defs>' + paths + '</svg>';
            body.appendChild(svg);
        }

        // ── 드래그 편집 (마우스·펜만 — 터치는 스크롤과 겹치므로 시트에서 고친다) ──
        if (opts.onBarDrag) {
            var drag = null;
            var tipEl = el('div', 'g-tip');
            tipEl.hidden = true;

            body.addEventListener('pointerdown', function (e) {
                if (e.button !== 0 || e.pointerType === 'touch') return;
                var barEl = e.target.closest('.g-bar.draggable');
                if (!barEl) return;
                var r = rows[+barEl.dataset.row];
                var b = r.bars[+barEl.dataset.bar];
                var mode = e.target.classList.contains('g-rz')
                    ? (e.target.classList.contains('l') ? 'start' : 'end')
                    : (b.drag === 'move' ? 'move' : null);
                if (!mode) return;
                drag = { el: barEl, row: r, bar: b, mode: mode, x: e.clientX, from: D.num(b.from), to: D.num(b.to), moved: false };
                drag.newFrom = drag.from;
                drag.newTo = drag.to;
                barEl.setPointerCapture(e.pointerId);
                barEl.classList.add('dragging');
                e.preventDefault();
            });

            body.addEventListener('pointermove', function (e) {
                if (!drag) return;
                var dx = e.clientX - drag.x;
                if (Math.abs(dx) > 3) drag.moved = true;
                var d = Math.round(dx / ppd);
                var from = drag.from, to = drag.to;
                if (drag.mode === 'move') { from += d; to += d; }
                else if (drag.mode === 'start') from = Math.min(from + d, to);
                else to = Math.max(to + d, from);
                drag.newFrom = from;
                drag.newTo = to;
                drag.el.style.left = x(from) + 'px';
                drag.el.style.width = ((to - from + 1) * ppd - 1) + 'px';
                tipEl.textContent = drag.bar.drag === 'end' ? D.md(D.str(to)) : D.md(D.str(from)) + ' ~ ' + D.md(D.str(to));
                tipEl.style.left = (e.clientX + 12) + 'px';
                tipEl.style.top = (e.clientY - 32) + 'px';
                tipEl.hidden = false;
            });

            var finish = function (commit) {
                if (!drag) return;
                var g = drag;
                drag = null;
                tipEl.hidden = true;
                g.el.classList.remove('dragging');
                if (g.moved) {
                    suppressClick = true;
                    setTimeout(function () { suppressClick = false; }, 0);
                }
                if (commit && (g.newFrom !== g.from || g.newTo !== g.to)) {
                    opts.onBarDrag(g.row, g.bar, { from: D.str(g.newFrom), to: D.str(g.newTo) });
                } else {
                    g.el.style.left = x(g.from) + 'px';
                    g.el.style.width = ((g.to - g.from + 1) * ppd - 1) + 'px';
                }
            };
            body.addEventListener('pointerup', function () { finish(true); });
            body.addEventListener('pointercancel', function () { finish(false); });
        }

        grid.appendChild(body);
        host.innerHTML = '';
        host.appendChild(scroll);
        if (tipEl) host.appendChild(tipEl);

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
            // 경계일 보정(+1)이 작업보다 늦게 시작하게 만들지 않는다 — 단계는 가장 이른 작업보다 늦게 시작하지 않는다
            if (stage.worksFrom && stage.worksFrom <= stage.targetDate && pf > stage.worksFrom) pf = stage.worksFrom;
            bars.push({
                from: pf, to: stage.targetDate, top: lane.planTop, h: lane.planH,
                cls: 'plan' + (index % 2 ? ' alt' : ''), label: withLabel ? stage.name : null,
                title: stage.name + ' 목표 ' + stage.targetDate, kind: 'plan'
            });
        }
        if (stage.actualFrom && stage.actualTo) {
            var af = index > 0 && stage.actualFrom < stage.actualTo ? D.add(stage.actualFrom, 1) : stage.actualFrom;
            if (stage.worksActualFrom && stage.worksActualFrom <= stage.actualTo && af > stage.worksActualFrom) af = stage.worksActualFrom;
            if (stage.completedDate) {
                var lateDone = stage.doneLate;      // 서버가 Rules.IsDoneLate 로 정한 값
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
