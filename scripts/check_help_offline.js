// 専用プロファイルのヘッドレスブラウザーで同梱ヘルプを検証します。
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { spawn } = require('node:child_process');
const { parseArgs } = require('node:util');
const pageCount = require('../docs/user-help/pages.json').length;
const { values } = parseArgs({ options: {
    browser: { type: 'string' }, index: { type: 'string' }, output: { type: 'string' },
} });
for (const name of ['browser', 'index', 'output']) assert(values[name], name);
const output = path.resolve(values.output);
fs.mkdirSync(output, { recursive: true });
const profile = fs.mkdtempSync(path.join(output, 'profile-'));
const browser = spawn(path.resolve(values.browser), [
    '--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check',
    '--disable-background-networking', '--disable-component-update', '--disable-sync',
    '--remote-debugging-port=0', `--user-data-dir=${profile}`, 'about:blank',
], { windowsHide: true, stdio: 'ignore' });
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
async function until(action) {
    const limit = Date.now() + 15000;
    while (Date.now() < limit) {
        const result = await action();
        if (result) return result;
        await sleep(50);
    }
    throw new Error('ブラウザーの応答が時間内に確認できません。');
}
async function connect(address) {
    const socket = new WebSocket(address);
    await new Promise((resolve, reject) => {
        socket.addEventListener('open', resolve, { once: true });
        socket.addEventListener('error', reject, { once: true });
    });
    let sequence = 0;
    const pending = new Map();
    const exceptions = [];
    socket.addEventListener('message', event => {
        const result = JSON.parse(event.data);
        if (result.method === 'Runtime.exceptionThrown') exceptions.push(result.params);
        const waiter = pending.get(result.id);
        if (!waiter) return;
        pending.delete(result.id);
        clearTimeout(waiter.timeout);
        if (result.error) waiter.reject(new Error(JSON.stringify(result.error)));
        else waiter.resolve(result.result);
    });
    return {
        socket, exceptions,
        call(method, params = {}) {
            const id = ++sequence;
            return new Promise((resolve, reject) => {
                const timeout = setTimeout(() => {
                    pending.delete(id);
                    reject(new Error(`応答なし: ${method}`));
                }, 15000);
                pending.set(id, { resolve, reject, timeout });
                socket.send(JSON.stringify({ id, method, params }));
            });
        },
    };
}
(async () => {
    let control;
    let page;
    try {
        const portFile = path.join(profile, 'DevToolsActivePort');
        await until(() => fs.existsSync(portFile));
        const [port, endpoint] = fs.readFileSync(portFile, 'utf8').trim().split(/\r?\n/);
        control = await connect(`ws://127.0.0.1:${port}${endpoint}`);
        const version = await control.call('Browser.getVersion');
        const targets = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
        page = await connect(targets.find(target => target.type === 'page').webSocketDebuggerUrl);
        await page.call('Page.enable');
        await page.call('Runtime.enable');
        const evaluate = async expression => {
            const result = await page.call('Runtime.evaluate', { expression, returnByValue: true });
            assert(!result.exceptionDetails, JSON.stringify(result.exceptionDetails));
            return result.result.value;
        };
        const index = path.resolve(values.index);
        const pages = ['index.html', ...fs.readdirSync(path.join(path.dirname(index), 'pages'))
            .filter(name => name.endsWith('.html')).sort().map(name => `pages/${name}`)];
        assert.equal(pages.length, pageCount);
        const rows = [];
        for (const width of [1365, 390]) {
            await page.call('Emulation.setDeviceMetricsOverride', {
                width, height: 900, deviceScaleFactor: 1, mobile: false,
            });
            for (const name of pages) {
                const url = pathToFileURL(path.join(path.dirname(index), name)).href;
                await page.call('Page.navigate', { url });
                await until(() => evaluate(`location.href === ${JSON.stringify(url)} && document.readyState === 'complete' && Array.isArray(window.HELP_INDEX)`));
                const state = await evaluate(`({title:document.title, nav:document.querySelectorAll('nav[aria-label="ヘルプの目次"] a').length,
                    overflow:document.documentElement.scrollWidth>innerWidth+1})`);
                assert(state.title.includes('UWSC#'), name);
                assert.equal(state.nav, pageCount, name);
                if (state.overflow) {
                    const offenders = await evaluate(`([...document.querySelectorAll('main *')].filter(e=>e.getBoundingClientRect().right>innerWidth+1).slice(0,20).map(e=>({tag:e.tagName,right:e.getBoundingClientRect().right,text:e.textContent.slice(0,200),ancestors:[e,e.parentElement,e.parentElement.parentElement].map(p=>({tag:p.tagName,width:p.getBoundingClientRect().width,overflow:getComputedStyle(p).overflow,position:getComputedStyle(p).position,scroll:p.scrollWidth}))})))`);
                    const text = await evaluate(`(()=>{const result=[];const walker=document.createTreeWalker(document.querySelector('main'),NodeFilter.SHOW_TEXT);while(walker.nextNode()){const n=walker.currentNode;let p=n.parentElement,clipped=false;while(p&&p!==document.documentElement){if(['auto','scroll','hidden','clip'].includes(getComputedStyle(p).overflowX)){clipped=true;break}p=p.parentElement}if(clipped)continue;const range=document.createRange();range.selectNodeContents(n);if([...range.getClientRects()].some(r=>r.right>innerWidth+1))result.push({text:n.textContent,parent:n.parentElement.tagName});}return result})()`);
                    fs.writeFileSync(path.join(output, 'overflow.json'), JSON.stringify({ width, name, offenders, text }, null, 2));
                    const picture = await page.call('Page.captureScreenshot', { format: 'png' });
                    fs.writeFileSync(path.join(output, 'overflow.png'), Buffer.from(picture.data, 'base64'));
                }
                assert.equal(state.overflow, false, `${width}:${name}`);
                if (width === 390 && name === 'pages/functions-controls.html') {
                    const codeScroll = await evaluate(`(()=>{const p=document.getElementById('getitem').closest('section').querySelector('pre');return p.scrollWidth>p.clientWidth&&getComputedStyle(p).overflowX==='auto'})()`);
                    assert(codeScroll, '長いコード枠の横スクロール');
                }
                rows.push({ width, name, ...state });
            }
            const home = pathToFileURL(index).href;
            const editor = new URL('pages/editor.html', home).href;
            const loaded = url => until(() => evaluate(
                `location.href === ${JSON.stringify(url)} && document.readyState === 'complete' && Array.isArray(window.HELP_INDEX)`));
            await page.call('Page.navigate', { url: home });
            await loaded(home);
            assert(await evaluate(`(() => {
                const link = [...document.querySelectorAll('nav a')].find(a => a.href === ${JSON.stringify(editor)});
                if (!link) return false;
                link.click();
                return true;
            })()`));
            await loaded(editor);
            await evaluate("document.querySelector('.brand').click()");
            await loaded(home);
            if (width === 390) {
                await page.call('Page.navigate', { url: editor });
                await loaded(editor);
            }
            await until(() => evaluate("document.readyState === 'complete' && Array.isArray(window.HELP_INDEX)"));
            await evaluate(`document.querySelector('#search').value='GETOLEITEM';document.querySelector('#search').dispatchEvent(new Event('input'))`);
            const selected = await evaluate(`(()=>{const a=[...document.querySelectorAll('#results a')].find(a=>a.href.endsWith('/pages/functions-com.html#getoleitem'));if(!a)return false;a.click();return true})()`);
            assert(selected);
            await until(() => evaluate("location.hash === '#getoleitem' && document.readyState === 'complete' && !!document.getElementById('getoleitem')"));
            const screenshot = await page.call('Page.captureScreenshot', { format: 'png' });
            fs.writeFileSync(path.join(output, `com-${width}.png`), Buffer.from(screenshot.data, 'base64'));
        }
        assert.equal(page.exceptions.length, 0);
        fs.writeFileSync(path.join(output, 'result.json'), JSON.stringify({ result: 'PASS',
            version, file_origin: true, pages: rows, search_navigation_widths: [1365, 390],
            long_code_scroll_checked: true,
            javascript_exceptions: page.exceptions, own_profile: profile }, null, 2));
        console.log(`PASS: file://の${rows.length}画面と2幅の検索遷移`);
    } finally {
        page?.socket.close();
        if (control) {
            try { await control.call('Browser.close'); } catch {}
            control.socket.close();
        }
        if (browser.exitCode === null) browser.kill();
    }
})().catch(error => {
    fs.writeFileSync(path.join(output, 'result.json'), JSON.stringify({
        result: 'FAIL', error: String(error), stack: error.stack,
    }, null, 2));
    console.error(error);
    process.exitCode = 1;
});
