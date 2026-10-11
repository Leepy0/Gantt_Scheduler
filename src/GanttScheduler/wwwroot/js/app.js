// 공통 유틸: 서버 호출, 날짜·시각, 토스트, 시트(대화상자), 확인창, 임시 저장, 인쇄
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

    // 상태 칩 — 서버가 정한 {cls, icon, text}(Views.*Chip)를 Razor 의 _StatusChip 과 같은 마크업으로 그린다
    App.chip = function (c) {
        if (!c || !c.text) return '';
        return '<span class="chip ' + App.esc(c.cls || '') + '">' + (c.icon ? App.icon(c.icon) : '') + App.esc(c.text) + '</span>';
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

    function z(v) { return (v < 10 ? '0' : '') + v; }

    // ── 시각: 화면의 시각은 모두 브라우저 현지 시각, 같은 형식으로 보인다 ──
    // 서버는 UTC(ISO, 끝에 Z)로 내려 준다. 올해가 아닌 기록은 연도를 붙인다('MM-dd HH:mm' / 'yyyy-MM-dd HH:mm')
    App.time = function (iso) {
        if (!iso) return '';
        var t = new Date(iso);
        if (isNaN(t.getTime())) return '';
        var md = z(t.getMonth() + 1) + '-' + z(t.getDate()) + ' ' + z(t.getHours()) + ':' + z(t.getMinutes());
        return t.getFullYear() === new Date().getFullYear() ? md : t.getFullYear() + '-' + md;
    };

    App.timeFull = function (iso) {
        var t = new Date(iso);
        if (isNaN(t.getTime())) return '';
        return t.getFullYear() + '-' + z(t.getMonth() + 1) + '-' + z(t.getDate()) + ' ' + z(t.getHours()) + ':' + z(t.getMinutes());
    };

    // 화면에 넣을 시각 조각 — 서버 렌더의 <time datetime> 과 같은 모양
    App.timeTag = function (iso) {
        if (!iso) return '';
        return '<time datetime="' + App.esc(iso) + '" title="' + App.esc(App.timeFull(iso)) + ' (이 PC 의 시각)">' + App.esc(App.time(iso)) + '</time>';
    };

    // 서버가 그린 <time datetime="…Z"> 를 현지 시각으로 바꾼다 (글자는 스크립트가 못 돌 때의 한국 시간)
    App.localizeTimes = function (root) {
        (root || document).querySelectorAll('time[datetime]').forEach(function (el) {
            var iso = el.getAttribute('datetime');
            if (!/T\d\d:\d\d/.test(iso)) return;      // 날짜만 있는 값은 그대로
            el.textContent = App.time(iso);
            el.title = App.timeFull(iso) + ' (이 PC 의 시각)';
        });
    };

    // ── 오늘 ────────────────────────────────────────────────────────
    // App.today: 한국 날짜(지연 판정 기준, 규칙 8). 오래 열어 둔 화면은 /version·변경 응답의 today 로 갱신된다.
    // App.userToday(): '오늘 시작·오늘 완료' 같은 실적 기본값에 쓰는 접속자 PC 의 날짜 — 해외 법인에서 저녁에 눌러도 그 나라 날짜가 들어간다.
    // PC 시계가 틀린 경우를 막으려고 한국 날짜 ± 허용 일수 안일 때만 쓴다. 허용 일수는 서버 규칙(Rules.ClientDayWindow)을
    // 화면이 받아(data-day-window) 쓴다 — 단추가 넣은 날짜는 사용자가 고칠 수 있는 입력값이라 서버가 바꾸지 않으므로 여기서 지킨다.
    // 서버는 진행률만 올린 작업의 자동 실적 시작에만 같은 판정(Rules.UserToday)을 한다
    App.localToday = function () {
        var t = new Date();
        return t.getFullYear() + '-' + z(t.getMonth() + 1) + '-' + z(t.getDate());
    };
    App.today = document.documentElement.getAttribute('data-today') || App.localToday();
    var dayWindow = parseInt(document.documentElement.getAttribute('data-day-window'), 10);
    if (isNaN(dayWindow)) dayWindow = 0;     // 값이 없으면 한국 날짜만 (서버와 어긋나는 쪽보다 안전한 쪽)
    App.userToday = function () {
        var local = App.localToday();
        return Math.abs(App.d.diff(local, App.today)) <= dayWindow ? local : App.today;
    };
    var todayListeners = [];
    App.onToday = function (fn) { todayListeners.push(fn); };
    App.setToday = function (day) {
        if (!day || day === App.today) return;
        var old = App.today;
        App.today = day;
        todayListeners.forEach(function (fn) { try { fn(day, old); } catch (e) { /* 화면 하나의 오류가 다른 화면 갱신을 막지 않게 */ } });
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
            // 서버 오류(5xx)에는 오류 번호가 붙어 온다(X-Error-Id). 같은 번호가 서버 로그에 남으므로 관리자에게 알릴 수 있게 보인다
            var errorId = (body && body.errorId) || res.headers.get('X-Error-Id');
            // 503: 서버가 새 버전으로 바뀌는 중 (IIS 가 점검 안내 화면을 대신 내보낸다)
            var err = body && body.error
                ? fail(body.error, body.field)
                : res.status === 503
                    ? fail('서버를 업데이트하는 중입니다. 잠시 후 다시 시도해 주세요. 입력한 내용은 그대로 있습니다.')
                    : fail('요청을 처리하지 못했습니다(' + res.status + (errorId ? ', 오류 번호 ' + errorId : '') + '). 잠시 후 다시 시도해 주세요.');
            err.status = res.status;
            err.errorId = errorId || null;
            err.data = body;          // 409(동시 수정) 처럼 화면이 후속 처리를 해야 하는 응답에 쓴다
            throw err;
        }
        return body;
    }

    App.get = function (url) {
        return send(url, { headers: { 'Accept': 'application/json' } });
    };

    // 사람이 누르지 않아도 주기적으로 보내는 확인(상세 화면의 변경 확인 등). 서버는 이 요청을 '사용 중'으로 치지 않는다
    // (ActivityTracker.IsUserRequest) — 켜 둔 화면 때문에 자동 업데이트가 미뤄지지 않게
    App.poll = function (url) {
        return send(url, { headers: { 'Accept': 'application/json', 'X-Background': '1' } });
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

    // ── 임시 저장(긴 입력) ──────────────────────────────────────────
    // 시트를 닫거나 화면을 새로 고쳐도 쓰던 진행 내용·사유가 사라지지 않게 이 탭(sessionStorage)에만 둔다.
    // 저장소를 못 쓰는 환경(사생활 보호 모드 등)에서는 조용히 건너뛴다
    App.draft = {
        get: function (key) {
            try { var v = sessionStorage.getItem('gs.draft.' + key); return v ? JSON.parse(v) : null; } catch (e) { return null; }
        },
        set: function (key, value) {
            try {
                if (value == null) sessionStorage.removeItem('gs.draft.' + key);
                else sessionStorage.setItem('gs.draft.' + key, JSON.stringify(value));
            } catch (e) { /* 무시 */ }
        },
        clear: function (key) { App.draft.set(key, null); }
    };

    // 처리 중 잠금: 한 번 누른 단추는 응답이 올 때까지 다시 눌리지 않는다(이중 등록 방지)
    App.busy = async function (btn, fn) {
        if (!btn) return fn();
        if (btn.getAttribute('aria-busy') === 'true') return;
        btn.setAttribute('aria-busy', 'true');
        btn.disabled = true;
        try { return await fn(); } finally {
            btn.removeAttribute('aria-busy');
            btn.disabled = false;
        }
    };

    // ── 시트: 데스크톱은 가운데 대화상자, 모바일은 아래에서 올라오는 시트 ──
    // opts: { title, html, primary, wide, danger: {label, onClick}, onSubmit(form, sheet), onOpen(form, sheet), onClose(saved),
    //         draftKey: 긴 입력(textarea)을 임시 저장할 열쇠,
    //         onConflict(form, sheet, err): 저장이 409(다른 사용자가 먼저 고침)로 거절됐을 때 — 최신 버전으로 바꾸고 바뀐 값을 알린다.
    //                                       문자열을 돌려주면 그것을 오류 문구로 보인다 }
    // 입력을 바꾼 뒤 바깥·Esc·닫기·취소로 닫으려 하면 버릴지 묻는다
    var sheetSeq = 0;
    App.sheet = function (opts) {
        var dlg = document.createElement('dialog');
        var titleId = 'sheet-title-' + (++sheetSeq);
        dlg.className = 'sheet' + (opts.wide ? ' wide' : '');
        dlg.setAttribute('aria-labelledby', titleId);
        dlg.innerHTML =
            '<form class="sheet-form" novalidate>' +
            '<div class="sheet-head"><h2 id="' + titleId + '">' + App.esc(opts.title) + '</h2>' +
            '<button type="button" class="btn icon ghost" data-close aria-label="닫기">' + App.icon('x') + '</button></div>' +
            '<div class="sheet-body"><p class="form-error" role="alert" hidden></p>' +
            '<div class="form-note draft-note" data-draft-note hidden></div>' + (opts.html || '') + '</div>' +
            '<div class="sheet-foot">' +
            (opts.danger ? '<button type="button" class="btn danger" data-danger>' + App.esc(opts.danger.label) + '</button>' : '') +
            '<span class="spacer"></span>' +
            '<button type="button" class="btn" data-close>' + (opts.primary ? '취소' : '닫기') + '</button>' +
            (opts.primary ? '<button type="submit" class="btn primary">' + App.esc(opts.primary) + '</button>' : '') +
            '</div></form>';
        document.body.appendChild(dlg);

        var form = dlg.querySelector('form');
        var body = dlg.querySelector('.sheet-body');
        var errorEl = dlg.querySelector('.form-error');
        var submitBtn = dlg.querySelector('button[type=submit]');
        var busy = false;
        var draftKey = opts.draftKey || null;

        // 바뀌었는지 볼 입력: 이름이 있는 입력과 모든 textarea(진행 내용 등). 누르는 즉시 저장되는 체크리스트처럼
        // 이름이 없는 입력은 보지 않는다
        function tracked() {
            return Array.prototype.filter.call(form.querySelectorAll('input, select, textarea'), function (el) {
                if (el.type === 'button' || el.type === 'submit' || el.type === 'file') return false;
                return !!el.name || el.tagName === 'TEXTAREA';
            });
        }
        function state() {
            return tracked().map(function (el, i) {
                return (el.name || 't' + i) + '=' + (el.type === 'checkbox' || el.type === 'radio' ? el.checked : el.value);
            }).join('\u0001');
        }
        var initial = '';

        // ── 임시 저장 ──
        function textareas() { return Array.prototype.slice.call(form.querySelectorAll('textarea')); }
        function draftName(el, i) { return el.name || el.getAttribute('data-draft') || 't' + i; }
        function collectDraft() {
            var out = null;
            textareas().forEach(function (el, i) {
                if (el.value.trim() && el.value !== el.defaultValue) { out = out || {}; out[draftName(el, i)] = el.value; }
            });
            return out;
        }
        var draftTimer = null;
        function saveDraft() {
            if (!draftKey) return;
            clearTimeout(draftTimer);
            App.draft.set(draftKey, collectDraft());
        }
        function offerDraft() {
            var saved = draftKey ? App.draft.get(draftKey) : null;
            if (!saved) return;
            var areas = textareas();
            var differs = areas.some(function (el, i) { var v = saved[draftName(el, i)]; return v != null && v !== el.value; });
            if (!differs) return;
            var note = dlg.querySelector('[data-draft-note]');
            note.innerHTML = App.icon('edit') + '<span>저장하지 않고 닫은 작성 내용이 있습니다.</span>' +
                '<span class="spacer"></span><button type="button" class="btn small" data-draft-restore>복원</button>' +
                '<button type="button" class="btn small ghost" data-draft-drop>버리기</button>';
            note.hidden = false;
            note.querySelector('[data-draft-restore]').addEventListener('click', function () {
                areas.forEach(function (el, i) {
                    var v = saved[draftName(el, i)];
                    if (v != null) { el.value = v; el.dispatchEvent(new Event('input', { bubbles: true })); }
                });
                note.hidden = true;
                var first = areas.filter(function (el, i) { return saved[draftName(el, i)] != null; })[0];
                if (first) first.focus();
            });
            note.querySelector('[data-draft-drop]').addEventListener('click', function () {
                App.draft.clear(draftKey);
                note.hidden = true;
            });
        }
        form.addEventListener('input', function (e) {
            if (!draftKey || e.target.tagName !== 'TEXTAREA') return;
            clearTimeout(draftTimer);
            draftTimer = setTimeout(saveDraft, 300);
        });

        var sheet = {
            el: dlg,
            form: form,
            // 코드로 닫기(삭제·이동 뒤 등) — 묻지 않는다
            close: function () { dlg.close(); },
            isDirty: function () { return state() !== initial; },
            // 지금 입력을 '바뀌지 않은 상태'로 본다 — 시트를 열어 둔 채 저장하고 이어서 입력할 때(저장 후 계속 추가)
            markClean: function () { initial = state(); if (draftKey) App.draft.clear(draftKey); },
            saveDraft: saveDraft,
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
            },
            // 동시 수정 뒤 '다른 사용자가 바꾼 값' 안내. field 가 없으면 본문 맨 위에, text 가 비면 지운다.
            // applied: 내가 손대지 않은 칸이라 그 값으로 바꿔 넣었다는 뜻
            conflictNote: function (field, text, applied) {
                var host = field ? form.querySelector('[data-field="' + field + '"]') : body;
                if (!host) host = body;
                var key = field || '_';
                var note = host.querySelector('[data-conflict-note="' + key + '"]');
                if (!text) { if (note) note.remove(); return; }
                if (!note) {
                    note = document.createElement('span');
                    note.className = 'hint conflict-note';
                    note.setAttribute('data-conflict-note', key);
                    if (field) host.appendChild(note); else errorEl.insertAdjacentElement('afterend', note);
                }
                note.innerHTML = App.icon('alert') + '<span>다른 사용자가 바꾼 값: ' + App.esc(text) + (applied ? ' — 그 값으로 바꿨습니다' : '') + '</span>';
            },
            clearConflictNotes: function () {
                form.querySelectorAll('[data-conflict-note]').forEach(function (n) { n.remove(); });
            }
        };

        // 닫기 요청: 바뀐 입력이 있으면 버릴지 묻는다
        var asking = false;
        async function requestClose() {
            if (asking || !dlg.open) return;
            if (sheet.isDirty()) {
                asking = true;
                var ok = await App.confirm({ title: '작성 중인 내용', message: '작성 중인 내용을 버릴까요? 저장하지 않은 입력이 사라집니다.', verb: '버리기' });
                asking = false;
                if (!ok) return;
                if (draftKey) App.draft.clear(draftKey);
            }
            dlg.close();
        }

        // 바깥(backdrop) 클릭은 누른 곳과 뗀 곳이 모두 바깥일 때만 닫는다 — 입력칸에서 글자를 끌어 선택하다 바깥에서 떼면
        // click 대상이 dialog 가 되어 닫히던 문제를 막는다
        var downOutside = false;
        dlg.addEventListener('pointerdown', function (e) { downOutside = e.target === dlg; });
        dlg.addEventListener('click', function (e) {
            if (e.target.closest('[data-close]')) { requestClose(); return; }
            if (e.target === dlg && downOutside) requestClose();
            downOutside = false;
        });
        // Esc: 브라우저가 바로 닫지 않게 막고 같은 확인을 거친다
        dlg.addEventListener('keydown', function (e) {
            if (e.key === 'Escape') { e.preventDefault(); requestClose(); }
        });
        dlg.addEventListener('cancel', function (e) { e.preventDefault(); requestClose(); });

        var saved = false;
        dlg.addEventListener('close', function () {
            clearTimeout(draftTimer);
            dlg.remove();
            if (opts.onClose) opts.onClose(saved);   // saved: 저장하고 닫혔는지
        });

        if (opts.danger) {
            // 위험 동작(삭제·사용 중지)은 응답이 올 때까지 다시 눌리지 않게 잠근다 — 두 번 눌러 두 번 처리되지 않게
            var dangerBtn = dlg.querySelector('[data-danger]');
            dangerBtn.addEventListener('click', function () {
                App.busy(dangerBtn, function () { return opts.danger.onClick(sheet); });
            });
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
                if (done !== false) {
                    saved = true;
                    if (draftKey) App.draft.clear(draftKey);
                    dlg.close();
                }
            } catch (err) {
                var message = err.message;
                // 다른 사용자가 먼저 고쳤으면 시트가 최신 버전으로 바꾸고 바뀐 값을 보여 준다 — 입력한 값은 그대로 두어 다시 저장할 수 있다
                if (err.status === 409 && opts.onConflict) {
                    try {
                        var m = await opts.onConflict(form, sheet, err);
                        if (typeof m === 'string') message = m;
                    } catch (e2) { message = e2.message || message; }
                }
                sheet.setError(message, err.field);
            } finally {
                busy = false;
                if (submitBtn) { submitBtn.disabled = false; submitBtn.textContent = label; }
            }
        });

        dlg.showModal();
        if (opts.onOpen) opts.onOpen(form, sheet);
        initial = state();
        offerDraft();

        // 첫 포커스: 데스크톱은 첫 입력칸, 모바일은 시트 자체(가상 키보드가 바로 올라와 내용을 가리지 않게).
        // onOpen 이 이미 다른 곳(사유 입력 등)에 포커스를 줬으면 그대로 둔다
        var active = document.activeElement;
        if (!dlg.contains(active) || active.matches('[data-close]')) {
            var first = App.isMobile() ? null
                : body.querySelector('input:not([type=hidden]):not([type=checkbox]):not([type=radio]):not([type=range]):not([disabled]), select:not([disabled]), textarea:not([disabled])');
            if (first) first.focus();
            else { dlg.setAttribute('tabindex', '-1'); dlg.focus(); }
        }
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
        var pollers = [];
        // 화면별로 1분 확인에 얹을 일 (상세 화면의 '다른 사용자가 바꿨습니다' 확인 등). 서버가 정상일 때만 부른다
        App.onPoll = function (fn) { pollers.push(fn); };
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
                    if (v.today) App.setToday(v.today);
                    if (!v.updating) pollers.forEach(function (fn) { try { fn(v); } catch (e) { /* 무시 */ } });
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

    // ── 인쇄 ─────────────────────────────────────────────────────────
    // [data-print] 단추는 인쇄 창을 연다. 화면은 App.onPrint 로 인쇄 직전(간트를 월 배율로)·직후(원래대로)에 할 일을 건다.
    // 인쇄 미리보기·PDF 저장은 beforeprint 없이 print 매체로만 바뀌는 경우가 있어 둘 다 듣는다
    App.onPrint = function (before, after) {
        var printing = false;
        function b() { if (printing) return; printing = true; before(); }
        function a() { if (!printing) return; printing = false; after(); }
        window.addEventListener('beforeprint', b);
        window.addEventListener('afterprint', a);
        var mq = window.matchMedia ? window.matchMedia('print') : null;
        if (mq && mq.addEventListener) mq.addEventListener('change', function (e) { if (e.matches) b(); else a(); });
    };
    document.addEventListener('click', function (e) {
        if (e.target.closest && e.target.closest('[data-print]')) window.print();
    });

    window.App = App;
    App.localizeTimes();
})();
