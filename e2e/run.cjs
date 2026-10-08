// 화면 점검 스크립트: 실행 중인 앱을 데스크톱·모바일 크기로 돌며 주요 흐름을 눌러 보고 화면을 캡처한다.
//   BASE_URL  대상 주소 (기본 http://localhost:5080) — 샘플 데이터(Seed:Demo)로 막 띄운 앱이어야 한다
//   OUT_DIR   결과 폴더 (기본 e2e/out) — screens/*.png, report.md
//   PW_CHANNEL=chrome  설치된 Chrome 사용 (CI). 지정하지 않으면 Playwright 의 Chromium
const fs = require('fs');
const path = require('path');

let chromium;
try { ({ chromium } = require('playwright')); } catch (e) { ({ chromium } = require('playwright-core')); }

const BASE = (process.env.BASE_URL || 'http://localhost:5080').replace(/\/$/, '');
const OUT = process.env.OUT_DIR || path.join(__dirname, 'out');
const SHOTS = path.join(OUT, 'screens');
fs.mkdirSync(SHOTS, { recursive: true });

const results = [];      // { name, ok, error }
const problems = [];     // 콘솔 오류·실패한 요청

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
        // 400 은 규칙 위반 응답으로 일부러 유발하는 단계가 있다. 그 밖의 404·500 대는 문제로 본다
        if (r.status() >= 500 || r.status() === 404) problems.push(`[${label}] ${r.status()} ${r.request().method()} ${r.url()}`);
    });
}

async function shot(page, name, full = true) {
    await page.waitForTimeout(150);
    await page.screenshot({ path: path.join(SHOTS, name + '.png'), fullPage: full });
}

function assert(cond, message) { if (!cond) throw new Error(message); }

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
    await page.locator('dialog.sheet[open] button[type=submit]').click();
    await page.waitForSelector('dialog.sheet[open]', { state: 'detached', timeout: 10000 });
}

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
        await shot(page, 'd00-welcome');
    });

    await step('이름 등록 후 대시보드 표시', async () => {
        await page.fill('input[name=Name]', '평영');
        await page.fill('input[name=Dept]', '물류기술팀');
        await page.click('button[type=submit]');
        await page.waitForSelector('.kpis');
        assert((await page.locator('#gantt .g-row').count()) >= 5, '간트 행이 5개 미만');
        assert((await page.locator('.kpi.alarm .kpi-value').innerText()).trim() === '2', '지연 KPI 가 2 가 아님');
        await shot(page, 'd01-dashboard');
    });

    await step('대시보드 간트 일 단위 배율', async () => {
        await page.click('#zoom [data-zoom=day]');
        assert((await page.locator('#gantt .g-tick.today').count()) === 1, '오늘 눈금이 없음');
        await shot(page, 'd02-dashboard-day', false);
        await page.click('#zoom [data-zoom=week]');
    });

    await step('과제 목록·필터', async () => {
        await page.goto(BASE + '/Projects');
        assert((await page.locator('.ptable-row').count()) === 6, '진행 과제가 6건이 아님');
        await shot(page, 'd03-projects');
        await page.locator('.state-tab', { hasText: '지연' }).click();
        await page.waitForURL(/state=delayed/);
        assert((await page.locator('.ptable-row').count()) === 2, '지연 과제가 2건이 아님');
        await page.goto(BASE + '/Projects?state=all&q=' + encodeURIComponent('베트남'));
        assert((await page.locator('.ptable-row').count()) === 2, '검색 결과가 2건이 아님');
    });

    await step('과제 상세 간트', async () => {
        await openProject(page, '광주 냉장고 3라인 AGV 도입');
        assert((await page.locator('#gantt .g-row.stage').count()) === 8, '단계 행이 8개가 아님');
        assert((await page.locator('#gantt .g-row.work').count()) === 3, '작업 행이 3개가 아님');
        await shot(page, 'd04-detail-gantt');
    });

    await step('단계 목표일 변경 — 사유 없이는 거절, 사유 입력 후 연쇄 이동', async () => {
        await page.locator('#gantt .g-row.stage', { hasText: '검수' }).locator('.g-name').click();
        await page.waitForSelector('dialog.sheet[open]');
        await page.fill('dialog.sheet[open] input[name=targetDate]', kstToday(12));
        await page.locator('dialog.sheet[open] button[type=submit]').click();
        await page.waitForSelector('dialog.sheet[open] .form-error:not([hidden])');
        await shot(page, 'd05-stage-sheet-error', false);
        await page.fill('dialog.sheet[open] textarea[name=reason]', '주행 시험 재실시로 일주일 순연');
        await page.check('dialog.sheet[open] input[name=cascade]');
        await submitSheet(page);
        const steps = await page.locator('#stepper .step').allInnerTexts();
        assert(steps[4].includes(kstToday(12).slice(5)), '검수 목표일이 바뀌지 않음: ' + steps[4]);
        assert(steps[5].includes(kstToday(19).slice(5)), '입고 목표일이 연쇄 이동하지 않음: ' + steps[5]);
    });

    await step('작업 추가·진행률 수정', async () => {
        await page.click('#add-work');
        await page.waitForSelector('dialog.sheet[open]');
        await page.fill('dialog.sheet[open] input[name=title]', '안전 인증 서류 준비');
        await page.fill('dialog.sheet[open] input[name=coOwner]', '신규담당');
        await shot(page, 'd06-work-sheet-new', false);
        await submitSheet(page);
        assert((await page.locator('#gantt .g-row.work').count()) === 4, '작업이 추가되지 않음');

        await page.locator('#gantt .g-row.work', { hasText: '주행 시험' }).locator('.g-name').click();
        await page.waitForSelector('dialog.sheet[open]');
        await setRange(page, 'dialog.sheet[open] input[name=progress]', 70);
        await page.fill('dialog.sheet[open] [data-comment]', '곡선 구간 속도 재확인 완료');
        await page.click('dialog.sheet[open] [data-add-comment]');
        await page.waitForFunction(() => document.querySelector('dialog.sheet[open] [data-feed]').innerText.includes('곡선 구간 속도 재확인 완료'));
        await shot(page, 'd07-work-sheet-edit', false);
        await submitSheet(page);
        const row = await page.locator('#gantt .g-row.work', { hasText: '주행 시험' }).innerText();
        assert(row.includes('70%'), '진행률이 70% 로 바뀌지 않음: ' + row);
    });

    await step('목록 보기·정보·활동 탭', async () => {
        await page.click('#view-mode [data-view=list]');
        assert((await page.locator('#schedule-list .stage-card').count()) === 8, '단계 카드가 8개가 아님');
        await shot(page, 'd08-detail-list');
        await page.click('.tabs [data-tab=info]');
        assert((await page.locator('#tab-info .simple-table tbody tr').count()) === 1, '기기 내역이 1행이 아님');
        await shot(page, 'd09-detail-info');
        await page.click('.tabs [data-tab=activity]');
        await page.fill('#comment-text', '검수 일정 협력사와 재협의 완료');
        await page.click('#comment-form button[type=submit]');
        await page.waitForFunction(() => document.getElementById('feed').innerText.includes('재협의 완료'));
        const feed = await page.locator('#feed').innerText();
        assert(feed.includes('연쇄 이동'), '연쇄 이동 이력이 없음');
        await shot(page, 'd10-detail-activity');
        await page.click('.tabs [data-tab=schedule]');
        await page.click('#view-mode [data-view=gantt]');
    });

    await step('모든 단계 완료 시 자동으로 완료 상태', async () => {
        await openProject(page, '폴란드 세탁기 창고 입출고 개선');
        for (let i = 0; i < 3; i++) {
            await page.locator('#stepper .step').nth(i).click();
            await page.waitForSelector('dialog.sheet[open]');
            await page.click('dialog.sheet[open] [data-done-today]');
            await submitSheet(page);
        }
        const chips = await page.locator('#summary .summary-chips').innerText();
        assert(chips.includes('완료'), '완료 상태가 아님: ' + chips);
    });

    await step('상태 변경 — 보류', async () => {
        await openProject(page, '멕시코 냉장고 AGV 도입 검토');
        await page.click('#btn-state');
        await page.waitForSelector('dialog.sheet[open]');
        await page.fill('dialog.sheet[open] textarea[name=reason]', '투자 심의 일정 미확정');
        await shot(page, 'd11-state-sheet', false);
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
        await shot(page, 'd12-edit-form');
        await page.click('#edit-form button[type=submit]');
        await page.waitForSelector('#summary .summary-meta');
        assert((await page.locator('#stepper .step').count()) === 8, '기본 단계 8개가 만들어지지 않음');
        assert((await page.locator('#summary').innerText()).includes('베트남 · 냉장고 · 1라인'), '위치가 저장되지 않음');
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

    await step('Excel 내보내기 응답', async () => {
        const list = await desktop.request.get(BASE + '/Projects?handler=Excel&state=all');
        assert(list.ok(), '목록 Excel 응답 ' + list.status());
        assert((await list.body()).length > 100, '목록 Excel 이 비어 있음');
        const detail = await desktop.request.get(BASE + '/Projects/Detail?handler=Excel&id=1');
        assert(detail.ok(), '상세 Excel 응답 ' + detail.status());
    });

    await step('관리 — 계층 추가·순서 이동, 공휴일', async () => {
        await page.goto(BASE + '/Admin');
        await page.waitForSelector('.columns .node');
        await shot(page, 'd13-admin-location');
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
        await shot(page, 'd14-admin-category');
        await page.click('#admin-tabs [data-kind=user]');
        await page.waitForFunction(() => document.querySelector('.columns') && document.querySelector('.columns').innerText.includes('평영'));
        await shot(page, 'd15-admin-user');
        await page.click('#admin-tabs [data-kind=holiday]');
        await page.waitForFunction(() => document.querySelector('.columns') && document.querySelector('.columns').innerText.includes('한글날'));
    });

    await step('과제 삭제', async () => {
        await openProject(page, '베트남 냉장고 1라인 컨베이어 개선');
        await page.click('.tabs [data-tab=info]');
        await page.click('#btn-delete');
        await page.waitForSelector('dialog.sheet[open] [data-yes]');
        await shot(page, 'd16-delete-confirm', false);
        await page.click('dialog.sheet[open] [data-yes]');
        await page.waitForURL(/\/Projects(\?|$)/);
        await page.goto(BASE + '/Projects?state=all');
        assert((await page.locator('.ptable-row', { hasText: '컨베이어 개선' }).count()) === 0, '삭제되지 않음');
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
        await noOverflow('대시보드');
        await shot(m, 'm01-dashboard');
    });

    await step('모바일 과제 목록', async () => {
        await m.locator('.bottom-nav a', { hasText: '과제' }).click();
        await m.waitForSelector('.ptable-row');
        await noOverflow('과제 목록');
        await shot(m, 'm02-projects');
    });

    await step('모바일 과제 상세 — 기본은 목록 보기', async () => {
        await m.locator('.ptable-row', { hasText: '베트남 세탁기 2라인 AMR 확대' }).click();
        await m.waitForSelector('#schedule-list .stage-card');
        await noOverflow('과제 상세');
        await shot(m, 'm03-detail-list');
    });

    await step('모바일 작업 시트', async () => {
        await m.locator('#schedule-list .work-item', { hasText: '제어기 조립' }).click();
        await m.waitForSelector('dialog.sheet[open]');
        await shot(m, 'm04-work-sheet', false);
        await setRange(m, 'dialog.sheet[open] input[name=progress]', 85);
        await submitSheet(m);
        assert((await m.locator('#schedule-list .work-item', { hasText: '제어기 조립' }).innerText()).includes('85%'), '진행률이 저장되지 않음');
    });

    await step('모바일 간트 보기', async () => {
        await m.click('#view-mode [data-view=gantt]');
        await m.waitForSelector('#gantt .g-row.stage');
        await noOverflow('간트');
        await shot(m, 'm05-detail-gantt');
        await m.locator('#gantt .g-row.stage', { hasText: '제작' }).locator('.g-name').click();
        await m.waitForSelector('dialog.sheet[open]');
        await shot(m, 'm06-stage-sheet', false);
        await m.locator('dialog.sheet[open] [data-close]').first().click();
    });

    await step('모바일 과제 등록·관리 화면', async () => {
        await m.goto(BASE + '/Projects/Edit');
        await m.waitForSelector('#edit-form');
        await noOverflow('과제 등록');
        await shot(m, 'm07-edit-form');
        await m.goto(BASE + '/Admin');
        await m.waitForSelector('.columns .node');
        await noOverflow('관리');
        await shot(m, 'm08-admin');
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
