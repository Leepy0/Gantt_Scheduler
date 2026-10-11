// 내 할 일: 작업 행에서 바로 진행률·실적·진행 내용 갱신.
// 저장하면 서버(UpdateWorkProgressAsync)가 같은 규칙으로 저장하고 그 행을 다시 나눠(지연·이번 주·이후) 돌려준다 — 화면은 그 행만 바꾸거나 옮긴다.
// 묶음·칩·지연 판정은 서버 값만 쓴다
(function () {
    'use strict';

    var data = App.data();
    var esc = App.esc;
    var md = function (s) { return s ? s.slice(5) : ''; };

    // 행 모양 — My/Index.cshtml 의 WorkRows 와 같은 마크업
    function rowHtml(w) {
        return '<a class="row-link" href="' + App.url('/Projects/Detail?id=' + w.projectId) + '#w' + w.workId + '">' +
            '<span class="row-main">' +
            '<span class="row-title">' + esc(w.title) + '</span>' +
            '<span class="row-sub">' + esc(w.project) + ' · ' + esc(w.stage) + ' · ' + esc(w.role) + ' · 목표 ' + md(w.planStart) + '~' + md(w.planEnd) + '</span>' +
            '<span class="work-progress"><span class="progress" aria-hidden="true"><i style="width:' + (+w.progress || 0) + '%"></i></span><span class="num">' + (+w.progress || 0) + '%</span></span>' +
            '</span>' + App.chip(w.chip) + '</a>';
    }

    function state(w) { return { progress: w.progress, actualStart: w.actualStart, actualEnd: w.actualEnd }; }

    function workOf(li) {
        try { return JSON.parse(li.dataset.work); } catch (e) { return null; }
    }

    // 묶음마다 건수·빈 안내·숨김을 다시 맞춘다
    function recount() {
        var total = 0;
        document.querySelectorAll('[data-my-group]').forEach(function (sec) {
            var n = sec.querySelectorAll('[data-my-work]').length;
            total += n;
            var count = sec.querySelector('[data-my-count]');
            if (count) count.textContent = n;
            var empty = sec.querySelector('[data-my-empty]');
            if (empty) empty.hidden = n > 0;
            else sec.hidden = n === 0;
        });
        var t = document.getElementById('my-total');
        if (t) t.textContent = total;
    }

    // 묶음 안 순서: 목표 종료 → 작업명 (서버 Reporting.My 와 같은 순서로 끼워 넣는다)
    function place(li, w) {
        var list = document.querySelector('[data-my-group="' + w.group + '"] [data-my-list]');
        if (!list) return;
        var before = null;
        list.querySelectorAll('[data-my-work]').forEach(function (other) {
            if (before || other === li) return;
            var o = workOf(other);
            if (o && (o.planEnd > w.planEnd || (o.planEnd === w.planEnd && o.title > w.title))) before = other;
        });
        list.insertBefore(li, before);
    }

    function setError(li, message) {
        var box = li.querySelector('[data-my-error]');
        if (!message) { if (box) box.remove(); return; }
        if (!box) {
            box = document.createElement('p');
            box.className = 'form-error';
            box.setAttribute('data-my-error', '');
            box.setAttribute('role', 'alert');
            li.appendChild(box);
        }
        box.innerHTML = App.icon('alert') + '<span>' + esc(message) + '</span>';
    }

    // 행 하나를 새 값으로 다시 그린다 (진행 내용 입력은 남긴다 — 실패했을 때 다시 보낼 수 있게)
    function render(li, w, keepNote) {
        var note = keepNote ? Quick.note(li) : '';
        li.dataset.work = JSON.stringify(w);
        li.innerHTML = rowHtml(w) + Quick.html(state(w), { save: true, noteMax: data.noteMax, label: w.title + ' 빠른 갱신' });
        if (note) li.querySelector('[data-quick-note]').value = note;
        bind(li);
    }

    async function save(li, change) {
        var w = workOf(li);
        if (!w || li.getAttribute('aria-busy') === 'true') return;
        var payload = {
            workId: w.workId, version: w.version, progress: w.progress,
            actualStart: null, actualEnd: null, note: Quick.note(li) || null, clientToday: App.localToday()
        };
        Object.keys(change).forEach(function (k) { payload[k] = change[k]; });
        li.setAttribute('aria-busy', 'true');
        li.querySelectorAll('[data-quick] button, [data-quick] input').forEach(function (el) { el.disabled = true; });
        try {
            var r = await App.post(App.url('/My?handler=Progress'), payload);
            if (r.today) App.setToday(r.today);
            setError(li, null);
            if (!r.work) {
                // 내 할 일에서 빠짐(완료 등) — 잠깐 알리고 행을 뺀다
                App.toast('"' + w.title + '" 작업을 완료했습니다.');
                li.remove();
                recount();
                return;
            }
            var moved = r.work.group !== w.group;
            render(li, r.work, false);
            if (moved) place(li, r.work);
            li.classList.remove('saved');
            void li.offsetWidth;       // 저장 표시(잠깐 색)를 다시 보이게
            li.classList.add('saved');
            recount();
            App.toast(moved ? '저장했습니다. 묶음을 옮겼습니다.' : '저장했습니다.');
        } catch (err) {
            if (err.status === 409 && err.data && err.data.work) {
                // 다른 사람이 먼저 고침 — 최신 값으로 다시 그리고(입력한 진행 내용은 남긴다) 다시 누르게 한다
                render(li, err.data.work, true);
                if (err.data.work.group !== w.group) place(li, err.data.work);
                recount();
            } else if (err.status === 409 && err.data && err.data.conflict) {
                // 그 사이 다른 사람이 완료했거나 담당을 바꿔 내 할 일에서 빠짐 — 옛 값으로 다시 그리면 누를 때마다 409 만 나므로 행을 뺀다
                App.toast('"' + w.title + '" 작업은 다른 사용자가 먼저 바꿔 내 할 일에서 빠졌습니다.');
                li.remove();
                recount();
                return;
            } else {
                render(li, w, true);
            }
            setError(li, err.message);
        } finally {
            li.removeAttribute('aria-busy');
        }
    }

    function bind(li) {
        var q = li.querySelector('[data-quick]');
        if (!q) return;
        Quick.bind(q, {
            progress: function (v) { save(li, { progress: v }); },
            // '오늘'은 접속한 PC 의 날짜(App.userToday — 서버도 같은 허용 범위로 받는다)
            start: function () { save(li, { actualStart: App.userToday() }); },
            end: function () {
                var w = workOf(li);
                save(li, { actualStart: (w && w.actualStart) || App.userToday(), actualEnd: App.userToday(), progress: 100 });
            },
            note: function (text) {
                if (!text) { setError(li, '진행 내용을 입력해 주세요.'); return; }
                save(li, {});
            }
        });
    }

    document.querySelectorAll('[data-my-work]').forEach(function (li) {
        var w = workOf(li);
        if (!w) return;
        li.insertAdjacentHTML('beforeend', Quick.html(state(w), { save: true, noteMax: data.noteMax, label: w.title + ' 빠른 갱신' }));
        bind(li);
    });
})();
