// 관리: 기준 정보를 단계별 열(컬럼 탐색) 형태로 조회·편집
(function () {
    'use strict';

    var esc = App.esc, icon = App.icon;
    var body = document.getElementById('admin-body');
    var isAdmin = body.dataset.admin === '1';

    var CATEGORY_KINDS = { general: '일반', automation: '물류 자동화 (기기 내역 입력)', container: '용기 (용기 내역 입력)' };
    var STAGE_KINDS = { normal: '일반', inspection: '검수 (입고 추천일 기준)', inbound: '입고 (추천일 표시)' };

    // 종류별 화면 구성. removable: 사용 중지 대신 바로 삭제하는 단계
    var KINDS = {
        location: { levels: ['법인', '제품군', '라인', '층'] },
        category: { levels: ['유형', '기본 단계'], removable: [2] },
        equipment: { levels: ['대분류', '하위 타입'] },
        container: { levels: ['용기 종류'] },
        vendor: { levels: ['협력사'] },
        user: { levels: ['사용자'], fixedOrder: true },
        holiday: { levels: ['공휴일'], fixedOrder: true, removable: [1] }
    };

    var kind = 'location';
    var nodes = [];
    var selected = {};      // level → 선택한 노드 id

    function url(handler) { return App.url('/Admin?handler=' + handler); }

    function conf() { return KINDS[kind]; }
    function removable(level) { return (conf().removable || []).indexOf(level) >= 0; }

    function childrenOf(level, parentId) {
        return nodes.filter(function (n) { return n.level === level && (level === 1 || n.parentId === parentId); });
    }

    function extraText(n) {
        if (kind === 'location' && n.level === 1) return [n.country, '해상 ' + (n.oceanLeadDays || 0) + '일'].filter(Boolean).join(' · ');
        if (kind === 'category' && n.level === 1) return n.kind === 'automation' ? '기기 내역' : n.kind === 'container' ? '용기 내역' : '';
        if (kind === 'category' && n.level === 2) return n.kind === 'inspection' ? '검수' : n.kind === 'inbound' ? '입고' : '';
        if (kind === 'user') return [n.dept, n.isAdmin ? '관리자' : '', n.ips && n.ips.length ? 'IP ' + n.ips.length : '미접속'].filter(Boolean).join(' · ');
        if (kind === 'holiday') return n.date;
        return '';
    }

    function render() {
        var c = conf();
        var html = '<div class="columns">';
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
                    '<input name="name" maxlength="100" required placeholder="' + c.levels[level - 1] + ' 이름" aria-label="' + c.levels[level - 1] + ' 이름" />' +
                    '<button type="submit" class="btn">' + icon('plus') + '<span class="hide-sm">추가</span></button></form>';
            }
            html += '</div>';
            parentId = selected[level];
        }
        html += '</div>';
        body.innerHTML = html;
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

    async function change(handler, payload) {
        var r = await App.post(url(handler), payload);
        nodes = r.nodes;
        render();
    }

    function find(level, id) {
        return nodes.filter(function (n) { return n.level === level && n.id === id; })[0];
    }

    function field(name, label, inner) {
        return '<label class="field" data-field="' + name + '"><span class="label">' + label + '</span>' + inner + '</label>';
    }

    function options(map, value) {
        return Object.keys(map).map(function (k) {
            return '<option value="' + k + '"' + (k === value ? ' selected' : '') + '>' + map[k] + '</option>';
        }).join('');
    }

    function openEdit(level, id) {
        var n = find(level, id);
        if (!n) return;
        var c = conf();
        var html = field('name', '이름', '<input name="name" maxlength="100" required value="' + esc(n.name) + '"' + (isAdmin ? '' : ' disabled') + ' />');
        if (kind === 'location' && level === 1) {
            html += field('country', '국가', '<input name="country" maxlength="50" value="' + esc(n.country || '') + '" />') +
                field('oceanLeadDays', '해상 운송 소요일', '<input name="oceanLeadDays" type="number" min="0" max="365" inputmode="numeric" value="' + (n.oceanLeadDays || 0) + '" />') +
                '<span class="hint">입고 단계의 추천일(검수일 + 소요일) 계산에 사용합니다. 국내는 0.</span>';
        }
        if (kind === 'category' && level === 1) html += field('subKind', '내역 입력', '<select name="subKind">' + options(CATEGORY_KINDS, n.kind) + '</select>');
        if (kind === 'category' && level === 2) html += field('subKind', '단계 성격', '<select name="subKind">' + options(STAGE_KINDS, n.kind) + '</select>');
        if (kind === 'holiday') html += field('date', '날짜', '<input name="date" type="date" required value="' + n.date + '" />');
        if (kind === 'user') {
            html += field('dept', '부서', '<input name="dept" maxlength="50" value="' + esc(n.dept || '') + '" />') +
                '<label class="check"><input type="checkbox" name="isAdmin"' + (n.isAdmin ? ' checked' : '') + ' />관리자 (기준 정보 변경·과제 삭제)</label>' +
                '<div class="field"><span class="label">접속 IP</span><div class="readonly">' + (n.ips && n.ips.length ? esc(n.ips.join(', ')) : '접속 이력 없음') + '</div></div>';
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
                        await change('Toggle', { kind: kind, level: level, id: id, dir: 0 });
                        sheet.close();
                        App.toast(isRemovable ? '삭제했습니다.' : (n.active ? '사용 중지했습니다. 기존 과제에는 그대로 표시됩니다.' : '다시 사용합니다.'));
                    } catch (err) { sheet.setError(err.message, err.field); }
                }
            },
            onSubmit: async function (form) {
                var f = form.elements;
                await change('Save', {
                    kind: kind, level: level, id: id, parentId: n.parentId,
                    name: f.name.value,
                    country: f.country ? f.country.value : null,
                    oceanLeadDays: f.oceanLeadDays ? +f.oceanLeadDays.value : null,
                    subKind: f.subKind ? f.subKind.value : null,
                    dept: f.dept ? f.dept.value : null,
                    isAdmin: f.isAdmin ? f.isAdmin.checked : null,
                    date: f.date ? f.date.value : null
                });
                App.toast('저장했습니다.');
            }
        });
    }

    // ── 이벤트 ──────────────────────────────────────────────────────
    document.querySelectorAll('#admin-tabs [data-kind]').forEach(function (b) {
        b.addEventListener('click', function () {
            kind = b.dataset.kind;
            selected = {};
            document.querySelectorAll('#admin-tabs [data-kind]').forEach(function (x) {
                x.classList.toggle('on', x === b);
                x.setAttribute('aria-selected', x === b ? 'true' : 'false');
            });
            load();
        });
    });

    body.addEventListener('click', async function (e) {
        var t;
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
        try {
            await change('Save', {
                kind: kind, level: level, id: null,
                parentId: level === 1 ? null : selected[level - 1],
                name: form.elements.name.value,
                date: form.elements.date ? form.elements.date.value : null
            });
        } catch (err) { App.toast(err.message, 'error'); }
    });

    load();
})();
