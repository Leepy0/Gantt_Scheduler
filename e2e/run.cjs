// 화면 점검 스크립트: 실행 중인 앱을 데스크톱·모바일 크기로 돌며 주요 흐름을 눌러 보고 화면을 캡처한다.
//   BASE_URL  대상 주소 (기본 http://localhost:5080) — 샘플 데이터(Seed:Demo)로 막 띄운 앱이어야 한다
//   OUT_DIR   결과 폴더 (기본 e2e/out) — screens/*.png, exports/*(내보낸 Excel·PDF), report.md
//   PW_CHANNEL=chrome  설치된 Chrome 사용 (CI). 지정하지 않으면 Playwright 의 Chromium
//   FAKE_EXPORT=1      내보내기 파일의 형식 검사를 건너뛴다 (가짜 내보내기를 쓰는 로컬 섀도 빌드용)
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

const results = [];      // { name, ok, error }
const problems = [];     // 콘솔 오류·실패한 요청
// 일부러 404 를 받는 요청 (없는 과제 화면, 삭제된 과제의 변경 확인)
const expected404 = ['/Projects/Detail?id=999999', 'handler=Stamp'];

function kstToday(offset = 0) {
    return new Date(Date.now() + 9 * 3600e3 + offset * 86400e3).toISOString().slice(0, 10);
}

async function step(name, fn) {
    try {
        await fn();
        results.push({ name, ok: true });
        console.log('  ok   ' + name);
    } catch (err) {
        results.push({ name, ok: false, error: String(err && err.message || err).split('\n')[0] });
        console.log('  FAIL ' + name + ' — ' + String(err && err.message || err).split('\n')[0]);
    }
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

const stageRow = (page, name) => page.locator('#gantt .g-row.stage', { hasText: name }).locator('.g-name');
const workRow = (page, name) => page.locator('#gantt .g-row.work', { hasText: name });

(async () => {
    const browser = await chromium.launch(process.env.PW_CHANNEL ? { channel: process.env.PW_CHANNEL } : {});

    // ── 데스크톱 ────────────────────────────────────────────────────
    const desktop = await browser.newContext({ viewport: { width: 1440, height: 900 }, locale: 'ko-KR', timezoneId: 'Asia/Seoul' });
    const page = await desktop.newPage();
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
        assert((await page.evaluate(() => document.activeElement && document.activeElement.name)) === 'title', '첫 포커스가 작업명이 아님');
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

    await step('기준선 다시 확정', async () => {
        await page.click('#btn-baseline');
        await page.waitForSelector(SHEET + ' [data-yes]');
        await page.click(SHEET + ' [data-yes]');
        await page.waitForFunction(() => document.querySelector('#summary .summary-chips').innerText.includes('기준선 유지'));
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
        await page.click('#comment-form button[type=submit]');
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

        const other = await desktop.newPage();
        other.setDefaultTimeout(10000);
        await openProject(other, '광주 냉장고 3라인 AGV 도입');
        await other.locator('#stepper .step', { hasText: '설치' }).click();
        await other.waitForSelector(SHEET);
        await other.fill(SHEET + ' input[name=name]', '설치·시운전');
        await submitSheet(other);
        await other.close();

        await page.fill(SHEET + ' input[name=name]', '현장 설치');
        await page.locator(SHEET + ' button[type=submit]').click();
        assert((await sheetError(page)).includes('다른 사용자가 먼저'), '동시 수정 안내가 아님');
        const note = await page.locator(SHEET + ' [data-field=name] [data-conflict-note]').innerText();
        assert(note.includes('다른 사용자가 바꾼 값') && note.includes('설치·시운전'), '바뀐 값 안내가 없음: ' + note);
        assert((await page.inputValue(SHEET + ' input[name=name]')) === '현장 설치', '입력한 값이 사라짐');
        await shot(page, 'd12-conflict', false);
        const names = await page.locator('#stepper .step-name').allInnerTexts();
        assert(names.some((n) => n.includes('설치·시운전')), '화면이 최신 내용으로 바뀌지 않음: ' + names.join(','));
        // 같은 시트에서 다시 저장하면 최신 버전으로 저장된다 (예전에는 몇 번을 눌러도 거절됐다)
        await submitSheet(page);
        const after = await page.locator('#stepper .step-name').allInnerTexts();
        assert(after.some((n) => n.includes('현장 설치')), '다시 저장되지 않음: ' + after.join(','));
    });

    await step('다른 사용자가 이 과제를 바꾸면 띠로 알리고 새로 고침하면 반영, 내 저장은 띠 없음, 날짜가 바뀌면 다시 읽음', async () => {
        await page.evaluate(() => App.checkVersion());
        await page.waitForTimeout(500);
        assert(await page.locator('#change-banner').isHidden(), '아무도 바꾸지 않았는데 띠가 뜸');

        const other = await desktop.newPage();
        other.setDefaultTimeout(10000);
        await openProject(other, '광주 냉장고 3라인 AGV 도입');
        await other.click('.tabs [data-tab=activity]');
        await other.fill('#comment-text', '다른 화면에서 남긴 메모');
        await other.click('#comment-form button[type=submit]');
        await other.waitForFunction(() => document.getElementById('feed').innerText.includes('다른 화면에서 남긴 메모'));
        await other.close();

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
        await page.click('#comment-form button[type=submit]');
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
        const p2 = await abroad.newPage();
        watch(p2, 'abroad');
        p2.setDefaultTimeout(10000);
        await openProject(p2, '광주 냉장고 3라인 AGV 도입');
        await workRow(p2, '안전 인증 서류 준비').locator('.g-name').click();
        await p2.waitForSelector(SHEET);
        await p2.click(SHEET + ' [data-start-today]');
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
        await page.click('#edit-form button[type=submit]');
        await page.waitForSelector('#summary .summary-meta');
        assert((await page.locator('#stepper .step').count()) === 8, '기본 단계 8개가 만들어지지 않음');
        assert((await page.locator('#summary').innerText()).includes('베트남 · 냉장고 · 1라인'), '위치가 저장되지 않음');
        assert((await page.locator('#stepper .step').nth(4).innerText()).includes('체크 0/3'), '체크리스트 템플릿이 복사되지 않음');
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
        await shot(page, 'd15-insights-leadtime');

        await page.locator('.tabs a', { hasText: '주간 보고' }).click();
        await page.waitForSelector('.weekly');
        const weekly = await page.locator('main').innerText();
        assert(weekly.includes('주행 시험 재실시로 일주일 순연'), '이번 주 일정 변경 사유가 없음');
        assert(weekly.includes('재협의 완료'), '이번 주 진행 내용이 없음');
        await shot(page, 'd16-insights-weekly');

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
        await page.waitForSelector(SHEET + ' [data-yes]');
        await page.click(SHEET + ' [data-yes]');
        await page.waitForURL(/\/Projects(\?|$)/);
        await page.goto(BASE + '/Projects?state=all');
        assert((await page.locator('.ptable-row', { hasText: '컨베이어 개선' }).count()) === 0, '삭제되지 않음');
        await other.evaluate(() => App.checkVersion());
        await other.waitForSelector('#change-banner.alarm:not([hidden])');
        assert((await other.locator('#change-banner').innerText()).includes('삭제'), '삭제됨 안내가 아님');
        await other.close();
    });

    await desktop.close();

    // ── 모바일 ──────────────────────────────────────────────────────
    const mobile = await browser.newContext({
        viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true,
        locale: 'ko-KR', timezoneId: 'Asia/Seoul',
        userAgent: 'Mozilla/5.0 (Linux; Android 14; SM-S928N) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Mobile Safari/537.36',
    });
    const m = await mobile.newPage();
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
        await shot(m, 'm10-insights-history');
        await m.goto(BASE + '/Projects/Edit');
        await m.waitForSelector('#edit-form');
        await noOverflow('과제 등록');
        await m.goto(BASE + '/Admin');
        await m.waitForSelector('.columns .node');
        await noOverflow('관리');
        await m.goto(BASE + '/Admin/Update');
        await m.waitForSelector('.update-version');
        await noOverflow('업데이트');
        await shot(m, 'm11-update');
        await shot(m, 'm11-admin');
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

    await mobile.close();
    await browser.close();

    // ── 결과 정리 ───────────────────────────────────────────────────
    const failed = results.filter((r) => !r.ok);
    const lines = [
        `# 화면 점검 결과`,
        ``,
        `- 대상: ${BASE}`,
        `- 단계: ${results.length - failed.length} / ${results.length} 통과`,
        `- 콘솔·서버 오류: ${problems.length}건`,
        ``,
        ...results.map((r) => `- ${r.ok ? '통과' : '**실패**'} — ${r.name}${r.ok ? '' : ` — \`${r.error}\``}`),
    ];
    if (problems.length) lines.push('', '## 콘솔·서버 오류', '', ...problems.map((p) => '- `' + p.replace(/`/g, "'") + '`'));
    fs.writeFileSync(path.join(OUT, 'report.md'), lines.join('\n') + '\n');

    console.log(`\n${results.length - failed.length}/${results.length} passed, ${problems.length} console/server problems`);
    process.exit(failed.length || problems.length ? 1 : 0);
})().catch((err) => {
    console.error(err);
    process.exit(2);
});
