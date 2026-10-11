// 화면 점검 스크립트: 실행 중인 앱을 데스크톱·모바일 크기로 돌며 주요 흐름을 눌러 보고 화면을 캡처한다.
//   BASE_URL  대상 주소 (기본 http://localhost:5080) — 샘플 데이터(Seed:Demo)로 막 띄운 앱이어야 한다
//   OUT_DIR   결과 폴더 (기본 e2e/out) — screens/*.png, exports/*(내보낸 Excel·PDF), report.md
//   PW_CHANNEL=chrome  설치된 Chrome 사용 (CI). 지정하지 않으면 Playwright 의 Chromium
//   FAKE_EXPORT=1      내보내기 파일의 형식 검사를 건너뛴다 (가짜 내보내기를 쓰는 로컬 섀도 빌드용)
//   ONLY=<문자열>      이름에 이 문자열이 든 단계만 돈다 ('|' 로 여러 개). 등록 단계는 다른 단계의 전제라 항상 돈다.
//                      단계끼리 상태를 이어 쓰므로(샘플 과제를 고친 결과 등) 앞 단계를 건너뛰면 실패할 수 있다
//   APP_LOG=<파일>     앱 로그. 경고·오류 줄을 모아 보고서(report.md)에 요약한다 (CI)
// 단계가 실패하면 그때의 화면(screens/fail-NN.png), 주소, 화면에 보인 오류 문구를 보고서에 남긴다
const fs = require('fs');
const path = require('path');

let chromium;
try { ({ chromium } = require('playwright')); } catch (e) { ({ chromium } = require('playwright-core')); }

const BASE = (process.env.BASE_URL || 'http://localhost:5080').replace(/\/$/, '');
const OUT = process.env.OUT_DIR || path.join(__dirname, 'out');
const SHOTS = path.join(OUT, 'screens');
const EXPORTS = path.join(OUT, 'exports');
fs.mkdirSync(SHOTS, { recursive: true });
fs.mkdirSync(EXPORTS, { recursive: true });

const results = [];      // { name, ok, error, skipped, url, screen, texts }
const problems = [];     // 콘솔 오류·실패한 요청
// 일부러 404 를 받는 요청 (없는 과제 화면, 삭제된 과제의 변경 확인)
const expected404 = ['/Projects/Detail?id=999999', 'handler=Stamp'];
// 일부러 흉내 내는 서버 오류 (오류 번호 안내 점검 — 응답을 가로채 500 을 돌려준다)
const expected5xx = ['/__e2e-error'];
const ONLY = (process.env.ONLY || '').split('|').map((x) => x.trim()).filter(Boolean);
// ONLY 와 관계없이 도는 단계 (이름 앞부분). 이름 등록이 없으면 다른 화면이 열리지 않는다
const SETUP = ['처음 접속하면', '이름·소속'];
let active = null;       // 지금 단계가 쓰는 화면 (실패 캡처용). 다른 화면(동시 수정용 두 번째 화면 등)을 쓰는 동안은 그 화면
let home = null;         // 단계가 끝나면 돌아갈 기본 화면 (데스크톱·모바일)

function kstToday(offset = 0) {
    return new Date(Date.now() + 9 * 3600e3 + offset * 86400e3).toISOString().slice(0, 10);
}

async function step(name, fn) {
    if (ONLY.length && !SETUP.some((x) => name.startsWith(x)) && !ONLY.some((x) => name.includes(x))) {
        results.push({ name, skipped: true });
        console.log('  skip ' + name);
        return;
    }
    try {
        await fn();
        results.push({ name, ok: true });
        console.log('  ok   ' + name);
    } catch (err) {
        const r = { name, ok: false, error: String(err && err.message || err).split('\n')[0] };
        await capture(r);
        results.push(r);
        console.log('  FAIL ' + name + ' — ' + r.error + (r.url ? ' (' + r.url + ')' : ''));
    } finally {
        active = home;
    }
}

// 실패한 단계의 화면·주소·화면에 보인 오류 문구 (원인을 다시 돌리지 않고 보고서에서 볼 수 있게)
async function capture(r) {
    if (!active) return;
    const n = String(results.length + 1).padStart(2, '0');
    try { r.url = active.url(); } catch (e) { /* 닫힌 화면 */ }
    try {
        r.texts = await active.evaluate(() => Array.from(document.querySelectorAll(
            '.form-error:not([hidden]), .form-warn:not([hidden]), .toast, .notice, .field.invalid .hint'))
            .map((el) => el.innerText.trim().replace(/\s+/g, ' ')).filter(Boolean).slice(0, 5));
    } catch (e) { /* 이동 중 */ }
    try {
        await active.screenshot({ path: path.join(SHOTS, 'fail-' + n + '.png') });
        r.screen = 'screens/fail-' + n + '.png';
    } catch (e) { /* 캡처 실패는 무시 */ }
}

// 앱 로그의 경고·오류를 종류별로 센다. ASP.NET 콘솔 로그는 "warn: 범주[번호]" 다음 줄에 내용이 온다
function appLogSummary(file) {
    let lines;
    try { lines = fs.readFileSync(file, 'utf8').split(/\r?\n/); } catch (e) { return ['- 앱 로그를 읽지 못했습니다: ' + file]; }
    const counts = new Map();
    for (let i = 0; i < lines.length; i++) {
        const m = /^(warn|fail|crit): ([^\[]+)\[\d+\]/.exec(lines[i]);
        if (!m) continue;
        const key = m[1] + ' ' + m[2] + ' — ' + (lines[i + 1] || '').trim().slice(0, 160);
        counts.set(key, (counts.get(key) || 0) + 1);
    }
    return [...counts].map(([k, n]) => '- ' + n + '× `' + k.replace(/`/g, "'") + '`');
}

function watch(page, label) {
    // 리소스 로드 실패는 아래 response 에서 주소와 함께 잡으므로 콘솔의 같은 메시지는 건너뛴다
    page.on('console', (m) => {
        if (m.type() === 'error' && !m.text().includes('Failed to load resource')) problems.push(`[${label}] console: ${m.text()}`);
    });
    page.on('pageerror', (e) => problems.push(`[${label}] pageerror: ${e.message}`));
    page.on('response', (r) => {
        // 400·409 는 규칙 위반·동시 수정 응답으로 일부러 유발하는 단계가 있다. 그 밖의 404·500 대는 문제로 본다
        if (r.status() === 404 && expected404.some((u) => r.url().includes(u))) return;
        if (r.status() >= 500 && expected5xx.some((u) => r.url().includes(u))) return;
        if (r.status() >= 500 || r.status() === 404) problems.push(`[${label}] ${r.status()} ${r.request().method()} ${r.url()}`);
    });
}

async function shot(page, name, full = true) {
    await page.waitForTimeout(150);
    await page.screenshot({ path: path.join(SHOTS, name + '.png'), fullPage: full });
}

function assert(cond, message) { if (!cond) throw new Error(message); }

const SHEET = 'dialog.sheet[open]';

async function openProject(page, title) {
    await page.goto(BASE + '/Projects?state=all');
    await page.locator('.ptable-row', { hasText: title }).first().click();
    await page.waitForSelector('#summary .summary-meta');
}

// 슬라이더(range)는 값을 넣고 input 이벤트를 직접 보낸다
async function setRange(page, selector, value) {
    await page.locator(selector).evaluate((el, v) => {
        el.value = v;
        el.dispatchEvent(new Event('input', { bubbles: true }));
    }, String(value));
}

async function submitSheet(page) {
    await page.locator(SHEET + ' button[type=submit]').click();
    await page.waitForSelector(SHEET, { state: 'detached', timeout: 10000 });
}

async function closeSheet(page) {
    await page.locator(SHEET + ' [data-close]').first().click();
    await page.waitForSelector(SHEET, { state: 'detached' });
}

// 입력을 바꾼 시트를 닫는다: 닫기 → '작성 중인 내용을 버릴까요?' → 버리기
async function discardSheet(page) {
    await page.locator(SHEET + ' [data-close]').first().click();
    await page.locator('dialog[open] [data-yes]', { hasText: '버리기' }).click();
    await page.waitForSelector(SHEET, { state: 'detached' });
}

// 브라우저 현지 시각 표시(App.time)와 같은 형식을 주어진 시간대로 만든다
function localTime(iso, timeZone) {
    const fmt = (d) => Object.fromEntries(new Intl.DateTimeFormat('en-US', {
        timeZone, year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hourCycle: 'h23',
    }).formatToParts(d).map((x) => [x.type, x.value]));
    const t = fmt(new Date(iso));
    const thisYear = fmt(new Date()).year;
    return (t.year === thisYear ? '' : t.year + '-') + `${t.month}-${t.day} ${t.hour}:${t.minute}`;
}

function localDate(timeZone) {
    return new Intl.DateTimeFormat('en-CA', { timeZone, year: 'numeric', month: '2-digit', day: '2-digit' }).format(new Date());
}

async function sheetError(page) {
    await page.waitForSelector(SHEET + ' .form-error:not([hidden])');
    return (await page.locator(SHEET + ' .sheet-body > .form-error').innerText()).trim();
}

// 업데이트 화면: '지금 확인' 응답에 "적용한 새 버전이 시작되지 않아 되돌린" 상태를 덧붙인다.
// 실제로 서버를 죽였다 살리는 흐름은 tools/release/selftest.sh 가 확인하고, 여기서는 그 상태의 화면만 본다
async function mockStartFailure(p) {
    await p.route('**/Admin/Update?handler=Check', async (route) => {
        const res = await route.fetch();
        const body = await res.json();
        const at = new Date().toISOString();
        body.status.staged = { version: '99.0.1', at, origin: 'download', by: '자동', sha256: 'a'.repeat(64), notes: '', schemaChange: false };
        body.status.skipAuto = '99.0.1';
        body.status.startFailure = {
            version: '99.0.1', from: body.status.version, rolledBack: true, at, retry: 'apply',
            error: '새 버전(v99.0.1)이 시작되지 않아(시작하다 멈춤) 이전 버전(v' + body.status.version + ')으로 자동으로 되돌렸습니다.',
            detail: '2026-10-11 02:10:00\nSystem.IO.InvalidDataException: Failed to load configuration from file \'C:\\GanttScheduler\\appsettings.json\'.\n ---> System.FormatException: Could not parse the JSON file.',
        };
        await route.fulfill({ response: res, json: body });
    });
}

const stageRow = (page, name) => page.locator('#gantt .g-row.stage', { hasText: name }).locator('.g-name');
const workRow = (page, name) => page.locator('#gantt .g-row.work', { hasText: name });

// 지금 연 상세 화면의 과제 JSON (서버가 계산한 값 — 미리보기와 저장 결과를 비교할 때)
async function projectData(p) {
    return p.evaluate(async () => {
        const id = new URLSearchParams(location.search).get('id');
        return (await App.get(App.url('/Projects/Detail?handler=Data&id=' + id))).project;
    });
}
const worksOf = (P) => P.stages.flatMap((s) => s.works);

// ── 가져오기 양식 채우기 ─────────────────────────────────────────
// 내려받은 양식(xlsx = zip)의 시트에 줄을 채워 다시 묶는다. 외부 라이브러리 없이 zip 을 풀고(STORE·DEFLATE) STORE 로 다시 묶는다.
// 섀도 빌드(FAKE_EXPORT)의 양식은 '# 시트명' 줄과 TAB 칸의 글자라 그 형식에 줄을 붙인다
const zlib = require('zlib');
const CRC_TABLE = (() => {
    const t = new Uint32Array(256);
    for (let n = 0; n < 256; n++) { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xEDB88320 ^ (c >>> 1) : c >>> 1; t[n] = c >>> 0; }
    return t;
})();
function crc32(buf) {
    let c = 0xFFFFFFFF;
    for (let i = 0; i < buf.length; i++) c = CRC_TABLE[(c ^ buf[i]) & 0xFF] ^ (c >>> 8);
    return (c ^ 0xFFFFFFFF) >>> 0;
}
function unzip(buf) {
    let eocd = buf.length - 22;
    while (eocd >= 0 && buf.readUInt32LE(eocd) !== 0x06054b50) eocd--;
    if (eocd < 0) throw new Error('zip 이 아님');
    const count = buf.readUInt16LE(eocd + 10);
    let p = buf.readUInt32LE(eocd + 16);
    const files = [];
    for (let i = 0; i < count; i++) {
        const method = buf.readUInt16LE(p + 10), size = buf.readUInt32LE(p + 20);
        const nameLen = buf.readUInt16LE(p + 28), extraLen = buf.readUInt16LE(p + 30), commentLen = buf.readUInt16LE(p + 32);
        const local = buf.readUInt32LE(p + 42);
        const name = buf.subarray(p + 46, p + 46 + nameLen).toString('utf8');
        const start = local + 30 + buf.readUInt16LE(local + 26) + buf.readUInt16LE(local + 28);
        const raw = buf.subarray(start, start + size);
        files.push({ name, data: method === 8 ? zlib.inflateRawSync(raw) : Buffer.from(raw) });
        p += 46 + nameLen + extraLen + commentLen;
    }
    return files;
}
function zip(files) {
    const locals = [], centrals = [];
    let offset = 0;
    for (const f of files) {
        const name = Buffer.from(f.name, 'utf8'), crc = crc32(f.data);
        const h = Buffer.alloc(30);
        h.writeUInt32LE(0x04034b50, 0); h.writeUInt16LE(20, 4); h.writeUInt16LE(0x0800, 6); h.writeUInt16LE(0, 8);
        h.writeUInt32LE(crc, 14); h.writeUInt32LE(f.data.length, 18); h.writeUInt32LE(f.data.length, 22); h.writeUInt16LE(name.length, 26);
        const c = Buffer.alloc(46);
        c.writeUInt32LE(0x02014b50, 0); c.writeUInt16LE(20, 4); c.writeUInt16LE(20, 6); c.writeUInt16LE(0x0800, 8);
        c.writeUInt32LE(crc, 16); c.writeUInt32LE(f.data.length, 20); c.writeUInt32LE(f.data.length, 24); c.writeUInt16LE(name.length, 28);
        c.writeUInt32LE(offset, 42);
        locals.push(h, name, f.data);
        centrals.push(c, name);
        offset += 30 + name.length + f.data.length;
    }
    const central = Buffer.concat(centrals);
    const end = Buffer.alloc(22);
    end.writeUInt32LE(0x06054b50, 0); end.writeUInt16LE(files.length, 8); end.writeUInt16LE(files.length, 10);
    end.writeUInt32LE(central.length, 12); end.writeUInt32LE(offset, 16);
    return Buffer.concat([...locals, central, end]);
}
const xmlEsc = (s) => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
function colName(i) { let s = ''; for (i++; i > 0; i = Math.floor((i - 1) / 26)) s = String.fromCharCode(65 + (i - 1) % 26) + s; return s; }
// sheets: { '과제': [[칸, …], …], '작업': [...] } — 2행부터 채운다(1행은 양식 머리글)
function fillTemplate(template, sheets) {
    if (template.subarray(0, 2).toString('latin1') !== 'PK') {
        // 섀도 빌드의 글자 양식
        let text = template.toString('utf8');
        for (const [name, rows] of Object.entries(sheets)) {
            const lines = rows.map((r) => r.join('\t')).join('\n');
            text = text.replace(new RegExp('(# ' + name + '\\n[^\\n]*\\n)'), '$1' + lines + '\n');
        }
        return Buffer.from(text, 'utf8');
    }
    const files = unzip(template);
    const get = (n) => files.find((f) => f.name === n);
    const workbook = get('xl/workbook.xml').data.toString('utf8');
    const rels = get('xl/_rels/workbook.xml.rels').data.toString('utf8');
    for (const [name, rows] of Object.entries(sheets)) {
        // ClosedXML 은 'x:' 같은 이름공간 접두사를 붙여 쓴다 — 접두사가 있든 없든 찾는다
        const rid = new RegExp('<(?:\\w+:)?sheet [^>]*name="' + name + '"[^>]*r:id="([^"]+)"').exec(workbook)[1];
        const rel = (rels.match(/<(?:\w+:)?Relationship [^>]*>/g) || []).find((r) => r.includes('Id="' + rid + '"'));
        let target = /Target="([^"]+)"/.exec(rel)[1].replace(/^\//, '');
        if (!target.startsWith('xl/')) target = 'xl/' + target;
        const f = get(target);
        let xml = f.data.toString('utf8');
        const ns = (/<(\w+:)?sheetData/.exec(xml)[1]) || '';
        // 서식만 걸린 빈 줄(2행부터)은 지우고 채운 줄을 넣는다
        xml = xml.replace(/<(\w+:)?row r="(\d+)"[^>]*?(?:\/>|>[\s\S]*?<\/(?:\w+:)?row>)/g, (m, p, r) => (+r >= 2 ? '' : m));
        const body = rows.map((cells, i) => '<' + ns + 'row r="' + (i + 2) + '">' + cells.map((v, j) => v === '' ? '' :
            '<' + ns + 'c r="' + colName(j) + (i + 2) + '" t="inlineStr"><' + ns + 'is><' + ns + 't xml:space="preserve">' + xmlEsc(v) + '</' + ns + 't></' + ns + 'is></' + ns + 'c>').join('') +
            '</' + ns + 'row>').join('');
        xml = xml.replace('<' + ns + 'sheetData />', '<' + ns + 'sheetData></' + ns + 'sheetData>').replace('<' + ns + 'sheetData/>', '<' + ns + 'sheetData></' + ns + 'sheetData>')
            .replace('</' + ns + 'sheetData>', body + '</' + ns + 'sheetData>');
        f.data = Buffer.from(xml, 'utf8');
    }
    return zip(files);
}

(async () => {
    const browser = await chromium.launch(process.env.PW_CHANNEL ? { channel: process.env.PW_CHANNEL } : {});
    // 작업 시트의 '작업 정보·일정·선행·메모'는 기본으로 접혀 있고 펼침을 기억한다(App.pref). 여러 단계가 그 칸을 직접 고치므로
    // 모든 화면을 펼친 것으로 시작한다(처음 저장소 값). 접힌 기본 모양은 '작업 시트 빠른 갱신' 단계가 따로 확인한다.
    // (addInitScript 는 이 환경에서 route.fetch 를 멈추게 해 쓰지 않는다)
    const plainContext = browser.newContext.bind(browser);
    browser.newContext = (opts) => plainContext(Object.assign({
        storageState: { cookies: [], origins: [{ origin: new URL(BASE).origin, localStorage: [{ name: 'gs.detail.workMore', value: '1' }] }] },
    }, opts));

    // ── 데스크톱 ────────────────────────────────────────────────────
    const desktop = await browser.newContext({ viewport: { width: 1440, height: 900 }, locale: 'ko-KR', timezoneId: 'Asia/Seoul' });
    const page = await desktop.newPage();
    home = active = page;
    watch(page, 'desktop');
    page.setDefaultTimeout(10000);

    console.log('desktop');
    await step('처음 접속하면 이름 등록 화면으로 이동', async () => {
        await page.goto(BASE + '/');
        assert(page.url().includes('/Welcome'), '등록 화면으로 이동하지 않음: ' + page.url());
    });

    await step('이름·소속(그룹 → 파트) 등록 후 대시보드 표시', async () => {
        await page.fill('input[name=Name]', '평영');
        await page.selectOption('#org-group', { label: '물류기술그룹' });
        await page.selectOption('#org-part', { label: '자동화파트' });
        await shot(page, 'd00-welcome');
        await page.click('button[type=submit]');
        await page.waitForSelector('.kpis');
        assert((await page.locator('#gantt .g-row').count()) >= 5, '간트 행이 5개 미만');
        assert((await page.locator('.kpi.alarm .kpi-value').innerText()).trim() === '2', '지연 KPI 가 2 가 아님');
        assert((await page.locator('.top .me').getAttribute('title')).includes('물류기술그룹 · 자동화파트'), '소속이 저장되지 않음');
        await shot(page, 'd01-dashboard');
    });

    await step('대시보드 간트 일 단위 배율', async () => {
        await page.click('#zoom [data-zoom=day]');
        assert((await page.locator('#gantt .g-tick.today').count()) === 1, '오늘 눈금이 없음');
        await page.click('#zoom [data-zoom=week]');
    });

    await step('내 이름 → 사용자 바꾸기 화면, 보안 헤더', async () => {
        const res = await page.goto(BASE + '/Projects');
        const h = res.headers();
        assert(h['x-content-type-options'] === 'nosniff' && h['x-frame-options'] === 'SAMEORIGIN' && h['referrer-policy'] === 'same-origin',
            '보안 헤더가 없음: ' + JSON.stringify(h));
        await page.click('.top .me');
        await page.waitForURL(/\/Welcome\?switch=1/);
        const card = await page.locator('.welcome-card').innerText();
        assert(card.includes('사용자 바꾸기') && card.includes('평영'), '지금 사용자가 표시되지 않음: ' + card);
        assert(card.includes('동명이인'), '기존 이름 연결 안내가 없음');
        // 관리자에 연결된 기기를 다른 이름으로 옮기면 돌아올 수 없다는 안내와 확인란
        assert(card.includes('다시 관리자로 돌아올 수 없습니다'), '관리자 기기 안내가 없음: ' + card);
        assert(!(await page.isChecked('#leave-admin input[name=LeaveAdmin]')), '확인란이 기본으로 켜져 있음');
        await shot(page, 'd01b-switch-user');
        // 취소하면 원래 화면으로
        await page.locator('.welcome-card a.btn', { hasText: '취소' }).click();
        await page.waitForURL(/\/Projects$/);
        // 이 기기에 이미 연결된 자기 이름(관리자)은 다시 골라도 된다
        await page.click('.top .me');
        await page.fill('input[name=Name]', '평영');
        await page.click('button[type=submit]');
        await page.waitForURL(/\/Projects$/);
        assert((await page.locator('.top .me').innerText()).includes('평영'), '사용자가 바뀜');
    });

    await step('과제 목록·필터', async () => {
        await page.goto(BASE + '/Projects');
        assert((await page.locator('.ptable-row').count()) === 6, '진행 과제가 6건이 아님');
        await shot(page, 'd02-projects');
        await page.locator('.state-tab', { hasText: '지연' }).click();
        await page.waitForURL(/state=delayed/);
        assert((await page.locator('.ptable-row').count()) === 2, '지연 과제가 2건이 아님');
        await page.goto(BASE + '/Projects?state=all&q=' + encodeURIComponent('베트남'));
        assert((await page.locator('.ptable-row').count()) === 2, '검색 결과가 2건이 아님');
    });

    await step('대시보드 숫자·막대 — 누르면 같은 판정의 목록(7일 내 목표·올해 완료·유형), 조건 안내와 해제', async () => {
        await page.goto(BASE + '/');
        const due = +(await page.locator('[data-kpi=due] .kpi-value').innerText());
        await page.click('[data-kpi=due]');
        await page.waitForURL(/state=active&due=7/);
        assert((await page.locator('[data-special-note]').innerText()).includes('단계 목표일 7일 안'), '대시보드 조건 안내가 없음');
        const rows = await page.locator('.ptable-row').count();
        assert(rows === due, `7일 내 목표 ${due}건인데 목록 ${rows}건`);
        await shot(page, 'd02b-projects-due');
        // 상태 탭을 바꾸면 대시보드 조건은 풀린다
        await page.locator('.state-tab', { hasText: '전체' }).click();
        await page.waitForURL((u) => /state=all/.test(u.toString()) && !/due=/.test(u.toString()));
        assert((await page.locator('[data-special-note]').count()) === 0, '상태를 바꿨는데 대시보드 조건이 남음');

        await page.goto(BASE + '/');
        const done = +(await page.locator('[data-kpi=done] .kpi-value').innerText());
        await page.click('[data-kpi=done]');
        await page.waitForURL(/state=done&doneYear=\d{4}/);
        assert((await page.locator('.ptable-row').count()) === done, '올해 완료 KPI 와 목록 건수가 다름');

        await page.goto(BASE + '/');
        const bar = page.locator('[data-bar-category]').first();
        const total = +(await bar.locator('.bar-num').innerText());
        await bar.click();
        await page.waitForURL(/state=all&current=true&category=\d+/);
        assert((await page.locator('.ptable-row').count()) === total, '유형 막대 건수와 목록 건수가 다름');
        await page.goto(BASE + '/');
        const corp = page.locator('[data-bar-corp]').first();
        const corpTotal = +(await corp.locator('.bar-num').innerText());
        await corp.click();
        await page.waitForURL(/state=all&current=true&corp=\d+/);
        assert((await page.locator('.ptable-row').count()) === corpTotal, '법인 막대 건수와 목록 건수가 다름');
    });

    await step('과제 상세 간트 — 기준선·의존선 표시', async () => {
        await openProject(page, '광주 냉장고 3라인 AGV 도입');
        assert((await page.locator('#gantt .g-row.stage').count()) === 8, '단계 행이 8개가 아님');
        assert((await page.locator('#gantt .g-row.work').count()) === 3, '작업 행이 3개가 아님');
        assert((await page.locator('#gantt .g-links svg > path').count()) >= 1, '의존선이 없음');
        assert((await page.locator('#gantt .g-bar.base').count()) >= 3, '작업 기준선 막대가 없음');
        assert((await page.locator('#summary .summary-chips').innerText()).includes('기준선'), '기준선 표시가 없음');
        await shot(page, 'd03-detail-gantt');
    });

    await step('접근성 — 간트 이름 칸은 단추(키보드로 열기), 첫 포커스는 첫 입력칸, 요약은 aria-live 없음', async () => {
        assert((await page.locator('#summary').getAttribute('aria-live')) === null, '요약에 aria-live 가 남아 있음');
        const btn = page.locator('#gantt .g-row.work .g-name-btn').first();
        await btn.focus();
        await page.keyboard.press('Enter');
        await page.waitForSelector(SHEET);
        // 작업 시트의 맨 위는 '빠른 갱신'(진행률 칩·오늘 시작/완료·진행 내용) — 첫 입력칸인 진행 내용에 포커스
        assert(await page.evaluate(() => !!document.activeElement && document.activeElement.matches('[data-quick-note]')), '첫 포커스가 빠른 갱신의 진행 내용이 아님');
        await page.keyboard.press('Escape');      // 바꾼 것이 없으면 묻지 않고 닫힌다
        await page.waitForSelector(SHEET, { state: 'detached' });
    });

    await step('체크리스트 — 남은 항목이 있으면 단계 완료 거절, 체크·추가·삭제', async () => {
        await stageRow(page, '검수').click();
        await page.waitForSelector(SHEET + ' [data-checks] li');
        await page.click(SHEET + ' [data-done-today]');
        await page.locator(SHEET + ' button[type=submit]').click();
        assert((await sheetError(page)).includes('체크리스트 2개'), '체크리스트 미완료 거절 메시지가 아님');
        await shot(page, 'd04-stage-sheet-checklist', false);
        await page.click(SHEET + ' [data-clear-done]');

        const boxes = page.locator(SHEET + ' [data-checks] input[type=checkbox]');
        for (let i = 1; i < 3; i++) {
            await boxes.nth(i).check();
            await page.waitForFunction((n) => document.querySelector('dialog.sheet[open] [data-check-count]').textContent === n, (i + 1) + '/3');
        }
        await page.fill(SHEET + ' [data-new-check]', '현장 사진 첨부');
        await page.click(SHEET + ' [data-add-check]');
        await page.waitForFunction(() => document.querySelector('dialog.sheet[open] [data-check-count]').textContent === '3/4');
        // 문구 고치기: 그 줄이 입력칸이 되고 Enter 로 저장 ('옛 → 새' 이력)
        await page.locator(SHEET + ' [data-checks] li', { hasText: '현장 사진 첨부' }).locator('[data-edit-check]').click();
        await page.fill(SHEET + ' [data-edit-text]', '현장 사진 첨부(전·후)');
        await shot(page, 'd04b-check-edit', false);
        await page.press(SHEET + ' [data-edit-text]', 'Enter');
        await page.waitForFunction(() => document.querySelector('dialog.sheet[open] [data-checks]').innerText.includes('현장 사진 첨부(전·후)'));
        assert(await page.locator(SHEET + ' .sheet-body > .form-error').isHidden(), '체크 문구 수정이 오류');
        assert((await projectData(page)).activities.some((a) => a.text.includes('체크 항목 수정: 현장 사진 첨부 → 현장 사진 첨부(전·후)')), '체크 문구 수정 이력이 없음');
        await page.locator(SHEET + ' [data-checks] li', { hasText: '현장 사진 첨부' }).locator('[data-del-check]').click();
        await page.waitForFunction(() => document.querySelector('dialog.sheet[open] [data-check-count]').textContent === '3/3');
        await closeSheet(page);
        assert((await page.locator('#stepper .step').nth(4).innerText()).includes('체크 3/3'), '단계 표시에 체크 수가 없음');
    });

    await step('단계 목표일 변경 — 사유 없이는 거절, 사유 입력 후 근무일 기준 연쇄 이동', async () => {
        const before = await page.locator('#stepper .step').allInnerTexts();
        await stageRow(page, '검수').click();
        await page.waitForSelector(SHEET);
        await page.fill(SHEET + ' input[name=targetDate]', kstToday(12));
        await page.locator(SHEET + ' button[type=submit]').click();
        assert((await sheetError(page)).includes('사유'), '사유 요구 메시지가 아님');
        await page.fill(SHEET + ' textarea[name=reason]', '주행 시험 재실시로 일주일 순연');
        await page.check(SHEET + ' input[name=cascade]');
        await submitSheet(page);
        const steps = await page.locator('#stepper .step').allInnerTexts();
        assert(steps[4].includes(kstToday(12).slice(5)), '검수 목표일이 바뀌지 않음: ' + steps[4]);
        assert(steps[5] !== before[5] && steps[6] !== before[6], '이후 단계가 연쇄 이동하지 않음');
        assert(steps[4].includes('기준선 +7일'), '기준선 대비 표시가 없음: ' + steps[4]);
    });

    await step('작업 추가·진행률 수정', async () => {
        await page.click('#add-work');
        await page.waitForSelector(SHEET);
        await page.fill(SHEET + ' input[name=title]', '안전 인증 서류 준비');
        await page.fill(SHEET + ' input[name=coOwner]', '신규담당');
        // 목록에 없는 이름은 새 담당자로 등록된다고 알린다 (막지는 않는다)
        assert(await page.locator(SHEET + ' [data-field=coOwner] [data-name-hint]').isVisible(), '새 담당자 안내가 없음');
        assert(!(await page.locator(SHEET + ' [data-field=owner] [data-name-hint]').isVisible()), '등록된 담당자에 새 담당자 안내가 뜸');
        await shot(page, 'd05-work-sheet-new', false);
        await submitSheet(page);
        assert((await page.locator('#gantt .g-row.work').count()) === 4, '작업이 추가되지 않음');

        await workRow(page, '주행 시험').locator('.g-name').click();
        await page.waitForSelector(SHEET);
        await setRange(page, SHEET + ' input[name=progress]', 70);
        await page.fill(SHEET + ' [data-comment]', '곡선 구간 속도 재확인 완료');
        await page.click(SHEET + ' [data-add-comment]');
        await page.waitForFunction(() => document.querySelector('dialog.sheet[open] [data-feed]').innerText.includes('곡선 구간 속도 재확인 완료'));
        await submitSheet(page);
        const row = await workRow(page, '주행 시험').innerText();
        assert(row.includes('70%'), '진행률이 70% 로 바뀌지 않음: ' + row);
    });

    await step('편집 창 닫기 — 입력을 바꾸면 Esc·바깥 클릭에 버릴지 묻고, 글자를 끌어 선택해도 닫히지 않음', async () => {
        await workRow(page, '주행 시험').locator('.g-name').click();
        await page.waitForSelector(SHEET);
        await page.fill(SHEET + ' textarea[name=note]', '현장 확인 메모');
        await page.keyboard.press('Escape');
        await page.waitForSelector('dialog[open] [data-yes]');
        assert((await page.locator('dialog[open]', { has: page.locator('[data-yes]') }).innerText()).includes('버릴까요'), '버릴지 묻는 창이 아님');
        await shot(page, 'd34-sheet-discard-confirm', false);
        await page.click('dialog[open] [data-no]');
        assert((await page.inputValue(SHEET + ' textarea[name=note]')) === '현장 확인 메모', '취소했는데 입력이 사라짐');
        // 입력칸에서 글자를 끌어 선택하다 바깥에서 손을 떼도 닫히지 않는다
        const box = await page.locator(SHEET + ' textarea[name=note]').boundingBox();
        await page.mouse.move(box.x + 8, box.y + 8);
        await page.mouse.down();
        await page.mouse.move(6, 6, { steps: 5 });
        await page.mouse.up();
        await page.waitForTimeout(200);
        assert((await page.locator('dialog[open]').count()) === 1, '끌어 선택하다 바깥에서 떼면 닫히거나 묻는 창이 뜸');
        // 바깥을 누르면 묻고, 버리면 닫힌다
        await page.mouse.click(6, 6);
        await page.locator('dialog[open] [data-yes]', { hasText: '버리기' }).click();
        await page.waitForSelector(SHEET, { state: 'detached' });
        assert(!(await workRow(page, '주행 시험').innerText()).includes('현장 확인 메모'), '버린 내용이 저장됨');
    });

    await step('작성 중인 진행 내용 — 화면을 새로 고쳐도 다시 열면 복원 제안, 등록하면 지움', async () => {
        await workRow(page, '주행 시험').locator('.g-name').click();
        await page.waitForSelector(SHEET);
        await page.fill(SHEET + ' [data-comment]', '센서 교정값 확인 중');
        await page.waitForTimeout(500);     // 0.3초 뒤 임시 저장
        await page.reload();
        await page.waitForSelector('#summary .summary-meta');
        await workRow(page, '주행 시험').locator('.g-name').click();
        await page.waitForSelector(SHEET + ' [data-draft-note]:not([hidden])');
        await shot(page, 'd35-draft-restore', false);
        await page.click(SHEET + ' [data-draft-restore]');
        assert((await page.inputValue(SHEET + ' [data-comment]')) === '센서 교정값 확인 중', '복원되지 않음');
        await page.click(SHEET + ' [data-add-comment]');
        await page.waitForFunction(() => document.querySelector('dialog.sheet[open] [data-feed]').innerText.includes('센서 교정값 확인 중'));
        await closeSheet(page);
        await workRow(page, '주행 시험').locator('.g-name').click();
        await page.waitForSelector(SHEET);
        assert(await page.locator(SHEET + ' [data-draft-note]').isHidden(), '등록한 뒤에도 복원 제안이 뜸');
        await closeSheet(page);
    });

    await step('간트 드래그 — 작업 막대를 끌면 사유를 받고 일정 변경', async () => {
        await page.click('#zoom [data-zoom=day]');
        const bar = workRow(page, '검수 문서 검토').locator('.g-bar.draggable');
        await bar.scrollIntoViewIfNeeded();
        const titleBefore = await bar.getAttribute('title');
        const box = await bar.boundingBox();
        await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
        await page.mouse.down();
        await page.mouse.move(box.x + box.width / 2 + 28 * 3, box.y + box.height / 2, { steps: 6 });   // 일 단위 배율: 하루 28px → 3일
        await shot(page, 'd06-gantt-drag', false);
        await page.mouse.up();
        await page.waitForSelector(SHEET);
        assert((await page.locator(SHEET + ' .sheet-head h2').innerText()).includes('목표 일정 변경'), '일정 변경 확인 창이 아님');
        await page.fill(SHEET + ' textarea[name=reason]', '검수 일정 순연에 맞춤');
        await submitSheet(page);
        const titleAfter = await workRow(page, '검수 문서 검토').locator('.g-bar.draggable').getAttribute('title');
        assert(titleAfter !== titleBefore, '드래그한 일정이 저장되지 않음: ' + titleAfter);
        await page.click('#zoom [data-zoom=week]');
    });

    await step('의존 관계 — 선행보다 이르면 거절, 일정을 맞추면 연결', async () => {
        const links = await page.locator('#gantt .g-links svg > path').count();
        await workRow(page, '안전 인증 서류 준비').locator('.g-name').click();
        await page.waitForSelector(SHEET);
        await page.locator(SHEET + ' .pick-list label', { hasText: '검수 문서 검토' }).locator('input').check();
        await page.locator(SHEET + ' button[type=submit]').click();
        assert((await sheetError(page)).includes('이후에 시작해야'), '선행 관계 거절 메시지가 아님');
        await shot(page, 'd07-work-sheet-predecessor', false);
        await page.fill(SHEET + ' input[name=planStart]', kstToday(25));
        await page.fill(SHEET + ' input[name=planEnd]', kstToday(27));
        await page.fill(SHEET + ' textarea[name=reason]', '선행 작업 종료 이후로 조정');
        await submitSheet(page);
        assert((await page.locator('#gantt .g-links svg > path').count()) === links + 1, '의존선이 추가되지 않음');
        await shot(page, 'd08-detail-gantt-links');
    });

    await step('저장 전 미리보기 — 막대를 끌면 함께 밀리는 후행 작업과 단계 예상 종료를 표로 보이고, 저장 결과가 미리보기와 같음', async () => {
        const succ = worksOf(await projectData(page)).find((w) => w.title === '안전 인증 서류 준비');
        await page.click('#zoom [data-zoom=day]');
        const bar = workRow(page, '검수 문서 검토').locator('.g-bar.draggable');
        await bar.scrollIntoViewIfNeeded();
        const box = await bar.boundingBox();
        await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
        await page.mouse.down();
        await page.mouse.move(box.x + box.width / 2 + 28, box.y + box.height / 2, { steps: 4 });     // 하루
        await page.mouse.up();
        await page.waitForSelector(SHEET + ' [data-plan-preview]');
        // 끈 일정은 시트에서 고칠 수 있다: 종료를 후행 시작일까지 늘리면 후행이 밀린다 (계산은 서버)
        await page.fill(SHEET + ' input[name=planEnd]', succ.planStart);
        const row = page.locator(SHEET + ' [data-preview-moves] tr[data-work="' + succ.id + '"]');
        await row.waitFor();
        const cells = await row.locator('td').allInnerTexts();
        assert(cells[1].includes(succ.planStart.slice(5)), '지금 일정 칸이 다름: ' + cells.join(' | '));
        assert(await page.locator(SHEET + ' [data-preview-stages] li').count() >= 1, '단계 예상 종료 전/후 줄이 없음');
        assert((await page.locator(SHEET + ' [data-plan-note]').innerText()).includes('근무일'), '근무일 수(서버 값)가 없음');
        await page.locator(SHEET + ' [data-plan-preview]').scrollIntoViewIfNeeded();
        await shot(page, 'd08b-plan-preview', false);
        await page.fill(SHEET + ' textarea[name=reason]', '검수 문서 보완');
        await submitSheet(page);
        const moved = worksOf(await projectData(page)).find((w) => w.id === succ.id);
        assert(cells[2].includes(moved.planStart.slice(5) + '~' + moved.planEnd.slice(5)),
            '저장 결과가 미리보기와 다름: ' + cells[2] + ' / ' + moved.planStart + '~' + moved.planEnd);
        await page.click('#zoom [data-zoom=week]');
    });

    await step('의존 유형·간격 — 작업 시트에서 선행마다 유형·간격을 고르면 막히는 이유를 저장 전에 보이고, 저장하면 유형별 의존선·연결 변경 이력', async () => {
        const pred = worksOf(await projectData(page)).find((w) => w.title === '검수 문서 검토');
        await workRow(page, '안전 인증 서류 준비').locator('.g-name').click();
        await page.waitForSelector(SHEET);
        const opts = page.locator(SHEET + ' [data-pre-row="' + pred.id + '"] .pre-opts');
        assert(await opts.isVisible(), '고른 선행에 유형·간격 칸이 없음');
        assert(await page.locator(SHEET + ' .pre-row .pre-opts[hidden]').count() >= 1, '고르지 않은 선행에도 유형·간격 칸이 보임');
        // 간격을 크게 주면 저장하기 전에 막히는 이유를 보인다
        await page.fill(SHEET + ' input[name="preLag.' + pred.id + '"]', '30');
        await page.waitForSelector(SHEET + ' [data-work-preview] [data-preview-issue=block]');
        // 같이 시작 +2근무일이면 통과
        await page.selectOption(SHEET + ' select[name="preType.' + pred.id + '"]', 'ss');
        await page.fill(SHEET + ' input[name="preLag.' + pred.id + '"]', '2');
        await page.waitForFunction(() => {
            const el = document.querySelector('dialog.sheet[open] [data-work-preview]');
            return el && !el.hasAttribute('aria-busy') && !el.querySelector('[data-preview-issue=block]');
        });
        await opts.scrollIntoViewIfNeeded();
        await shot(page, 'd08c-link-type', false);
        await submitSheet(page);
        assert((await page.locator('#gantt .g-links svg > path[data-link-type=ss]').count()) === 1, '같이 시작 의존선이 없음');
        const P = await projectData(page);
        const link = P.links.find((l) => l.from === pred.id);
        assert(link && link.type === 'ss' && link.lag === 2, '유형·간격이 저장되지 않음: ' + JSON.stringify(link));
        assert(P.activities.some((a) => a.type === 'linkChanged' && a.text.includes('같이 시작 +2근무일')), '연결 변경 이력이 없음');
        // 다시 열면 고른 유형·간격이 남아 있다
        await workRow(page, '안전 인증 서류 준비').locator('.g-name').click();
        await page.waitForSelector(SHEET);
        assert((await page.inputValue(SHEET + ' select[name="preType.' + pred.id + '"]')) === 'ss', '유형이 남지 않음');
        assert((await page.inputValue(SHEET + ' input[name="preLag.' + pred.id + '"]')) === '2', '간격이 남지 않음');
        await closeSheet(page);
    });

    await step('재계획 — 시작일이 지난 미착수 작업이 있으면 단추가 보이고, 오늘 기준 예측으로 옮겨질 작업을 표로 보임', async () => {
        await openProject(page, '베트남 세탁기 2라인 AMR 확대');
        await page.waitForSelector('#replan:not([hidden])');
        assert(Number(await page.locator('#replan-count').innerText()) >= 1, '재계획 대상 수가 없음');
        // 간트: 시작 전 작업은 예상 일정(서버 전진 계산)에 노랑 점선
        assert((await workRow(page, '출하 검사').locator('.g-bar.ghost.fc').count()) === 1, '예상 일정 막대가 없음');
        await page.click('#replan');
        await page.waitForSelector(SHEET + ' [data-replan-preview] [data-preview-moves] tbody tr');
        const text = await page.locator(SHEET + ' [data-preview-moves]').innerText();
        assert(text.includes('출하 검사') && text.includes('시작 지연'), '옮겨질 작업 표가 다름: ' + text);
        await shot(page, 'd08d-replan', false);
        await closeSheet(page);        // 저장은 모바일 단계에서 한다
    });

    await step('과제 간 연결 — 단계 시트에서 다른 과제 단계에 이으면 어긋날 때 두 과제의 단계에 경고 칩, 칩을 누르면 상대 과제', async () => {
        await openProject(page, '광주 냉장고 3라인 AGV 도입');
        const P = await projectData(page);
        const stage = P.stages.find((s) => s.name === '입고');
        // 상대 과제에서 이 단계 목표일보다 늦은 미완료 단계를 선행으로 고르면 어긋난다
        const targets = await page.evaluate(async (id) => (await App.get(App.url('/Projects/Detail?handler=LinkTargets&id=' + id +
            '&q=' + encodeURIComponent('멕시코 에어컨')))).projects, P.id);
        const other = targets[0].stages.find((s) => !s.completedDate && s.targetDate && s.targetDate > stage.targetDate);
        assert(other, '고를 상대 단계가 없음');
        await stageRow(page, '입고').click();
        await page.waitForSelector(SHEET + ' [data-cross]');
        await page.click(SHEET + ' [data-cross-open]');
        await page.selectOption(SHEET + ' [data-cross-role]', 'pred');
        await page.fill(SHEET + ' [data-cross-q]', '멕시코 에어컨');
        await page.waitForSelector(SHEET + ' [data-cross-stage] option[value="' + other.id + '"]', { state: 'attached' });
        await page.selectOption(SHEET + ' [data-cross-stage]', String(other.id));
        await page.click(SHEET + ' [data-cross-save]');
        await page.waitForSelector(SHEET + ' [data-cross-list] li.bad');
        assert((await page.locator(SHEET + ' [data-cross-list]').innerText()).includes('멕시코 에어컨 용기 표준화'), '연결 목록에 상대 과제가 없음');
        await page.locator(SHEET + ' [data-cross]').scrollIntoViewIfNeeded();
        await shot(page, 'd08e-cross-link', false);
        await closeSheet(page);
        assert(await stageRow(page, '입고').locator('.warn use[href="#i-link"]').count() === 1, '간트 이름 칸에 연결 경고(노랑)가 없음');
        // 목록 보기의 단계 카드 칩 → 상대 과제로 이동, 상대 과제 단계에도 경고
        await page.click('#view-mode [data-view=list]');
        await page.locator('#schedule-list .stage-card', { hasText: '입고' }).locator('[data-cross-chip].warn').click();
        await page.waitForFunction(() => document.getElementById('p-title') && document.getElementById('p-title').textContent.includes('멕시코 에어컨'));
        await page.locator('#schedule-list [data-cross-chip].warn', { hasText: '광주 냉장고 3라인' }).waitFor();
        // 연결 위반은 위험(노랑)이다 — 자기 일정에 문제가 없는 선행 쪽 과제에 지연(빨강) 칩이 붙지 않는다
        assert(await page.locator('#schedule-list [data-cross-chip].alarm').count() === 0, '과제 간 연결 위반이 빨강으로 보임');
        await shot(page, 'd08f-cross-warn');
        await page.click('#view-mode [data-view=gantt]');
        await openProject(page, '광주 냉장고 3라인 AGV 도입');
    });

    await step('기준선 다시 확정 — 사유를 받고 직전·새 목표 종료를 이력에 남김', async () => {
        await page.click('#btn-baseline');
        await page.waitForSelector(SHEET + ' [data-baseline-ends]');
        assert((await page.locator(SHEET + ' [data-baseline-ends]').innerText()).includes('기준선 목표 종료'), '직전·새 목표 종료 안내가 없음');
        await page.locator(SHEET + ' button[type=submit]').click();
        assert((await sheetError(page)).includes('사유'), '사유 없이 다시 확정됨');
        await page.fill(SHEET + ' textarea[name=reason]', '계약 범위 변경');
        await shot(page, 'd08g-rebaseline', false);
        await submitSheet(page);
        await page.waitForFunction(() => document.querySelector('#summary .summary-chips').innerText.includes('기준선 유지'));
        const act = (await projectData(page)).activities.find((a) => a.type === 'baselineSet');
        assert(act && /기준선 다시 확정 \(\d+차, 목표 종료/.test(act.text) && act.reason === '계약 범위 변경', '기준선 다시 확정 이력이 이상함: ' + JSON.stringify(act));
    });

    await step('기준선 차수 이력 — 정보 탭에 차수·시각·사람·사유, 이전 차수를 고르면 그 차수 대비 +n일로 다시 그림', async () => {
        await page.click('.tabs [data-tab=info]');
        await page.waitForSelector('#baseline-card .baseline-list li');
        const P = await projectData(page);
        assert(P.baselines.length >= 2, '차수가 둘 이상이 아님: ' + P.baselines.length);
        assert(P.baselines[0].reason === '계약 범위 변경', '최신 차수 사유가 이상함');
        assert(await page.locator('#baseline-card li.on').count() === 1, '비교 중 차수 표시가 없음');
        await page.selectOption('#baseline-compare', '1');
        await page.waitForFunction(() => document.querySelector('#summary .summary-chips').innerText.includes('1차 기준선'));
        assert((await page.locator('#baseline-card li.on').getAttribute('data-baseline-no')) === '1', '1차가 비교 중으로 표시되지 않음');
        // 고른 차수는 저장 뒤에도 유지된다(요청 주소에 붙여 보냄)
        await page.click('.tabs [data-tab=activity]');
        await page.fill('#comment-text', '차수 비교 중 댓글');
        await page.click('#comment-btn');
        await page.waitForFunction(() => document.querySelector('#feed') && document.querySelector('#feed').innerText.includes('차수 비교 중 댓글'));
        assert((await page.locator('#summary .summary-chips').innerText()).includes('1차 기준선'), '저장 뒤 비교 차수가 풀림');
        await page.click('.tabs [data-tab=info]');
        await shot(page, 'd08h-baseline-history');
        await page.selectOption('#baseline-compare', '');
        await page.waitForFunction(() => !document.querySelector('#summary .summary-chips').innerText.includes('1차 기준선'));
        await page.click('.tabs [data-tab=schedule]');
    });

    await step('목록 보기·정보·활동 탭', async () => {
        await page.click('#view-mode [data-view=list]');
        assert((await page.locator('#schedule-list .stage-card').count()) === 8, '단계 카드가 8개가 아님');
        await shot(page, 'd09-detail-list');
        await page.click('.tabs [data-tab=info]');
        assert((await page.locator('#tab-info .simple-table tbody tr').count()) === 1, '기기 내역이 1행이 아님');
        assert((await page.locator('#tab-info').innerText()).includes('물류기술그룹 · 자동화파트'), '담당 소속이 보이지 않음');
        await page.click('.tabs [data-tab=activity]');
        await page.fill('#comment-text', '검수 일정 협력사와 재협의 완료');
        await page.click('#comment-btn');
        await page.waitForFunction(() => document.getElementById('feed').innerText.includes('재협의 완료'));
        const feed = await page.locator('#feed').innerText();
        assert(feed.includes('연쇄 이동'), '연쇄 이동 이력이 없음');
        assert(feed.includes('기준선 다시 확정'), '기준선 이력이 없음');
        await shot(page, 'd10-detail-activity');
    });

    await step('진행 내용 등록 — 처리 중에는 다시 등록되지 않음', async () => {
        await page.route((u) => u.href.includes('handler=Comment'), async (r) => { await new Promise((res) => setTimeout(res, 600)); await r.continue(); });
        await page.fill('#comment-text', '중복 등록 확인용 메모');
        // 단추를 두 번 누른 것과 같은 상황: 응답 전에 제출을 두 번 보낸다
        await page.evaluate(() => { const f = document.getElementById('comment-form'); f.requestSubmit(); f.requestSubmit(); });
        assert(await page.isDisabled('#comment-btn'), '처리 중인데 등록 단추가 눌림');
        await page.waitForFunction(() => document.getElementById('feed').innerText.includes('중복 등록 확인용 메모'));
        await page.unroute((u) => u.href.includes('handler=Comment'));
        await page.waitForTimeout(800);
        assert((await page.locator('#feed li', { hasText: '중복 등록 확인용 메모' }).count()) === 1, '같은 글이 두 번 등록됨');
    });

    await step('자료 — 올리기·미리보기·내려받기·삭제', async () => {
        await page.click('.tabs [data-tab=files]');
        // 1x1 PNG
        const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==', 'base64');
        await page.setInputFiles('#file-input', { name: '현장사진.png', mimeType: 'image/png', buffer: png });
        await page.selectOption('#file-stage', { label: '검수' });
        await page.click('#upload-btn');
        await page.waitForFunction(() => document.getElementById('files').innerText.includes('현장사진.png'));
        // 과제 첨부 합계 (상한 Storage:ProjectMaxMB 는 기본 없음)
        assert((await page.locator('#files-total').innerText()).includes('첨부 합계'), '첨부 합계가 없음');
        await shot(page, 'd11-detail-files');

        const href = await page.locator('#files a.row-title').getAttribute('href');
        const res = await desktop.request.get(new URL(href, BASE).toString());
        assert(res.ok() && (await res.body()).equals(png), '내려받은 파일이 올린 파일과 다름');
        assert(res.headers()['x-content-type-options'] === 'nosniff', '내려받기에 nosniff 가 없음');

        await page.click('#files [data-preview]');
        await page.waitForSelector(SHEET + ' img.preview-img');
        await closeSheet(page);

        // X-Frame-Options: SAMEORIGIN 이어도 같은 출처의 틀(iframe) 미리보기는 열린다
        const framed = await page.evaluate(async (src) => {
            const f = document.createElement('iframe');
            f.src = src;
            document.body.appendChild(f);
            await new Promise((r) => { f.onload = r; setTimeout(r, 5000); });
            const ok = !!(f.contentDocument && f.contentDocument.querySelector('img'));
            f.remove();
            return ok;
        }, new URL(href + '&inline=true', BASE).toString());
        assert(framed, '같은 출처 iframe 안에서 첨부가 열리지 않음');

        // PDF 는 시트 안의 iframe 으로 미리 본다
        const pdf = Buffer.from('%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n' +
            '3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 200]>>endobj\ntrailer<</Root 1 0 R>>\n%%EOF\n');
        await page.setInputFiles('#file-input', { name: '검수표.pdf', mimeType: 'application/pdf', buffer: pdf });
        await page.click('#upload-btn');
        await page.waitForFunction(() => document.getElementById('files').innerText.includes('검수표.pdf'));
        const pdfRow = page.locator('#files li', { hasText: '검수표.pdf' });
        const [pdfRes] = await Promise.all([
            page.waitForResponse((r) => r.url().includes('inline=true')),
            pdfRow.locator('[data-preview]').click(),
        ]);
        await page.waitForSelector(SHEET + ' iframe.preview-frame');
        assert(pdfRes.status() === 200 && pdfRes.headers()['x-frame-options'] === 'SAMEORIGIN', 'PDF 미리보기 응답이 이상함: ' + pdfRes.status());
        await page.waitForTimeout(300);
        await closeSheet(page);
        await pdfRow.locator('[data-delete-file]').click();
        await page.click(SHEET + ' [data-yes]');
        await page.waitForFunction(() => !document.getElementById('files').innerText.includes('검수표.pdf'));

        // 받지 않는 형식은 화면에서 먼저 거른다
        await page.setInputFiles('#file-input', { name: 'run.exe', mimeType: 'application/octet-stream', buffer: Buffer.from('MZ') });
        await page.click('#upload-btn');
        await page.waitForSelector('.toast.error');

        await page.click('#files [data-delete-file]');
        await page.click(SHEET + ' [data-yes]');
        await page.waitForFunction(() => document.getElementById('files').innerText.includes('올린 자료가 없습니다'));
        await page.click('.tabs [data-tab=schedule]');
        await page.click('#view-mode [data-view=gantt]');
    });

    await step('동시 수정 — 다른 화면에서 먼저 고치면 바뀐 값을 보여 주고, 같은 시트에서 다시 저장', async () => {
        await stageRow(page, '설치').click();
        await page.waitForSelector(SHEET);

        const other = active = await desktop.newPage();
        other.setDefaultTimeout(10000);
        await openProject(other, '광주 냉장고 3라인 AGV 도입');
        await other.locator('#stepper .step', { hasText: '설치' }).click();
        await other.waitForSelector(SHEET);
        await other.fill(SHEET + ' input[name=name]', '설치·시운전');
        await other.fill(SHEET + ' input[name=targetDate]', kstToday(40));
        await other.fill(SHEET + ' textarea[name=reason]', '설치 일정 조정');
        await submitSheet(other);
        await other.close();
        active = page;

        await page.fill(SHEET + ' input[name=name]', '현장 설치');
        await page.locator(SHEET + ' button[type=submit]').click();
        assert((await sheetError(page)).includes('다른 사용자가 먼저'), '동시 수정 안내가 아님');
        const note = await page.locator(SHEET + ' [data-field=name] [data-conflict-note]').innerText();
        assert(note.includes('다른 사용자가 바꾼 값') && note.includes('설치·시운전'), '바뀐 값 안내가 없음: ' + note);
        assert((await page.inputValue(SHEET + ' input[name=name]')) === '현장 설치', '입력한 값이 사라짐');
        // 내가 손대지 않은 목표일은 그 사람이 바꾼 값으로 맞춘다 (그대로 저장하면 옛 값으로 되돌리므로)
        assert((await page.inputValue(SHEET + ' input[name=targetDate]')) === kstToday(40), '손대지 않은 목표일이 최신 값으로 바뀌지 않음');
        assert((await page.locator(SHEET + ' [data-field=targetDate] [data-conflict-note]').innerText()).includes('그 값으로 바꿨습니다'), '맞춘 값 안내가 없음');
        await shot(page, 'd12-conflict', false);
        const names = await page.locator('#stepper .step-name').allInnerTexts();
        assert(names.some((n) => n.includes('설치·시운전')), '화면이 최신 내용으로 바뀌지 않음: ' + names.join(','));
        // 같은 시트에서 다시 저장하면 최신 버전으로 저장된다 (예전에는 몇 번을 눌러도 거절됐다)
        await submitSheet(page);
        const after = await page.locator('#stepper .step').allInnerTexts();
        const installed = after.find((n) => n.includes('현장 설치')) || '';
        assert(installed, '다시 저장되지 않음: ' + after.join(','));
        assert(installed.includes(kstToday(40).slice(5)), '다른 사람이 바꾼 목표일이 되돌려짐: ' + installed);
    });

    await step('동시 수정 — 작업 시트를 연 뒤 다른 화면이 새 작업을 선행으로 걸어도, 목록을 다시 그려 다시 저장하면 그 연결이 남음', async () => {
        const W = '안전 인증 서류 준비', X = '동시 추가 선행 작업';
        await workRow(page, W).locator('.g-name').click();
        await page.waitForSelector(SHEET);
        const wStart = await page.inputValue(SHEET + ' input[name=planStart]');
        const wStage = await page.inputValue(SHEET + ' select[name=stageId]');
        const xDay = new Date(Date.parse(wStart + 'T00:00:00Z') - 5 * 86400e3).toISOString().slice(0, 10);

        // 다른 화면: 새 작업 X 를 만들고 W 의 선행으로 건다 (X 는 W 보다 먼저 끝난다)
        const other = active = await desktop.newPage();
        other.setDefaultTimeout(10000);
        await openProject(other, '광주 냉장고 3라인 AGV 도입');
        await other.click('#add-work');
        await other.waitForSelector(SHEET);
        await other.fill(SHEET + ' input[name=title]', X);
        await other.selectOption(SHEET + ' select[name=stageId]', wStage);
        await other.fill(SHEET + ' input[name=planStart]', xDay);
        await other.fill(SHEET + ' input[name=planEnd]', xDay);
        await submitSheet(other);
        await workRow(other, W).locator('.g-name').click();
        await other.waitForSelector(SHEET);
        await other.locator(SHEET + ' .pick-list label', { hasText: X }).locator('input').check();
        await submitSheet(other);
        await other.close();
        active = page;

        // 시트를 연 뒤 생긴 작업이라 처음 목록에는 없다. 메모만 고쳐 저장 → 409 → 최신 과제로 목록을 다시 그린다
        assert((await page.locator(SHEET + ' .pick-list label', { hasText: X }).count()) === 0, '시트를 연 뒤 생긴 작업이 처음부터 목록에 있음');
        await page.fill(SHEET + ' textarea[name=note]', '동시 수정 확인');
        await page.locator(SHEET + ' button[type=submit]').click();
        assert((await sheetError(page)).includes('다른 사용자가 먼저'), '동시 수정 안내가 아님');
        assert(await page.locator(SHEET + ' .pick-list label', { hasText: X }).locator('input').isChecked(), '다른 화면이 건 선행 작업이 목록에 없거나 체크되지 않음');
        const note = await page.locator(SHEET + ' [data-field=predecessors] [data-conflict-note]').innerText();
        assert(note.includes(X) && note.includes('그 값으로 바꿨습니다'), '선행 작업 안내가 다름: ' + note);
        assert((await page.inputValue(SHEET + ' select[name=stageId]')) === wStage, '단계 선택이 비었거나 바뀜');
        await page.locator(SHEET + ' [data-field=predecessors]').scrollIntoViewIfNeeded();
        await shot(page, 'd12b-work-conflict', false);
        await submitSheet(page);

        // 다시 열면 연결과 내 메모가 모두 남아 있다
        await workRow(page, W).locator('.g-name').click();
        await page.waitForSelector(SHEET);
        assert(await page.locator(SHEET + ' .pick-list label', { hasText: X }).locator('input').isChecked(), '다시 저장한 뒤 선행 연결이 지워짐');
        assert((await page.inputValue(SHEET + ' textarea[name=note]')) === '동시 수정 확인', '내 메모가 저장되지 않음');
        await closeSheet(page);

        // 정리: 추가한 작업을 지운다 (뒤 단계의 작업 수·의존선이 그대로이도록)
        await workRow(page, X).locator('.g-name').click();
        await page.waitForSelector(SHEET);
        await page.click(SHEET + ' [data-danger]');
        await page.click('dialog[open] [data-yes]');
        await page.waitForSelector(SHEET, { state: 'detached' });
        assert((await workRow(page, X).count()) === 0, '추가한 작업이 지워지지 않음');
    });

    await step('다른 사용자가 이 과제를 바꾸면 띠로 알리고 새로 고침하면 반영, 내 저장은 띠 없음, 날짜가 바뀌면 다시 읽음', async () => {
        await page.evaluate(() => App.checkVersion());
        await page.waitForTimeout(500);
        assert(await page.locator('#change-banner').isHidden(), '아무도 바꾸지 않았는데 띠가 뜸');

        const other = active = await desktop.newPage();
        other.setDefaultTimeout(10000);
        await openProject(other, '광주 냉장고 3라인 AGV 도입');
        await other.click('.tabs [data-tab=activity]');
        await other.fill('#comment-text', '다른 화면에서 남긴 메모');
        await other.click('#comment-form button[type=submit]');
        await other.waitForFunction(() => document.getElementById('feed').innerText.includes('다른 화면에서 남긴 메모'));
        await other.close();
        active = page;

        await page.evaluate(() => App.checkVersion());
        await page.waitForSelector('#change-banner:not([hidden])');
        assert((await page.locator('#change-banner').innerText()).includes('다른 사용자가 이 과제를 바꿨습니다'), '띠 문구가 아님');
        await shot(page, 'd37-change-banner', false);
        await page.click('#change-banner [data-banner-refresh]');
        await page.waitForSelector('#change-banner', { state: 'hidden' });
        assert((await page.locator('#feed').innerText()).includes('다른 화면에서 남긴 메모'), '새로 고침해도 반영되지 않음');

        // 내가 저장한 직후에는 띠가 뜨지 않는다
        await page.click('.tabs [data-tab=activity]');
        await page.fill('#comment-text', '내가 남긴 메모');
        await page.click('#comment-btn');
        await page.waitForFunction(() => document.getElementById('feed').innerText.includes('내가 남긴 메모'));
        await page.evaluate(() => App.checkVersion());
        await page.waitForTimeout(500);
        assert(await page.locator('#change-banner').isHidden(), '내 저장 뒤에 띠가 뜸');

        // 날짜가 바뀌면(자정을 넘겨 열어 둔 화면) 최신 과제를 다시 읽는다
        const build = await page.evaluate(() => document.documentElement.getAttribute('data-build'));
        await page.route('**/version', (r) => r.fulfill({ json: { ok: true, build, version: '0', updating: false, today: kstToday(1) } }));
        await Promise.all([
            page.waitForRequest((r) => r.url().includes('handler=Data')),
            page.evaluate(() => App.checkVersion()),
        ]);
        await page.unroute('**/version');
        await page.click('.tabs [data-tab=schedule]');
    });

    await step('뒤로 가면 목록 필터 유지, 탭은 주소에 남고, 작업 링크는 연 뒤 지움, 없는 과제는 안내 화면', async () => {
        await page.goto(BASE + '/Projects?state=delayed');
        await page.locator('.ptable-row').first().click();
        await page.waitForSelector('#summary .summary-meta');
        await page.click('.tabs [data-tab=files]');
        assert(page.url().endsWith('#files'), '탭이 주소에 남지 않음: ' + page.url());
        await page.reload();
        await page.waitForSelector('#summary .summary-meta');
        assert(await page.locator('#tab-files').isVisible(), '새로 고친 뒤 자료 탭이 아님');
        assert((await page.locator('.tabs [data-tab=files]').getAttribute('aria-selected')) === 'true', '자료 탭이 선택되지 않음');
        await page.click('#back-link');
        await page.waitForURL(/state=delayed/);

        await page.goto(BASE + '/My');
        await page.locator('.row-link[href*="#w"]').first().click();
        await page.waitForSelector(SHEET);
        assert((await page.evaluate(() => window.location.hash)) === '', '작업 링크(#w)가 주소에 남음');
        await closeSheet(page);
        await page.reload();
        await page.waitForSelector('#summary .summary-meta');
        await page.waitForTimeout(300);
        assert((await page.locator(SHEET).count()) === 0, '새로 고치면 작업 창이 다시 뜸');

        const res = await page.goto(BASE + '/Projects/Detail?id=999999');
        assert(res.status() === 404, '없는 과제가 404 가 아님: ' + res.status());
        assert((await page.locator('main').innerText()).includes('찾는 화면이 없습니다'), '404 안내 문구가 없음');
        await shot(page, 'd36-not-found');
    });

    await step('해외 PC — 오늘 시작은 그 나라 날짜, 시각은 그 나라 시각', async () => {
        const zone = 'America/Mexico_City';
        const abroad = await browser.newContext({ viewport: { width: 1280, height: 800 }, locale: 'ko-KR', timezoneId: zone });
        const p2 = active = await abroad.newPage();
        watch(p2, 'abroad');
        p2.setDefaultTimeout(10000);
        await openProject(p2, '광주 냉장고 3라인 AGV 도입');
        await workRow(p2, '안전 인증 서류 준비').locator('.g-name').click();
        await p2.waitForSelector(SHEET);
        await p2.click(SHEET + ' [data-quick-start]');
        const value = await p2.inputValue(SHEET + ' input[name=actualStart]');
        assert(value === localDate(zone), `오늘 시작이 그 나라 날짜가 아님: ${value} (현지 ${localDate(zone)}, 한국 ${kstToday()})`);
        await discardSheet(p2);
        await p2.goto(BASE + '/Insights/History');
        const t = p2.locator('.history time').first();
        const iso = await t.getAttribute('datetime');
        assert(iso.endsWith('Z'), '변경 이력 시각이 UTC 가 아님: ' + iso);
        const text = (await t.innerText()).trim();
        assert(text === localTime(iso, zone), `변경 이력 시각이 현지 시각이 아님: ${text} (기대 ${localTime(iso, zone)})`);
        await shot(p2, 'd41-abroad-history');
        await abroad.close();
    });

    await step('인쇄 — 메뉴·도구 없이, 간트는 월 배율로 종이 한 폭에 (상세·대시보드·내 할 일)', async () => {
        await openProject(page, '광주 냉장고 3라인 AGV 도입');
        assert(await page.locator('#summary [data-print]').isVisible(), '상세에 인쇄 단추가 없음');
        const zoomBefore = await page.evaluate(() => document.querySelector('#gantt').dataset.zoom);
        await page.emulateMedia({ media: 'print' });
        await page.waitForFunction(() => document.querySelector('#gantt').dataset.zoom === 'month');
        assert(await page.locator('.top').isHidden(), '인쇄에 머리 메뉴가 보임');
        assert(await page.locator('#tab-schedule .toolbar').isHidden(), '인쇄에 도구 모음이 보임');
        assert((await page.evaluate(() => getComputedStyle(document.querySelector('#gantt .g-scroll')).overflowX)) === 'visible', '간트가 스크롤 안에 갇힘');
        // 화면 흉내(emulateMedia)는 창 폭이 그대로라 실제 종이 폭 대신 간트가 계산한 폭(이름 칸 + 일정)을 본다
        const width = await page.evaluate(() => {
            const h = document.querySelector('#gantt');
            return parseFloat(h.style.getPropertyValue('--g-width')) + parseFloat(getComputedStyle(h).getPropertyValue('--g-left'));
        });
        assert(width <= 1047, '간트가 종이 폭(A4 가로)보다 넓음: ' + width);
        await shot(page, 'd38-print-detail');
        await page.pdf({ path: path.join(EXPORTS, '인쇄_과제상세.pdf'), format: 'A4', landscape: true, printBackground: true });
        await page.emulateMedia({ media: 'screen' });
        await page.waitForFunction((z) => document.querySelector('#gantt').dataset.zoom === z, zoomBefore);

        await page.goto(BASE + '/');
        assert(await page.locator('.page-head [data-print]').isVisible(), '대시보드에 인쇄 단추가 없음');
        await page.emulateMedia({ media: 'print' });
        await page.waitForFunction(() => document.querySelector('#gantt').dataset.zoom === 'month');
        await shot(page, 'd39-print-dashboard');
        await page.pdf({ path: path.join(EXPORTS, '인쇄_대시보드.pdf'), format: 'A4', landscape: true, printBackground: true });
        await page.emulateMedia({ media: 'screen' });

        await page.goto(BASE + '/My');
        assert(await page.locator('.page-head [data-print]').isVisible(), '내 할 일에 인쇄 단추가 없음');
        await page.emulateMedia({ media: 'print' });
        assert(await page.locator('.bottom-nav, .top').first().isHidden(), '인쇄에 메뉴가 보임');
        await shot(page, 'd40-print-my');
        await page.emulateMedia({ media: 'screen' });
        for (const f of ['인쇄_과제상세.pdf', '인쇄_대시보드.pdf']) {
            const size = fs.statSync(path.join(EXPORTS, f)).size;
            assert(size > 3000, f + ' 이 너무 작음: ' + size);
        }
    });

    await step('과제 복제', async () => {
        await openProject(page, '광주 냉장고 3라인 AGV 도입');
        await page.click('#btn-clone');
        await page.waitForSelector(SHEET);
        await page.fill(SHEET + ' input[name=title]', '광주 냉장고 5라인 AGV 도입');
        await page.fill(SHEET + ' input[name=startDate]', kstToday(30));
        await page.locator(SHEET + ' button[type=submit]').click();
        await page.waitForFunction(() => document.getElementById('p-title').textContent === '광주 냉장고 5라인 AGV 도입');
        assert((await page.locator('#stepper .step').count()) === 8, '단계가 복사되지 않음');
        assert((await page.locator('#gantt .g-row.work').count()) === 4, '작업이 복사되지 않음');
        assert((await page.locator('#gantt .g-links svg > path').count()) === 2, '의존 관계가 복사되지 않음');
        assert((await page.locator('#summary').innerText()).includes('0%'), '진행률이 초기화되지 않음');
    });

    await step('연쇄 이동 — 이후 단계의 미착수 작업도 같은 근무일만큼 이동(선택)', async () => {
        await openProject(page, '광주 2공장 물류 동선 Simulation');
        // 이후 단계(검증)에 아직 시작하지 않은 작업을 하나 둔다
        await page.click('#add-work');
        await page.waitForSelector(SHEET);
        await page.fill(SHEET + ' input[name=title]', '검증 시나리오 실행');
        await page.locator(SHEET + ' select[name=stageId]').selectOption({ label: '검증' });
        await page.fill(SHEET + ' input[name=planStart]', kstToday(15));
        await page.fill(SHEET + ' input[name=planEnd]', kstToday(17));
        await submitSheet(page);
        const bar = () => workRow(page, '검증 시나리오 실행').locator('.g-bar.draggable');
        const before = await bar().getAttribute('title');

        await stageRow(page, '모델링').click();
        await page.waitForSelector(SHEET);
        await page.fill(SHEET + ' input[name=targetDate]', kstToday(14));
        const option = page.locator(SHEET + ' label', { has: page.locator('input[name=cascadeWorks]') });
        assert(await option.isVisible(), '작업 포함 선택지가 보이지 않음');
        assert((await option.innerText()).includes('미착수 작업 1건'), '작업 수 표시가 아님: ' + (await option.innerText()));
        assert(!(await page.isChecked(SHEET + ' input[name=cascadeWorks]')), '작업 포함이 기본으로 켜져 있음');
        await page.check(SHEET + ' input[name=cascade]');
        await page.check(SHEET + ' input[name=cascadeWorks]');
        await page.fill(SHEET + ' textarea[name=reason]', '모델링 범위 확대');
        await shot(page, 'd30-cascade-works', false);
        await submitSheet(page);
        const after = await bar().getAttribute('title');
        assert(after !== before, '이후 단계 작업이 옮겨지지 않음: ' + after);
    });

    await step('간트에서 단계 목표일을 끌어도 이후 단계 작업 포함 선택지가 보임', async () => {
        await page.click('#zoom [data-zoom=day]');
        const handle = page.locator('#gantt .g-row.stage', { hasText: '모델링' }).locator('.g-bar.draggable .g-rz.r');
        await handle.scrollIntoViewIfNeeded();
        const box = await handle.boundingBox();
        await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
        await page.mouse.down();
        await page.mouse.move(box.x + box.width / 2 + 28 * 2, box.y + box.height / 2, { steps: 6 });   // 하루 28px → 2일
        await page.mouse.up();
        await page.waitForSelector(SHEET);
        assert((await page.locator(SHEET + ' .sheet-head h2').innerText()).includes('목표일 변경'), '목표일 변경 확인 창이 아님');
        const option = page.locator(SHEET + ' label', { has: page.locator('input[name=cascadeWorks]') });
        assert(await option.isVisible(), '끌기 시트에 작업 포함 선택지가 없음');
        assert((await option.innerText()).includes('미착수 작업 1건'), '작업 수 표시가 아님: ' + (await option.innerText()));
        assert(!(await page.isChecked(SHEET + ' input[name=cascadeWorks]')), '작업 포함이 기본으로 켜져 있음');
        await shot(page, 'd30b-gantt-target-cascade-works', false);
        await closeSheet(page);     // 저장하지 않으면 그대로
        await page.click('#zoom [data-zoom=week]');
    });

    await step('단계 완료 — 남은 작업을 완료일로 닫고 완료', async () => {
        await stageRow(page, '모델링').click();
        await page.waitForSelector(SHEET);
        const close = page.locator(SHEET + ' [data-close-works]');
        assert(await close.isHidden(), '완료일을 넣기 전에 선택지가 보임');
        await page.click(SHEET + ' [data-done-today]');
        assert(await close.isVisible(), '남은 작업 닫기 선택지가 보이지 않음');
        assert((await close.innerText()).includes('남은 작업 2건'), '남은 작업 수가 아님: ' + (await close.innerText()));
        await page.check(SHEET + ' input[name=closeWorks]');
        await shot(page, 'd31-stage-close-works', false);
        await submitSheet(page);
        for (const name of ['AGV 로직 구현', '시나리오 정의']) {
            const row = workRow(page, name);
            assert((await row.innerText()).includes('100%'), name + ' 이 닫히지 않음: ' + (await row.innerText()));
            assert((await row.locator('.alarm').count()) === 0, name + ' 에 지연 표시가 남음');
        }
        assert((await stageRow(page, '모델링').innerText()).includes('완료'), '단계가 완료되지 않음');
        await shot(page, 'd32-stage-closed-works');
    });

    await step('단계만 완료하면 남은 작업은 완료(단계)로 보이고 끌 수 없으며, 완료를 취소하면 되돌아옴', async () => {
        // '검증' 단계에는 앞에서 만든 미착수 작업(검증 시나리오 실행)이 있다
        const bar = () => workRow(page, '검증 시나리오 실행').locator('.g-bar.draggable');
        assert((await bar().count()) === 1, '완료 전 작업 막대를 끌 수 없음');
        await stageRow(page, '검증').click();
        await page.waitForSelector(SHEET);
        await page.click(SHEET + ' [data-done-today]');
        assert(!(await page.isChecked(SHEET + ' input[name=closeWorks]')), '남은 작업 닫기가 기본으로 켜져 있음');
        await submitSheet(page);
        assert((await bar().count()) === 0, '단계 완료로 끝난 작업을 끌 수 있음');
        assert((await workRow(page, '검증 시나리오 실행').locator('.g-bar.ghost').count()) === 0, "끝난 작업에 '시작 전' 막대가 남음");
        await shot(page, 'd33-stage-done-open-work');

        await page.click('#view-mode [data-view=list]');
        const item = page.locator('#schedule-list .work-item', { has: page.locator('.work-title', { hasText: '검증 시나리오 실행' }) });
        assert((await item.innerText()).includes('완료(단계)'), '완료(단계) 표시가 없음: ' + (await item.innerText()));
        await page.click('#view-mode [data-view=gantt]');

        await stageRow(page, '검증').click();
        await page.waitForSelector(SHEET);
        await page.click(SHEET + ' [data-clear-done]');
        await submitSheet(page);
        assert((await bar().count()) === 1, '완료를 취소했는데 작업 막대를 끌 수 없음');
    });

    await step('모든 단계 완료 시 자동으로 완료 상태', async () => {
        await openProject(page, '폴란드 세탁기 창고 입출고 개선');
        for (let i = 0; i < 3; i++) {
            await page.locator('#stepper .step').nth(i).click();
            await page.waitForSelector(SHEET);
            await page.click(SHEET + ' [data-done-today]');
            await submitSheet(page);
        }
        const chips = await page.locator('#summary .summary-chips').innerText();
        assert(chips.includes('완료'), '완료 상태가 아님: ' + chips);
    });

    await step('상태 변경 — 보류', async () => {
        await openProject(page, '멕시코 냉장고 AGV 도입 검토');
        await page.click('#btn-state');
        await page.waitForSelector(SHEET);
        await page.fill(SHEET + ' textarea[name=reason]', '투자 심의 일정 미확정');
        await submitSheet(page);
        assert((await page.locator('#summary .summary-chips').innerText()).includes('보류'), '보류로 바뀌지 않음');
    });

    await step('과제 등록 — 위치 연쇄 선택, 기기 내역', async () => {
        await page.goto(BASE + '/Projects/Edit');
        await page.fill('input[name="Form.Title"]', '베트남 냉장고 1라인 컨베이어 개선');
        await page.locator('.choice', { hasText: '물류 자동화' }).click();
        await page.locator('#cascade select[data-level="1"]').selectOption({ label: '베트남' });
        await page.locator('#cascade select[data-level="2"]').selectOption({ label: '냉장고' });
        // 하위가 하나뿐인 라인은 자동 선택된다
        assert((await page.locator('#cascade select[data-level="3"]').inputValue()) !== '', '라인이 자동 선택되지 않음');
        await page.locator('#items .item-row select').first().selectOption({ label: '컨베이어' });
        await page.locator('#items .item-row input[data-k=Quantity]').first().fill('3');
        await shot(page, 'd13-edit-form');

        // 단계 목표일 초안: 유형의 기본 기간으로 채워지고, 기본 기간이 없는 입고는 비어 있으며 법인(베트남, 해상 12일)의 추천 입고일을 보인다
        await page.waitForSelector('#draft .sched-row [data-apply-hint]');
        const draftRows = page.locator('#draft .sched-row');
        assert((await draftRows.count()) === 8, '초안 표가 8줄이 아님');
        assert(/^\d{4}-\d{2}-\d{2}$/.test(await draftRows.nth(0).locator('input').inputValue()), '첫 단계 초안이 비어 있음');
        assert((await draftRows.nth(0).locator('.sched-gap').innerText()).includes('+10근무일'), '간격이 기본 기간과 다름');
        const inbound = draftRows.filter({ hasText: '입고' });
        assert((await inbound.locator('input').inputValue()) === '', '기본 기간이 없는 입고가 채워짐');
        const hint = (await inbound.locator('.sched-hint b').innerText()).trim();
        await inbound.locator('[data-apply-hint]').click();
        await page.waitForFunction((h) => [...document.querySelectorAll('#draft .sched-row')]
            .some((r) => r.innerText.includes('입고') && r.querySelector('input').value === h && r.classList.contains('changed')), hint);
        // 사람이 고친 날짜 뒤 단계(설치)는 고친 날짜부터 다시 센다
        const before = await draftRows.nth(6).locator('input').inputValue();
        const approval = await draftRows.nth(1).locator('input').inputValue();
        await draftRows.nth(0).locator('input').fill(kstToday(30));
        await page.waitForFunction((b) => document.querySelectorAll('#draft .sched-row')[1].querySelector('input').value !== b, approval);
        assert((await draftRows.nth(6).locator('input').inputValue()) === before, '사람이 정한 입고 뒤의 설치가 움직임');
        const firstTarget = await draftRows.nth(0).locator('input').inputValue();
        await shot(page, 'd13b-edit-draft');
        // 고친 날짜가 범위 밖이면 표는 그대로 두고 위에 이유만 보이며 그 칸을 가리킨다 (표를 지우면 고친 날짜가 등록할 때 빠진다)
        const orderInput = draftRows.nth(2).locator('input');
        const orderDate = await orderInput.inputValue();
        await orderInput.fill('2099-12-31');
        await page.waitForSelector('#draft [data-draft-error]');
        assert((await draftRows.count()) === 8, '초안을 받지 못하자 표가 사라짐');
        assert((await page.locator('#draft [data-draft-error]').innerText()).includes('사이로'), '범위 밖 이유가 보이지 않음');
        assert((await draftRows.nth(2).locator('.field.invalid').count()) === 1, '범위 밖 칸을 가리키지 않음');
        await shot(page, 'd13c-edit-draft-error', false);
        await orderInput.fill(orderDate);
        await page.waitForSelector('#draft [data-draft-error]', { state: 'detached' });
        assert((await draftRows.nth(2).locator('.field.invalid').count()) === 0, '고친 뒤에도 오류 표시가 남음');

        await page.click('#edit-form button[type=submit]');
        await page.waitForSelector('#summary .summary-meta');
        assert((await page.locator('#stepper .step').count()) === 8, '기본 단계 8개가 만들어지지 않음');
        assert((await page.locator('#summary').innerText()).includes('베트남 · 냉장고 · 1라인'), '위치가 저장되지 않음');
        assert((await page.locator('#stepper .step').nth(4).innerText()).includes('체크 0/3'), '체크리스트 템플릿이 복사되지 않음');
        assert((await page.locator('#stepper .step').nth(0).innerText()).includes('목표 ' + firstTarget.slice(5)), '초안 목표일로 등록되지 않음');
        assert((await page.locator('#stepper .step').nth(5).innerText()).includes('목표 ' + hint.slice(5)), '추천 입고일이 등록되지 않음');
        await page.click('.tabs [data-tab=activity]');
        assert((await page.locator('#feed').innerText()).includes('[검토] 목표일 설정 ' + firstTarget), '목표일 설정 이력이 없음');
        await page.click('.tabs [data-tab=schedule]');
    });

    await step('과제 수정 화면에 기존 값 표시', async () => {
        await page.locator('#summary a', { hasText: '수정' }).click();
        await page.waitForSelector('#edit-form');
        assert((await page.inputValue('input[name="Form.Title"]')) === '베트남 냉장고 1라인 컨베이어 개선', '과제명이 채워지지 않음');
        assert((await page.locator('#items .item-row input[data-k=Quantity]').first().inputValue()) === '3', '기기 수량이 채워지지 않음');
        await page.fill('input[name="Form.Vendor"]', '한성로보틱스');
        await page.click('#edit-form button[type=submit]');
        await page.waitForSelector('#summary .summary-meta');
        await page.click('.tabs [data-tab=info]');
        assert((await page.locator('#tab-info').innerText()).includes('한성로보틱스'), '협력사가 저장되지 않음');
    });

    await step('과제 수정 화면 — 동시 수정 뒤 손대지 않은 항목은 최신 값으로 맞추고 같은 화면에서 다시 저장', async () => {
        const id = new URL(page.url()).searchParams.get('id');
        await page.goto(BASE + '/Projects/Edit?id=' + id);
        await page.waitForSelector('#edit-form');
        const other = active = await desktop.newPage();
        other.setDefaultTimeout(10000);
        await other.goto(BASE + '/Projects/Edit?id=' + id);
        await other.fill('textarea[name="Form.Description"]', '다른 화면에서 쓴 설명');
        await other.click('#edit-form button[type=submit]');
        await other.waitForSelector('#summary .summary-meta');
        await other.close();
        active = page;

        await page.fill('input[name="Form.Vendor"]', '대성물류');
        await page.click('#edit-form button[type=submit]');
        await page.waitForSelector('#edit-form .form-error');
        assert((await page.locator('#edit-form .form-error').innerText()).includes('다른 사용자가 이 과제를 먼저 수정했습니다'), '충돌 안내가 아님');
        const note = await page.locator('#edit-form [data-field=description] [data-conflict-note]').innerText();
        assert(note.includes('다른 화면에서 쓴 설명') && note.includes('그 값으로 바꿨습니다'), '바뀐 값 안내가 아님: ' + note);
        assert((await page.inputValue('textarea[name="Form.Description"]')) === '다른 화면에서 쓴 설명', '손대지 않은 설명이 최신 값이 아님');
        assert((await page.inputValue('input[name="Form.Vendor"]')) === '대성물류', '입력한 협력사가 사라짐');
        await shot(page, 'd42-edit-conflict');
        await page.click('#edit-form button[type=submit]');
        await page.waitForSelector('#summary .summary-meta');
        await page.click('.tabs [data-tab=info]');
        const info = await page.locator('#tab-info').innerText();
        assert(info.includes('대성물류') && info.includes('다른 화면에서 쓴 설명'), '다시 저장한 내용이 아님');
    });

    await step('단계 일정 편집 — 한 시트에서 여러 단계, 기존 목표일을 바꾸면 사유 한 번, 동시 수정 뒤 같은 시트에서 다시 저장', async () => {
        // 앞 단계가 등록한 '베트남 냉장고 1라인 컨베이어 개선'(목표일 초안으로 등록)의 상세에 있다 (정보 탭이 열려 있다)
        await page.click('.tabs [data-tab=schedule]');
        const url = page.url().split('#')[0];
        await page.click('#edit-schedule');
        await page.waitForSelector(SHEET + ' .sched-row');
        const rows = page.locator(SHEET + ' .sched-row');
        assert((await rows.count()) === 8, '단계 일정 표가 8줄이 아님');
        assert((await rows.nth(1).locator('[data-gap]').innerText()).includes('+10근무일'), '간격(서버 계산)이 보이지 않음');

        // 다른 화면이 같은 표로 검토·품의 목표일을 먼저 바꾼다
        const other = active = await desktop.newPage();
        other.setDefaultTimeout(10000);
        await other.goto(url);
        await other.click('#edit-schedule');
        await other.waitForSelector(SHEET + ' .sched-row');
        const otherRows = other.locator(SHEET + ' .sched-row');
        const otherReview = kstToday(33), otherApproval = kstToday(60);
        await otherRows.nth(0).locator('input[name^=target]').fill(otherReview);
        await otherRows.nth(1).locator('input[name^=target]').fill(otherApproval);
        await other.fill(SHEET + ' textarea[name=reason]', '다른 화면의 일정 조정');
        await submitSheet(other);
        await other.close();
        active = page;

        // 이 화면: 검토(이미 정한 목표일)와 안정화를 바꾼다 → 바꾼 줄 표시와 사유 칸
        const mine = kstToday(35);
        await rows.nth(0).locator('input[name^=target]').fill(mine);
        await rows.nth(7).locator('input[name^=target]').fill(kstToday(400));
        await page.waitForSelector(SHEET + ' [data-reason]:not([hidden])');
        assert((await rows.nth(0).getAttribute('class')).includes('changed'), '바꾼 줄 표시가 없음');
        assert((await page.locator(SHEET + ' [data-reason-note]').innerText()).includes('검토, 안정화'), '사유가 필요한 단계 안내가 아님');
        await page.waitForFunction(() => !document.querySelector('dialog.sheet[open] [data-rows]').hasAttribute('aria-busy'));   // 간격 미리보기 응답
        await shot(page, 'd42b-schedule-sheet', false);

        // 저장 → 그사이 바뀐 검토가 있어 409. 둘 다 바꾼 검토는 내 값을 두고, 손대지 않은 품의는 최신 값으로 맞춘다
        await page.locator(SHEET + ' button[type=submit]').click();
        assert((await sheetError(page)).includes('다른 사용자가 먼저 수정'), '충돌 안내가 아님');
        const reviewNote = await page.locator(SHEET + ' .sched-row').nth(0).locator('[data-conflict-note]').innerText();
        assert(reviewNote.includes(otherReview) && !reviewNote.includes('그 값으로'), '둘 다 바꾼 칸 안내가 아님: ' + reviewNote);
        assert((await page.locator(SHEET + ' .sched-row').nth(0).locator('input[name^=target]').inputValue()) === mine, '내 입력이 사라짐');
        const approvalNote = await page.locator(SHEET + ' .sched-row').nth(1).locator('[data-conflict-note]').innerText();
        assert(approvalNote.includes('그 값으로 바꿨습니다'), '손대지 않은 칸을 최신 값으로 맞추지 않음: ' + approvalNote);
        assert((await page.locator(SHEET + ' .sched-row').nth(1).locator('input[name^=target]').inputValue()) === otherApproval, '품의가 최신 값이 아님');
        await shot(page, 'd42c-schedule-conflict', false);

        // 다시 저장: 이미 정한 목표일을 바꾸므로 사유 없이는 거절, 사유를 넣으면 저장
        await page.locator(SHEET + ' button[type=submit]').click();
        assert((await sheetError(page)).includes('사유'), '사유 없이 저장됨');
        await page.fill(SHEET + ' textarea[name=reason]', '고객 요청으로 일정 재조정');
        await submitSheet(page);

        assert((await page.locator('#stepper .step').nth(0).innerText()).includes('목표 ' + mine.slice(5)), '검토 목표일이 저장되지 않음');
        assert((await page.locator('#stepper .step').nth(1).innerText()).includes('목표 ' + otherApproval.slice(5)), '다른 화면의 품의 변경을 되돌림');
        await page.click('.tabs [data-tab=activity]');
        const feed = await page.locator('#feed').innerText();
        assert(feed.includes('고객 요청으로 일정 재조정') && feed.includes('[안정화] 목표일'), '변경 이력·사유가 없음');
        await page.click('.tabs [data-tab=schedule]');
    });

    await step('내 할 일', async () => {
        await page.goto(BASE + '/My');
        const text = await page.locator('main').innerText();
        assert(text.includes('안전 인증 서류 준비'), '내가 담당인 작업이 보이지 않음');
        assert(text.includes('담당 과제의 진행 단계'), '담당 과제 단계가 보이지 않음');
        await shot(page, 'd14-my');
        await page.locator('.row-link', { hasText: '안전 인증 서류 준비' }).first().click();
        await page.waitForSelector(SHEET);
        assert((await page.locator(SHEET + ' .sheet-head h2').innerText()).includes('안전 인증 서류 준비'), '작업 편집 창이 열리지 않음');
        await closeSheet(page);
    });

    await step('분석 — 리드타임·주간 보고·변경 이력', async () => {
        await page.goto(BASE + '/Insights/LeadTime');
        assert((await page.locator('.lead-table tbody tr').count()) >= 8, '리드타임 행이 부족함');
        const head = await page.locator('.lead-table thead').first().innerText();
        assert(['평균(근무일)', '0일 표본', '처음 목표 대비', '기준선 대비'].every((x) => head.includes(x)), '리드타임 새 열이 없음: ' + head);
        await shot(page, 'd15-insights-leadtime');
        await page.click('[data-zero-exclude]');
        await page.waitForURL(/zero=exclude/);
        assert((await page.locator('[data-zero-exclude].on').count()) === 1, '0일 표본 제외가 선택되지 않음');
        await shot(page, 'd15b-leadtime-zero');

        await page.locator('.tabs a', { hasText: '주간 보고' }).click();
        await page.waitForSelector('.weekly');
        const weekly = await page.locator('main').innerText();
        assert(weekly.includes('주행 시험 재실시로 일주일 순연'), '이번 주 일정 변경 사유가 없음');
        assert(weekly.includes('재협의 완료'), '이번 주 진행 내용이 없음');
        // 일정 변경은 결정 단위, 대상 수를 함께 보이고 상태·기준선·과제 완료는 따로 센다
        assert((await page.locator('[data-kpi=changes] .kpi-sub').innerText()).startsWith('대상'), '일정 변경 대상 수가 없음');
        assert((await page.locator('[data-kpi=states]').count()) === 1 && (await page.locator('[data-kpi=completed]').count()) === 1, '상태·기준선/과제 완료 KPI 가 없음');
        // 지연 과제는 변동이 없어도 카드, 밀린 항목(D+n)
        assert((await page.locator('.weekly .weekly-late li').count()) > 0, '밀린 항목이 없음');
        assert((await page.locator('[data-weekly-asof]').count()) === 0, '이번 주인데 기준일 안내가 있음');
        await shot(page, 'd16-insights-weekly');
        // 지난 주: 그 주 마지막 날 기준 안내
        await page.click('a[aria-label="이전 주"]');
        await page.waitForSelector('[data-weekly-asof]');
        assert((await page.locator('[data-weekly-asof]').innerText()).includes('진행률·현재 단계는 지금 기준'), '지난 주 기준일 안내가 없음');
        await shot(page, 'd16b-weekly-past');

        await page.locator('.tabs a', { hasText: '변경 이력' }).click();
        await page.waitForSelector('.history li');
        const total = +(await page.locator('.card-head .count').innerText());
        assert(total > 20, '이력 건수가 너무 적음: ' + total);
        await page.selectOption('select[name=kind]', 'schedule');
        await page.click('.filters button[type=submit]');
        await page.waitForURL(/kind=schedule/);
        const filtered = +(await page.locator('.card-head .count').innerText());
        assert(filtered > 0 && filtered < total, '구분 필터가 적용되지 않음');
        assert((await page.locator('.history .chip').allInnerTexts()).every((t) => t === '일정 변경'), '다른 구분이 섞여 있음');
        await shot(page, 'd17-insights-history');
    });

    await step('사유 분류 — 단계 목표일을 바꿀 때 분류를 고르면 변경 분석의 원인 파레토에 오름', async () => {
        await openProject(page, '광주 냉장고 3라인 AGV 도입');
        await stageRow(page, '안정화').click();
        await page.waitForSelector(SHEET);
        const cur = await page.inputValue(SHEET + ' input[name=targetDate]');
        assert(cur, '안정화 목표일이 없음');
        const next = new Date(Date.parse(cur + 'T00:00:00Z') + 3 * 86400e3).toISOString().slice(0, 10);
        await page.fill(SHEET + ' input[name=targetDate]', next);
        await page.fill(SHEET + ' textarea[name=reason]', '설비 부품 입고 지연');
        const options = await page.locator(SHEET + ' select[name=reasonCode] option').allInnerTexts();
        assert(options.includes('자재·납품 지연'), '사유 분류 선택지가 없음: ' + options.join(','));
        await page.selectOption(SHEET + ' select[name=reasonCode]', { label: '자재·납품 지연' });
        await shot(page, 'd15c-reason-code');
        await submitSheet(page);
        await page.goto(BASE + '/Insights/Changes');
        await page.waitForSelector('[data-pareto]');
        assert((await page.locator('[data-pareto-row="자재·납품 지연"]').count()) === 1, '파레토에 분류가 없음');
        assert(+(await page.locator('[data-kpi=decisions] .kpi-value').innerText()) > 0, '결정 수가 0');
        assert((await page.locator('[data-replan-top] tbody tr').count()) > 0, '재계획 상위 과제가 없음');
        assert((await page.locator('[data-changes-group=category] tbody tr').count()) > 0, '유형별 월간 결정 표가 없음');
        await shot(page, 'd15d-changes');
    });

    await step('추세·부하·단계 병목 — 스냅샷 추이와 법인 × 기기 수량, 사람 × 주 부하, 칸을 누르면 그 사람의 할 일(읽기 전용)', async () => {
        await page.goto(BASE + '/Insights/Trend');
        await page.waitForSelector('[data-trend-note]');
        assert((await page.locator('[data-trend] svg path.series').count()) >= 4, '추세 그래프가 없음(시작 직후 스냅샷)');
        assert((await page.locator('[data-equipment] tbody tr').count()) >= 1, '법인 × 기기 수량 표가 없음');
        await shot(page, 'd15e-trend');
        await page.click('[data-range-year]');
        await page.waitForURL(/range=1y/);
        await page.waitForSelector('[data-trend-note]');

        await page.goto(BASE + '/Insights/Load');
        await page.waitForSelector('[data-load-note]');
        assert((await page.locator('[data-load-person]').count()) >= 2, '부하 표에 사람이 부족함');
        assert((await page.locator('[data-load] thead th').count()) === 9, '부하 표가 8주가 아님');
        await shot(page, 'd15f-load');
        const other = page.locator('[data-load-person]').filter({ hasNotText: '평영' }).locator('.load-cell a').first();
        const who = await other.locator('xpath=ancestor::tr').getAttribute('data-load-person');
        await other.click();
        await page.waitForSelector('[data-my-readonly]');
        assert((await page.locator('h1').innerText()).includes(who), '다른 사람의 할 일 제목이 아님');
        assert((await page.locator('[data-quick]').count()) === 0 && (await page.locator('#news').count()) === 0, '읽기 전용인데 바로 갱신·새 소식이 보임');
        await shot(page, 'd15g-my-readonly');

        await page.goto(BASE + '/Insights/LeadTime');
        assert((await page.locator('.lead-table thead').first().innerText()).includes('진행 중 · 평균 초과'), '단계 병목 열이 없음');
        assert((await page.locator('[data-lead-active]').allInnerTexts()).some((t) => t.includes('진행 중')), '진행 중 과제 수가 없음');
    });

    await step('기간 보고 — 월·직접 지정 기간, 조직·변동만 조건, 지연·위험 요약, 변경 이력 Excel', async () => {
        await page.goto(BASE + '/Insights/Weekly');
        await page.waitForSelector('[data-delay-summary]');
        assert((await page.locator('[data-delay-summary] tbody tr').count()) >= 2, '지연·위험 요약이 부족함');
        await page.click('[data-period-month]');
        await page.waitForURL(/period=month/);
        assert((await page.locator('.weekly dt').allInnerTexts()).some((t) => t.includes('다음 달 계획')) || (await page.locator('.weekly').count()) === 0, '월간 보고 문구가 아님');
        assert((await page.locator('a[aria-label="이전 달"]').count()) === 1, '이전 달 단추가 없음');
        await shot(page, 'd16c-report-month');
        await page.click('[data-period-custom]');
        await page.waitForURL(/period=custom/);
        await page.fill('#report-filters input[name=from]', kstToday(-20));
        await page.fill('#report-filters input[name=to]', kstToday(0));
        await page.selectOption('#report-filters select[name=org]', { label: '물류기술그룹' });
        await page.check('#report-filters input[name=changed]');
        await page.click('#report-filters button[type=submit]');
        await page.waitForURL(/changed=true/);
        assert((await page.locator('[data-report-range]').innerText()).includes(kstToday(-20)), '직접 지정 기간이 아님');
        assert((await page.locator('[data-weekly-quiet]').count()) === 0, '변동 있는 과제만인데 접힌 표가 있음');
        await shot(page, 'd16d-report-custom');
        const xlsx = await desktop.request.get(BASE + '/Insights/Weekly?handler=Excel&period=month');
        assert(xlsx.ok(), '월간 보고 Excel 실패 ' + xlsx.status());
        fs.writeFileSync(path.join(EXPORTS, '월간보고.xlsx'), await xlsx.body());

        await page.goto(BASE + '/Insights/History?kind=schedule');
        await page.waitForSelector('#history-excel');
        const href = await page.getAttribute('#history-excel', 'href');
        assert(href.includes('handler=Excel') && href.includes('kind=schedule'), '이력 Excel 링크에 조건이 없음: ' + href);
        const res = await desktop.request.get(BASE + href);
        assert(res.ok(), '변경 이력 Excel 실패 ' + res.status());
        const body = await res.body();
        fs.writeFileSync(path.join(EXPORTS, '변경이력.xlsx'), body);
        if (!process.env.FAKE_EXPORT) assert(body.subarray(0, 2).toString('latin1') === 'PK', '변경 이력 Excel 형식이 아님');

        // 대시보드: 조직 조건과 막대 범위 안내
        await page.goto(BASE + '/?scope=focus');
        await page.waitForSelector('#dash-filters select[name=org]');
        assert((await page.locator('[data-bars-scope]').count()) === 1, '막대 범위 안내가 없음');
        const bar = await page.getAttribute('[data-bar-category]', 'href');
        assert(bar.includes('current=true'), '막대 링크가 같은 범위가 아님: ' + bar);
        await page.goto(BASE + bar);
        await page.waitForSelector('[data-special-note]');
        await page.goto(BASE + '/?reset=1');

        await page.goto(BASE + '/Admin#reason');
        await page.waitForSelector('.columns .node');
        assert((await page.locator('.columns .node').count()) >= 7, '지연 사유 분류가 부족함');
        await shot(page, 'd18e-admin-reason');
    });

    await step('활동 더 보기 — 최근 50건만 먼저 싣고 나머지는 나눠 받음, 이 과제 전체 이력으로 이동', async () => {
        await openProject(page, '광주 냉장고 3라인 AGV 도입');
        // 활동을 50건 넘게 쌓는다: 미완료 단계의 체크를 켜고 끄기(짝수 번이라 원래 상태로 돌아온다). 주간 보고에 오르지 않는 활동이다
        const added = await page.evaluate(async () => {
            const id = +new URLSearchParams(location.search).get('id');
            const token = document.querySelector('input[name="__RequestVerificationToken"]').value;
            const call = async (handler, body) => {
                const r = await fetch('/Projects/Detail?handler=' + handler + (body ? '' : '&id=' + id), body
                    ? { method: 'POST', headers: { 'Content-Type': 'application/json', Accept: 'application/json', 'X-CSRF-TOKEN': token }, body: JSON.stringify(body) }
                    : { headers: { Accept: 'application/json' } });
                const b = await r.json();
                if (!b.ok) throw new Error(handler + ': ' + (b.error || r.status));
                return b;
            };
            let p = (await call('Data')).project;
            let stage = p.stages.find((x) => !x.completedDate && x.checks.length);
            if (!stage) {
                stage = p.stages.find((x) => !x.completedDate);
                p = (await call('SaveCheck', { stageId: stage.id, id: null, text: '활동 더 보기 확인용' })).project;
                stage = p.stages.find((x) => x.id === stage.id);
            }
            const check = stage.checks[0];
            let done = check.done;
            for (let i = 0; i < 60; i++) { done = !done; await call('ToggleCheck', { id: check.id, done }); }
            return { id, total: (await call('Data')).project.activityTotal };
        });
        assert(added.total > 60, '활동이 쌓이지 않음: ' + added.total);
        await page.reload();
        await page.click('.tabs [data-tab=activity]');
        await page.waitForSelector('#feed > li');
        assert((await page.locator('#feed > li').count()) === 50, '처음에 50건이 아님: ' + (await page.locator('#feed > li').count()));
        assert((await page.locator('#activity-count').innerText()).trim() === String(added.total), '탭 숫자가 전체 건수가 아님');
        assert(await page.locator('#feed-more').isVisible(), '더 보기 단추가 없음');
        assert((await page.locator('#feed-status').innerText()).includes('전체 ' + added.total + '건'), '건수 안내가 없음');
        await page.locator('.feed-foot').scrollIntoViewIfNeeded();
        await shot(page, 'd46-activity-more', false);
        const firstIds = await page.locator('#feed > li .feed-text').allInnerTexts();
        await page.click('#feed-more');
        await page.waitForFunction(() => document.querySelectorAll('#feed > li').length > 50);
        // 끝까지 받으면 단추가 사라지고 전체 건수만큼 쌓인다 (겹치거나 빠진 항목 없이)
        while (await page.locator('#feed-more').isVisible()) {
            const n = await page.locator('#feed > li').count();
            await page.click('#feed-more');
            await page.waitForFunction((k) => document.querySelectorAll('#feed > li').length > k || document.getElementById('feed-more').hidden, n);
        }
        assert((await page.locator('#feed > li').count()) === added.total, '모두 받은 뒤 건수가 다름: ' + (await page.locator('#feed > li').count()));
        const allTexts = await page.locator('#feed > li .feed-text').allInnerTexts();
        assert(firstIds.every((t, i) => allTexts[i] === t), '처음 50건 순서가 바뀜');
        // 새 활동을 남기면 받아 둔 옛 활동을 잃지 않고 맨 위에 더해진다
        await page.fill('#comment-text', '더 보기 뒤 남긴 메모');
        await page.click('#comment-btn');
        await page.waitForFunction((k) => document.querySelectorAll('#feed > li').length === k, added.total + 1);
        assert((await page.locator('#feed > li .feed-text').first().innerText()).includes('더 보기 뒤 남긴 메모'), '새 메모가 맨 위가 아님');

        // 그 사이 다른 화면이 한 쪽(50건)보다 많이 남기면 변경 응답의 최근 목록이 받아 둔 목록과 겹치지 않는다 —
        // 가운데가 빈 채 이어 붙이지 않고 최근 목록부터 다시 쌓아, '더 보기'로 빠짐없이 받는지 본다
        const gap = 52;   // 짝수 번 켜고 꺼서 체크 상태는 그대로
        await page.evaluate(async (n) => {
            const id = +new URLSearchParams(location.search).get('id');
            const token = document.querySelector('input[name="__RequestVerificationToken"]').value;
            const r = await fetch('/Projects/Detail?handler=Data&id=' + id, { headers: { Accept: 'application/json' } });
            const p = (await r.json()).project;
            const check = p.stages.find((x) => !x.completedDate && x.checks.length).checks[0];
            let done = check.done;
            for (let i = 0; i < n; i++) {
                done = !done;
                const res = await fetch('/Projects/Detail?handler=ToggleCheck', {
                    method: 'POST', headers: { 'Content-Type': 'application/json', Accept: 'application/json', 'X-CSRF-TOKEN': token },
                    body: JSON.stringify({ id: check.id, done }),
                });
                if (!res.ok) throw new Error('ToggleCheck ' + res.status);
            }
        }, gap);
        await page.fill('#comment-text', '빈틈 확인 메모');
        await page.click('#comment-btn');
        await page.waitForFunction(() => (document.querySelector('#feed > li .feed-text') || {}).textContent === '빈틈 확인 메모');
        const expectedTotal = added.total + 1 + gap + 1;
        assert((await page.locator('#feed > li').count()) === 50, '이어지지 않는 옛 목록이 남음: ' + (await page.locator('#feed > li').count()));
        assert(await page.locator('#feed-more').isVisible(), '다시 쌓은 뒤 더 보기 단추가 없음');
        while (await page.locator('#feed-more').isVisible()) {
            const n = await page.locator('#feed > li').count();
            await page.click('#feed-more');
            await page.waitForFunction((k) => document.querySelectorAll('#feed > li').length > k || document.getElementById('feed-more').hidden, n);
        }
        assert((await page.locator('#feed > li').count()) === expectedTotal, '빈틈 뒤 모두 받은 건수가 다름: ' + (await page.locator('#feed > li').count()) + ' / ' + expectedTotal);
        assert((await page.locator('#activity-count').innerText()).trim() === String(expectedTotal), '탭 숫자가 전체 건수가 아님(빈틈 뒤)');

        // 이 과제 전체 이력 → 과제 조건이 걸린 변경 이력
        await page.click('#feed-history');
        await page.waitForURL(/\/Insights\/History\?projectId=/);
        await page.waitForSelector('#history-project');
        assert((await page.locator('#history-project').innerText()).includes('광주 냉장고 3라인 AGV 도입'), '과제 조건 표시가 없음');
        const total = +(await page.locator('.card-head .count').innerText());
        assert(total === expectedTotal, '과제 이력 건수가 다름: ' + total + ' / ' + expectedTotal);
        const titles = await page.locator('.history .history-body > a').allInnerTexts();
        assert(titles.length === 50 && titles.every((t) => t === '광주 냉장고 3라인 AGV 도입'), '다른 과제 이력이 섞임');
        assert((await page.locator('.pager').innerText()).includes('1 / '), '쪽 이동이 없음');
        await page.locator('.pager a', { hasText: '다음' }).click();
        await page.waitForURL(/p=2/);
        assert(page.url().includes('projectId=' + added.id), '쪽을 넘기면 과제 조건이 풀림: ' + page.url());
        await page.selectOption('select[name=kind]', 'progress');
        await page.click('.filters button[type=submit]');
        await page.waitForURL(/kind=progress/);
        assert(page.url().includes('projectId=' + added.id), '조회하면 과제 조건이 풀림');
        assert((await page.locator('.history .chip').allInnerTexts()).every((t) => t === '진행'), '다른 구분이 섞임');
        await shot(page, 'd46b-history-project');
        await page.click('#history-all');
        await page.waitForURL((u) => u.pathname === '/Insights/History' && !u.search.includes('projectId'));
        assert((await page.locator('#history-project').count()) === 0, '모든 과제로 돌아가지 않음');
        // 여러 과제의 이력은 1년까지 — 넘게 고르면 시작일을 맞추고 안내
        await page.goto(BASE + '/Insights/History?from=2020-01-01&to=' + kstToday());
        assert(await page.locator('#history-notice').isVisible(), '기간 상한 안내가 없음');
    });

    await step('작업 시트 진행 내용 더 보기 — 빠르게 두 번 눌러도 같은 활동이 두 번 붙지 않음', async () => {
        await openProject(page, '광주 냉장고 3라인 AGV 도입');
        // 한 작업에 진행 내용을 한 쪽(50건)보다 많이 남기고, 그 작업의 활동 전체 건수를 센다
        const total = await page.evaluate(async () => {
            const id = +new URLSearchParams(location.search).get('id');
            const token = document.querySelector('input[name="__RequestVerificationToken"]').value;
            const get = async (u) => (await fetch(u, { headers: { Accept: 'application/json' } })).json();
            const p = (await get('/Projects/Detail?handler=Data&id=' + id)).project;
            const stage = p.stages.find((s) => s.works.some((w) => w.title === '주행 시험'));
            const work = stage.works.find((w) => w.title === '주행 시험');
            for (let i = 0; i < 55; i++) {
                const res = await fetch('/Projects/Detail?handler=Comment', {
                    method: 'POST', headers: { 'Content-Type': 'application/json', Accept: 'application/json', 'X-CSRF-TOKEN': token },
                    body: JSON.stringify({ projectId: id, stageId: stage.id, workId: work.id, text: '진행 내용 ' + (i + 1) }),
                });
                if (!res.ok) throw new Error('Comment ' + res.status);
            }
            let n = 0, before = null;
            for (;;) {
                const r = await get('/Projects/Detail?handler=Activities&id=' + id + '&workId=' + work.id + (before ? '&beforeId=' + before : ''));
                n += r.activities.length;
                if (!r.more || !r.activities.length) return n;
                before = r.activities[r.activities.length - 1].id;
            }
        });
        assert(total > 55, '작업 활동이 쌓이지 않음: ' + total);
        await page.reload();
        await page.waitForSelector('#gantt .g-row.work');
        // '더 보기' 응답을 늦춰 두 번째 누름이 응답 전에 들어가게 한다
        const slow = (u) => u.href.includes('handler=Activities') && u.href.includes('beforeId=');
        await page.route(slow, async (r) => { await new Promise((res) => setTimeout(res, 600)); await r.continue(); });
        try {
            await workRow(page, '주행 시험').locator('.g-name').click();
            await page.waitForSelector(SHEET + ' [data-work-more] button');
            const items = () => page.locator(SHEET + ' [data-feed] > li:not([data-work-more])').count();
            assert((await items()) === 50, '처음에 50건이 아님: ' + (await items()));
            await page.locator(SHEET + ' [data-work-more] button').dblclick();
            await page.waitForFunction(() => document.querySelectorAll('dialog.sheet[open] [data-feed] > li:not([data-work-more])').length > 50);
            await page.waitForTimeout(900);     // 두 번째 요청이 나갔다면 그 응답까지 기다린다
            while (await page.locator(SHEET + ' [data-work-more] button').count()) {
                const n = await items();
                await page.locator(SHEET + ' [data-work-more] button').click();
                await page.waitForFunction((k) => document.querySelectorAll('dialog.sheet[open] [data-feed] > li:not([data-work-more])').length > k, n);
            }
            const texts = await page.locator(SHEET + ' [data-feed] > li .feed-text').allInnerTexts();
            assert(texts.length === total, '모두 받은 뒤 건수가 다름(겹침): ' + texts.length + ' / ' + total);
            const notes = texts.filter((t) => /^진행 내용 \d+$/.test(t));
            assert(notes.length === 55 && new Set(notes).size === 55, '같은 진행 내용이 두 번 붙음');
            await closeSheet(page);
        } finally {
            await page.unroute(slow);
        }
    });

    // ── 협업: 관심 과제·알림·멘션·활동 조건·댓글 고치기 ──
    // 다른 사용자는 X-Forwarded-For 로 다른 PC 를 흉내 낸다(개발 환경은 같은 서버의 프록시 머리글을 믿는다 — appsettings.Development.json).
    // 이 단계들은 '광주 2공장 물류 동선 Simulation' 에 댓글을 남기고, 평영이 그 과제를 관심 과제로 둔다
    const COLLAB = '광주 2공장 물류 동선 Simulation';

    await step('관심 과제 — 상세·목록의 별을 누르면 눌린 상태로 바뀌고 내 할 일에 관심 과제로 보임', async () => {
        await openProject(page, COLLAB);
        const star = page.locator('#watch-btn');
        assert((await star.getAttribute('aria-pressed')) === 'false', '처음부터 관심 과제로 표시됨');
        await star.click();
        await page.waitForFunction(() => document.getElementById('watch-btn').getAttribute('aria-pressed') === 'true');
        assert((await star.getAttribute('aria-label')).includes('빼기'), '별의 이름이 바뀌지 않음');
        await page.goto(BASE + '/Projects?state=all');
        const row = page.locator('.prow', { hasText: COLLAB });
        assert((await row.locator('[data-watch]').getAttribute('aria-pressed')) === 'true', '목록의 별이 눌려 있지 않음');
        // 목록에서 다른 과제의 별을 눌러도 행(상세)으로 넘어가지 않는다
        const other = page.locator('.prow', { hasText: '멕시코 에어컨 용기 표준화' }).locator('[data-watch]');
        await other.click();
        await page.waitForFunction(() => [...document.querySelectorAll('.prow')].some((r) => r.innerText.includes('멕시코 에어컨 용기 표준화') && r.querySelector('[data-watch]').getAttribute('aria-pressed') === 'true'));
        assert(new URL(page.url()).pathname === '/Projects', '별을 누르니 상세로 넘어감: ' + page.url());
        await shot(page, 'd48-watch-list', false);
        await other.click();
        await page.waitForFunction(() => [...document.querySelectorAll('.prow')].some((r) => r.innerText.includes('멕시코 에어컨 용기 표준화') && r.querySelector('[data-watch]').getAttribute('aria-pressed') === 'false'));
        await page.goto(BASE + '/My');
        const watched = await page.locator('#watched').innerText();
        assert(watched.includes(COLLAB) && !watched.includes('멕시코 에어컨 용기 표준화'), '내 할 일의 관심 과제가 다름: ' + watched);
    });

    await step('알림 — 다른 사람이 나를 알릴 사람으로 고르면 종에 읽지 않은 수, 새 소식·새 활동 표시, 알림을 누르면 그 활동을 강조', async () => {
        const otherPc = await browser.newContext({
            viewport: { width: 1440, height: 900 }, locale: 'ko-KR', timezoneId: 'Asia/Seoul', extraHTTPHeaders: { 'X-Forwarded-For': '10.77.0.21' },
        });
        const c = await otherPc.newPage();
        watch(c, 'colleague');
        c.setDefaultTimeout(10000);
        active = c;
        await c.goto(BASE + '/');
        await c.waitForURL(/\/Welcome/);
        await c.fill('input[name=Name]', '동료');
        await c.click('button[type=submit]');
        await c.waitForSelector('.kpis');
        await openProject(c, COLLAB);
        await c.click('.tabs [data-tab=activity]');
        // 본문에서 '@' 를 치면 이름 칸으로 옮겨 목록에서 고른다(이름에 공백이 있을 수 있어 본문을 파싱하지 않는다)
        await c.fill('#comment-text', '모델 검증 결과 공유합니다 ');
        await c.focus('#comment-text');
        await c.keyboard.press('End');
        await c.keyboard.type('@');
        await c.waitForFunction(() => document.activeElement && document.activeElement.matches('[data-mention-input]'));
        await c.keyboard.type('없는사람');
        await c.keyboard.press('Enter');
        assert((await c.locator('#comment-mentions [data-mention-hint]').innerText()).includes('목록에 없습니다'), '목록에 없는 이름을 받음');
        await c.fill('#comment-mentions [data-mention-input]', '평영');
        await c.keyboard.press('Enter');
        assert((await c.locator('#comment-mentions .mention-chip').allInnerTexts()).join() === '@평영', '고른 사람 칩이 없음');
        assert((await c.inputValue('#comment-text')).includes('@평영'), '본문에 @이름이 들어가지 않음');
        await shot(c, 'd49-mention-pick', false);
        await c.click('#comment-btn');
        await c.waitForSelector('#feed > li .mention-chip');
        assert((await c.locator('#comment-mentions .mention-chip').count()) === 0, '등록 뒤 알릴 사람이 남음');
        await c.fill('#comment-text', '2차 시뮬레이션은 다음 주에 돌립니다');
        await c.click('#comment-btn');
        await c.waitForFunction(() => document.querySelector('#feed > li .feed-text').textContent.includes('2차 시뮬레이션'));
        await otherPc.close();
        active = page;

        // 평영: 종에 읽지 않은 수(멘션 1 + 관심 과제 1), 목록의 새 활동, 내 할 일의 새 소식
        await page.goto(BASE + '/Projects?state=all');
        await page.waitForFunction(() => !document.getElementById('bell-count').hidden && document.getElementById('bell-count').textContent === '2');
        assert((await page.getAttribute('#bell', 'aria-label')).includes('읽지 않은 2건'), '종의 이름에 읽지 않은 수가 없음');
        assert((await page.locator('.prow', { hasText: COLLAB }).innerText()).includes('새 활동 2'), '목록에 새 활동 수가 없음');
        await shot(page, 'd50-bell-new-count', false);
        await page.goto(BASE + '/My');
        const news = await page.locator('#news').innerText();
        assert(news.includes('모델 검증 결과') && news.includes('멘션') && news.includes('2차 시뮬레이션') && news.includes('관심 과제'), '새 소식이 다름: ' + news);
        await shot(page, 'd50b-my-news', false);

        await page.click('#bell');
        await page.waitForURL(/\/My\/Notifications/);
        assert((await page.locator('.note-list > li.unread').count()) === 2, '읽지 않은 알림이 2건이 아님');
        await shot(page, 'd51-notifications', false);
        await page.locator('.note-list > li', { hasText: '모델 검증 결과' }).locator('.note-link').click();
        await page.waitForURL(/\/Projects\/Detail\?id=\d+#a\d+$/);
        await page.waitForSelector('#feed > li.hit');
        const hit = await page.locator('#feed > li.hit').innerText();
        assert(hit.includes('모델 검증 결과') && hit.includes('@평영'), '강조한 활동이 다름: ' + hit);
        assert((await page.locator('#feed .feed-divider').innerText()).includes('여기부터'), '새 활동 구분선이 없음');
        assert((await page.locator('#activity-new').innerText()).trim() === '새 2', '활동 탭에 새 활동 수가 없음');
        await shot(page, 'd52-activity-deeplink', false);
        // 누른 알림은 읽음 — 남은 하나는 '모두 읽음'
        await page.goto(BASE + '/My/Notifications');
        assert((await page.locator('.note-list > li.unread').count()) === 1, '누른 알림이 읽음이 되지 않음');
        await page.click('#read-all');
        await page.waitForFunction(() => document.querySelectorAll('.note-list > li.unread').length === 0 && document.getElementById('bell-count').hidden);
        assert(await page.locator('#read-all').isDisabled(), '모두 읽은 뒤에도 단추가 켜져 있음');
    });

    await step('활동 조건·댓글 고치기 — 종류 칩·단계·검색으로 거르고 조건대로 더 보기, 내 댓글은 고치고 지움', async () => {
        await openProject(page, COLLAB);
        await page.click('.tabs [data-tab=activity]');
        await page.fill('#comment-text', 'e2e 확인용 첫 글');
        await page.click('#comment-btn');
        await page.waitForSelector('#feed > li:has-text("e2e 확인용 첫 글") [data-edit-comment]');
        // 고치기: 원문은 기록으로 남고 '(수정됨)'
        await page.locator('#feed > li', { hasText: 'e2e 확인용 첫 글' }).locator('[data-edit-comment]').click();
        await page.waitForSelector(SHEET + ' textarea[name=text]');
        await page.fill(SHEET + ' textarea[name=text]', 'e2e 확인용 고친 글');
        await submitSheet(page);
        await page.waitForSelector('#feed > li:has-text("e2e 확인용 고친 글") .edited');
        assert((await page.locator('#feed').innerText()).includes('댓글 수정'), '수정 기록이 없음');
        // '해당 댓글 보기' 는 그 댓글을 강조한다
        await page.locator('#feed > li', { hasText: '댓글 수정' }).first().locator('[data-goto]').click();
        await page.waitForSelector('#feed > li.hit:has-text("e2e 확인용 고친 글")');

        // 종류 칩: 댓글만
        await page.click('#feed-filters [data-kind=comment]');
        await page.waitForFunction(() => document.getElementById('feed-status').textContent.includes('조건에 맞는'));
        const comments = await page.locator('#feed').innerText();
        assert(comments.includes('e2e 확인용 고친 글') && !comments.includes('과제 등록'), '댓글 조건이 맞지 않음');
        // 검색까지 겹치면 둘 다 맞는 것만
        await page.fill('#feed-q', '고친 글');
        await page.waitForFunction(() => document.querySelectorAll('#feed > li').length === 1 && document.querySelector('#feed > li').innerText.includes('고친 글'));
        await shot(page, 'd53-activity-filter', false);
        // 단계 조건: 그 단계의 활동만
        // 검색어는 입력을 멈춘 뒤(0.3초) 조회한다 — 지운 결과가 그려진 다음에 조건을 바꾼다
        await page.fill('#feed-q', '');
        await page.waitForFunction(() => document.querySelectorAll('#feed > li').length > 1);
        await page.click('#feed-filters [data-kind=""]');
        await page.selectOption('#feed-stage', { label: '모델링' });
        await page.waitForFunction(() => document.getElementById('feed-status').textContent.includes('조건에 맞는'));
        const metas = await page.locator('#feed > li .feed-meta').allInnerTexts();
        assert(metas.length > 0 && metas.every((t) => t.includes('모델링')), '단계 조건이 맞지 않음: ' + metas.join(' / '));
        await page.selectOption('#feed-stage', '');
        await page.waitForFunction(() => !document.getElementById('feed-status').textContent.includes('조건에 맞는'));

        // 지우기: 자리 표시만 남는다
        await page.locator('#feed > li', { hasText: 'e2e 확인용 고친 글' }).locator('[data-delete-comment]').click();
        await page.locator('dialog[open] [data-yes]').click();
        await page.waitForSelector('#feed > li:has-text("삭제된 댓글")');
        assert((await page.locator('#feed').innerText()).includes('댓글 삭제'), '삭제 기록이 없음');
        assert(!(await page.locator('#feed').innerText()).includes('e2e 확인용 고친 글'), '지운 글이 남음');
        await shot(page, 'd54-comment-deleted', false);

        // 조건을 건 채로 더 보기: 체크를 켜고 끈 활동이 많은 과제에서 '진행' 만
        await openProject(page, '광주 냉장고 3라인 AGV 도입');
        await page.click('.tabs [data-tab=activity]');
        await page.click('#feed-filters [data-kind=progress]');
        await page.waitForFunction(() => document.querySelectorAll('#feed > li').length === 50 && !document.getElementById('feed-more').hidden);
        await page.click('#feed-more');
        await page.waitForFunction(() => document.querySelectorAll('#feed > li').length > 50);
        // 이 과제에는 '더 보기' 단계가 남긴 메모(댓글)가 있다 — 진행 조건에서는 빠진다
        const texts = await page.locator('#feed > li .feed-text').allInnerTexts();
        assert(!texts.some((t) => t.includes('메모')), '진행이 아닌 활동(댓글)이 섞임');
        assert(texts.some((t) => t.includes('체크')), '체크 활동이 없음');
    });

    await step('대시보드 간트 — 과제가 많아도 페이지가 늘지 않고 간트 안에서 스크롤, 머리글은 붙어 있음', async () => {
        // 진행 과제를 잠시 늘린다(복제) — 끝나면 지운다
        await openProject(page, '광주 냉장고 3라인 AGV 도입');
        const post = (handler, body) => page.evaluate(async ([h, b]) => {
            const token = document.querySelector('input[name="__RequestVerificationToken"]').value;
            const r = await fetch('/Projects/Detail?handler=' + h, {
                method: 'POST', headers: { 'Content-Type': 'application/json', Accept: 'application/json', 'X-CSRF-TOKEN': token }, body: JSON.stringify(b),
            });
            const x = await r.json();
            if (!x.ok) throw new Error(h + ': ' + (x.error || r.status));
            return x;
        }, [handler, body]);
        const src = +new URL(page.url()).searchParams.get('id');
        const ids = [];
        try {
            for (let i = 0; i < 8; i++) {
                const r = await post('Clone', { projectId: src, title: '간트 스크롤 확인 ' + (i + 1), startDate: null, includeWorks: false });
                ids.push(+new URL(r.redirect, BASE).searchParams.get('id'));
            }
            await page.setViewportSize({ width: 1440, height: 640 });
            await page.goto(BASE + '/');
            await page.waitForSelector('#gantt.gantt-host.tall .g-row');
            const g = await page.evaluate(() => {
                const s = document.querySelector('#gantt .g-scroll');
                return { sh: s.scrollHeight, ch: s.clientHeight, max: getComputedStyle(s).maxHeight, rows: document.querySelectorAll('#gantt .g-row').length };
            });
            assert(g.rows >= 9, '간트 행이 늘지 않음: ' + g.rows);
            assert(g.max !== 'none' && g.ch <= parseFloat(g.max), '간트 높이 제한이 없음: ' + JSON.stringify(g));
            assert(g.sh > g.ch + 100, '간트 안에서 스크롤되지 않음: ' + JSON.stringify(g));
            // 안쪽으로 내려도 날짜 머리글은 위에 붙어 있고, 맨 아래 행까지 내려 볼 수 있다
            await page.evaluate(() => { document.querySelector('#gantt .g-scroll').scrollTop = 99999; });
            const pos = await page.evaluate(() => {
                const s = document.querySelector('#gantt .g-scroll').getBoundingClientRect();
                const h = document.querySelector('#gantt .g-head').getBoundingClientRect();
                const rows = document.querySelectorAll('#gantt .g-row');
                const last = rows[rows.length - 1].getBoundingClientRect();
                return { scroll: s.top, head: h.top, lastBottom: last.bottom, scrollBottom: s.bottom };
            });
            assert(Math.abs(pos.head - pos.scroll) <= 1, '머리글이 붙어 있지 않음: ' + JSON.stringify(pos));
            assert(pos.lastBottom <= pos.scrollBottom + 1, '마지막 행이 보이지 않음: ' + JSON.stringify(pos));
            await page.locator('#gantt').scrollIntoViewIfNeeded();
            await shot(page, 'd47-dashboard-gantt-scroll', false);
        } finally {
            await page.setViewportSize({ width: 1440, height: 900 });
            await openProject(page, '광주 냉장고 3라인 AGV 도입');
            for (const id of ids) await post('Delete', { id });
        }
        await page.goto(BASE + '/');
        assert((await page.locator('#gantt .g-row', { hasText: '간트 스크롤 확인' }).count()) === 0, '확인용 복제 과제가 남음');
    });

    await step('Excel·PDF 내보내기', async () => {
        const targets = [
            ['과제목록.xlsx', '/Projects?handler=Excel&state=all'],
            ['과제일정.xlsx', '/Projects/Detail?handler=Excel&id=1'],
            ['과제일정.pdf', '/Projects/Detail?handler=Pdf&id=1'],
            ['주간보고.xlsx', '/Insights/Weekly?handler=Excel'],
            ['주간보고.pdf', '/Insights/Weekly?handler=Pdf'],
        ];
        for (const [name, url] of targets) {
            const res = await desktop.request.get(BASE + url);
            assert(res.ok(), name + ' 응답 ' + res.status() + ' ' + (await res.text()).slice(0, 200));
            const body = await res.body();
            fs.writeFileSync(path.join(EXPORTS, name), body);
            if (!process.env.FAKE_EXPORT) {
                const magic = body.subarray(0, 4).toString('latin1');
                assert(name.endsWith('.pdf') ? magic === '%PDF' : magic.startsWith('PK'), name + ' 형식이 아님: ' + magic);
                assert(body.length > 3000, name + ' 이 너무 작음: ' + body.length);
            }
        }
    });

    await step('관리 — 계층 추가·순서 이동, 체크 항목, 조직, 사용자 소속', async () => {
        await page.goto(BASE + '/Admin');
        await page.waitForSelector('.columns .node');
        const corpForm = page.locator('form[data-add="1"]');
        await corpForm.locator('input[name=name]').fill('인도');
        await corpForm.locator('button[type=submit]').click();
        await page.waitForFunction(() => [...document.querySelectorAll('.column:first-child .node-name')].some((n) => n.textContent === '인도'));
        await page.locator('.column').first().locator('.node', { hasText: '인도' }).locator('[data-move$=":-1"]').click();
        await page.waitForFunction(() => {
            const names = [...document.querySelectorAll('.column:first-child .node-name')].map((n) => n.textContent);
            return names.indexOf('인도') === names.length - 2;
        });

        await page.click('#admin-tabs [data-kind=category]');
        await page.waitForSelector('.columns .node');
        assert((await page.locator('.columns .column').count()) === 3, '유형·단계·체크 항목 3열이 아님');
        await page.locator('.column').nth(1).locator('.node-main', { hasText: '검수' }).click();
        await page.waitForFunction(() => document.querySelectorAll('.column')[2].innerText.includes('FAT 성적서 확인'));
        await shot(page, 'd18-admin-category');

        await page.click('#admin-tabs [data-kind=org]');
        await page.waitForFunction(() => document.querySelector('.columns') && document.querySelector('.columns').innerText.includes('자동화파트'));
        const partForm = page.locator('form[data-add="2"]');
        await partForm.locator('input[name=name]').fill('시뮬레이션파트');
        await partForm.locator('button[type=submit]').click();
        await page.waitForFunction(() => document.querySelector('.columns').innerText.includes('시뮬레이션파트'));
        await shot(page, 'd19-admin-org');

        await page.click('#admin-tabs [data-kind=user]');
        await page.waitForFunction(() => document.querySelector('.columns') && document.querySelector('.columns').innerText.includes('평영'));
        await page.locator('.node', { hasText: '신규담당' }).locator('.node-main').click();
        await page.waitForSelector(SHEET + ' [data-org-group]');
        await page.selectOption(SHEET + ' [data-org-group]', { label: '물류기술그룹' });
        await page.selectOption(SHEET + ' [data-org-part]', { label: '시뮬레이션파트' });
        await shot(page, 'd20-admin-user', false);
        await submitSheet(page);
        await page.waitForFunction(() => [...document.querySelectorAll('.node')].some((n) => n.innerText.includes('신규담당') && n.innerText.includes('물류기술그룹 · 시뮬레이션파트')));

        // 사용자별 접속 IP: 마지막 접속 표시, 직접 연결·해제 (지금 이 기기는 해제할 수 없다)
        assert((await page.locator('.node', { hasText: '평영' }).innerText()).includes('최근 접속'), '마지막 접속일이 없음');
        await page.locator('.node', { hasText: '평영' }).locator('.node-main').click();
        await page.waitForSelector(SHEET + ' [data-ips]');
        const ips = page.locator(SHEET + ' [data-ips]');
        assert((await ips.innerText()).includes('지금 이 기기'), '지금 기기 표시가 없음');
        assert((await ips.locator('[data-remove-ip]').count()) === 0, '지금 기기의 IP 에 해제 단추가 있음');
        await page.fill(SHEET + ' input[name=newIp]', '10');
        await page.click(SHEET + ' [data-add-ip]');
        assert((await sheetError(page)).includes('IP 주소 형식'), '잘못된 IP 를 받음');
        await page.fill(SHEET + ' input[name=newIp]', '10.20.30.40');
        await page.press(SHEET + ' input[name=newIp]', 'Enter');
        await page.waitForFunction(() => document.querySelector('dialog.sheet[open] [data-ips]').innerText.includes('10.20.30.40'));
        assert(await page.locator(SHEET).isVisible(), 'Enter 로 시트가 저장·닫힘');
        await shot(page, 'd20b-admin-user-ip', false);
        await ips.locator('[data-remove-ip="10.20.30.40"]').click();
        await page.locator('dialog.sheet[open] [data-yes]').click();
        await page.waitForFunction(() => !document.querySelector('dialog.sheet[open] [data-ips]').innerText.includes('10.20.30.40'));
        await closeSheet(page);
        assert((await page.locator('#no-admin').count()) === 0, '관리자가 있는데 관리자 없음 안내가 뜸');

        await page.click('#admin-tabs [data-kind=holiday]');
        await page.waitForFunction(() => document.querySelector('.columns') && document.querySelector('.columns').innerText.includes('한글날'));
    });

    await step('관리 — 단계 템플릿 기본 기간(근무일) 입력', async () => {
        await page.goto(BASE + '/Admin');
        await page.click('#admin-tabs [data-kind=category]');
        await page.waitForSelector('.columns .node');
        await page.locator('.column').first().locator('.node-main', { hasText: 'Simulation' }).click();
        const node = page.locator('.column').nth(1).locator('.node', { hasText: '검증' });
        await page.waitForFunction(() => document.querySelectorAll('.column')[1].innerText.includes('기본 10근무일'));
        await node.locator('.node-actions [data-edit]').click();
        await page.waitForSelector(SHEET + ' input[name=defaultWorkDays]');
        assert((await page.inputValue(SHEET + ' input[name=defaultWorkDays]')) === '10', '기본 기간이 채워지지 않음');
        assert(await page.locator(SHEET + ' [data-lead-hint]').isVisible(), '과거 평균 안내가 없음');
        await page.fill(SHEET + ' input[name=defaultWorkDays]', '1.5');
        await page.locator(SHEET + ' button[type=submit]').click();
        assert((await sheetError(page)).includes('정수'), '정수가 아닌 기본 기간을 받음');
        await page.fill(SHEET + ' input[name=defaultWorkDays]', '12');
        await shot(page, 'd18b-admin-default-days', false);
        await submitSheet(page);
        await page.waitForFunction(() => document.querySelectorAll('.column')[1].innerText.includes('기본 12근무일'));

        // 등록 화면 초안에 바로 쓰인다
        await page.goto(BASE + '/Projects/Edit');
        await page.locator('.choice', { hasText: 'Simulation' }).click();
        await page.waitForSelector('#draft .sched-row');
        await page.waitForFunction(() => document.querySelector('#draft').innerText.includes('기본 12근무일'));
        assert((await page.locator('#draft .sched-row').nth(2).locator('.sched-gap').innerText()).includes('+12근무일'), '바꾼 기본 기간이 초안에 쓰이지 않음');
    });

    await step('관리 — 사용자 메일 주소(관리자만 정함), 메일 서버가 없으면 시험 메일은 이유를 알림', async () => {
        await page.goto(BASE + '/Admin');
        await page.click('#admin-tabs [data-kind=user]');
        await page.waitForFunction(() => document.querySelector('.columns') && document.querySelector('.columns').innerText.includes('김물류'));
        await page.locator('.node', { hasText: '김물류' }).locator('.node-main').click();
        await page.waitForSelector(SHEET + ' input[name=email]');
        await page.fill(SHEET + ' input[name=email]', '주소아님');
        await page.click(SHEET + ' button[type=submit]');
        assert((await sheetError(page)).includes('메일 주소'), '잘못된 메일 주소를 받음');
        await page.fill(SHEET + ' input[name=email]', 'kim@example.com');
        await page.click(SHEET + ' [data-test-mail]');
        await page.waitForFunction(() => {
            const el = document.querySelector('dialog.sheet[open] .sheet-body > .form-error');
            return el && !el.hidden && el.innerText.includes('Notify:Smtp');
        });
        await shot(page, 'd20c-admin-user-email', false);
        await submitSheet(page);
        await page.locator('.node', { hasText: '김물류' }).locator('.node-main').click();
        await page.waitForSelector(SHEET + ' input[name=email]');
        assert((await page.inputValue(SHEET + ' input[name=email]')) === 'kim@example.com', '메일 주소가 저장되지 않음');
        await closeSheet(page);
    });

    await step('관리 — 공휴일 연도 칩·여러 줄 붙여넣기·저장/삭제 때 일정 영향 안내', async () => {
        const year = +kstToday().slice(0, 4);
        await page.goto(BASE + '/Admin#holiday');
        await page.waitForSelector('.year-chips [data-year]');
        assert((await page.locator(`.year-chip[data-year="${year}"][aria-pressed=true]`).count()) === 1, '올해 칩이 선택되지 않음');
        assert((await page.locator(`.year-chip[data-year="${year + 1}"]`).count()) === 1, '내년 칩이 없음');
        await page.click(`.year-chip[data-year="${year + 1}"]`);
        const dates = await page.locator('.columns .node-extra').allInnerTexts();
        assert(dates.length > 0 && dates.every((d) => d.startsWith(String(year + 1))), '연도 칩이 그 해만 보이지 않음: ' + dates.join(','));
        await shot(page, 'd18c-admin-holiday-years');

        // 여러 줄 붙여넣기: 읽지 못하는 줄은 줄 번호와 함께 알리고 아무것도 넣지 않는다
        const y = year + 3;
        await page.click('[data-paste]');
        await page.fill(SHEET + ' textarea[name=text]', `${y}-01-01\t신정\n${y}.3.1 삼일절\n잘못된 줄`);
        await page.locator(SHEET + ' button[type=submit]').click();
        assert((await sheetError(page)).startsWith('3번째 줄'), '잘못된 줄 안내가 없음');
        await page.fill(SHEET + ' textarea[name=text]', `${y}-01-01\t신정\n${y}.3.1 삼일절`);
        await shot(page, 'd18d-admin-holiday-paste', false);
        await submitSheet(page);
        await page.waitForSelector('[data-holiday-notice]');
        assert((await page.locator('[data-holiday-notice]').innerText()).includes('2건을 추가했습니다'), '붙여넣기 결과 안내가 없음');
        assert((await page.locator(`.year-chip[data-year="${y}"][aria-pressed=true]`).count()) === 1, '붙여 넣은 해로 바뀌지 않음');
        assert((await page.locator('.columns').innerText()).includes('삼일절'), '붙여 넣은 공휴일이 목록에 없음');

        // 진행 과제 일정 안의 날을 저장하면 영향 안내, 지우면 다시 안내 (일정은 원래대로)
        const day = kstToday(3);
        const form = page.locator('form[data-add="1"]');
        await form.locator('input[name=date]').fill(day);
        await form.locator('input[name=name]').fill('e2e 임시 공휴일');
        await form.locator('button[type=submit]').click();
        await page.waitForFunction(() => (document.querySelector('[data-holiday-notice]') || {}).innerText?.includes('공휴일을 저장했습니다'));
        const saved = await page.locator('[data-holiday-notice]').innerText();
        assert(saved.includes('진행 과제'), '일정 영향 안내가 없음: ' + saved);
        await page.locator('.node', { hasText: 'e2e 임시 공휴일' }).locator('.node-main').click();
        await page.click(SHEET + ' [data-danger]');
        await page.locator('dialog[open] [data-yes]').click();
        await page.waitForSelector(SHEET, { state: 'detached' });
        await page.waitForFunction(() => (document.querySelector('[data-holiday-notice]') || {}).innerText?.includes('공휴일을 삭제했습니다'));
        assert(!(await page.locator('.columns').innerText()).includes('e2e 임시 공휴일'), '지운 공휴일이 남음');
    });

    await step('업데이트 — 버전·상태 표시, 설정 저장, 로컬 빌드는 적용 불가 안내', async () => {
        await page.goto(BASE + '/Admin');
        await page.click('a[href$="/Admin/Update"]');
        await page.waitForSelector('.update-version');
        assert(/^v\d+\.\d+\.\d+$/.test((await page.locator('.update-version').innerText()).trim()), '버전 표시가 없음');
        // 화면 점검은 로컬 빌드로 돌므로 스스로 교체하지 않는다는 안내가 보여야 한다
        await page.waitForSelector('.update-body .form-note');
        assert((await page.locator('.update-body').innerText()).includes('로컬 빌드'), '로컬 빌드 안내가 없음');
        assert((await page.locator('[data-act=apply]').count()) === 0, '받아 둔 것이 없는데 적용 단추가 있음');
        await page.click('[data-act=check]');
        await page.waitForFunction(() => !document.querySelector('[data-act=check]').disabled
            && !document.querySelector('.update-body dl').innerText.includes('아직 확인하지 않음'), null, { timeout: 45000 });
        await page.selectOption('#settings-form select[name=windowFrom]', '22');
        await page.selectOption('#settings-form select[name=windowTo]', '4');
        await page.fill('#settings-form input[name=idleMinutes]', '45');
        await page.click('#settings-form button[type=submit]');
        await page.waitForSelector('.toast');
        await page.reload();
        await page.waitForSelector('#settings-form');
        assert((await page.inputValue('#settings-form input[name=idleMinutes]')) === '45', '설정이 저장되지 않음');
        assert((await page.inputValue('#settings-form select[name=windowFrom]')) === '22', '시간대가 저장되지 않음');
        // 잘못된 파일을 올리면 원인과 해결 방법을 보여 준다
        await page.setInputFiles('#upload-form input[type=file]', { name: 'wrong.zip', mimeType: 'application/zip', buffer: Buffer.from('zip 이 아님') });
        await page.click('#upload-form button[type=submit]');
        await page.waitForSelector('.update-body .form-error .fix');
        await shot(page, 'd24-update');
    });

    await step('업데이트 — 서명 검증 표시, 시작되지 않아 되돌린 버전 안내·다시 시도·안내 닫기', async () => {
        await page.goto(BASE + '/Admin/Update');
        await page.waitForSelector('.update-version');
        // 서명 검증 표시는 빌드에 공개키(update-key.pub)가 들었는지에 따라 다르다. 서버 상태를 먼저 읽어 그에 맞는 표시인지 본다
        // (공개키를 커밋하면 켜짐·공개키 지문, 없으면 출처를 확인하지 않는다는 안내)
        const sig = await page.evaluate(async () => {
            const r = await fetch(App.url('/Admin/Update?handler=Status'), { headers: { 'Accept': 'application/json' } });
            const s = (await r.json()).status;
            return { on: s.signatureCheck, keyId: s.keyId || '', canApply: s.canApply };
        });
        // 첫 카드(현재 버전)의 목록 — 아래에 백업 카드의 목록도 있다
        const sigText = await page.locator('.update-body dl').first().innerText();
        if (sig.on) assert(sigText.includes('켜짐') && sigText.includes(sig.keyId), '서명 검증 켜짐·공개키 표시가 없음: ' + sigText);
        else assert(sigText.includes('서명 검증 꺼짐'), '서명 검증 꺼짐 안내가 없음: ' + sigText);
        assert((await page.locator('#upload-form input[type=file]').getAttribute('multiple')) !== null, '배포 정보·서명 파일을 함께 고를 수 없음');
        await mockStartFailure(page);
        await page.click('[data-act=check]');
        await page.waitForSelector('.start-failure');
        const text = await page.locator('.start-failure').innerText();
        assert(text.includes('자동으로 되돌렸습니다') && text.includes('InvalidDataException'), '시작 실패 안내·원인이 없음: ' + text);
        assert(text.includes('자동으로 다시 받거나 적용하지 않습니다'), '자동 적용 제외 안내가 없음');
        assert(await page.locator('.start-failure [data-act=retry]').isVisible(), '다시 시도 단추가 없음');
        // 받아 둔 파일로 다시 적용하는 단추는 '지금 적용'과 같이 막힌다 (화면 점검은 로컬 빌드라 적용할 수 없다)
        assert((await page.locator('.start-failure [data-act=retry]').isDisabled()) === !sig.canApply,
            '다시 시도 단추가 적용 가능 여부와 다름 (적용 가능: ' + sig.canApply + ')');
        assert((await page.locator('[data-act=apply]').isDisabled()) === !sig.canApply, '지금 적용 단추가 적용 가능 여부와 다름');
        assert((await page.locator('.update-body').innerText()).includes('시작되지 않아 되돌렸던 버전이라'), '받아 둔 버전의 자동 적용 안내가 다름');
        await shot(page, 'd24b-update-start-failure');
        // 안내 닫기 → 서버에 기록된 안내가 없어지고(실제 응답) 화면에서도 사라진다
        await page.unroute('**/Admin/Update?handler=Check');
        await page.click('.start-failure [data-act=dismiss-failure]');
        await page.waitForSelector('.start-failure', { state: 'detached' });
    });

    await step('서버 상태 — 관리 화면에서 들어가 점검 결과·첨부 대조·최근 경고를 보고, 첫 설정이 덜 됐으면 점검표', async () => {
        await page.goto(BASE + '/Admin');
        await page.click('#status-link');
        await page.waitForSelector('#checks');
        for (const key of ['db', 'schema', 'disk', 'folders', 'pdf', 'holidays', 'backup', 'setup'])
            assert((await page.locator(`#checks tr[data-check=${key}]`).count()) === 1, '점검 항목이 없음: ' + key);
        assert((await page.locator('#checks tr[data-check=db] .chip').innerText()).includes('정상'), 'DB 연결 점검이 정상이 아님');
        assert((await page.locator('#attachments').innerText()).includes('기록'), '첨부 대조가 없음');
        // 샘플 데이터는 첫 설정이 끝난 상태라 점검표가 없다
        assert((await page.locator('#setup').count()) === 0, '첫 설정이 끝났는데 점검표가 보임');
        await page.click('#logs [data-logs=error]');
        await page.waitForSelector('#logs [data-logs=error].on');
        await page.click('#logs [data-logs=all]');
        // 전체 화면 캡처는 고정 머리글이 지금 스크롤 위치에 찍히므로 맨 위로 올린 뒤 찍는다
        await page.evaluate(() => window.scrollTo(0, 0));
        await shot(page, 'd43-status');
        // 처음 설치한 서버처럼 법인·내년 공휴일이 없다고 답하게 해 점검표를 그린다
        await page.route('**/Admin/Status?handler=Data', async (route) => {
            const res = await route.fetch();
            const body = await res.json();
            body.report.setupDone = false;
            body.report.setup.forEach((x) => { if (x.key === 'corp' || x.key === 'holiday-next') { x.done = false; x.detail = '0개'; } });
            await route.fulfill({ response: res, json: body });
        });
        await page.click('#status-refresh');
        await page.waitForSelector('#setup');
        const setup = await page.locator('#setup').innerText();
        assert(setup.includes('첫 설정 점검표') && setup.includes('법인·위치'), '첫 설정 점검표가 없음: ' + setup);
        assert((await page.locator('#setup [data-setup=corp] a').getAttribute('href')).endsWith('/Admin#location'), '법인 설정 바로 가기가 없음');
        await shot(page, 'd43b-status-setup');
        await page.unroute('**/Admin/Status?handler=Data');
        // 점검표의 바로 가기는 관리 화면의 그 탭을 연다
        await page.click('#setup [data-setup=corp] a');
        await page.waitForSelector('#admin-tabs [data-kind=location].on');
        await page.goto(BASE + '/Admin#holiday');
        await page.waitForSelector('#admin-tabs [data-kind=holiday].on');
    });

    await step('업데이트 — 지금 백업하면 목록에 남고, 진단 정보를 보고 내려받음', async () => {
        await page.goto(BASE + '/Admin/Update#backup');
        await page.waitForSelector('#backup');
        const before = await page.locator('#backup tr[data-backup]').count();
        await page.click('#backup [data-act=backup]');
        await page.waitForSelector('.toast');
        await page.waitForFunction((n) => document.querySelectorAll('#backup tr[data-backup]').length === n + 1, before);
        const row = await page.locator('#backup tr[data-backup]').first().innerText();
        assert(row.includes('지금 백업') && row.includes('성공') && row.includes('평영'), '백업 목록이 이상함: ' + row);
        assert(/gantt-\d{8}-\d{6}/.test(row), '백업 이름이 없음: ' + row);
        await page.click('#diagnostics [data-act=diag]');
        await page.waitForSelector('#diag-state');
        assert((await page.locator('#diag-state').innerText()).includes('지금 버전'), '진단 상태 요약이 없음');
        const [download] = await Promise.all([page.waitForEvent('download'), page.click('#diag-download')]);
        const file = path.join(EXPORTS, '업데이트진단.txt');
        await download.saveAs(file);
        const text = fs.readFileSync(file, 'utf8');
        assert(text.includes('== 상태 요약 ==') && text.includes('apply.log'), '진단 파일 내용이 이상함');
        await page.evaluate(() => window.scrollTo(0, 0));
        await shot(page, 'd44-update-backup');
    });

    await step('오류 번호 — 서버 오류 안내와 오류 화면에 번호를 보임', async () => {
        await page.goto(BASE + '/Projects');
        // 서버가 오류 번호 머리글과 함께 500 을 준 경우 (본문이 JSON 이 아니어도)
        await page.route('**/__e2e-error', (r) => r.fulfill({ status: 500, contentType: 'text/plain', headers: { 'X-Error-Id': '0HE2E:00000042' }, body: 'boom' }));
        const message = await page.evaluate(() => App.get(App.url('/__e2e-error')).then(() => '', (e) => e.message));
        assert(message.includes('500') && message.includes('오류 번호 0HE2E:00000042'), '오류 번호가 안내에 없음: ' + message);
        await page.unroute('**/__e2e-error');
        await page.goto(BASE + '/Error');
        const id = (await page.locator('#error-id code').innerText()).trim();
        assert(id.length > 4, '오류 화면에 오류 번호가 없음');
        await shot(page, 'd45-error-number', false);
    });

    await step('새 버전 알림 — 서버가 바뀌면 새로 고침 안내, 끊기면 연결 안내', async () => {
        await page.goto(BASE + '/Projects');
        const build = await page.evaluate(() => document.documentElement.getAttribute('data-build'));
        assert(build, '화면에 빌드 정보가 없음');
        await page.evaluate(() => App.checkVersion());
        await page.waitForTimeout(300);
        assert((await page.locator('.notice').count()) === 0, '서버가 그대로인데 알림이 뜸');
        // 서버가 새 버전으로 바뀐 상황
        await page.route('**/version', r => r.fulfill({ json: { ok: true, build: '99.0.0+fffffff', version: '99.0.0', updating: false } }));
        await page.evaluate(() => App.checkVersion());
        await page.waitForSelector('.notice');
        assert((await page.locator('.notice').innerText()).includes('v99.0.0'), '새 버전 번호가 알림에 없음');
        await shot(page, 'd25-new-version', false);
        // 업데이트 중 → 끊김은 예정된 것이라 경고로 바꾸지 않는다
        await page.unroute('**/version');
        await page.route('**/version', r => r.fulfill({ json: { ok: true, build, version: '0.0.0', updating: true } }));
        await page.evaluate(() => App.checkVersion());
        await page.waitForFunction(() => document.querySelector('.notice') && document.querySelector('.notice').innerText.includes('업데이트하는 중'));
        await page.unroute('**/version');
        await page.route('**/version', r => r.abort());
        await page.evaluate(() => App.checkVersion());
        await page.waitForTimeout(300);
        assert((await page.locator('.notice.alarm').count()) === 0, '업데이트 중의 끊김을 경고로 표시함');
        // 서버가 돌아오면 알림이 사라진다
        await page.unroute('**/version');
        await page.evaluate(() => App.checkVersion());
        await page.waitForFunction(() => !document.querySelector('.notice'));
        // 예고 없이 끊긴 경우: 두 번 연속 실패하면 연결 안내
        await page.route('**/version', r => r.abort());
        await page.evaluate(() => App.checkVersion());
        await page.waitForTimeout(300);
        await page.evaluate(() => App.checkVersion());
        await page.waitForSelector('.notice.alarm');
        await page.unroute('**/version');
        await page.evaluate(() => App.checkVersion());
        await page.waitForFunction(() => !document.querySelector('.notice'));
    });

    await step('과제 삭제 — 열려 있던 다른 화면은 삭제됨 띠', async () => {
        await openProject(page, '베트남 냉장고 1라인 컨베이어 개선');
        const other = await desktop.newPage();
        other.setDefaultTimeout(10000);
        await other.goto(page.url());
        await other.waitForSelector('#summary .summary-meta');
        await page.click('.tabs [data-tab=info]');
        await page.click('#btn-delete');
        await page.waitForSelector(SHEET + ' textarea[name=reason]');
        await page.fill(SHEET + ' textarea[name=reason]', '중복 등록');
        await shot(page, 'd48-delete-sheet', false);
        await page.locator(SHEET + ' button[type=submit]').click();
        await page.waitForURL(/\/Projects(\?|$)/);
        await page.goto(BASE + '/Projects?state=all');
        assert((await page.locator('.ptable-row', { hasText: '컨베이어 개선' }).count()) === 0, '삭제되지 않음');
        active = other;
        await other.evaluate(() => App.checkVersion());
        await other.waitForSelector('#change-banner.alarm:not([hidden])');
        assert((await other.locator('#change-banner').innerText()).includes('삭제'), '삭제됨 안내가 아님');
        await other.close();
        active = page;
    });

    await step('휴지통 — 관리 화면에서 지운 사람·사유를 보고 복원하면 목록·상세가 돌아옴', async () => {
        await page.goto(BASE + '/Admin');
        await page.click('#trash-link');
        await page.waitForSelector('#trash-list');
        const row = page.locator('.trash-row', { hasText: '컨베이어 개선' });
        assert(await row.count() === 1, '휴지통에 지운 과제가 없음');
        const text = await row.innerText();
        assert(text.includes('평영') && text.includes('사유: 중복 등록') && text.includes('영구 삭제 예정'), '지운 사람·사유·예정일이 없음: ' + text);
        await shot(page, 'd48b-trash');
        await row.locator('[data-restore]').click();
        await page.locator('dialog[open] [data-yes]').click();
        await page.waitForFunction(() => !document.querySelector('.trash-row') || !document.body.innerText.includes('컨베이어 개선'));
        await page.goto(BASE + '/Projects?state=all');
        assert((await page.locator('.ptable-row', { hasText: '컨베이어 개선' }).count()) === 1, '복원했는데 목록에 없음');
        await openProject(page, '베트남 냉장고 1라인 컨베이어 개선');
        const P = await projectData(page);
        assert(P.activities.some((a) => a.type === 'projectDeleted' && a.reason === '중복 등록'), '삭제 기록이 없음');
        assert(P.activities.some((a) => a.type === 'projectRestored'), '복원 기록이 없음');
    });

    await step('조기 경보 — 대시보드 지연 위험 카드·위험/정체 KPI 링크·간트 범위 토글과 조건 기억', async () => {
        await page.goto(BASE + '/?reset=1');
        await page.waitForSelector('#risk-card');
        assert(!/reset=/.test(page.url()), '조건 해제 뒤 주소에 쿼리가 남음: ' + page.url());
        const riskRows = await page.locator('#risk-card .rows li').count();
        const riskKpi = +(await page.locator('[data-kpi-risk]').innerText()).replace(/\D/g, '');
        assert(riskRows >= 1 && riskRows === Math.min(riskKpi, 8), `위험 카드 ${riskRows}건, KPI ${riskKpi}`);
        assert((await page.locator('#risk-card .chip.warn').count()) === riskRows, '위험 칩이 노랑(warn)이 아님');
        assert((await page.locator('#risk-card .risk-reasons').first().innerText()).length > 0, '위험 이유가 없음');
        await shot(page, 'd55-dashboard-risk');
        // KPI '위험' → 목록 위험 탭 (같은 판정이라 건수가 같다)
        await page.click('[data-kpi-risk]');
        await page.waitForURL(/state=risk/);
        assert((await page.locator('.ptable-row').count()) === riskKpi, '위험 탭 건수가 KPI 와 다름');
        assert((await page.locator('.ptable-row [data-risk]').count()) === riskKpi, '위험 탭 행에 위험 칩이 없음');
        await page.goto(BASE + '/');
        // 간트 범위: 주요(기본) → 전체 보기
        const focus = await page.locator('#gantt .g-row.project').count();
        await page.click('#gantt-scope [data-scope=all]');
        await page.waitForURL(/scope=all/);
        const all = await page.locator('#gantt .g-row.project').count();
        assert(all >= focus, `전체(${all})가 주요(${focus})보다 적음`);
        assert((await page.locator('#gantt-scope [data-scope=all]').getAttribute('aria-current')) === 'true', '범위 단추에 aria-current 가 없음');
        await page.selectOption('#gantt-sort', { label: '목표 종료 순' });
        await page.waitForURL(/sort=end/);
        // 조건 없이 다시 열면 마지막 조건(App.pref)으로 연다
        await page.goto(BASE + '/');
        await page.waitForURL(/sort=end/);
        assert(/scope=all/.test(page.url()), '기억한 범위로 열지 않음: ' + page.url());
        await page.click('#dash-reset');
        await page.waitForSelector('#risk-card');
        await page.goto(BASE + '/');
        await page.waitForSelector('#risk-card');
        assert(!/scope=|sort=/.test(page.url()), '해제한 뒤에도 조건을 되살림: ' + page.url());
    });

    await step('목록 정렬 — 머리글 링크(aria-sort)·반대 방향·정렬 기억·협력사 검색', async () => {
        await page.goto(BASE + '/Projects?state=all');
        await page.click('.ptable-head [data-sort=title]');
        await page.waitForURL(/sort=title/);
        assert((await page.locator('.ptable-head [aria-sort=ascending] [data-sort=title]').count()) === 1, 'aria-sort ascending 이 없음');
        const titles = await page.locator('.ptable-row .c-title .row-title').allInnerTexts();
        const sorted = [...titles].sort((a, b) => (a < b ? -1 : a > b ? 1 : 0));
        assert(JSON.stringify(titles) === JSON.stringify(sorted), '과제명 순이 아님: ' + titles.join(' / '));
        await page.click('.ptable-head [data-sort=title]');
        await page.waitForURL(/desc=true/);
        const desc = await page.locator('.ptable-row .c-title .row-title').allInnerTexts();
        assert(JSON.stringify(desc) === JSON.stringify([...sorted].reverse()), '반대 방향이 아님');
        assert((await page.locator('.ptable-head [aria-sort=descending]').count()) === 1, 'aria-sort descending 이 없음');
        await shot(page, 'd56-projects-sort');
        // 정렬 없이 열면 기억한 정렬로
        await page.goto(BASE + '/Projects?state=all');
        await page.waitForURL(/sort=title/);
        // '기본 순서'는 기억도 지운다 (고르기 상자는 좁은 화면용이라 값을 바꿔 보낸다)
        await page.evaluate(() => { const s = document.getElementById('list-sort'); s.selectedIndex = 0; s.dispatchEvent(new Event('change')); });
        await page.waitForURL((u) => !/sort=/.test(u.toString()));
        await page.goto(BASE + '/Projects?state=all');
        await page.waitForSelector('.ptable-row');
        assert(!/sort=/.test(page.url()), '기본 순서로 돌린 뒤에도 정렬을 되살림');
        // 검색: 협력사 이름으로도 찾는다
        await page.fill('#filters input[name=q]', '한성로보');
        await page.click('#filters button[type=submit]');
        await page.waitForURL(/q=/);
        assert((await page.locator('.ptable-row').count()) >= 1, '협력사 이름 검색 결과가 없음');
    });

    await step('내 할 일 — 행에서 바로 진행률·진행 내용 저장, 다른 화면이 먼저 고치면 최신 값으로', async () => {
        await page.goto(BASE + '/My');
        // 다른 단계가 쓰는 작업(광주 2공장 '검증 시나리오 실행' — 미착수여야 연쇄 이동 선택지가 보인다)을 피해 마지막 행을 쓴다
        const id = await page.locator('[data-my-work]').last().getAttribute('data-my-work');
        const sel = `[data-my-work="${id}"]`;
        await page.click(sel + ' [data-quick-progress="50"]');
        await page.waitForFunction((q) => document.querySelector(q + ' [data-quick-progress="50"]').getAttribute('aria-pressed') === 'true', sel);
        assert((await page.locator(sel + ' .work-progress .num').innerText()).trim() === '50%', '행의 진행률이 바뀌지 않음');
        assert(await page.locator(sel + ' [data-quick-start]').isHidden(), '진행률을 올렸는데 오늘 시작 단추가 남음(실적 시작이 채워지지 않음)');
        await page.fill(sel + ' [data-quick-note]', '현장 배선 절반 완료');
        await page.press(sel + ' [data-quick-note]', 'Enter');
        await page.waitForFunction((q) => !document.querySelector(q).hasAttribute('aria-busy') && document.querySelector(q + ' [data-quick-note]').value === '', sel);
        await shot(page, 'd57-my-quick');
        // 다른 화면이 먼저 바꾼 뒤 이 화면(옛 버전)에서 누르면 409 — 최신 값으로 다시 그리고 알린다
        const other = active = await desktop.newPage();
        other.setDefaultTimeout(10000);
        await other.goto(BASE + '/My');
        await other.click(sel + ' [data-quick-progress="75"]');
        await other.waitForFunction((q) => document.querySelector(q + ' [data-quick-progress="75"]').getAttribute('aria-pressed') === 'true', sel);
        await other.close();
        active = page;
        await page.click(sel + ' [data-quick-progress="100"]');
        await page.waitForSelector(sel + ' [data-my-error]');
        assert((await page.locator(sel + ' [data-my-error]').innerText()).includes('다른 사용자'), '동시 수정 안내가 아님');
        assert((await page.locator(sel + ' .work-progress .num').innerText()).trim() === '75%', '최신 값(75%)으로 다시 그리지 않음');
        // 진행 내용은 그 작업의 활동(댓글)으로 남았다
        await page.locator(sel + ' a.row-link').click();
        await page.waitForSelector(SHEET);
        await closeSheet(page);
        await page.click('.tabs [data-tab=activity]');
        await page.waitForFunction(() => document.getElementById('feed').textContent.includes('현장 배선 절반 완료'));
    });

    await step('작업 시트 빠른 갱신 — 맨 위 진행률 칩·진행 내용, 일정·선행·메모는 접힘(새 작업은 펼침)', async () => {
        await page.evaluate(() => App.pref.set('detail.workMore', '0'));
        try {
            await openProject(page, '광주 냉장고 3라인 AGV 도입');
            const open = worksOf(await projectData(page)).find((w) => !w.actualEnd && !w.closedOn);
            assert(open, '열린 작업이 없음');
            // 같은 주소에 해시만 바꾸면 다시 읽지 않으므로 한 번 비운 뒤 연다(상세는 #w{id} 로 작업 시트를 연다)
            const url = page.url().replace(/#.*/, '') + '#w' + open.id;
            await page.goto('about:blank');
            await page.goto(url);
            await page.waitForSelector(SHEET + ' [data-quick]');
            assert(!(await page.locator(SHEET + ' [data-work-more-fields]').evaluate((d) => d.open)), '일정·선행·메모가 펼쳐져 있음');
            assert(await page.locator(SHEET + ' input[name=planStart]').isHidden(), '접혔는데 목표 시작이 보임');
            const quickTop = await page.evaluate(() => {
                const q = document.querySelector('dialog.sheet[open] [data-quick]');
                const t = document.querySelector('dialog.sheet[open] [data-work-more-fields]');
                return q.getBoundingClientRect().top < t.getBoundingClientRect().top;
            });
            assert(quickTop, '빠른 갱신이 맨 위가 아님');
            await page.click(SHEET + ' [data-quick-progress="75"]');
            assert((await page.inputValue(SHEET + ' input[name=progress]')) === '75', '칩이 진행률을 바꾸지 않음');
            assert((await page.locator(SHEET + ' [data-quick-progress="75"]').getAttribute('aria-pressed')) === 'true', '누른 칩이 눌림 표시가 아님');
            await page.fill(SHEET + ' [data-quick-note]', '시운전 준비 중');
            await shot(page, 'd58-work-sheet-quick', false);
            await submitSheet(page);
            const saved = worksOf(await projectData(page)).find((w) => w.id === open.id);
            assert(saved.progress === 75 && saved.actualStart, '저장한 진행률(75%)·실적 시작이 없음');
            await page.click('.tabs [data-tab=activity]');
            await page.waitForFunction(() => document.getElementById('feed').textContent.includes('시운전 준비 중'));
            // 새 작업 추가는 처음부터 펼친다(작업명·일정을 먼저 정해야 하므로)
            await page.click('.tabs [data-tab=schedule]');
            await page.click('#add-work');
            await page.waitForSelector(SHEET + ' [data-work-more-fields][open]');
            assert(await page.locator(SHEET + ' input[name=title]').isVisible(), '새 작업인데 작업명이 접혀 있음');
            await closeSheet(page);
        } finally {
            await page.evaluate(() => App.pref.set('detail.workMore', '1'));
        }
    });

    // ── 입력 부담 줄이기·Excel 가져오기 ──
    // 이 단계들은 '광주 냉장고 5라인 AGV 도입'(복제본)에 담당 없는 작업을 더하고(내 할 일이 바뀌지 않게), 관리 > 유형 '물류' 의 '검토'에
    // 작업 템플릿 2개를 더하며, 과제 'e2e 가져오기 과제'(담당 e2e가져오기담당)와 복제본 'e2e 베트남 복제'(담당 김물류)를 만든다
    const PASTE = '광주 냉장고 5라인 AGV 도입';
    let importTemplate = null;     // 내려받은 가져오기 양식 (모바일 단계도 쓴다)

    await step('작업 여러 개 추가 — 붙여넣으면 서버가 해석한 표(문제 줄은 오류 모양)를 보이고, 고치면 n개를 한 번에 저장', async () => {
        await openProject(page, PASTE);
        await page.click('#view-mode [data-view=list]');
        const before = await projectData(page);
        const stage = before.stages.find((s) => !s.completedDate);
        await page.click('[data-add-works="' + stage.id + '"]');
        await page.waitForSelector(SHEET + ' textarea[name=text]');
        const good = ['작업명\t담당\t목표 시작\t목표 종료\t선행 작업명',
            '자재 입고 확인\t\t' + kstToday(40) + '\t' + kstToday(41),
            '설치 준비\t\t' + kstToday(50).replace(/-/g, '.') + '\t' + kstToday(52) + '\t자재 입고 확인'];
        await page.fill(SHEET + ' textarea[name=text]', good.concat(['틀린 줄\t\t언젠가\t']).join('\n'));
        await page.click(SHEET + ' [data-paste-check]');
        await page.waitForSelector(SHEET + ' [data-paste-table] tbody tr');
        assert((await page.locator(SHEET + ' [data-paste-table] tr.row-bad').count()) === 1, '문제 줄 표시가 1개가 아님');
        assert((await page.locator(SHEET + ' .row-issue').innerText()).includes('목표 시작'), '문제 칸 이름이 없음');
        assert(!(await page.locator(SHEET + ' [data-paste-table] .chip.alarm').count()), '문제 줄에 지연(빨강) 칩을 씀');
        assert((await page.locator(SHEET + ' button[type=submit]').innerText()).trim() === '추가', '문제가 있는데 n개 추가로 보임');
        await shot(page, 'd60-paste-works', false);
        // 저장을 눌러도 문제가 있으면 막는다
        await page.locator(SHEET + ' button[type=submit]').click();
        assert((await sheetError(page)).includes('문제 있는 줄'), '문제 있는 줄 안내가 없음');
        // 고치면 다시 확인되고 'n개 추가'
        await page.fill(SHEET + ' textarea[name=text]', good.join('\n'));
        await page.waitForFunction(() => /2개 추가/.test(document.querySelector('dialog.sheet[open] button[type=submit]').textContent));
        assert(!(await page.locator(SHEET + ' tr.row-bad').count()), '고쳤는데 문제 줄이 남음');
        await submitSheet(page);
        const after = await projectData(page);
        const works = worksOf(after);
        const a = works.find((w) => w.title === '자재 입고 확인'), b = works.find((w) => w.title === '설치 준비');
        assert(a && b && a.stageId === stage.id && b.planStart === kstToday(50), '붙여넣은 작업이 저장되지 않음');
        assert(after.links.some((l) => l.from === a.id && l.to === b.id), '선행 연결이 없음');
        assert(!a.owner && !b.owner, '담당을 비웠는데 담당이 들어감');
        await page.click('.tabs [data-tab=activity]');
        await page.waitForFunction(() => document.getElementById('feed').textContent.includes('작업 추가: 설치 준비 (여러 개 추가)'));
        await page.click('.tabs [data-tab=schedule]');
    });

    await step('저장 후 계속 추가 — 작업명만 비우고 담당·날짜는 그대로, 포커스는 작업명', async () => {
        await openProject(page, PASTE);
        await page.click('#view-mode [data-view=list]');
        const stage = (await projectData(page)).stages.find((s) => !s.completedDate);
        await page.click('[data-add-work="' + stage.id + '"]');
        await page.waitForSelector(SHEET + ' [data-continue]');
        await page.fill(SHEET + ' input[name=title]', '연속 추가 1');
        await page.fill(SHEET + ' input[name=owner]', '');
        await page.fill(SHEET + ' input[name=planStart]', kstToday(60));
        await page.fill(SHEET + ' input[name=planEnd]', kstToday(61));
        await page.click(SHEET + ' [data-continue]');
        await page.waitForFunction(() => document.querySelector('dialog.sheet[open] input[name=title]').value === '');
        assert(await page.locator(SHEET).isVisible(), '저장 후 계속인데 시트가 닫힘');
        assert((await page.inputValue(SHEET + ' input[name=planStart]')) === kstToday(60), '목표 시작이 유지되지 않음');
        assert((await page.inputValue(SHEET + ' input[name=owner]')) === '', '담당이 유지되지 않음');
        assert(await page.evaluate(() => document.activeElement && document.activeElement.name === 'title'), '포커스가 작업명이 아님');
        assert((await page.locator(SHEET + ' .pick-list label', { hasText: '연속 추가 1' }).count()) === 1, '방금 만든 작업이 선행 후보에 없음');
        await shot(page, 'd61-work-continue', false);
        await page.fill(SHEET + ' input[name=title]', '연속 추가 2');
        await submitSheet(page);
        const titles = worksOf(await projectData(page)).map((w) => w.title);
        assert(titles.includes('연속 추가 1') && titles.includes('연속 추가 2'), '연속 추가한 작업이 없음');
    });

    await step('관리 — 단계 아래 작업 템플릿 추가·선행, 등록 화면에 템플릿 작업 n개 함께 만들기', async () => {
        await page.goto(BASE + '/Admin#category');
        await page.waitForSelector('.columns .node');
        await page.locator('.column').first().locator('.node-main', { has: page.locator('.node-name', { hasText: /^물류$/ }) }).click();
        await page.locator('.column').nth(1).locator('.node-main', { hasText: '검토' }).click();
        await page.waitForSelector('[data-template-works]');
        assert((await page.locator('[data-template-works] .column-head').innerText()).includes('검토'), '작업 템플릿 판에 단계 이름이 없음');
        for (const [name, offset, days, pred] of [['현장 조사', '0', '3', ''], ['개선안 작성', '3', '4', '현장 조사']]) {
            await page.click('[data-template-works] [data-tw-add]');
            await page.waitForSelector(SHEET + ' input[name=offsetWorkDays]');
            await page.fill(SHEET + ' input[name=name]', name);
            await page.fill(SHEET + ' input[name=offsetWorkDays]', offset);
            await page.fill(SHEET + ' input[name=durationWorkDays]', days);
            if (pred) await page.selectOption(SHEET + ' select[name=predecessorId]', { label: pred });
            await submitSheet(page);
        }
        const rows = page.locator('[data-template-works] .node');
        assert((await rows.count()) === 2, '작업 템플릿이 2개가 아님');
        assert((await rows.nth(1).innerText()).includes('선행: 현장 조사'), '선행 표시가 없음');
        assert((await rows.nth(1).innerText()).includes('시작 +3근무일'), '시작 표시가 없음');
        await shot(page, 'd62-admin-template-works');
        await page.goto(BASE + '/Projects/Edit');
        await page.locator('.choice').filter({ has: page.getByText('물류', { exact: true }) }).click();
        await page.waitForSelector('#template-works-field:not([hidden])');
        assert((await page.locator('#template-works-label').innerText()) === '템플릿 작업 2개 함께 만들기', '템플릿 작업 수 안내가 다름');
        assert(await page.isChecked('#template-works'), '템플릿 작업 함께 만들기가 기본으로 켜져 있지 않음');
        await page.locator('.choice', { hasText: 'Simulation' }).click();
        assert(await page.locator('#template-works-field').isHidden(), '템플릿 작업이 없는 유형인데 보임');
    });

    await step('과제 복제 — 위치(법인 → 하위)와 담당을 정함', async () => {
        await openProject(page, PASTE);
        await page.click('#btn-clone');
        await page.waitForSelector(SHEET + ' [data-location-picker] select');
        assert((await page.inputValue(SHEET + ' input[name=owner]')) !== '', '담당 기본값(나)이 없음');
        await page.fill(SHEET + ' input[name=title]', 'e2e 베트남 복제');
        await page.selectOption(SHEET + ' [data-location-picker] select[data-level="1"]', { label: '베트남' });
        await page.selectOption(SHEET + ' [data-location-picker] select[data-level="2"]', { label: '세탁기' });
        // 하위가 하나뿐인 라인은 저절로 고른다
        assert((await page.inputValue(SHEET + ' [data-location-picker] select[data-level="3"]')) !== '', '라인이 자동 선택되지 않음');
        await page.fill(SHEET + ' input[name=owner]', '김물류');
        await page.uncheck(SHEET + ' input[name=works]');
        await shot(page, 'd63-clone-location', false);
        await page.locator(SHEET + ' button[type=submit]').click();
        await page.waitForFunction(() => document.getElementById('p-title').textContent === 'e2e 베트남 복제');
        const P = await projectData(page);
        assert(P.corporation === '베트남' && P.locationPath.includes('2라인') && P.owner === '김물류', '위치·담당이 반영되지 않음: ' + P.corporation + ' ' + P.locationPath + ' ' + P.owner);
    });

    await step('가져오기 — 양식 받기·올리기·미리보기(오류 행)·고쳐서 가져오기', async () => {
        await page.goto(BASE + '/Projects');
        await page.click('#btn-import');
        await page.waitForSelector('#import-form');
        const res = await desktop.request.get(BASE + '/Projects/Import?handler=Template');
        assert(res.ok(), '양식 응답 ' + res.status());
        importTemplate = await res.body();
        fs.writeFileSync(path.join(EXPORTS, '과제_가져오기_양식' + (process.env.FAKE_EXPORT ? '.txt' : '.xlsx')), importTemplate);
        if (!process.env.FAKE_EXPORT) assert(importTemplate.subarray(0, 2).toString('latin1') === 'PK', '양식이 xlsx 가 아님');
        const good = ['e2e 가져오기 과제', '물류', '한국', '냉장고>3라인', 'e2e가져오기담당', kstToday(7), '', 'Excel 로 가져온 과제', '보고=' + kstToday(40)];
        const bad = ['e2e 가져오기 오류', '없는 유형', '한국', '', '', '언젠가'];
        const works = [['e2e 가져오기 과제', '적용', '현장 적용', '', kstToday(60), kstToday(62)]];
        const file = (rows) => ({ name: '가져오기.xlsx', mimeType: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet', buffer: fillTemplate(importTemplate, rows) });

        await page.setInputFiles('#import-file', file({ '과제': [good, bad], '작업': works }));
        await page.click('#import-check');
        await page.waitForSelector('#import-result:not([hidden]) [data-import-projects]');
        assert((await page.locator('[data-import-projects] tr.row-bad').count()) === 1, '오류 행 표시가 1개가 아님');
        assert((await page.locator('[data-import-projects] .row-issue').allInnerTexts()).join(' ').includes('유형'), '유형 오류 안내가 없음');
        assert(await page.locator('#import-actions').isHidden(), '오류가 있는데 가져오기 단추가 보임');
        await page.evaluate(() => window.scrollTo(0, 0));
        await shot(page, 'd64-import-errors');

        await page.setInputFiles('#import-file', file({ '과제': [good], '작업': works }));
        await page.click('#import-check');
        await page.waitForSelector('#import-actions:not([hidden])');
        assert((await page.locator('[data-import-new-users]').innerText()).includes('e2e가져오기담당'), '새 담당자 안내가 없음');
        assert(await page.locator('#import-run').isDisabled(), '새 담당자를 확인하기 전에 가져오기를 누를 수 있음');
        await page.check('#import-confirm');
        await page.evaluate(() => window.scrollTo(0, 0));
        await shot(page, 'd64b-import-ready');
        await page.click('#import-run');
        await page.waitForFunction(() => document.getElementById('p-title') && document.getElementById('p-title').textContent === 'e2e 가져오기 과제');
        const P = await projectData(page);
        assert(P.owner === 'e2e가져오기담당' && P.locationPath.includes('3라인'), '가져온 과제의 담당·위치가 다름');
        assert(worksOf(P).map((w) => w.title).join(',') === '현장 적용', '작업 시트의 작업만 만들어야 함: ' + worksOf(P).map((w) => w.title));
        assert(P.stages.find((s) => s.name === '보고').targetDate === kstToday(40), '단계 목표일이 들어가지 않음');
        await page.click('.tabs [data-tab=activity]');
        await page.waitForFunction(() => document.getElementById('feed').textContent.includes('Excel 가져오기'));
    });

    await step('상황판 — 메뉴에서 새 창으로 열리고, 등록하지 않은 TV PC 에서 1920×1080 한 화면에 맞으며 과제가 바뀌면 다시 받음', async () => {
        await page.goto(BASE + '/');
        const [popup] = await Promise.all([desktop.waitForEvent('page'), page.click('#board-link')]);
        await popup.waitForSelector('#board');
        assert(new URL(popup.url()).pathname === '/Board', '상황판 링크가 새 창으로 열리지 않음: ' + popup.url());
        await popup.close();

        // 이름을 등록하지 않은 다른 PC(TV)
        const tv = await browser.newContext({ viewport: { width: 1920, height: 1080 }, locale: 'ko-KR', timezoneId: 'Asia/Seoul', extraHTTPHeaders: { 'X-Forwarded-For': '10.77.0.90' } });
        const b = await tv.newPage();
        watch(b, 'board');
        await b.goto(BASE + '/Board');
        assert(new URL(b.url()).pathname === '/Board', '등록하지 않은 PC 에서 상황판이 이름 등록으로 넘어감: ' + b.url());
        await b.waitForSelector('.board-kpi');
        assert((await b.locator('.board-kpi').count()) === 5, 'KPI 가 5개가 아님');
        const over = await b.evaluate(() => [document.documentElement.scrollHeight - innerHeight, document.documentElement.scrollWidth - innerWidth]);
        assert(over[0] <= 0 && over[1] <= 0, '상황판이 한 화면을 넘침: ' + over);
        assert((await b.locator('[data-board-alert]').count()) > 0, '지연·위험 과제 줄이 없음');
        await shot(b, 'd65-board', false);

        // 데이터 버전: 다른 PC 가 과제에 댓글을 남기면 1분 확인에서 화면을 다시 받는다(여기서는 확인을 바로 부른다)
        const before = await b.getAttribute('#board', 'data-stamp');
        await b.evaluate(() => window.BoardPoll());
        await b.waitForTimeout(300);
        assert((await b.getAttribute('#board', 'data-stamp')) === before, '바뀐 것이 없는데 확인값이 달라짐');
        // 바뀐 것이 없어도 '확인' 시각은 서버 응답으로 고친다(화면이 멈추지 않았음을 보이게) — 지운 뒤 다시 채워지는지 본다
        await b.evaluate(() => { document.querySelector('#board [data-checked]').textContent = ''; window.BoardPoll(); });
        await b.waitForFunction(() => /^\d{2}:\d{2}$/.test(document.querySelector('#board [data-checked]').textContent));
        await openProject(page, '한국 에어컨 1라인 리프터 교체');
        const id = +new URL(page.url()).searchParams.get('id');
        await page.evaluate(async (pid) => {
            const token = document.querySelector('input[name="__RequestVerificationToken"]').value;
            const r = await fetch('/Projects/Detail?handler=Comment', {
                method: 'POST', headers: { 'Content-Type': 'application/json', Accept: 'application/json', 'X-CSRF-TOKEN': token },
                body: JSON.stringify({ projectId: pid, text: '상황판 갱신 확인' }),
            });
            if (!(await r.json()).ok) throw new Error('댓글 실패 ' + r.status);
        }, id);
        await b.evaluate(() => window.BoardPoll());
        await b.waitForFunction((s) => document.getElementById('board').getAttribute('data-stamp') !== s, before);
        assert((await b.locator('.board-kpi').count()) === 5, '다시 받은 화면이 비어 있음');
        await tv.close();
    });

    await step('도움말 — 머리의 ? 로 열고, 대시보드 카드의 ? 는 그 절로 감', async () => {
        await page.goto(BASE + '/');
        await page.click('#help-link');
        await page.waitForSelector('.help-toc');
        assert((await page.locator('.help-sec').count()) >= 14, '도움말 절이 모자람');
        await page.goto(BASE + '/');
        await page.click('#risk-card [data-help=risk]');
        await page.waitForSelector('#risk:target');
        assert(new URL(page.url()).hash === '#risk', '위험 절로 가지 않음');
        await shot(page, 'd66-help', false);
    });

    await step('완료 목록 — 종료 연도 고르기(기본 올해), 전체 연도로 바꾸면 주소에 남음', async () => {
        await page.goto(BASE + '/Projects?state=done');
        const year = String(new Date(Date.now() + 9 * 3600e3).getUTCFullYear());
        assert((await page.locator('#list-year').inputValue()) === year, '기본 연도가 올해가 아님');
        await page.selectOption('#list-year', '0');
        await page.waitForURL(/year=0/);
        assert((await page.locator('#list-year').inputValue()) === '0', '전체 연도가 유지되지 않음');
        await page.click('#filters label:has(input[value=dropped])');
        await page.waitForURL(/state=dropped/);
        assert(!/year=0/.test(page.url()), '탭을 바꿨는데 연도가 남음');
        await shot(page, 'd67-list-year');
    });

    // 사용자 정리: 'e2e 오타'를 'e2e 원본'으로 합치고, 가져오기로 생긴 'e2e가져오기담당'의 담당을 김물류에게 넘겨 사용 중지한다
    await step('관리 — 사용자 합치기·담당 이관 후 사용 중지·변경 이력·사용 현황', async () => {
        await page.goto(BASE + '/Admin');
        await page.click('#admin-tabs [data-kind=user]');
        await page.waitForFunction(() => document.querySelector('.columns') && document.querySelector('.columns').innerText.includes('평영'));
        for (const name of ['e2e 오타', 'e2e 원본']) {
            const add = page.locator('form[data-add="1"]');
            await add.locator('input[name=name]').fill(name);
            await add.locator('button[type=submit]').click();
            await page.waitForFunction((n) => [...document.querySelectorAll('.node-name')].some((x) => x.textContent === n), name);
        }
        // 맡은 일(진행 과제·미완료 작업 수)이 사용자 목록에 보인다
        await page.waitForFunction(() => [...document.querySelectorAll('.node')].some((n) => n.innerText.includes('e2e가져오기담당') && n.innerText.includes('과제 1')));

        // 합치기: 사용자 시트 → 합치기 시트 → 확인
        await page.locator('.node', { hasText: 'e2e 오타' }).locator('.node-main').click();
        await page.waitForSelector(SHEET + ' [data-merge]');
        await page.click(SHEET + ' [data-merge]');
        await page.waitForSelector(SHEET + ' select[name=intoId]');
        const into = await page.$eval(SHEET + ' select[name=intoId]', (sel) => [...sel.options].find((o) => o.text.startsWith('e2e 원본')).value);
        await page.selectOption(SHEET + ' select[name=intoId]', into);
        await page.fill(SHEET + ' textarea[name=reason]', '이름 오타');
        await shot(page, 'd70-admin-merge', false);
        await page.click(SHEET + ' button[type=submit]');
        await page.locator('dialog.sheet[open] [data-yes]').click();
        await page.waitForFunction(() => ![...document.querySelectorAll('.node-name')].some((x) => x.textContent === 'e2e 오타'));
        await page.waitForSelector(SHEET, { state: 'detached' });

        // 이관: 맡은 일이 있는 사람의 '사용 중지'는 이관 시트로
        await page.locator('.node', { hasText: 'e2e가져오기담당' }).locator('.node-main').click();
        await page.waitForSelector(SHEET + ' [data-user-load]');
        await page.click(SHEET + ' [data-danger]');
        await page.waitForSelector(SHEET + ' select[name=toId]');
        assert(await page.locator(SHEET + ' input[name=projects]').isChecked(), '진행 과제 담당이 기본 선택이 아님');
        await page.click(SHEET + ' button[type=submit]');
        assert((await sheetError(page)).includes('넘겨받을 사람'), '넘겨받을 사람 없이 이관됨');
        const kim = await page.$eval(SHEET + ' select[name=toId]', (sel) => [...sel.options].find((o) => o.text.startsWith('김물류')).value);
        await page.selectOption(SHEET + ' select[name=toId]', kim);
        await page.fill(SHEET + ' textarea[name=reason]', '부서 이동');
        await shot(page, 'd71-admin-reassign', false);
        await submitSheet(page);
        await page.waitForFunction(() => [...document.querySelectorAll('.node.off')].some((n) => n.innerText.includes('e2e가져오기담당') && n.innerText.includes('사용 중지')));

        // 변경 이력 탭: 방금 한 일과 앞 단계의 기준 정보 변경(공휴일·작업 템플릿 등)
        await page.click('#admin-tabs [data-kind=changes]');
        await page.waitForSelector('[data-changes-list]');
        const all = await page.locator('[data-changes-list]').innerText();
        assert(all.includes('사용자 병합: e2e 오타 → e2e 원본'), '병합 기록이 없음');
        assert(all.includes('담당 이관 후 사용 중지: e2e가져오기담당 → 김물류'), '이관 기록이 없음');
        await page.selectOption('[data-changes-filter] select[name=kind]', 'holiday');
        await page.click('[data-changes-filter] button[type=submit]');
        await page.waitForFunction(() => {
            const list = document.querySelector('[data-changes-list]');
            return list && !list.innerText.includes('사용자 병합') && list.innerText.includes('공휴일');
        });
        await shot(page, 'd72-admin-changes');

        // 사용 현황 탭: 조직별 집계와 (관리자) 사람별 마지막 접속
        await page.click('#admin-tabs [data-kind=usage]');
        await page.waitForSelector('[data-usage-orgs] tbody tr');
        assert((await page.locator('[data-usage-people]').innerText()).includes('평영'), '사람별 접속이 없음');
        await shot(page, 'd73-admin-usage');
    });

    await desktop.close();

    // ── 모바일 ──────────────────────────────────────────────────────
    const mobile = await browser.newContext({
        viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true,
        locale: 'ko-KR', timezoneId: 'Asia/Seoul',
        userAgent: 'Mozilla/5.0 (Linux; Android 14; SM-S928N) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Mobile Safari/537.36',
    });
    const m = await mobile.newPage();
    home = active = m;
    watch(m, 'mobile');
    m.setDefaultTimeout(10000);

    // 가로 넘침 검사: 본문이 화면 폭보다 넓으면 좌우 스크롤이 생긴다
    async function noOverflow(label) {
        const over = await m.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
        assert(over <= 0, label + ' 화면이 가로로 ' + over + 'px 넘침');
    }

    console.log('mobile');
    await step('모바일 대시보드', async () => {
        await m.goto(BASE + '/');
        await m.waitForSelector('.kpis');
        assert(await m.locator('.bottom-nav').isVisible(), '하단 탭이 보이지 않음');
        assert((await m.locator('.bottom-nav a').count()) === 5, '하단 탭이 5개가 아님');
        assert((await m.locator('.bottom-nav a[aria-current=page]').innerText()).includes('대시보드'), '하단 탭에 지금 화면 표시(aria-current)가 없음');
        await noOverflow('대시보드');
        await shot(m, 'm01-dashboard');
    });

    await step('모바일 내 할 일', async () => {
        await m.locator('.bottom-nav a', { hasText: '내 할 일' }).click();
        await m.waitForSelector('.page-head h1');
        await noOverflow('내 할 일');
        await shot(m, 'm02-my');
    });

    await step('모바일 내 할 일 바로 갱신 — 진행률 칩(48px)·진행 내용', async () => {
        await m.goto(BASE + '/My');
        const id = await m.locator('[data-my-work]').last().getAttribute('data-my-work');
        const sel = `[data-my-work="${id}"]`;
        const box = await m.locator(sel + ' [data-quick-progress="25"]').boundingBox();
        assert(box && box.height >= 48, '진행률 칩 높이가 48px 미만: ' + (box && box.height));
        await noOverflow('내 할 일 빠른 갱신');
        await m.click(sel + ' [data-quick-progress="25"]');
        await m.waitForFunction((q) => document.querySelector(q + ' [data-quick-progress="25"]').getAttribute('aria-pressed') === 'true', sel);
        await m.fill(sel + ' [data-quick-note]', '모바일에서 남김');
        await m.click(sel + ' [data-quick-save]');
        await m.waitForFunction((q) => !document.querySelector(q).hasAttribute('aria-busy') && document.querySelector(q + ' [data-quick-note]').value === '', sel);
        await shot(m, 'm14-my-quick');
    });

    await step('모바일 작업 시트 빠른 갱신 — 맨 위 칩, 접힌 일정', async () => {
        await m.goto(BASE + '/Projects?state=all');
        await m.evaluate(() => App.pref.set('detail.workMore', '0'));
        try {
            await m.locator('.ptable-row', { hasText: '광주 냉장고 3라인 AGV 도입' }).first().click();
            await m.waitForSelector('#summary .summary-meta');
            const open = worksOf(await projectData(m)).find((w) => !w.actualEnd && !w.closedOn);
            const url = m.url().replace(/#.*/, '') + '#w' + open.id;
            await m.goto('about:blank');
            await m.goto(url);
            await m.waitForSelector(SHEET + ' [data-quick]');
            const box = await m.locator(SHEET + ' [data-quick-progress="50"]').boundingBox();
            assert(box && box.height >= 48, '시트의 진행률 칩 높이가 48px 미만');
            assert(await m.locator(SHEET + ' input[name=planStart]').isHidden(), '모바일에서 일정이 접혀 있지 않음');
            await shot(m, 'm05c-work-sheet-quick', false);
            await closeSheet(m);
        } finally {
            await m.evaluate(() => App.pref.set('detail.workMore', '1'));
        }
    });

    await step('모바일 과제 목록', async () => {
        await m.locator('.bottom-nav a', { hasText: '과제' }).click();
        await m.waitForSelector('.ptable-row');
        await noOverflow('과제 목록');
        await shot(m, 'm03-projects');
    });

    await step('모바일 과제 상세 — 기본은 목록 보기', async () => {
        await m.locator('.ptable-row', { hasText: '베트남 세탁기 2라인 AMR 확대' }).click();
        await m.waitForSelector('#schedule-list .stage-card');
        await noOverflow('과제 상세');
        await shot(m, 'm04-detail-list');
    });

    await step('모바일 재계획 — 하단 시트의 표에서 옮겨질 작업을 보고 사유를 넣어 저장하면 단추가 사라짐', async () => {
        await m.waitForSelector('#replan:not([hidden])');
        const item = m.locator('#schedule-list .work-item', { has: m.locator('.work-title', { hasText: '출하 검사' }) });
        assert((await item.innerText()).includes('예상'), '시작 전 작업에 예상 일정이 없음');
        await m.click('#replan');
        await m.waitForSelector(SHEET + ' [data-preview-moves] tbody tr');
        const over = await m.evaluate(() => { const b = document.querySelector('dialog.sheet[open] .sheet-body'); return b.scrollWidth - b.clientWidth; });
        assert(over <= 0, '재계획 시트가 가로로 넘침 ' + over);
        await noOverflow('재계획 시트');
        await shot(m, 'm04b-replan', false);
        await m.fill(SHEET + ' textarea[name=reason]', '제어기 조립 지연 반영');
        await submitSheet(m);
        await m.waitForSelector('#replan', { state: 'hidden' });
        assert(!(await item.innerText()).includes('예상'), '재계획한 뒤에도 예상 일정이 목표와 다름');
    });

    await step('모바일 작업 시트', async () => {
        // 다른 작업의 '선행: 제어기 조립' 표시와 겹치지 않게 제목으로 찾는다
        const item = m.locator('#schedule-list .work-item', { has: m.locator('.work-title', { hasText: '제어기 조립' }) });
        await item.click();
        await m.waitForSelector(SHEET);
        // 모바일은 시트를 열자마자 입력칸에 포커스를 주지 않는다 (가상 키보드가 내용을 가리지 않게)
        assert((await m.evaluate(() => document.activeElement && document.activeElement.tagName)) !== 'INPUT', '모바일 시트가 입력칸에 포커스를 줌');
        await shot(m, 'm05-work-sheet', false);
        await setRange(m, SHEET + ' input[name=progress]', 85);
        await submitSheet(m);
        assert((await item.innerText()).includes('85%'), '진행률이 저장되지 않음');
    });

    await step('모바일 작업 시트 — 선행마다 유형·간격 칸(터치 크기), 근무일 수는 서버 미리보기', async () => {
        await m.locator('#schedule-list .work-item', { has: m.locator('.work-title', { hasText: '출하 검사' }) }).click();
        await m.waitForSelector(SHEET);
        const sel = m.locator(SHEET + ' .pre-row .pre-opts:not([hidden]) select').first();
        await sel.scrollIntoViewIfNeeded();
        const h = await sel.evaluate((el) => el.getBoundingClientRect().height);
        assert(h >= 44, '유형 선택이 터치하기에 작음: ' + h);
        await m.waitForFunction(() => /근무일 \d+일/.test(document.querySelector('dialog.sheet[open] [data-plan-hint]').textContent));
        const over = await m.evaluate(() => { const b = document.querySelector('dialog.sheet[open] .sheet-body'); return b.scrollWidth - b.clientWidth; });
        assert(over <= 0, '작업 시트가 가로로 넘침 ' + over);
        await shot(m, 'm05b-work-link-type', false);
        await closeSheet(m);
    });

    await step('모바일 간트 보기·단계 시트', async () => {
        await m.click('#view-mode [data-view=gantt]');
        await m.waitForSelector('#gantt .g-row.stage');
        await noOverflow('간트');
        await shot(m, 'm06-detail-gantt');
        await stageRow(m, '검수').click();
        await m.waitForSelector(SHEET);
        await shot(m, 'm07-stage-sheet', false);
        await closeSheet(m);
    });

    await step('모바일 단계 시트 — 목표일을 바꾸면 이후 단계 작업 포함 선택지', async () => {
        await openProject(m, '광주 2공장 물류 동선 Simulation');
        await m.click('#view-mode [data-view=list]');
        await m.locator('#schedule-list .stage-head', { hasText: '모델링' }).click();
        await m.waitForSelector(SHEET);
        await m.fill(SHEET + ' input[name=targetDate]', kstToday(20));
        const option = m.locator(SHEET + ' label', { has: m.locator('input[name=cascadeWorks]') });
        assert(await option.isVisible(), '작업 포함 선택지가 보이지 않음');
        await noOverflow('단계 시트');
        await shot(m, 'm07b-stage-cascade-works', false);
        await discardSheet(m);
        await openProject(m, '베트남 세탁기 2라인 AMR 확대');     // 다음 단계(자료 탭)는 원래 과제에서 본다
    });

    await step('모바일 단계 일정 편집 — 하단 시트, 날짜 두 칸과 간격', async () => {
        await m.click('#edit-schedule');
        await m.waitForSelector(SHEET + ' .sched-row');
        const w = await m.evaluate(() => document.querySelector('dialog.sheet[open]').getBoundingClientRect().width);
        assert(w >= 389, '모바일 시트가 화면 폭 전체가 아님: ' + w);
        const over = await m.evaluate(() => { const b = document.querySelector('dialog.sheet[open] .sheet-body'); return b.scrollWidth - b.clientWidth; });
        assert(over <= 0, '시트가 가로로 넘침 ' + over);
        const gapBefore = await m.locator(SHEET + ' .sched-row').nth(6).locator('[data-gap]').innerText();
        await m.locator(SHEET + ' .sched-row').nth(6).locator('input[name^=target]').fill(kstToday(200));
        await m.waitForSelector(SHEET + ' [data-reason]:not([hidden])');
        // 간격은 서버가 다시 계산해 준다 (미리보기 응답 뒤에 바뀐다)
        await m.waitForFunction((b) => {
            const rows = document.querySelector('dialog.sheet[open] [data-rows]');
            return !rows.hasAttribute('aria-busy') && rows.querySelectorAll('.sched-row')[6].querySelector('[data-gap]').innerText !== b;
        }, gapBefore);
        await shot(m, 'm07c-schedule-sheet', false);
        await discardSheet(m);
    });

    await step('모바일 과제 등록 — 단계 목표일 초안', async () => {
        await m.goto(BASE + '/Projects/Edit');
        await m.locator('.choice', { hasText: '물류 자동화' }).click();
        await m.waitForSelector('#draft .sched-row');
        await noOverflow('과제 등록 초안');
        await m.locator('#draft-field').scrollIntoViewIfNeeded();
        await shot(m, 'm11e-edit-draft');
        await openProject(m, '베트남 세탁기 2라인 AMR 확대');     // 다음 단계(자료 탭)는 원래 과제에서 본다
    });

    await step('모바일 과제 간 연결 — 단계 카드의 경고 칩과 단계 시트의 연결 목록', async () => {
        await openProject(m, '광주 냉장고 3라인 AGV 도입');
        await m.click('#view-mode [data-view=list]');
        await m.waitForSelector('#schedule-list .stage-cross [data-cross-chip].warn');
        await noOverflow('과제 간 연결 칩');
        await m.locator('#schedule-list .stage-head', { hasText: '입고' }).click();
        await m.waitForSelector(SHEET + ' [data-cross-list] li.bad');
        await m.locator(SHEET + ' [data-cross]').scrollIntoViewIfNeeded();
        const over = await m.evaluate(() => { const b = document.querySelector('dialog.sheet[open] .sheet-body'); return b.scrollWidth - b.clientWidth; });
        assert(over <= 0, '단계 시트가 가로로 넘침 ' + over);
        await shot(m, 'm07d-cross-link', false);
        await closeSheet(m);
        await openProject(m, '베트남 세탁기 2라인 AMR 확대');     // 다음 단계(자료 탭)는 원래 과제에서 본다
    });

    await step('모바일 자료·분석·과제 등록·관리 화면', async () => {
        await m.click('.tabs [data-tab=files]');
        await noOverflow('자료');
        await shot(m, 'm08-detail-files');
        await m.goto(BASE + '/Insights/Weekly');
        await m.waitForSelector('.kpis');
        await noOverflow('주간 보고');
        await shot(m, 'm09-insights-weekly');
        await m.goto(BASE + '/Insights/LeadTime');
        await noOverflow('리드타임');
        await m.goto(BASE + '/Insights/History');
        await noOverflow('변경 이력');
        // 날짜 두 칸이 한 줄을 나눠 써서 날짜가 잘리지 않는다(고르기 칸은 다음 줄)
        const dates = await m.$$eval('.filter-row input[type=date]', (els) => els.map((e) => ({ w: e.getBoundingClientRect().width, top: e.getBoundingClientRect().top })));
        assert(dates.length === 2 && dates.every((d) => d.w >= 140) && dates[0].top === dates[1].top, '변경 이력 날짜 칸이 좁음: ' + JSON.stringify(dates));
        await shot(m, 'm10-insights-history');
        await m.goto(BASE + '/Projects/Edit');
        await m.waitForSelector('#edit-form');
        await noOverflow('과제 등록');
        await m.goto(BASE + '/Admin');
        await m.waitForSelector('.columns .node');
        await noOverflow('관리');
        await shot(m, 'm11-admin');
        await m.goto(BASE + '/Admin#holiday');
        await m.waitForSelector('.year-chips [data-year]');
        await noOverflow('관리 공휴일');
        const chip = await m.locator('.year-chip').first().boundingBox();
        assert(chip && chip.height >= 40, '모바일 연도 칩이 너무 작음: ' + JSON.stringify(chip));
        await shot(m, 'm11f-admin-holiday');
        await m.goto(BASE + '/Admin/Update');
        await m.waitForSelector('.update-version');
        await noOverflow('업데이트');
        await shot(m, 'm11-update');
    });

    await step('모바일 분석 확장 — 추세·변경 분석·부하·기간 보고가 화면 안에 들어옴', async () => {
        for (const [url, name, shotName] of [['/Insights/Trend', '추세', 'm09b-trend'], ['/Insights/Changes', '변경 분석', 'm09c-changes'],
            ['/Insights/Load', '부하', 'm09d-load'], ['/Insights/Weekly?period=month', '월간 보고', 'm09e-report-month']]) {
            await m.goto(BASE + url);
            await m.waitForSelector('.tabs');
            await noOverflow(name);
            await shot(m, shotName);
        }
        // 조건 고르기 칸은 터치 크기
        await m.goto(BASE + '/Insights/Trend');
        const sel = await m.locator('[data-analytics-filter] select').first().boundingBox();
        assert(sel && sel.height >= 40, '모바일 조건 칸이 작음: ' + JSON.stringify(sel));
    });

    await step('모바일 활동 더 보기·과제 전체 이력', async () => {
        await m.goto(BASE + '/Projects?state=all');
        await m.locator('.ptable-row', { hasText: '광주 냉장고 3라인 AGV 도입' }).first().click();
        await m.waitForSelector('#summary .summary-meta');
        await m.click('.tabs [data-tab=activity]');
        await m.waitForSelector('#feed > li');
        assert(await m.locator('#feed-more').isVisible(), '더 보기 단추가 없음');
        await m.click('#feed-more');
        await m.waitForFunction(() => document.querySelectorAll('#feed > li').length > 50);
        await noOverflow('활동 더 보기');
        await m.locator('.feed-foot').scrollIntoViewIfNeeded();
        await shot(m, 'm13-activity-more', false);
        await m.click('#feed-history');
        await m.waitForSelector('#history-project');
        await noOverflow('과제 전체 이력');
        await shot(m, 'm13b-history-project', false);
    });

    await step('모바일 알림·활동 조건·알릴 사람 고르기 — 하단 탭 화면에서 넘치지 않고 입력은 터치 크기', async () => {
        await m.goto(BASE + '/My/Notifications');
        await m.waitForSelector('.page-head h1');
        assert(await m.locator('#bell').isVisible(), '모바일 상단에 알림 종이 없음');
        await noOverflow('알림 목록');
        await shot(m, 'm14-notifications');
        await m.goto(BASE + '/Projects?state=all');
        await m.locator('.ptable-row', { hasText: '광주 2공장 물류 동선 Simulation' }).first().click();
        await m.waitForSelector('#summary .summary-meta');
        await m.click('.tabs [data-tab=activity]');
        await m.waitForSelector('#feed > li');
        const box = await m.locator('#comment-mentions [data-mention-input]').boundingBox();
        assert(box && box.height >= 48, '이름 칸이 터치 크기(48px)보다 작음: ' + (box && box.height));
        await m.click('#feed-filters [data-kind=comment]');
        await m.waitForFunction(() => document.getElementById('feed-status').textContent.includes('조건에 맞는'));
        await noOverflow('활동 조건');
        await m.locator('#feed-filters').scrollIntoViewIfNeeded();
        await shot(m, 'm15-activity-filter', false);
        await m.evaluate(() => window.scrollTo(0, 0));
        await m.locator('#comment-form').scrollIntoViewIfNeeded();
        await shot(m, 'm15b-mention-box', false);
        await m.goto(BASE + '/My');
        await m.waitForSelector('#watched');
        await noOverflow('내 할 일 관심 과제');
    });

    await step('모바일 업데이트 — 시작되지 않아 되돌린 버전 안내', async () => {
        await m.goto(BASE + '/Admin/Update');
        await m.waitForSelector('.update-version');
        await mockStartFailure(m);
        await m.click('[data-act=check]');
        await m.waitForSelector('.start-failure');
        await noOverflow('업데이트 시작 실패 안내');
        await shot(m, 'm11d-update-start-failure');
        await m.unroute('**/Admin/Update?handler=Check');
    });

    await step('모바일 서버 상태·백업·진단 정보', async () => {
        await m.goto(BASE + '/Admin/Status');
        await m.waitForSelector('#checks');
        await noOverflow('서버 상태');
        await shot(m, 'm12-status');
        await m.goto(BASE + '/Admin/Update#backup');
        await m.waitForSelector('#backup tr[data-backup]');
        await m.click('#diagnostics [data-act=diag]');
        await m.waitForSelector('#diag-state');
        await noOverflow('업데이트 백업·진단');
        await m.evaluate(() => window.scrollTo(0, 0));
        await shot(m, 'm12b-update-backup');
    });

    await step('모바일 과제 삭제·휴지통 — 하단 시트로 지우고, 휴지통에서 영구 삭제하면 기록에 남음', async () => {
        await openProject(m, '베트남 냉장고 1라인 컨베이어 개선');
        await m.click('.tabs [data-tab=info]');
        await m.click('#btn-delete');
        await m.waitForSelector(SHEET + ' textarea[name=reason]');
        await noOverflow('과제 삭제 시트');
        await shot(m, 'm14-delete-sheet', false);
        await m.locator(SHEET + ' button[type=submit]').click();
        await m.waitForURL(/\/Projects(\?|$)/);
        await m.goto(BASE + '/Admin/Trash');
        await m.waitForSelector('#trash-list');
        await noOverflow('휴지통');
        const box = await m.locator('.trash-row [data-purge]').first().boundingBox();
        assert(box && box.height >= 44, '휴지통 단추가 터치 크기보다 작음');
        await shot(m, 'm14b-trash');
        await m.locator('.trash-row', { hasText: '컨베이어 개선' }).locator('[data-purge]').click();
        await m.locator('dialog[open] [data-yes]').click();
        await m.waitForFunction(() => !document.querySelector('#trash-list') || !document.querySelector('#trash-list').innerText.includes('컨베이어 개선'));
        assert((await m.locator('#purge-log').innerText()).includes('컨베이어 개선'), '영구 삭제 기록이 없음');
        await noOverflow('휴지통 기록');
        // 좁은 화면에서는 기록을 행 목록으로 쌓는다 — 영구 삭제(누가·언제) 칸이 가로로 밀지 않아도 화면 안에 보여야 한다
        const purged = await m.locator('#purge-log td[data-label="영구 삭제"]').first().boundingBox();
        const vw = m.viewportSize().width;
        assert(purged && purged.x >= 0 && purged.x + purged.width <= vw + 1, '모바일 영구 삭제 기록의 영구 삭제 칸이 화면 밖에 있음');
        await shot(m, 'm14c-trash-log');
    });

    await step('모바일 작업 여러 개 추가 — 하단 시트, 줄마다 이름표로 쌓은 표', async () => {
        await openProject(m, PASTE);
        const stage = (await projectData(m)).stages.find((x) => !x.completedDate);
        await m.click('[data-add-works="' + stage.id + '"]');
        await m.waitForSelector(SHEET + ' textarea[name=text]');
        await m.fill(SHEET + ' textarea[name=text]', '모바일 점검\t\t' + kstToday(70) + '\t' + kstToday(71) + '\n틀린 줄\t\t모름\t');
        await m.click(SHEET + ' [data-paste-check]');
        await m.waitForSelector(SHEET + ' [data-paste-table] tr.row-bad');
        await noOverflow('작업 여러 개 추가');
        const btn = await m.locator(SHEET + ' [data-paste-check]').boundingBox();
        assert(btn && btn.height >= 40, '확인 단추가 너무 작음');
        await shot(m, 'm19-paste-works', false);
        await discardSheet(m);
    });

    await step('모바일 작업 시트 — 저장 후 계속 추가 단추가 하단에서 넘치지 않음', async () => {
        await openProject(m, PASTE);
        const stage = (await projectData(m)).stages.find((x) => !x.completedDate);
        await m.click('[data-add-work="' + stage.id + '"]');
        await m.waitForSelector(SHEET + ' [data-continue]');
        const foot = await m.evaluate(() => {
            const f = document.querySelector('dialog.sheet[open] .sheet-foot');
            return { scroll: f.scrollWidth, client: f.clientWidth, buttons: [...f.querySelectorAll('button')].map((b) => b.getBoundingClientRect().right) };
        });
        assert(foot.scroll <= foot.client && foot.buttons.every((r) => r <= 390), '시트 하단 단추가 넘침: ' + JSON.stringify(foot));
        await shot(m, 'm19b-work-continue', false);
        await closeSheet(m);
    });

    await step('모바일 가져오기·관리 작업 템플릿·복제 시트', async () => {
        await m.goto(BASE + '/Projects');
        await m.click('#btn-import');
        await m.waitForSelector('#import-form');
        if (importTemplate) {
            await m.setInputFiles('#import-file', { name: '가져오기.xlsx', mimeType: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
                buffer: fillTemplate(importTemplate, { '과제': [['모바일 가져오기', '없는 유형', '한국', '', '', kstToday(3)]] }) });
            await m.click('#import-check');
            await m.waitForSelector('[data-import-projects] tr.row-bad');
        }
        await noOverflow('가져오기');
        await m.evaluate(() => window.scrollTo(0, 0));
        await shot(m, 'm20-import');
        await m.goto(BASE + '/Admin#category');
        await m.waitForSelector('.columns .node');
        await m.locator('.column').first().locator('.node-main', { has: m.locator('.node-name', { hasText: /^물류$/ }) }).click();
        await m.locator('.column').nth(1).locator('.node-main', { hasText: '검토' }).click();
        await m.waitForSelector('[data-template-works] .node');
        await m.locator('[data-template-works]').scrollIntoViewIfNeeded();
        await noOverflow('관리 작업 템플릿');
        await shot(m, 'm21-admin-template-works', false);
        await openProject(m, PASTE);
        await m.click('#btn-clone');
        await m.waitForSelector(SHEET + ' [data-location-picker] select');
        await noOverflow('복제 시트');
        const sel = await m.locator(SHEET + ' [data-location-picker] select').first().boundingBox();
        assert(sel && sel.height >= 44, '위치 고르기 칸이 터치 크기가 아님: ' + JSON.stringify(sel));
        await shot(m, 'm22-clone-location', false);
        await closeSheet(m);
    });

    await step('모바일 도움말·상황판 — 머리의 ? 로 열고 넘치지 않음', async () => {
        await m.goto(BASE + '/');
        assert(await m.locator('#help-link').isVisible(), '모바일 머리에 도움말이 없음');
        await m.click('#help-link');
        await m.waitForSelector('.help-toc');
        await noOverflow('도움말');
        await shot(m, 'm23-help', false);
        await m.goto(BASE + '/Board');
        await m.waitForSelector('.board-kpi');
        await noOverflow('상황판');
        await shot(m, 'm24-board', false);
    });

    await step('모바일 사용자 바꾸기·관리 사용자 IP', async () => {
        await m.goto(BASE + '/Welcome?switch=1');
        await m.waitForSelector('.welcome-card input[name=Name]');
        await noOverflow('사용자 바꾸기');
        await shot(m, 'm11b-switch-user');
        await m.goto(BASE + '/Admin');
        await m.click('#admin-tabs [data-kind=user]');
        await m.waitForFunction(() => document.querySelector('.columns') && document.querySelector('.columns').innerText.includes('평영'));
        await m.locator('.node', { hasText: '평영' }).locator('.node-main').click();
        await m.waitForSelector(SHEET + ' [data-ips]');
        await noOverflow('사용자 IP');
        await shot(m, 'm11c-admin-user-ip', false);
        await closeSheet(m);
    });

    await step('모바일 관리 — 합치기·이관 시트, 변경 이력, 사용 현황이 화면 안에 들어옴', async () => {
        await m.goto(BASE + '/Admin');
        await m.click('#admin-tabs [data-kind=user]');
        await m.waitForFunction(() => document.querySelector('.columns') && document.querySelector('.columns').innerText.includes('김물류'));
        await m.locator('.node', { hasText: '김물류' }).locator('.node-main').click();
        await m.waitForSelector(SHEET + ' [data-merge]');
        await m.click(SHEET + ' [data-merge]');
        await m.waitForSelector(SHEET + ' select[name=intoId]');
        await noOverflow('합치기 시트');
        const sel = await m.locator(SHEET + ' select[name=intoId]').boundingBox();
        assert(sel && sel.height >= 44, '합칠 사람 칸이 터치 크기가 아님: ' + JSON.stringify(sel));
        await shot(m, 'm25-admin-merge', false);
        await closeSheet(m);
        // 김물류는 맡은 일이 있어 사용 중지 단추가 이관 시트를 연다 (저장하지 않는다)
        await m.locator('.node', { hasText: '김물류' }).locator('.node-main').click();
        await m.waitForSelector(SHEET + ' [data-danger]');
        await m.click(SHEET + ' [data-danger]');
        await m.waitForSelector(SHEET + ' select[name=toId]');
        await noOverflow('이관 시트');
        await shot(m, 'm25b-admin-reassign', false);
        await closeSheet(m);
        await m.click('#admin-tabs [data-kind=changes]');
        await m.waitForSelector('[data-changes-list]');
        await noOverflow('변경 이력 탭');
        await shot(m, 'm25c-admin-changes');
        await m.click('#admin-tabs [data-kind=usage]');
        await m.waitForSelector('[data-usage-orgs] tbody tr');
        await noOverflow('사용 현황 탭');
        await shot(m, 'm25d-admin-usage');
    });

    await mobile.close();
    await browser.close();

    // ── 결과 정리 ───────────────────────────────────────────────────
    const ran = results.filter((r) => !r.skipped);
    const failed = ran.filter((r) => !r.ok);
    const code = (t) => '`' + String(t).replace(/`/g, "'") + '`';
    const lines = [
        `# 화면 점검 결과`,
        ``,
        `- 대상: ${BASE}`,
        `- 단계: ${ran.length - failed.length} / ${ran.length} 통과` + (ran.length < results.length ? ` (ONLY=${ONLY.join('|')}, ${results.length - ran.length}개 건너뜀)` : ''),
        `- 콘솔·서버 오류: ${problems.length}건`,
        ``,
        ...ran.flatMap((r) => r.ok ? [`- 통과 — ${r.name}`] : [
            `- **실패** — ${r.name} — ${code(r.error)}`,
            ...(r.url ? [`  - 주소: ${code(r.url)}`] : []),
            ...(r.texts && r.texts.length ? [`  - 화면 문구: ${r.texts.map(code).join(' / ')}`] : []),
            ...(r.screen ? [`  - 화면: [${r.screen}](${r.screen})`] : []),
        ]),
    ];
    if (problems.length) lines.push('', '## 콘솔·서버 오류', '', ...problems.map((p) => '- ' + code(p)));
    if (process.env.APP_LOG) {
        const summary = appLogSummary(process.env.APP_LOG);
        lines.push('', '## 앱 로그 경고·오류', '', ...(summary.length ? summary : ['- 없음']));
    }
    fs.writeFileSync(path.join(OUT, 'report.md'), lines.join('\n') + '\n');

    console.log(`\n${ran.length - failed.length}/${ran.length} passed, ${problems.length} console/server problems`);
    process.exit(failed.length || problems.length ? 1 : 0);
})().catch((err) => {
    console.error(err);
    process.exit(2);
});
