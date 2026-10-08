// 과제 등록·수정: 위치 연쇄 선택, 유형별 기기/용기 내역
(function () {
    'use strict';

    var meta = App.data();
    var esc = App.esc;
    var LEVELS = ['법인', '제품군', '라인', '층'];

    // ── 위치 연쇄 선택 ──────────────────────────────────────────────
    var selects = Array.prototype.slice.call(document.querySelectorAll('#cascade select'));
    var hidden = document.getElementById('location-id');
    var byId = {};
    meta.locations.forEach(function (l) { byId[l.id] = l; });

    // 현재 값의 상위 경로 [법인, 제품군, …]
    function chainOf(id) {
        var chain = [];
        var cur = byId[id];
        while (cur) { chain.unshift(cur.id); cur = cur.parentId == null ? null : byId[cur.parentId]; }
        return chain;
    }

    function children(parentId, keepId) {
        return meta.locations.filter(function (l) {
            return (l.parentId || null) === (parentId || null) && (l.active || l.id === keepId);
        });
    }

    // 한 단계의 선택 상자를 채운다 (상위가 정해지지 않았으면 비활성)
    function setOptions(level, parentId, selected) {
        var sel = selects[level - 1];
        var list = level === 1 || parentId ? children(level === 1 ? null : parentId, selected) : [];
        var html = '<option value="">' + LEVELS[level - 1] + (level === 1 ? ' 선택' : ' (선택 안 함)') + '</option>';
        list.forEach(function (l) {
            html += '<option value="' + l.id + '"' + (l.id === selected ? ' selected' : '') + '>' + esc(l.name) + '</option>';
        });
        sel.innerHTML = html;
        sel.disabled = list.length === 0;
        return list;
    }

    // 상위 선택이 바뀌면 그 아래 단계를 모두 다시 채운다. 하위가 하나뿐이면 자동 선택
    function cascadeFrom(level, parentId) {
        var list = setOptions(level, parentId, null);
        var sel = selects[level - 1];
        if (list.length === 1 && level > 1) sel.value = String(list[0].id);
        if (level < selects.length) cascadeFrom(level + 1, sel.value ? +sel.value : null);
    }

    function syncHidden() {
        var deepest = 0;
        selects.forEach(function (s) { if (s.value) deepest = +s.value; });
        hidden.value = String(deepest);
    }

    // 처음 열 때: 저장된 위치의 경로대로 채운다
    var chain = chainOf(meta.form.locationId);
    var parent = null;
    for (var level = 1; level <= selects.length; level++) {
        var selected = chain[level - 1] || null;
        setOptions(level, parent, selected);
        parent = selected;
    }
    syncHidden();

    selects.forEach(function (sel, i) {
        sel.addEventListener('change', function () {
            if (i + 1 < selects.length) cascadeFrom(i + 2, sel.value ? +sel.value : null);
            syncHidden();
        });
    });

    // ── 기기/용기 내역 ──────────────────────────────────────────────
    var itemsField = document.getElementById('items-field');
    var itemsEl = document.getElementById('items');
    var itemsLabel = document.getElementById('items-label');
    var kindById = {};
    meta.categories.forEach(function (c) { kindById[c.id] = c.kind; });

    function currentKind() {
        var checked = document.querySelector('input[name="Form.CategoryId"]:checked');
        var id = checked ? +checked.value : meta.form.categoryId;
        return kindById[id] || 'general';
    }

    function typeOptions(kind, selected) {
        var html = '<option value="0">종류 선택</option>';
        if (kind === 'container') {
            meta.containers.forEach(function (c) {
                if (!c.active && c.id !== selected) return;
                html += '<option value="' + c.id + '"' + (c.id === selected ? ' selected' : '') + '>' + esc(c.name) + '</option>';
            });
            return html;
        }
        // 기기: 하위 타입이 있는 대분류는 묶음으로, 없는 대분류는 그대로 선택
        meta.equipment.filter(function (e) { return e.parentId == null; }).forEach(function (top) {
            var subs = meta.equipment.filter(function (e) { return e.parentId === top.id && (e.active || e.id === selected); });
            if (subs.length === 0) {
                if (!top.active && top.id !== selected) return;
                html += '<option value="' + top.id + '"' + (top.id === selected ? ' selected' : '') + '>' + esc(top.name) + '</option>';
            } else {
                html += '<optgroup label="' + esc(top.name) + '">';
                subs.forEach(function (s) {
                    html += '<option value="' + s.id + '"' + (s.id === selected ? ' selected' : '') + '>' + esc(top.name + ' · ' + s.name) + '</option>';
                });
                html += '</optgroup>';
            }
        });
        return html;
    }

    function addRow(kind, item) {
        item = item || { typeId: 0, quantity: '', note: '' };
        var row = document.createElement('div');
        row.className = 'item-row';
        row.innerHTML =
            '<select data-k="TypeId" aria-label="종류">' + typeOptions(kind, item.typeId) + '</select>' +
            '<input data-k="Quantity" type="number" min="1" inputmode="numeric" placeholder="수량" aria-label="수량" value="' + esc(item.quantity || '') + '" />' +
            '<input data-k="Note" class="item-note" type="text" maxlength="200" placeholder="비고" aria-label="비고" value="' + esc(item.note || '') + '" />' +
            '<button type="button" class="btn icon ghost" aria-label="행 삭제">' + App.icon('x') + '</button>';
        row.querySelector('button').addEventListener('click', function () { row.remove(); });
        itemsEl.appendChild(row);
    }

    function renderItems(items) {
        var kind = currentKind();
        itemsField.hidden = kind === 'general';
        itemsField.dataset.field = kind === 'container' ? 'containers' : 'equipments';
        itemsLabel.textContent = kind === 'container' ? '용기 내역' : '기기 내역';
        itemsEl.innerHTML = '';
        if (kind === 'general') return;
        (items && items.length ? items : [null]).forEach(function (it) { addRow(kind, it); });
    }

    document.getElementById('add-item').addEventListener('click', function () { addRow(currentKind()); });
    document.querySelectorAll('input[name="Form.CategoryId"]').forEach(function (r) {
        r.addEventListener('change', function () { renderItems(null); });
    });
    renderItems(meta.form.items);

    // 제출 직전: 빈 행은 빼고 서버가 받는 이름(Form.Items[i].X)을 붙인다
    document.getElementById('edit-form').addEventListener('submit', function () {
        syncHidden();
        var index = 0;
        itemsEl.querySelectorAll('.item-row').forEach(function (row) {
            var type = row.querySelector('[data-k=TypeId]');
            var qty = row.querySelector('[data-k=Quantity]');
            var note = row.querySelector('[data-k=Note]');
            if (type.value === '0' && !qty.value && !note.value) return;
            if (!qty.value) qty.value = '0';
            type.name = 'Form.Items[' + index + '].TypeId';
            qty.name = 'Form.Items[' + index + '].Quantity';
            note.name = 'Form.Items[' + index + '].Note';
            index++;
        });
    });

    // 서버가 알려 준 오류 필드 표시
    if (meta.errorField) {
        var f = document.querySelector('[data-field="' + meta.errorField + '"]');
        if (f) {
            f.classList.add('invalid');
            var input = f.querySelector('input:not([type=hidden]), select, textarea');
            if (input) input.focus();
        }
    }
})();
