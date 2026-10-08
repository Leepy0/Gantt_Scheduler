// 공통 유틸: 서버 호출, 날짜, 토스트, 시트(대화상자), 확인창
(function () {
    'use strict';

    var base = (window.APP_BASE || '/').replace(/\/$/, '');
    var App = {};

    App.url = function (path) { return base + path; };

    App.esc = function (s) {
        return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    };

    App.icon = function (name) {
        return '<svg class="ic" aria-hidden="true"><use href="#i-' + name + '"/></svg>';
    };

    App.data = function () {
        var el = document.getElementById('page-data');
        return el ? JSON.parse(el.textContent) : {};
    };

    // ── 날짜: 'yyyy-MM-dd' 문자열을 기준으로 다룬다 ──────────────────
    var DAY = 86400000;
    App.d = {
        // 문자열 → 일 번호(UTC 기준 epoch day)
        num: function (s) {
            var p = s.split('-');
            return Math.round(Date.UTC(+p[0], +p[1] - 1, +p[2]) / DAY);
        },
        str: function (n) {
            return new Date(n * DAY).toISOString().slice(0, 10);
        },
        add: function (s, days) { return App.d.str(App.d.num(s) + days); },
        diff: function (a, b) { return App.d.num(a) - App.d.num(b); },
        md: function (s) { return s ? s.slice(5) : ''; },
        // 0=일 … 6=토
        dow: function (n) { return new Date(n * DAY).getUTCDay(); },
        max: function (a, b) { return a > b ? a : b; },
        min: function (a, b) { return a < b ? a : b; }
    };

    // 서버 시각(UTC ISO) → 'MM-dd HH:mm' (브라우저 현지 시각)
    App.time = function (iso) {
        var t = new Date(iso);
        function z(v) { return (v < 10 ? '0' : '') + v; }
        return z(t.getMonth() + 1) + '-' + z(t.getDate()) + ' ' + z(t.getHours()) + ':' + z(t.getMinutes());
    };

    // ── 서버 호출 ────────────────────────────────────────────────────
    function token() {
        var el = document.querySelector('input[name="__RequestVerificationToken"]');
        return el ? el.value : '';
    }

    function fail(message, field) {
        var e = new Error(message);
        e.field = field || null;
        return e;
    }

    async function send(url, options) {
        var res;
        try {
            res = await fetch(url, options);
        } catch (err) {
            throw fail('서버에 연결하지 못했습니다. 네트워크를 확인한 뒤 다시 시도해 주세요.');
        }
        var body = null;
        try { body = await res.json(); } catch (err) { /* JSON 이 아닌 응답 */ }
        if (!res.ok || !body || body.ok === false) {
            if (body && body.error) throw fail(body.error, body.field);
            throw fail('요청을 처리하지 못했습니다(' + res.status + '). 잠시 후 다시 시도해 주세요.');
        }
        return body;
    }

    App.get = function (url) {
        return send(url, { headers: { 'Accept': 'application/json' } });
    };

    App.post = function (url, data) {
        return send(url, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json', 'Accept': 'application/json', 'X-CSRF-TOKEN': token() },
            body: JSON.stringify(data || {})
        });
    };

    // ── 토스트 ───────────────────────────────────────────────────────
    App.toast = function (message, type) {
        var root = document.getElementById('toasts');
        if (!root) return;
        var el = document.createElement('div');
        el.className = 'toast' + (type === 'error' ? ' error' : '');
        el.innerHTML = App.icon(type === 'error' ? 'alert' : 'check') + '<span>' + App.esc(message) + '</span>';
        root.appendChild(el);
        setTimeout(function () { el.remove(); }, type === 'error' ? 6000 : 3000);
    };

    // ── 시트: 데스크톱은 가운데 대화상자, 모바일은 아래에서 올라오는 시트 ──
    // opts: { title, html, primary, danger: {label, onClick}, onSubmit(form, sheet), onOpen(form, sheet) }
    App.sheet = function (opts) {
        var dlg = document.createElement('dialog');
        dlg.className = 'sheet';
        dlg.innerHTML =
            '<form class="sheet-form" novalidate>' +
            '<div class="sheet-head"><h2>' + App.esc(opts.title) + '</h2>' +
            '<button type="button" class="btn icon ghost" data-close aria-label="닫기">' + App.icon('x') + '</button></div>' +
            '<div class="sheet-body"><p class="form-error" role="alert" hidden></p>' + (opts.html || '') + '</div>' +
            '<div class="sheet-foot">' +
            (opts.danger ? '<button type="button" class="btn danger" data-danger>' + App.esc(opts.danger.label) + '</button>' : '') +
            '<span class="spacer"></span>' +
            '<button type="button" class="btn" data-close>' + (opts.primary ? '취소' : '닫기') + '</button>' +
            (opts.primary ? '<button type="submit" class="btn primary">' + App.esc(opts.primary) + '</button>' : '') +
            '</div></form>';
        document.body.appendChild(dlg);

        var form = dlg.querySelector('form');
        var errorEl = dlg.querySelector('.form-error');
        var submitBtn = dlg.querySelector('button[type=submit]');
        var busy = false;

        var sheet = {
            el: dlg,
            form: form,
            close: function () { dlg.close(); },
            setError: function (message, field) {
                form.querySelectorAll('.field.invalid').forEach(function (f) { f.classList.remove('invalid'); });
                if (!message) { errorEl.hidden = true; return; }
                errorEl.innerHTML = App.icon('alert') + '<span>' + App.esc(message) + '</span>';
                errorEl.hidden = false;
                var target = field ? form.querySelector('[data-field="' + field + '"]') : null;
                if (target) {
                    target.classList.add('invalid');
                    var input = target.querySelector('input, select, textarea');
                    if (input) input.focus();
                } else {
                    errorEl.scrollIntoView({ block: 'nearest' });
                }
            }
        };

        dlg.addEventListener('click', function (e) {
            if (e.target === dlg || e.target.closest('[data-close]')) dlg.close();
        });
        dlg.addEventListener('close', function () { dlg.remove(); });

        if (opts.danger) {
            dlg.querySelector('[data-danger]').addEventListener('click', function () { opts.danger.onClick(sheet); });
        }

        form.addEventListener('submit', async function (e) {
            e.preventDefault();
            if (busy || !opts.onSubmit) return;
            busy = true;
            sheet.setError(null);
            var label = submitBtn ? submitBtn.textContent : '';
            if (submitBtn) { submitBtn.disabled = true; submitBtn.textContent = '저장 중…'; }
            try {
                var done = await opts.onSubmit(form, sheet);
                if (done !== false) dlg.close();
            } catch (err) {
                sheet.setError(err.message, err.field);
            } finally {
                busy = false;
                if (submitBtn) { submitBtn.disabled = false; submitBtn.textContent = label; }
            }
        });

        dlg.showModal();
        if (opts.onOpen) opts.onOpen(form, sheet);
        return sheet;
    };

    // 되돌릴 수 없는 동작 확인 — 기본 포커스는 '취소', 실행 버튼은 동사형
    App.confirm = function (opts) {
        return new Promise(function (resolve) {
            var dlg = document.createElement('dialog');
            dlg.className = 'sheet';
            dlg.innerHTML =
                '<div class="sheet-head"><h2>' + App.esc(opts.title) + '</h2></div>' +
                '<div class="sheet-body"><p>' + App.esc(opts.message) + '</p></div>' +
                '<div class="sheet-foot"><span class="spacer"></span>' +
                '<button type="button" class="btn" data-no autofocus>취소</button>' +
                '<button type="button" class="btn danger solid" data-yes>' + App.esc(opts.verb) + '</button></div>';
            document.body.appendChild(dlg);
            var result = false;
            dlg.querySelector('[data-yes]').addEventListener('click', function () { result = true; dlg.close(); });
            dlg.querySelector('[data-no]').addEventListener('click', function () { dlg.close(); });
            dlg.addEventListener('click', function (e) { if (e.target === dlg) dlg.close(); });
            dlg.addEventListener('close', function () { dlg.remove(); resolve(result); });
            dlg.showModal();
            dlg.querySelector('[data-no]').focus();
        });
    };

    // 사용자 설정(보기 방식 등) 저장 — 저장소를 못 쓰는 환경에서도 화면은 동작해야 한다
    App.pref = {
        get: function (key, fallback) {
            try { var v = localStorage.getItem('gs.' + key); return v == null ? fallback : v; } catch (e) { return fallback; }
        },
        set: function (key, value) {
            try { localStorage.setItem('gs.' + key, value); } catch (e) { /* 무시 */ }
        }
    };

    App.isMobile = function () { return window.matchMedia('(max-width: 767px)').matches; };

    window.App = App;
})();
