// 협업 공용(모든 화면): 헤더 알림 종의 읽지 않은 수, 관심 과제 별 단추, 알릴 사람(멘션) 고르기
(function () {
    'use strict';

    var Collab = {};
    var esc = App.esc;

    // ── 알림 종 ─────────────────────────────────────────────────────
    // 읽지 않은 수는 화면을 그릴 때 세지 않고(모든 요청에 알림 조회가 붙지 않게) 가벼운 요청(/api/unread)으로 따로 묻는다.
    // 처음 한 번, 그 뒤로는 1분 버전 확인(App.onPoll — 보이는 탭만)에 얹는다. 화면을 옮길 때 깜빡이지 않게 마지막 값을 이 탭에 기억한다
    var bell = document.getElementById('bell');
    var bellCount = document.getElementById('bell-count');

    function showUnread(n) {
        if (!bell) return;
        n = Math.max(0, n || 0);
        bellCount.textContent = n > 99 ? '99+' : String(n);
        bellCount.hidden = n === 0;
        bell.classList.toggle('on', n > 0);
        var label = n > 0 ? '알림 — 읽지 않은 ' + n + '건' : '알림';
        bell.setAttribute('aria-label', label);
        bell.title = label;
        try { sessionStorage.setItem('gs.unread', String(n)); } catch (e) { /* 저장소를 못 쓰면 다음에 다시 묻는다 */ }
    }

    Collab.setUnread = showUnread;
    Collab.refreshUnread = function () {
        if (!bell) return Promise.resolve();
        return App.poll(App.url('/api/unread')).then(function (r) { showUnread(r.unread); }, function () { /* 다음 확인 때 다시 */ });
    };

    if (bell) {
        try { var cached = sessionStorage.getItem('gs.unread'); if (cached != null) showUnread(+cached); } catch (e) { /* 무시 */ }
        if (!document.hidden) Collab.refreshUnread();
        App.onPoll(Collab.refreshUnread);
    }

    // ── 관심 과제 별 ────────────────────────────────────────────────
    // [data-watch=과제 Id] 단추(상세 머리·과제 목록·내 할 일). aria-pressed 가 지금 상태다. 같은 과제의 다른 별도 함께 바꾼다
    function setWatch(btn, on) {
        var label = on ? '관심 과제에서 빼기' : '관심 과제로 두기';
        btn.setAttribute('aria-pressed', on ? 'true' : 'false');
        btn.setAttribute('aria-label', label + (btn.dataset.title ? ' — ' + btn.dataset.title : ''));
        btn.title = label;
    }

    document.addEventListener('click', function (e) {
        var btn = e.target.closest && e.target.closest('[data-watch]');
        if (!btn) return;
        // 목록 행(링크) 옆에 있으므로 행으로 넘어가지 않게 막는다
        e.preventDefault();
        e.stopPropagation();
        var on = btn.getAttribute('aria-pressed') !== 'true';
        App.busy(btn, async function () {
            try {
                var r = await App.post(App.url('/Projects/Detail?handler=Watch'), { projectId: +btn.dataset.watch, on: on });
                document.querySelectorAll('[data-watch="' + btn.dataset.watch + '"]').forEach(function (b) { setWatch(b, r.watched); });
                App.toast(r.watched ? '관심 과제로 두었습니다. 새 소식과 알림을 받습니다.' : '관심 과제에서 뺐습니다.');
            } catch (err) { App.toast(err.message, 'error'); }
        });
    });

    // ── 알릴 사람(멘션) 고르기 ──────────────────────────────────────
    // box 에 고른 사람 칩과 이름 입력칸(목록 datalist)을 그린다. 이름에 공백이 있을 수 있어 본문을 파싱하지 않고 목록에서 고른 사람만 받는다.
    // 본문(textarea)에서 '@' 를 치면 이름 칸으로 옮겨 고르게 하고, 고르면 본문의 그 '@' 자리에 '@이름 ' 을 넣는다 —
    // 본문 글자는 읽는 사람을 위한 표시일 뿐이고, 알림은 칩(고른 사람 Id)이 정한다.
    // people: [{id, name}], initial: 처음 고른 사람 [{id, name}]. 돌려주는 값: { ids(), reset() }
    var listSeq = 0;
    var HINT = '본문에서 @ 를 치거나 이 칸에 이름을 넣고 목록에서 고르면 그 사람에게 알림이 갑니다.';

    Collab.mentions = function (box, textarea, people, initial) {
        people = people || [];
        var picked = (initial || []).map(function (m) { return { id: m.id, name: m.name }; });
        var listId = 'dl-people-' + (++listSeq);
        var at = -1;       // 본문에서 '@' 를 친 자리 (이름을 고르면 거기에 '@이름 ' 을 넣는다)
        box.classList.add('mention-box');
        box.innerHTML =
            '<div class="mention-row"><span class="mention-label">' + App.icon('at') + '알릴 사람</span><span class="mention-chips" data-chips></span></div>' +
            '<div class="mention-add"><input data-mention-input list="' + listId + '" autocomplete="off" maxlength="50" placeholder="이름을 넣고 목록에서 고르기" aria-label="알릴 사람 이름" />' +
            '<button type="button" class="btn small" data-mention-add>' + App.icon('plus') + '추가</button></div>' +
            '<datalist id="' + listId + '">' + people.map(function (p) { return '<option value="' + esc(p.name) + '"></option>'; }).join('') + '</datalist>' +
            '<span class="hint" data-mention-hint>' + HINT + '</span>';
        var input = box.querySelector('[data-mention-input]');
        var chips = box.querySelector('[data-chips]');
        var hint = box.querySelector('[data-mention-hint]');

        function render() {
            chips.innerHTML = picked.length ? picked.map(function (p) {
                return '<span class="chip accent mention-chip">@' + esc(p.name) +
                    '<button type="button" class="chip-x" data-unpick="' + p.id + '" aria-label="' + esc(p.name) + ' 빼기" title="빼기">' + App.icon('x') + '</button></span>';
            }).join('') : '<span class="muted">없음</span>';
        }

        function say(text, bad) {
            hint.textContent = text;
            hint.classList.toggle('error', !!bad);
        }

        function backToText(name) {
            if (!textarea) return;
            if (at >= 0 && textarea.value.charAt(at) === '@') {
                var tail = textarea.value.slice(at + 1);
                var label = name ? '@' + name + ' ' : '@';
                textarea.value = textarea.value.slice(0, at) + label + tail;
                textarea.dispatchEvent(new Event('input', { bubbles: true }));
                var pos = at + label.length;
                textarea.focus();
                try { textarea.setSelectionRange(pos, pos); } catch (e) { /* 무시 */ }
            }
            at = -1;
        }

        function add() {
            var name = input.value.trim();
            if (!name) return false;
            var p = people.filter(function (x) { return x.name === name; })[0];
            if (!p) { say('"' + name + '" 은(는) 목록에 없습니다. 목록에 있는 사람만 고를 수 있습니다.', true); return false; }
            if (!picked.some(function (x) { return x.id === p.id; })) picked.push({ id: p.id, name: p.name });
            input.value = '';
            say(HINT, false);
            render();
            backToText(p.name);
            return true;
        }

        input.addEventListener('keydown', function (e) {
            if (e.key === 'Enter') { e.preventDefault(); add(); }
            else if (e.key === 'Escape' && at >= 0) { e.preventDefault(); e.stopPropagation(); backToText(null); }
        });
        // 목록(datalist)에서 고르면 바로 더한다 — 손으로 치는 중에는(이름이 다른 이름의 앞부분일 수 있어) 기다린다
        input.addEventListener('input', function (e) {
            if (e.inputType === 'insertReplacementText' || e.inputType == null) {
                if (people.some(function (x) { return x.name === input.value.trim(); })) add();
            }
        });
        box.querySelector('[data-mention-add]').addEventListener('click', add);
        chips.addEventListener('click', function (e) {
            var x = e.target.closest('[data-unpick]');
            if (!x) return;
            var id = +x.dataset.unpick;
            picked = picked.filter(function (p) { return p.id !== id; });
            render();
            input.focus();
        });
        if (textarea) {
            textarea.addEventListener('input', function (e) {
                if (e.inputType && e.inputType.indexOf('insert') !== 0) return;
                var pos = textarea.selectionStart;
                var v = textarea.value;
                // 단어 처음의 '@' 만 (메일 주소 a@b 는 건너뛴다)
                if (pos > 0 && v.charAt(pos - 1) === '@' && (pos === 1 || /\s/.test(v.charAt(pos - 2)))) {
                    at = pos - 1;
                    say('알릴 사람의 이름을 넣고 목록에서 고르세요. Esc 로 돌아갑니다.', false);
                    input.focus();
                }
            });
        }
        render();
        return {
            ids: function () { return picked.map(function (p) { return p.id; }); },
            reset: function () { picked = []; input.value = ''; at = -1; say(HINT, false); render(); }
        };
    };

    window.Collab = Collab;
})();
