// 소속(그룹 → 파트) 선택: 그룹을 고르면 그 아래 파트만 보여 주고, 가장 깊게 고른 것을 hidden 값으로 넣는다
window.OrgPicker = (function () {
    'use strict';

    // orgs: [{id, parentId, level, name}], selected: 현재 소속 id(파트 또는 그룹)
    function bind(orgs, groupSel, partSel, hidden, field, selected) {
        orgs = orgs || [];
        var groups = orgs.filter(function (o) { return o.level === 1; });
        if (field) field.hidden = groups.length === 0;
        if (groups.length === 0) return;

        var current = null;
        for (var i = 0; i < orgs.length; i++) if (orgs[i].id === selected) current = orgs[i];
        var groupId = current ? (current.level === 1 ? current.id : current.parentId) : null;
        var partId = current && current.level === 2 ? current.id : null;

        function options(list, placeholder, value) {
            return '<option value="">' + placeholder + '</option>' + list.map(function (o) {
                return '<option value="' + o.id + '"' + (o.id === value ? ' selected' : '') + '>' + App.esc(o.name) + '</option>';
            }).join('');
        }

        function fillParts(value) {
            var gid = groupSel.value ? +groupSel.value : null;
            var parts = orgs.filter(function (o) { return o.level === 2 && o.parentId === gid; });
            partSel.innerHTML = options(parts, parts.length ? '파트 선택' : '파트 없음', value);
            partSel.disabled = parts.length === 0;
        }

        function sync() { hidden.value = partSel.value || groupSel.value || ''; }

        groupSel.innerHTML = options(groups, '그룹 선택', groupId);
        fillParts(partId);
        sync();
        groupSel.addEventListener('change', function () { fillParts(null); sync(); });
        partSel.addEventListener('change', sync);
    }

    return { bind: bind };
})();
