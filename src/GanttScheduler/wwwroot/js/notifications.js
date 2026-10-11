// 알림 목록: 하나씩 읽음, 모두 읽음. 항목을 누르면 서버가 읽음으로 바꾸고 과제 상세의 그 활동으로 보낸다(?handler=Go — 스크립트 없이도 동작)
(function () {
    'use strict';

    var url = function (handler) { return App.url('/My/Notifications?handler=' + handler); };

    var unread = +(document.getElementById('unread-count') || {}).textContent || 0;

    function setCount(n) {
        unread = n;
        var el = document.getElementById('unread-count');
        if (el) el.textContent = n > 0 ? String(n) : '';
        if (window.Collab) Collab.setUnread(n);
    }

    // App.busy 가 끝나면 단추를 다시 켜므로, 끝난 뒤에 읽지 않은 수로 맞춘다
    function syncAll() {
        var all = document.getElementById('read-all');
        if (all) all.disabled = unread === 0;
    }

    function markRead(li) {
        li.classList.remove('unread');
        var b = li.querySelector('[data-read]');
        if (b) b.remove();
        var sr = li.querySelector('.sr-only');
        if (sr) sr.remove();
    }

    var list = document.getElementById('notes');
    if (list) {
        list.addEventListener('click', function (e) {
            var btn = e.target.closest('[data-read]');
            if (!btn) return;
            App.busy(btn, async function () {
                try {
                    var r = await App.post(url('Read'), { id: +btn.dataset.read });
                    markRead(btn.closest('li'));
                    setCount(r.unread);
                } catch (err) { App.toast(err.message, 'error'); }
            }).then(syncAll);
        });
    }

    var all = document.getElementById('read-all');
    if (all) {
        all.addEventListener('click', function () {
            App.busy(all, async function () {
                try {
                    var r = await App.post(url('ReadAll'), {});
                    document.querySelectorAll('#notes > li.unread').forEach(markRead);
                    setCount(0);
                    App.toast(r.read > 0 ? r.read + '건을 읽음으로 바꿨습니다.' : '읽지 않은 알림이 없습니다.');
                } catch (err) { App.toast(err.message, 'error'); }
            }).then(syncAll);
        });
    }
})();
