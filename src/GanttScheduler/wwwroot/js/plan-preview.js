// 저장 전 변경 미리보기 (What-if): 시트의 날짜·선택이 바뀌면 0.3초 뒤 서버(?handler=Preview)에 물어
// 함께 밀리는 작업, 단계 예상 종료 전/후·목표 초과, 저장이 막히는 이유를 시트 안에 그린다.
// 근무일·휴일·의존 밀기 계산은 모두 서버가 한다 — 화면은 받은 값만 그린다(실제 저장과 같은 계산)
window.PlanPreview = (function () {
    'use strict';

    var esc = App.esc, icon = App.icon, D = App.d;
    var DELAY = 300;

    function signed(n) { return (n > 0 ? '+' : '') + n; }
    function md(d) { return d ? D.md(d) : '미정'; }

    // 미리보기 주소: 값이 없는 인자는 뺀다
    function url(params) {
        var q = [];
        Object.keys(params).forEach(function (k) {
            var v = params[k];
            if (v == null || v === '' || v === false) return;
            q.push(encodeURIComponent(k) + '=' + encodeURIComponent(v));
        });
        return App.url('/Projects/Detail?handler=Preview&' + q.join('&'));
    }

    // 단계 한 줄: 목표일(연쇄 이동)·작업 기준 예상 종료 전/후, 목표 초과는 노랑(넘김)·빨강(목표일이 이미 지남 = 지연)
    function stageLine(s) {
        var bits = [];
        if (s.target !== s.newTarget) bits.push('목표일 ' + md(s.target) + ' → <b>' + md(s.newTarget) + '</b>');
        if (s.before !== s.after) bits.push('작업 기준 예상 종료 ' + md(s.before) + ' → <b>' + md(s.after) + '</b>');
        else if (s.after) bits.push('작업 기준 예상 종료 ' + md(s.after));
        var chip = '';
        if (s.overAfter > 0) {
            chip = App.chip({ cls: s.late ? 'alarm' : 'warn', icon: s.late ? 'alert' : 'clock', text: '목표일보다 ' + s.overAfter + '일 늦음' });
        } else if (s.overBefore > 0) {
            chip = App.chip({ cls: 'ok', icon: 'check', text: '목표일 안으로' });
        }
        return '<li data-preview-stage="' + s.id + '"><span class="preview-stage-name">' + esc(s.name) + '</span>' + chip +
            '<span class="preview-stage-text">' + bits.join(' · ') + '</span></li>';
    }

    // 결과 → HTML.
    // opts: { movedTitle: 옮겨지는 작업 표 제목, noMoves: 옮겨지는 작업이 없을 때 문구, hideNotes: 휴일 안내를 따로 그릴 때 }
    function html(r, opts) {
        opts = opts || {};
        if (!r) return '';
        var out = '';
        if (!opts.hideNotes) {
            (r.notes || []).forEach(function (n) { out += '<p class="hint preview-note">' + icon('alert') + '<span>' + esc(n) + '</span></p>'; });
        }
        (r.violations || []).forEach(function (v) {
            out += '<p class="' + (v.blocking ? 'form-error' : 'form-warn') + '" data-preview-issue="' + (v.blocking ? 'block' : 'warn') + '">' +
                icon(v.blocking ? 'alert' : 'link') + '<span>' + esc(v.text) + '</span></p>';
        });
        var blocked = (r.violations || []).some(function (v) { return v.blocking; });
        if (r.moved && r.moved.length) {
            out += '<div class="preview-block"><h3>' + esc(opts.movedTitle || '함께 옮겨지는 작업') + ' <span class="count">' + r.moved.length + '</span></h3>' +
                '<div class="table-scroll"><table class="simple-table preview-table" data-preview-moves>' +
                '<thead><tr><th>작업</th><th>지금</th><th>바뀐 뒤</th><th class="r">이동</th></tr></thead><tbody>' +
                r.moved.map(function (m) {
                    return '<tr data-work="' + m.id + '"><td><span class="preview-title">' + esc(m.title) + '</span>' +
                        '<span class="hint">' + esc(m.stage) + (m.startLate ? ' · 시작 지연' : '') + '</span></td>' +
                        '<td class="num">' + md(m.oldStart) + '~' + md(m.oldEnd) + '</td>' +
                        '<td class="num"><b>' + md(m.newStart) + '~' + md(m.newEnd) + '</b></td>' +
                        '<td class="r">' + signed(m.shift) + '근무일</td></tr>';
                }).join('') + '</tbody></table></div></div>';
        } else if (opts.noMoves && !blocked) {
            out += '<p class="hint" data-preview-none>' + esc(opts.noMoves) + '</p>';
        }
        var stages = (r.stages || []).filter(function (s) {
            return s.before !== s.after || s.target !== s.newTarget || s.overAfter > 0 || s.overBefore > 0;
        });
        if (stages.length) out += '<ul class="preview-stages" data-preview-stages>' + stages.map(stageLine).join('') + '</ul>';
        return out;
    }

    // el 에 미리보기를 붙인다. opts: html 옵션 + url(): 지금 입력으로 만든 주소(null 이면 지운다), onResult(preview|null, err)
    // update() 는 0.3초 디바운스, now() 는 바로. 빠르게 여러 번 바뀌면 마지막 요청의 결과만 그린다
    function attach(el, opts) {
        var seq = 0, timer = null, last = null;
        el.classList.add('preview');
        function run(delay) {
            clearTimeout(timer);
            var address = opts.url();
            if (!address) {
                seq++;
                last = null;
                el.innerHTML = '';
                el.removeAttribute('aria-busy');
                if (opts.onResult) opts.onResult(null);
                return;
            }
            // 새 결과를 받기 전까지 지난 결과를 흐리게 보인다 (바꾼 입력과 맞지 않는 값을 그대로 믿지 않게)
            el.setAttribute('aria-busy', 'true');
            timer = setTimeout(function () {
                var my = ++seq;
                App.get(address).then(function (r) {
                    if (my !== seq || !el.isConnected) return;
                    last = r.preview;
                    el.removeAttribute('aria-busy');
                    el.innerHTML = html(last, opts);
                    if (opts.onResult) opts.onResult(last);
                }, function (err) {
                    if (my !== seq || !el.isConnected) return;
                    last = null;
                    el.removeAttribute('aria-busy');
                    el.innerHTML = '<p class="hint preview-note">' + icon('alert') + '<span>' + esc(err.message) + '</span></p>';
                    if (opts.onResult) opts.onResult(null, err);
                });
            }, delay);
        }
        return {
            update: function () { run(DELAY); },
            now: function () { run(0); },
            result: function () { return last; },
            stop: function () { clearTimeout(timer); seq++; }
        };
    }

    return { attach: attach, html: html, url: url };
})();
