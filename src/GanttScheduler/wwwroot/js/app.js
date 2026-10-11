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
            // 503: 서버가 새 버전으로 바뀌는 중 (IIS 가 점검 안내 화면을 대신 내보낸다)
            var err = body && body.error
                ? fail(body.error, body.field)
                : res.status === 503
                    ? fail('서버를 업데이트하는 중입니다. 잠시 후 다시 시도해 주세요. 입력한 내용은 그대로 있습니다.')
                    : fail('요청을 처리하지 못했습니다(' + res.status + '). 잠시 후 다시 시도해 주세요.');
            err.status = res.status;
            err.data = body;          // 409(동시 수정) 처럼 화면이 후속 처리를 해야 하는 응답에 쓴다
            throw err;
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

    // 파일 올리기 (multipart) — Content-Type 은 브라우저가 경계 문자열과 함께 붙인다
    App.upload = function (url, formData) {
        return send(url, {
            method: 'POST',
            headers: { 'Accept': 'application/json', 'X-CSRF-TOKEN': token() },
            body: formData
        });
    };

    App.size = function (bytes) {
        if (bytes >= 1048576) return (bytes / 1048576).toFixed(1) + ' MB';
        if (bytes >= 1024) return Math.round(bytes / 1024) + ' KB';
        return bytes + ' B';
    };

    // ── 토스트 ───────────────────────────────────────────────────────
    App.toast = function (message, type) {
        var root = document.getElementById('toasts');
        if (!root) return;
        var el = document.createElement('div');
        el.className = 'toast' + (type === 'error' ? ' error' : '');
        el.innerHTML = App.icon(type === 'error' ? 'alert' : 'check') + '<span>' + App.esc(message) + '</span>';
        root.appendChild(el);
        // 연달아 떠도 화면을 가리지 않게 최근 2개만 둔다
        while (root.children.length > 2) root.firstChild.remove();
        setTimeout(function () { el.remove(); }, type === 'error' ? 6000 : 3000);
    };

    // ── 시트: 데스크톱은 가운데 대화상자, 모바일은 아래에서 올라오는 시트 ──
    // opts: { title, html, primary, danger: {label, onClick}, onSubmit(form, sheet), onOpen(form, sheet) }
    App.sheet = function (opts) {
        var dlg = document.createElement('dialog');
        dlg.className = 'sheet' + (opts.wide ? ' wide' : '');
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
        var saved = false;
        dlg.addEventListener('close', function () {
            dlg.remove();
            if (opts.onClose) opts.onClose(saved);   // saved: 저장하고 닫혔는지
        });

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
                if (done !== false) { saved = true; dlg.close(); }
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

    // 담당자 이름 입력: 자동완성 목록에 없는 이름이면 새 담당자로 등록된다고 알린다.
    // 막지는 않는다(IP 없는 담당자도 설계상 허용) — 오타로 사람이 하나 더 생기는 것만 눈에 띄게 한다.
    // keep: 지금 담당인 이름(사용 중지돼 목록에 없어도 새 사람이 아니다)
    var hintSeq = 0;
    App.nameHint = function (input, names, keep) {
        if (!input) return;
        var known = {};
        (names || []).forEach(function (n) { known[n] = true; });
        if (keep) known[keep] = true;
        var hint = document.createElement('span');
        hint.className = 'hint';
        hint.id = 'name-hint-' + (++hintSeq);
        hint.setAttribute('data-name-hint', '');
        hint.textContent = '목록에 없는 이름 — 새 담당자로 등록됩니다';
        input.insertAdjacentElement('afterend', hint);
        input.setAttribute('aria-describedby', hint.id);
        function sync() {
            var v = input.value.trim();
            hint.hidden = !v || known[v] === true;
        }
        input.addEventListener('input', sync);
        sync();
    };

    // ── 서버 버전 감시 ───────────────────────────────────────────────
    // 서버는 한 번 바뀌면 끝이지만 브라우저에는 옛 화면이 남는다. 열려 있는 화면이 주기적으로 서버 버전을 물어
    // 바뀌었으면 새로 고침을 권한다. 입력 중일 수 있으므로 자동으로 새로 고치지는 않는다
    (function () {
        // 이 화면을 내려 준 서버의 빌드. 스크립트 안에 직접 쓰면 '+' 같은 글자가 HTML 인코딩된 채로 남아
        // 서버 값과 달라지므로 속성으로 받는다
        var build = document.documentElement.getAttribute('data-build');
        if (!build || !window.fetch) return;
        var el = null, state = '', fails = 0, timer = null;

        function show(kind, version) {
            if (kind === state) return;
            state = kind;
            if (el) { el.remove(); el = null; }
            if (!kind) return;
            el = document.createElement('div');
            el.className = 'notice' + (kind === 'offline' ? ' alarm' : '');
            el.setAttribute('role', kind === 'offline' ? 'alert' : 'status');
            if (kind === 'new') {
                el.innerHTML = App.icon('refresh') + '<span>새 버전(v' + App.esc(version) + ')이 적용되었습니다. 새로 고치면 반영됩니다.</span>' +
                    '<button type="button" class="btn small">새로 고침</button>';
                el.querySelector('button').addEventListener('click', function () { location.reload(); });
            } else if (kind === 'updating') {
                el.innerHTML = '<span class="spinner"></span><span>서버를 업데이트하는 중입니다. 끝나면 자동으로 다시 연결됩니다.</span>';
            } else {
                el.innerHTML = App.icon('alert') + '<span>서버에 연결할 수 없습니다. 다시 연결하는 중입니다.</span>';
            }
            document.body.appendChild(el);
        }

        function next() {
            clearTimeout(timer);
            if (document.hidden) return;   // 보이지 않는 탭은 묻지 않는다 (다시 보일 때 바로 확인)
            timer = setTimeout(check, state === 'updating' || state === 'offline' ? 5000 : 60000);
        }

        function check() {
            fetch(App.url('/version'), { cache: 'no-store', headers: { 'Accept': 'application/json' } })
                .then(function (r) { if (!r.ok) throw new Error(r.status); return r.json(); })
                .then(function (v) {
                    fails = 0;
                    if (v.updating) show('updating');
                    else if (v.build !== build) show('new', v.version);
                    else show('');
                })
                .catch(function () {
                    fails++;
                    // 업데이트 중이라고 들은 뒤의 끊김은 예정된 것이고, 그 밖에는 두 번 연속 실패해야 끊김으로 본다
                    if (state === 'updating') return;
                    if (fails >= 2) show('offline');
                })
                .then(next);
        }

        document.addEventListener('visibilitychange', function () { if (!document.hidden) check(); });
        timer = setTimeout(check, 3000);   // 캐시에서 열린 옛 화면도 곧바로 알아채도록
        App.checkVersion = check;
    })();

    window.App = App;
})();
