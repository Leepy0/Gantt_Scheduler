// 관리 > 휴지통: 복원·영구 삭제. 끝나면 서버가 그린 목록을 다시 읽는다(목록·기록이 함께 바뀌므로)
(function () {
    'use strict';

    var url = function (handler) { return App.url('/Admin/Trash?handler=' + handler); };
    App.localizeTimes(document.getElementById('trash'));
    App.localizeTimes(document.getElementById('purge-log'));

    async function restore(btn) {
        var ok = await App.confirm({
            title: '과제 복원',
            message: '"' + btn.dataset.title + '" 과제를 되살립니다. 단계·작업·첨부·활동 이력이 그대로 돌아옵니다.',
            verb: '복원'
        });
        if (!ok) return;
        await App.busy(btn, async function () {
            try {
                await App.post(url('Restore'), { id: +btn.dataset.restore, version: +btn.dataset.version });
                App.toast('과제를 복원했습니다.');
                window.location.reload();
            } catch (err) { App.toast(err.message, 'error'); }
        });
    }

    async function purge(btn) {
        var ok = await App.confirm({
            title: '영구 삭제',
            message: '"' + btn.dataset.title + '" 과제와 단계·작업·첨부 파일·활동 이력을 영구히 지웁니다. 되돌릴 수 없습니다.',
            verb: '영구 삭제'
        });
        if (!ok) return;
        await App.busy(btn, async function () {
            try {
                await App.post(url('Purge'), { id: +btn.dataset.purge, version: +btn.dataset.version });
                App.toast('과제를 영구 삭제했습니다.');
                window.location.reload();
            } catch (err) { App.toast(err.message, 'error'); }
        });
    }

    document.addEventListener('click', function (e) {
        var t;
        if ((t = e.target.closest('[data-restore]'))) restore(t);
        else if ((t = e.target.closest('[data-purge]'))) purge(t);
    });
})();
