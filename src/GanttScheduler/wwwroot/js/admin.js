// 관리: 기준 정보를 단계별 열(컬럼 탐색) 형태로 조회·편집
(function () {
    'use strict';

    var esc = App.esc, icon = App.icon;
    var body = document.getElementById('admin-body');
    var isAdmin = body.dataset.admin === '1';
    // 종류별·단계별 이름 길이 상한 — 서버 검사·DB 열 길이와 같은 값(서버가 data-limits 로 준다)
    var limits = {};
    try { limits = JSON.parse(body.dataset.limits || '{}'); } catch (e) { limits = {}; }
    var fieldLimits = {};
    try { fieldLimits = JSON.parse(body.dataset.fieldLimits || '{}'); } catch (e) { fieldLimits = {}; }
    function maxLen(level) { var l = limits[kind]; return (l && l[level - 1]) || 50; }

    var CATEGORY_KINDS = { general: '일반', automation: '물류 자동화 (기기 내역 입력)', container: '용기 (용기 내역 입력)' };
    var STAGE_KINDS = { normal: '일반', inspection: '검수 (입고 추천일 기준)', inbound: '입고 (추천일 표시)' };

    // 종류별 화면 구성. removable: 사용 중지 대신 바로 삭제하는 단계
    var KINDS = {
        location: { levels: ['법인', '제품군', '라인', '층'] },
        category: { levels: ['유형', '기본 단계', '체크 항목'], removable: [2, 3] },
        equipment: { levels: ['대분류', '하위 타입'] },
        container: { levels: ['용기 종류'] },
        vendor: { levels: ['협력사'] },
        org: { levels: ['그룹', '파트'] },
        user: { levels: ['사용자'], fixedOrder: true },
        holiday: { levels: ['공휴일'], fixedOrder: true, removable: [1] },
        reason: { levels: ['지연 사유 분류'] }
    };

    var kind = 'location';
    var nodes = [];
    var selected = {};      // level → 선택한 노드 id
    var holidayYear = null; // 공휴일 탭에서 보고 있는 연도
    var holidayNotice = null; // 마지막 공휴일 변경의 일정 영향 안내 (서버 문구)

    function url(handler) { return App.url('/Admin?handler=' + handler); }

    function conf() { return KINDS[kind]; }
    function removable(level) { return (conf().removable || []).indexOf(level) >= 0; }

    function childrenOf(level, parentId) {
        return nodes.filter(function (n) {
            if (kind === 'holiday') return +n.date.slice(0, 4) === holidayYear;   // 공휴일은 연도별로 본다
            return n.level === level && (level === 1 || n.parentId === parentId);
        });
    }

    // ── 공휴일: 연도 칩·여러 줄 붙여넣기 ──
    // 올해·내년은 항상 칩을 보인다. 공휴일이 하나도 없는 해는 근무일 계산이 주말만 빼고 세므로 노랑(위험)으로 알린다
    // (내년은 정부 발표가 나오는 10월부터 — 서버의 관리자 알림과 같은 기준)
    function holidayYears() {
        var y = +App.today.slice(0, 4), month = +App.today.slice(5, 7);
        var counts = {};
        counts[y] = 0;
        counts[y + 1] = 0;
        nodes.forEach(function (n) { var k = +n.date.slice(0, 4); counts[k] = (counts[k] || 0) + 1; });
        var years = Object.keys(counts).map(Number).sort(function (a, b) { return a - b; });
        function missing(k) { return counts[k] === 0 && (k === y || (k === y + 1 && month >= 10)); }
        return { years: years, counts: counts, thisYear: y, missing: missing };
    }

    function holidayBarHtml() {
        var h = holidayYears();
        if (holidayYear == null || h.years.indexOf(holidayYear) < 0) holidayYear = h.thisYear;
        var html = '<div class="year-bar"><div class="year-chips" role="group" aria-label="연도">';
        h.years.forEach(function (y) {
            html += '<button type="button" class="year-chip' + (h.missing(y) ? ' warn' : '') + '" data-year="' + y + '" aria-pressed="' + (y === holidayYear) + '">' +
                (h.missing(y) ? icon('alert') : '') + y + '년 <span class="num">' + h.counts[y] + '</span></button>';
        });
        html += '</div>';
        if (isAdmin) html += '<button type="button" class="btn small" data-paste>' + icon('plus') + '여러 날 붙여넣기</button>';
        html += '</div>';
        var gaps = h.years.filter(h.missing);
        if (gaps.length) {
            html += '<p class="form-warn" data-holiday-missing>' + icon('alert') + '<span>' + gaps.join('·') + '년 공휴일이 없습니다. 그 해 일정의 근무일 계산(일정 이동·지연 일수)이 주말만 빼고 셉니다.' +
                (isAdmin ? ' 정부 발표(월력요항)를 \'여러 날 붙여넣기\'로 한 번에 넣을 수 있습니다.' : '') + '</span></p>';
        }
        if (holidayNotice) html += '<p class="form-note" data-holiday-notice>' + icon('calendar') + '<span>' + esc(holidayNotice) + '</span></p>';
        return html;
    }

    function openPaste() {
        App.sheet({
            title: '공휴일 여러 날 붙여넣기',
            primary: '추가',
            html: field('text', '공휴일 목록', '<textarea name="text" rows="10" placeholder="2028-01-01\t신정&#10;2028-01-26\t설날"></textarea>') +
                '<span class="hint">한 줄에 날짜와 이름 하나씩입니다. 날짜와 이름은 TAB(Excel·표에서 복사)이나 공백으로 나눕니다. ' +
                '2028-01-01 · 2028.1.1 · 2028년 1월 1일 모두 됩니다. 이미 있는 날짜는 건너뜁니다.</span>',
            onSubmit: async function (form) {
                var before = holidayYears().counts;
                await change('PasteHolidays', { text: form.elements.text.value });
                // 새로 들어간 해 중 가장 이른 해를 보인다
                var after = holidayYears();
                var grown = after.years.filter(function (y) { return after.counts[y] > (before[y] || 0); });
                if (grown.length) { holidayYear = grown[0]; render(); }
            }
        });
    }

    function extraText(n) {
        if (kind === 'location' && n.level === 1) return [n.country, '해상 ' + (n.oceanLeadDays || 0) + '일'].filter(Boolean).join(' · ');
        if (kind === 'category' && n.level === 1) return n.kind === 'automation' ? '기기 내역' : n.kind === 'container' ? '용기 내역' : '';
        if (kind === 'category' && n.level === 2) {
            return [n.kind === 'inspection' ? '검수' : n.kind === 'inbound' ? '입고' : '',
                n.defaultWorkDays != null ? '기본 ' + n.defaultWorkDays + '근무일' : ''].filter(Boolean).join(' · ');
        }
        if (kind === 'user') return [n.org, n.isAdmin ? '관리자' : '', n.lastSeen ? '최근 접속 ' + n.lastSeen : '미접속'].filter(Boolean).join(' · ');
        if (kind === 'holiday') return n.date;
        return '';
    }

    function render() {
        var c = conf();
        var html = kind === 'holiday' ? holidayBarHtml() : '';
        html += '<div class="columns">';
        var parentId = null;
        for (var level = 1; level <= c.levels.length; level++) {
            var list = level === 1 || parentId != null ? childrenOf(level, parentId) : [];
            // 선택이 없거나 사라졌으면 첫 항목을 고른다
            if (!list.some(function (n) { return n.id === selected[level]; })) selected[level] = list.length ? list[0].id : null;
            var hasNext = level < c.levels.length;
            var canAdd = isAdmin && (level === 1 || parentId != null);

            html += '<div class="column"><div class="column-head"><span>' + c.levels[level - 1] + '</span><span class="count">' + list.length + '</span></div><div class="column-list">';
            if (!list.length) {
                html += '<p class="column-empty">' + (level > 1 && parentId == null ? '상위 항목을 먼저 선택해 주세요.' : '등록된 항목이 없습니다.') + '</p>';
            }
            list.forEach(function (n, i) {
                var extra = extraText(n);
                html += '<div class="node' + (hasNext && n.id === selected[level] ? ' sel' : '') + (n.active ? '' : ' off') + '">' +
                    '<button type="button" class="node-main" data-select="' + level + ':' + n.id + '"' + (hasNext ? '' : ' data-edit="' + level + ':' + n.id + '"') + '>' +
                    '<span class="node-name">' + esc(n.name) + '</span>' +
                    (extra ? '<span class="node-extra">' + esc(extra) + '</span>' : '') +
                    (n.active ? '' : '<span class="chip">사용 중지</span>') + '</button>';
                if (isAdmin) {
                    html += '<span class="node-actions">';
                    if (!c.fixedOrder) {
                        html += '<button type="button" class="btn icon small" data-move="' + level + ':' + n.id + ':-1" aria-label="위로"' + (i === 0 ? ' disabled' : '') + '>' + icon('up') + '</button>' +
                            '<button type="button" class="btn icon small" data-move="' + level + ':' + n.id + ':1" aria-label="아래로"' + (i === list.length - 1 ? ' disabled' : '') + '>' + icon('down') + '</button>';
                    }
                    html += '<button type="button" class="btn icon small" data-edit="' + level + ':' + n.id + '" aria-label="편집">' + icon('edit') + '</button></span>';
                }
                html += '</div>';
            });
            html += '</div>';
            if (canAdd) {
                html += '<form class="add-row" data-add="' + level + '">' +
                    (kind === 'holiday' ? '<input type="date" name="date" required aria-label="날짜" />' : '') +
                    '<input name="name" maxlength="' + maxLen(level) + '" required placeholder="' + c.levels[level - 1] + ' 이름" aria-label="' + c.levels[level - 1] + ' 이름" />' +
                    '<button type="submit" class="btn">' + icon('plus') + '<span class="hide-sm">추가</span></button></form>';
            }
            html += '</div>';
            parentId = selected[level];
        }
        html += '</div>';
        if (kind === 'category') html += templateWorksHtml();
        body.innerHTML = html;
    }

    // ── 유형 > 단계 아래의 작업 템플릿 (서버 노드 level 4) ──
    // 체크 항목 열과 따로 그린다: 체크 항목의 하위가 아니라 단계의 또 다른 목록이기 때문이다
    function templateWorksOf(stageId) {
        return nodes.filter(function (n) { return n.level === 4 && n.parentId === stageId; });
    }

    function templateWorkExtra(n) {
        var pred = n.predecessorId ? find(4, n.predecessorId) : null;
        return ['시작 +' + (n.offsetWorkDays || 0) + '근무일', '기간 ' + (n.durationWorkDays || 1) + '근무일', pred ? '선행: ' + pred.name : '']
            .filter(Boolean).join(' · ');
    }

    function templateWorksHtml() {
        var stage = selected[2] ? find(2, selected[2]) : null;
        if (!stage) return '';
        var list = templateWorksOf(stage.id);
        var html = '<section class="column tw-panel" data-template-works><div class="column-head"><span>작업 템플릿 · ' + esc(stage.name) +
            '</span><span class="count">' + list.length + '</span></div>' +
            '<p class="hint">과제를 등록할 때 이 단계에 함께 만드는 작업입니다. 시작은 단계 시작일(첫 단계는 착수일, 이후는 앞 단계 목표일 다음 근무일)부터 센 근무일입니다. 담당은 과제 담당이 됩니다.</p>' +
            '<div class="column-list">';
        if (!list.length) html += '<p class="column-empty">등록된 작업 템플릿이 없습니다.</p>';
        list.forEach(function (n, i) {
            html += '<div class="node">' +
                '<button type="button" class="node-main" data-tw-edit="' + n.id + '"><span class="node-name">' + esc(n.name) + '</span>' +
                '<span class="node-extra">' + esc(templateWorkExtra(n)) + '</span></button>';
            if (isAdmin) {
                html += '<span class="node-actions">' +
                    '<button type="button" class="btn icon small" data-move="4:' + n.id + ':-1" aria-label="위로"' + (i === 0 ? ' disabled' : '') + '>' + icon('up') + '</button>' +
                    '<button type="button" class="btn icon small" data-move="4:' + n.id + ':1" aria-label="아래로"' + (i === list.length - 1 ? ' disabled' : '') + '>' + icon('down') + '</button>' +
                    '<button type="button" class="btn icon small" data-tw-edit="' + n.id + '" aria-label="편집">' + icon('edit') + '</button></span>';
            }
            html += '</div>';
        });
        html += '</div>';
        if (isAdmin) html += '<div class="tw-foot"><button type="button" class="btn" data-tw-add="' + stage.id + '">' + icon('plus') + '작업 템플릿 추가</button></div>';
        return html + '</section>';
    }

    function openTemplateWork(stageId, id) {
        var n = id ? find(4, id) : null;
        var parentId = n ? n.parentId : stageId;
        var stage = find(2, parentId);
        var others = templateWorksOf(parentId).filter(function (x) { return !n || x.id !== n.id; });
        var dis = isAdmin ? '' : ' disabled';
        var html = field('name', '작업명', '<input name="name" maxlength="' + maxLen(4) + '" required value="' + esc(n ? n.name : '') + '"' + dis + ' />') +
            '<div class="field-row">' +
            field('offsetWorkDays', '시작 (단계 시작부터, 근무일)', '<input name="offsetWorkDays" type="number" min="0" max="999" step="1" inputmode="numeric" required value="' + (n ? n.offsetWorkDays : 0) + '"' + dis + ' />') +
            field('durationWorkDays', '기간 (근무일)', '<input name="durationWorkDays" type="number" min="1" max="999" step="1" inputmode="numeric" required value="' + (n ? n.durationWorkDays : 5) + '"' + dis + ' />') +
            '</div>' +
            field('predecessorId', '선행 작업 <span class="opt">선택</span>', '<select name="predecessorId"' + dis + '><option value="">없음</option>' +
                others.map(function (x) { return '<option value="' + x.id + '"' + (n && n.predecessorId === x.id ? ' selected' : '') + '>' + esc(x.name) + '</option>'; }).join('') +
                '</select>') +
            '<span class="hint">선행을 고르면 과제에서 \'끝나면 시작\' 연결이 됩니다. 선행이 끝나기 전에 시작하도록 적혀 있으면 등록할 때 선행 뒤로 밀어 만듭니다.</span>';
        App.sheet({
            title: (n ? '작업 템플릿 · ' + n.name : '작업 템플릿 추가') + (stage ? ' (' + stage.name + ')' : ''),
            html: html,
            primary: isAdmin ? (n ? '저장' : '추가') : null,
            danger: !isAdmin || !n ? null : {
                label: '삭제',
                onClick: async function (sheet) {
                    var ok = await App.confirm({ title: '삭제', message: '"' + n.name + '" 작업 템플릿을 삭제합니다. 이미 등록한 과제의 작업은 그대로 남습니다.', verb: '삭제' });
                    if (!ok) return;
                    try {
                        await change('Toggle', { kind: kind, level: 4, id: n.id, dir: 0, active: null });
                        sheet.close();
                        App.toast('삭제했습니다.');
                    } catch (err) { sheet.setError(err.message, err.field); }
                }
            },
            onSubmit: async function (form) {
                var f = form.elements;
                ['offsetWorkDays', 'durationWorkDays'].forEach(function (k) {
                    if (!/^\d+$/.test(f[k].value.trim())) {
                        var bad = new Error('근무일은 0 이상의 정수로 입력해 주세요.');
                        bad.field = k;
                        throw bad;
                    }
                });
                await change('Save', {
                    kind: kind, level: 4, id: n ? n.id : null, parentId: parentId, name: f.name.value,
                    offsetWorkDays: +f.offsetWorkDays.value, durationWorkDays: +f.durationWorkDays.value,
                    predecessorId: f.predecessorId.value ? +f.predecessorId.value : null
                });
                App.toast(n ? '저장했습니다.' : '작업 템플릿을 추가했습니다.');
            }
        });
    }

    async function load() {
        body.innerHTML = '<div class="loading"><span class="spinner"></span>불러오는 중…</div>';
        try {
            var r = await App.get(url('Data') + '&kind=' + kind);
            nodes = r.nodes;
            render();
        } catch (err) {
            body.innerHTML = '<div class="card"><p class="form-error">' + icon('alert') + '<span>' + esc(err.message) + '</span></p>' +
                '<div class="form-actions"><button type="button" class="btn" id="retry">다시 시도</button></div></div>';
            document.getElementById('retry').addEventListener('click', load);
        }
    }

    // 변경 → 최신 목록. 서버 안내(notice — 공휴일이 걸린 진행 과제 등)가 있으면 알리고 공휴일 탭에는 남겨 둔다
    async function change(handler, payload) {
        var r = await App.post(url(handler), payload);
        nodes = r.nodes;
        holidayNotice = kind === 'holiday' ? (r.notice || null) : null;
        render();
        if (r.notice) App.toast(r.notice);
        return r;
    }

    function find(level, id) {
        return nodes.filter(function (n) { return n.level === level && n.id === id; })[0];
    }

    // 사용자에 연결된 접속 IP 목록. 지금 이 기기의 IP 는 해제할 수 없다(스스로 잠기지 않게)
    function ipItems(n) {
        var list = n.addresses || [];
        if (!list.length) return '<li class="muted">접속 이력 없음</li>';
        return list.map(function (a) {
            return '<li><span class="ip">' + esc(a.ip) + '</span>' +
                '<span class="hint">마지막 접속 ' + esc(a.lastSeen) + (a.current ? ' · 지금 이 기기' : '') + '</span>' +
                (isAdmin && !a.current ? '<button type="button" class="btn icon small ghost" data-remove-ip="' + esc(a.ip) + '" aria-label="' + esc(a.ip) + ' 연결 해제" title="연결 해제">' + icon('x') + '</button>' : '') +
                '</li>';
        }).join('');
    }

    function field(name, label, inner) {
        return '<label class="field" data-field="' + name + '"><span class="label">' + label + '</span>' + inner + '</label>';
    }

    function options(map, value) {
        return Object.keys(map).map(function (k) {
            return '<option value="' + k + '"' + (k === value ? ' selected' : '') + '>' + map[k] + '</option>';
        }).join('');
    }

    async function openEdit(level, id) {
        var n = find(level, id);
        if (!n) return;
        // 사용자 편집에는 소속(그룹 → 파트) 목록이 필요하다
        var orgs = [];
        if (kind === 'user') {
            try { orgs = (await App.get(url('Data') + '&kind=org')).nodes.filter(function (o) { return o.active || o.id === n.orgUnitId; }); }
            catch (err) { App.toast(err.message, 'error'); return; }
        }
        var c = conf();
        var html = field('name', '이름', '<input name="name" maxlength="' + maxLen(level) + '" required value="' + esc(n.name) + '"' + (isAdmin ? '' : ' disabled') + ' />');
        if (kind === 'location' && level === 1) {
            html += field('country', '국가', '<input name="country" maxlength="' + (fieldLimits.country || '') + '" value="' + esc(n.country || '') + '" />') +
                field('oceanLeadDays', '해상 운송 소요일', '<input name="oceanLeadDays" type="number" min="0" max="365" inputmode="numeric" value="' + (n.oceanLeadDays || 0) + '" />') +
                '<span class="hint">입고 단계의 추천일(검수일 + 소요일) 계산에 사용합니다. 국내는 0.</span>';
        }
        if (kind === 'category' && level === 1) html += field('subKind', '내역 입력', '<select name="subKind">' + options(CATEGORY_KINDS, n.kind) + '</select>');
        if (kind === 'category' && level === 2) {
            html += field('subKind', '단계 성격', '<select name="subKind">' + options(STAGE_KINDS, n.kind) + '</select>') +
                field('defaultWorkDays', '기본 기간(근무일) <span class="opt">선택</span>',
                    '<input name="defaultWorkDays" type="number" min="0" max="999" step="1" inputmode="numeric" value="' + (n.defaultWorkDays != null ? n.defaultWorkDays : '') + '"' +
                    (isAdmin ? '' : ' disabled') + ' />') +
                '<span class="hint">앞 단계 목표일(첫 단계는 착수일)부터 이 단계 목표일까지의 근무일. 과제 등록 화면의 목표일 초안에 씁니다. 비워 두면 초안을 비웁니다.</span>' +
                // 참고값: 같은 유형·같은 이름 단계의 과거 리드타임(완료 과제, 서버가 표본이 충분할 때만 준다)
                (n.lead
                    ? '<div class="form-note" data-lead-hint>' + icon('chart') + '<span>완료 과제 ' + n.lead.count + '건의 평균 <b>' + n.lead.avgWorkDays + '근무일</b>' +
                      ' <span class="muted">(앞 단계 완료일부터 이 단계 완료일까지)</span></span>' +
                      (isAdmin ? '<button type="button" class="btn small" data-apply-lead="' + Math.round(n.lead.avgWorkDays) + '">평균 적용</button>' : '') + '</div>'
                    : '<span class="hint" data-lead-hint>완료된 과제가 쌓이면 이 단계의 과거 평균 기간을 함께 보여 줍니다.</span>');
        }
        if (kind === 'holiday') html += field('date', '날짜', '<input name="date" type="date" required value="' + n.date + '" />');
        if (kind === 'user') {
            html += '<div class="field" data-field="orgUnitId" data-org-field><span class="label">소속</span><div class="field-row">' +
                '<select data-org-group aria-label="그룹"' + (isAdmin ? '' : ' disabled') + '></select>' +
                '<select data-org-part aria-label="파트"' + (isAdmin ? '' : ' disabled') + '></select></div>' +
                '<input type="hidden" name="orgUnitId" /></div>' +
                '<label class="check"><input type="checkbox" name="isAdmin"' + (n.isAdmin ? ' checked' : '') + ' />관리자 (기준 정보 변경·과제 삭제)</label>' +
                // 메일 요약 받는 주소 — 관리자만 보고 정한다(IP 식별이라 본인 입력은 가로채기 위험)
                (isAdmin ? field('email', '메일 주소 <span class="opt">선택</span>', '<div class="ip-add"><input name="email" type="text" inputmode="email" maxlength="' + (fieldLimits.email || '') + '" autocomplete="off" placeholder="name@company.com" value="' + esc(n.email || '') + '" />' +
                    '<button type="button" class="btn" data-test-mail>' + icon('mail') + '시험 메일</button></div>') +
                    '<span class="hint">하루 한 번 받을 메일 요약(읽지 않은 알림, 내 작업의 지연·임박)의 주소입니다. 시험 메일로 메일 서버 설정을 확인할 수 있습니다.</span>' : '') +
                '<div class="field" data-field="ip"><span class="label">접속 IP</span><ul class="ip-list" data-ips>' + ipItems(n) + '</ul>' +
                (isAdmin ? '<div class="ip-add"><input name="newIp" maxlength="' + (fieldLimits.ip || '') + '" inputmode="decimal" autocomplete="off" placeholder="IP 주소 (예: 10.1.2.3)" aria-label="연결할 IP 주소" />' +
                    '<button type="button" class="btn" data-add-ip>' + icon('plus') + '연결</button></div>' +
                    '<span class="hint">관리자는 이름 등록 화면에서 새 PC 를 연결할 수 없습니다. PC 를 바꾼 관리자는 여기서 IP 를 추가해 주세요.</span>' : '') +
                '</div>';
        }

        var isRemovable = removable(level);
        App.sheet({
            title: c.levels[level - 1] + ' · ' + n.name,
            html: html,
            primary: isAdmin ? '저장' : null,
            danger: !isAdmin ? null : {
                label: isRemovable ? '삭제' : (n.active ? '사용 중지' : '다시 사용'),
                onClick: async function (sheet) {
                    if (isRemovable) {
                        var ok = await App.confirm({ title: '삭제', message: '"' + n.name + '" 항목을 삭제합니다. 되돌릴 수 없습니다.', verb: '삭제' });
                        if (!ok) return;
                    }
                    try {
                        // 원하는 상태를 실어 보낸다 — 서버는 뒤집지 않고 그 값으로 맞춘다(두 번 눌러도·오래된 화면에서 눌러도 반대로 되지 않게)
                        var r = await change('Toggle', { kind: kind, level: level, id: id, dir: 0, active: isRemovable ? null : !n.active });
                        sheet.close();
                        if (!r.notice) App.toast(isRemovable ? '삭제했습니다.' : (n.active ? '사용 중지했습니다. 기존 과제에는 그대로 표시됩니다.' : '다시 사용합니다.'));
                    } catch (err) { sheet.setError(err.message, err.field); }
                }
            },
            onOpen: function (form, sheet) {
                var lead = form.querySelector('[data-apply-lead]');
                if (lead) lead.addEventListener('click', function () {
                    form.elements.defaultWorkDays.value = lead.dataset.applyLead;
                    form.elements.defaultWorkDays.dispatchEvent(new Event('input', { bubbles: true }));
                });
                if (kind !== 'user') return;
                OrgPicker.bind(orgs, form.querySelector('[data-org-group]'), form.querySelector('[data-org-part]'),
                    form.elements.orgUnitId, form.querySelector('[data-org-field]'), n.orgUnitId);
                bindIps(form, sheet, level, id);
                var test = form.querySelector('[data-test-mail]');
                if (test) test.addEventListener('click', function () {
                    App.busy(test, async function () {
                        sheet.setError(null);       // 앞의 오류(저장 실패 등)를 지우고 이번 결과만 보인다
                        try {
                            await App.post(url('TestMail'), { email: form.elements.email.value });
                            sheet.setError(null);
                            App.toast(form.elements.email.value.trim() + ' 로 시험 메일을 보냈습니다.');
                        } catch (err) { sheet.setError(err.message, err.field); }
                    });
                });
            },
            onSubmit: async function (form) {
                var f = form.elements;
                var days = f.defaultWorkDays ? f.defaultWorkDays.value.trim() : '';
                if (days !== '' && !/^\d+$/.test(days)) {
                    var bad = new Error('기본 기간은 0 이상의 정수(근무일)로 입력해 주세요.');
                    bad.field = 'defaultWorkDays';
                    throw bad;
                }
                var saved = await change('Save', {
                    kind: kind, level: level, id: id, parentId: n.parentId,
                    name: f.name.value,
                    country: f.country ? f.country.value : null,
                    oceanLeadDays: f.oceanLeadDays ? +f.oceanLeadDays.value : null,
                    subKind: f.subKind ? f.subKind.value : null,
                    orgUnitId: f.orgUnitId && f.orgUnitId.value ? +f.orgUnitId.value : null,
                    isAdmin: f.isAdmin ? f.isAdmin.checked : null,
                    date: f.date ? f.date.value : null,
                    defaultWorkDays: days === '' ? null : +days,
                    email: f.email ? f.email.value : null
                });
                if (!saved.notice) App.toast('저장했습니다.');
            }
        });
    }

    // 사용자 시트의 IP 연결·해제. 저장 단추와 따로 바로 반영한다
    function bindIps(form, sheet, level, id) {
        var input = form.elements.newIp;
        var box = form.querySelector('[data-ips]');
        function refresh() {
            var n = find(level, id);
            if (n) box.innerHTML = ipItems(n);
        }
        async function add() {
            try {
                await change('AddIp', { userId: id, ip: input.value });
                input.value = '';
                sheet.setError(null);
                refresh();
                App.toast('IP 를 연결했습니다.');
            } catch (err) { sheet.setError(err.message, err.field); }
        }
        if (input) {
            form.querySelector('[data-add-ip]').addEventListener('click', add);
            // 입력란에서 Enter 는 시트 저장이 아니라 IP 연결
            input.addEventListener('keydown', function (e) { if (e.key === 'Enter') { e.preventDefault(); add(); } });
        }
        box.addEventListener('click', async function (e) {
            var t = e.target.closest('[data-remove-ip]');
            if (!t) return;
            var ip = t.dataset.removeIp;
            var ok = await App.confirm({ title: 'IP 연결 해제', message: ip + ' 연결을 해제합니다. 그 기기에서 다시 접속하면 이름을 새로 등록해야 합니다.', verb: '연결 해제' });
            if (!ok) return;
            try {
                await change('RemoveIp', { userId: id, ip: ip });
                sheet.setError(null);
                refresh();
                App.toast('IP 연결을 해제했습니다.');
            } catch (err) { sheet.setError(err.message, err.field); }
        });
    }

    // ── 이벤트 ──────────────────────────────────────────────────────
    document.querySelectorAll('#admin-tabs [data-kind]').forEach(function (b) {
        b.addEventListener('click', function () {
            kind = b.dataset.kind;
            selected = {};
            holidayNotice = null;
            document.querySelectorAll('#admin-tabs [data-kind]').forEach(function (x) {
                x.classList.toggle('on', x === b);
                x.setAttribute('aria-selected', x === b ? 'true' : 'false');
            });
            load();
        });
    });

    body.addEventListener('click', async function (e) {
        var t;
        if ((t = e.target.closest('[data-year]'))) {
            holidayYear = +t.dataset.year;
            render();
            return;
        }
        if (e.target.closest('[data-paste]')) { openPaste(); return; }
        if ((t = e.target.closest('[data-tw-add]'))) { openTemplateWork(+t.dataset.twAdd, null); return; }
        if ((t = e.target.closest('[data-tw-edit]'))) { openTemplateWork(null, +t.dataset.twEdit); return; }
        if ((t = e.target.closest('[data-move]'))) {
            var m = t.dataset.move.split(':');
            try { await change('Move', { kind: kind, level: +m[0], id: +m[1], dir: +m[2] }); }
            catch (err) { App.toast(err.message, 'error'); }
            return;
        }
        if ((t = e.target.closest('.node-actions [data-edit]'))) {
            var p = t.dataset.edit.split(':');
            openEdit(+p[0], +p[1]);
            return;
        }
        if ((t = e.target.closest('[data-select]'))) {
            var s = t.dataset.select.split(':');
            var level = +s[0], id = +s[1];
            if (level < conf().levels.length) {
                // 하위가 있는 단계: 선택해서 다음 열을 연다
                selected[level] = id;
                for (var l = level + 1; l <= conf().levels.length; l++) delete selected[l];
                render();
            } else {
                openEdit(level, id);
            }
        }
    });

    body.addEventListener('submit', async function (e) {
        var form = e.target.closest('[data-add]');
        if (!form) return;
        e.preventDefault();
        var level = +form.dataset.add;
        // 공휴일은 넣은 날의 연도를 보인다 (다른 해에 넣으면 지금 칩에서는 안 보인다)
        if (kind === 'holiday' && form.elements.date && form.elements.date.value) holidayYear = +form.elements.date.value.slice(0, 4);
        try {
            await change('Save', {
                kind: kind, level: level, id: null,
                parentId: level === 1 ? null : selected[level - 1],
                name: form.elements.name.value,
                date: form.elements.date ? form.elements.date.value : null
            });
        } catch (err) { App.toast(err.message, 'error'); }
    });

    // 다른 화면(서버 상태의 첫 설정 점검표 등)에서 /Admin#holiday 처럼 탭을 정해 들어올 수 있다
    function tabFromHash() {
        var k = location.hash.slice(1);
        return KINDS[k] ? document.querySelector('#admin-tabs [data-kind="' + k + '"]') : null;
    }
    window.addEventListener('hashchange', function () {
        var t = tabFromHash();
        if (t && t.dataset.kind !== kind) t.click();
    });
    var startTab = tabFromHash();
    if (startTab) startTab.click(); else load();
})();
