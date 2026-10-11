// 위치 연쇄 선택(법인 → 제품군 → 라인 → 층) — 시트 안에서 쓰는 작은 판. 등록 화면(edit.js)과 같은 규칙:
// 사용 중지 항목은 지금 값일 때만 보이고, 하위가 하나뿐이면 저절로 고른다. 가장 깊이 고른 항목이 위치다
(function () {
    'use strict';

    var LEVELS = ['법인', '제품군', '라인', '층'];

    function html(name) {
        return '<div class="cascade" data-location-picker="' + name + '">' + LEVELS.map(function (label, i) {
            return '<select data-level="' + (i + 1) + '" aria-label="' + label + '"></select>';
        }).join('') + '</div>';
    }

    // locations: [{id, parentId, level, name, active}], initialId: 처음 고를 위치. 돌려주는 value() 는 고른 가장 깊은 위치 Id (없으면 null)
    function bind(root, locations, initialId) {
        var selects = Array.prototype.slice.call(root.querySelectorAll('select'));
        var byId = {};
        locations.forEach(function (l) { byId[l.id] = l; });

        function chainOf(id) {
            var chain = [];
            var cur = byId[id];
            while (cur) { chain.unshift(cur.id); cur = cur.parentId == null ? null : byId[cur.parentId]; }
            return chain;
        }
        function children(parentId, keepId) {
            return locations.filter(function (l) {
                return (l.parentId || null) === (parentId || null) && (l.active || l.id === keepId);
            });
        }
        function fill(level, parentId, selected) {
            var sel = selects[level - 1];
            var list = level === 1 || parentId ? children(level === 1 ? null : parentId, selected) : [];
            sel.innerHTML = '<option value="">' + LEVELS[level - 1] + (level === 1 ? ' 선택' : ' (선택 안 함)') + '</option>' + list.map(function (l) {
                return '<option value="' + l.id + '"' + (l.id === selected ? ' selected' : '') + '>' + App.esc(l.name) + '</option>';
            }).join('');
            sel.disabled = list.length === 0;
            return list;
        }
        function cascadeFrom(level, parentId) {
            var list = fill(level, parentId, null);
            var sel = selects[level - 1];
            if (list.length === 1 && level > 1) sel.value = String(list[0].id);
            if (level < selects.length) cascadeFrom(level + 1, sel.value ? +sel.value : null);
        }

        var chain = chainOf(initialId);
        var parent = null;
        for (var level = 1; level <= selects.length; level++) {
            var selected = chain[level - 1] || null;
            fill(level, parent, selected);
            parent = selected;
        }
        selects.forEach(function (sel, i) {
            sel.addEventListener('change', function () {
                if (i + 1 < selects.length) cascadeFrom(i + 2, sel.value ? +sel.value : null);
            });
        });
        return {
            value: function () {
                var deepest = null;
                selects.forEach(function (s) { if (s.value) deepest = +s.value; });
                return deepest;
            }
        };
    }

    window.LocationPicker = { html: html, bind: bind };
})();
