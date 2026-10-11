// 빠른 갱신 조각: 진행률 칩(0/25/50/75/100 + 직접)·오늘 시작/오늘 완료·진행 내용 한 줄.
// 내 할 일(my.js)의 작업 행과 상세 작업 시트 맨 위(detail.js)가 같은 조각을 쓴다. 저장 규칙(종료가 있으면 100%,
// 진행률만 올리면 오늘 시작)은 서버(Rules.WorkActuals)가 정한다 — 여기서는 입력만 모은다
(function () {
    'use strict';

    var STEPS = [0, 25, 50, 75, 100];
    var esc = App.esc;

    // st: {progress, actualStart, actualEnd}
    // opts: { custom: 직접 입력칸 HTML(없으면 숫자 칸), noteMax: 진행 내용 최대 글자, save: '남기기' 단추를 둘지, label: 묶음 이름 }
    function html(st, opts) {
        opts = opts || {};
        var custom = opts.custom != null ? opts.custom
            : '<input type="number" class="quick-custom" data-quick-custom min="0" max="100" step="5" inputmode="numeric" value="' + (st.progress || 0) + '" aria-label="진행률 직접 입력(5 단위)" />';
        return '<div class="quick" data-quick role="group" aria-label="' + esc(opts.label || '빠른 갱신') + '">' +
            '<div class="quick-chips" role="group" aria-label="진행률">' +
            STEPS.map(function (v) {
                return '<button type="button" class="quick-chip" data-quick-progress="' + v + '" aria-pressed="' + (v === st.progress ? 'true' : 'false') + '">' + v + '%</button>';
            }).join('') + custom + '</div>' +
            '<div class="quick-row">' +
            '<button type="button" class="btn small" data-quick-start' + (st.actualStart ? ' hidden' : '') + '>' + App.icon('play') + '오늘 시작</button>' +
            '<button type="button" class="btn small" data-quick-end' + (st.actualEnd ? ' hidden' : '') + '>' + App.icon('check') + '오늘 완료</button>' +
            '<input type="text" class="quick-note" data-quick-note maxlength="' + (opts.noteMax || 2000) + '" placeholder="진행 내용 한 줄 (선택)" aria-label="진행 내용" />' +
            (opts.save ? '<button type="button" class="btn small" data-quick-save>' + App.icon('comment') + '남기기</button>' : '') +
            '</div></div>';
    }

    // 지금 값으로 칩 눌림·단추 보이기를 맞춘다 (종료가 있으면 진행률은 100% 로 고정)
    function update(root, st) {
        var done = !!st.actualEnd;
        root.querySelectorAll('[data-quick-progress]').forEach(function (b) {
            b.setAttribute('aria-pressed', +b.dataset.quickProgress === (done ? 100 : st.progress) ? 'true' : 'false');
            b.disabled = done;
        });
        var custom = root.querySelector('[data-quick-custom]');
        if (custom) { custom.value = done ? 100 : st.progress; custom.disabled = done; }
        var start = root.querySelector('[data-quick-start]');
        if (start) start.hidden = !!st.actualStart;
        var end = root.querySelector('[data-quick-end]');
        if (end) end.hidden = done;
    }

    // h: { progress(v), start(), end(), note(text) } — note 는 '남기기' 또는 Enter 로 부른다
    function bind(root, h) {
        root.addEventListener('click', function (e) {
            var chip = e.target.closest('[data-quick-progress]');
            if (chip && h.progress) { h.progress(+chip.dataset.quickProgress); return; }
            if (e.target.closest('[data-quick-start]') && h.start) { h.start(); return; }
            if (e.target.closest('[data-quick-end]') && h.end) { h.end(); return; }
            if (e.target.closest('[data-quick-save]') && h.note) h.note(note(root));
        });
        var custom = root.querySelector('[data-quick-custom]');
        if (custom && h.progress) {
            custom.addEventListener('change', function () {
                var v = parseInt(custom.value, 10);
                if (isNaN(v)) return;
                h.progress(v);     // 범위·5 단위는 서버가 확인해 알린다(RuleException → 행에 표시)
            });
        }
        var box = root.querySelector('[data-quick-note]');
        if (box && h.note) {
            box.addEventListener('keydown', function (e) {
                if (e.key === 'Enter' && !e.isComposing) { e.preventDefault(); h.note(note(root)); }
            });
        }
    }

    function note(root) {
        var box = root.querySelector('[data-quick-note]');
        return box ? box.value.trim() : '';
    }

    function clearNote(root) {
        var box = root.querySelector('[data-quick-note]');
        if (box) { box.value = ''; box.defaultValue = ''; }
    }

    window.Quick = { html: html, update: update, bind: bind, note: note, clearNote: clearNote, steps: STEPS };
})();
