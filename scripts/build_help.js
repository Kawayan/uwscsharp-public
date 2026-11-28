// 利用者向けヘルプをNode.jsの標準機能だけで生成・検査します。
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { parseArgs } = require('node:util');
const ROOT = path.resolve(__dirname, '..');
const SOURCE = path.join(ROOT, 'docs/user-help');
const WPF_PROJECT = path.join(ROOT, 'src/UWSCSharp.Wpf/UWSCSharp.Wpf.csproj');
const allowedExternalLinks = {
    'index.html': new Set([
        'https://github.com/Kawayan/uwscsharp-public/issues/new?template=bug-report.yml',
        'https://github.com/sponsors/Kawayan',
    ]),
    'pages/getting-started.html': new Set([
        'https://github.com/Kawayan/uwscsharp-public/releases',
        'https://dotnet.microsoft.com/ja-jp/download/dotnet/11.0',
    ]),
};
const read = file => fs.readFileSync(file, 'utf8').replace(/\r\n/g, '\n');
const pagePath = slug => slug === 'index' ? 'index.html' : `pages/${slug}.html`;
const relativePath = (from, to) => path.posix.relative(path.posix.dirname(from), to);
const readJson = file => JSON.parse(read(file));
const escape = value => String(value).replace(/[&<>"']/g,
    character => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#x27;' })[character]);

function decode(value) {
    const named = { amp: '&', lt: '<', gt: '>', quot: '"', apos: "'", nbsp: '\u00a0' };
    return value.replace(/&(#x[\da-f]+|#\d+|[a-z][a-z\d]+);/gi, (entity, name) => {
        if (!name.startsWith('#')) {
            assert(Object.hasOwn(named, name), `未対応の文字参照です。数値文字参照で指定してください: ${entity}`);
            return named[name];
        }
        const code = name[1].toLowerCase() === 'x'
            ? parseInt(name.slice(2), 16) : Number(name.slice(1));
        return code === 0 || code > 0x10ffff || (code >= 0xd800 && code <= 0xdfff)
            ? '\ufffd' : String.fromCodePoint(code);
    });
}

// 管理対象のHTML断片を読みます。コメントと属性内の「>」を本文と混同しません。
function scanHtml(html, visitor) {
    const tokens = /<!--[\s\S]*?-->|<![^>]*>|<\/?[a-z][a-z\d:-]*(?:\s+(?:[^\s<>"'=]+(?:\s*=\s*(?:"[^"]*"|'[^']*'|[^\s>]+))?)\s*)*\s*\/?>|[^<]+|</gi;
    for (const token of html.match(tokens) || []) {
        if (token.startsWith('<!')) continue;
        const tag = /^<(\/?)([a-z][a-z\d:-]*)/i.exec(token);
        if (!tag) {
            visitor.text?.(decode(token));
            continue;
        }
        const name = tag[2].toLowerCase();
        if (tag[1]) {
            visitor.end?.(name);
            continue;
        }
        const attrs = {};
        const expression = /([^\s<>"'=\/]+)(?:\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s>]+)))?/g;
        const attributes = token.slice(tag[0].length, token.endsWith('/>') ? -2 : -1);
        for (const match of attributes.matchAll(expression)) {
            attrs[match[1].toLowerCase()] = decode(match[2] ?? match[3] ?? match[4] ?? '');
        }
        visitor.start?.(name, attrs);
        if (token.endsWith('/>')) visitor.end?.(name);
    }
}

function document(html) {
    const result = { text: [], ids: new Set(), links: [] };
    scanHtml(html, {
        start(tag, attrs) {
            if (Object.hasOwn(attrs, 'id')) {
                assert(!result.ids.has(attrs.id), `IDが重複しています: ${attrs.id}`);
                result.ids.add(attrs.id);
            }
            for (const key of ['href', 'src']) {
                if (Object.hasOwn(attrs, key)) result.links.push(attrs[key]);
            }
        },
        text: text => result.text.push(text),
    });
    return result;
}

function referenceBody(catalog, category) {
    const group = catalog.categories.find(group => group.id === category);
    assert(group, `不明な分類: ${category}`);
    const body = [`<p class="lead">${escape(group.description)}</p>`];
    body.push('<div class="notice">コマンドライン版とUWSC# アプリで実装のある範囲を説明します。角括弧内は省略可能です。</div>');
    if (category === 'windows') body.push('<p>対象IDはGETIDで取得します。実行ごとに管理され、別の実行へ持ち越せません。</p>');
    if (category === 'display') body.push('<p>PRINTは制御文です。以下の関数・文は、それぞれ掲載した採用範囲で利用できます。</p>');
    for (const entry of catalog.entries.filter(entry => entry.category === category)) {
        assert(/^[a-z][a-z0-9-]*$/.test(entry.anchor), entry.anchor);
        body.push(`<section class="reference-entry"><h2 id="${entry.anchor}">${escape(entry.name)}</h2>`);
        body.push(`<p>${escape(entry.description)}</p>`);
        body.push(`<h3>書式</h3><pre><code>${escape(entry.signature)}</code></pre>`);
        body.push('<h3>引数</h3><dl class="parameters">');
        for (const [parameter, description] of entry.parameters) {
            body.push(`<dt><code>${escape(parameter)}</code></dt><dd>${escape(description)}</dd>`);
        }
        if (!entry.parameters.length) body.push('<dt>引数なし</dt><dd>引数は指定しません。</dd>');
        body.push(`</dl><h3>戻り値・結果</h3><p>${escape(entry.returns)}</p><h3>対応範囲と注意</h3><ul>`);
        for (const note of entry.notes) body.push(`<li>${escape(note)}</li>`);
        body.push(`</ul>`);
        if (entry.example) {
            const example = entry.example;
            const mode = Object.hasOwn(example, 'expected') ? 'verified' : 'manual';
            body.push(`<h3>例</h3><pre data-example="${mode}" data-name="${entry.anchor}"><code>${escape(example.code)}</code></pre>`);
            body.push(mode === 'verified'
                ? `<p>期待出力：</p><pre><code>${escape(example.expected)}</code></pre>`
                : '<p>対象や環境の準備が必要な使用例です。今回の実操作確認は行っていません。</p>');
        }
        body.push('</section>');
    }
    // コード例の内部を変更せず、意味のある単位で改行します。
    return body.join('\n');
}

function navigation(pages, current) {
    let group;
    let html = '';
    for (const page of pages) {
        if (page.group !== group) {
            if (group) html += '</ul></details></li>';
            group = page.group;
            if (group) {
                const opened = pages.some(p => p.slug === current && p.group === group) ? ' open' : '';
                html += `<li><details class="nav-section"${opened}><summary>${escape(group)}</summary><ul>`;
            }
        }
        const selected = page.slug === current ? ' aria-current="page"' : '';
        html += `<li><a${selected} href="${relativePath(pagePath(current), pagePath(page.slug))}">${escape(page.nav_title ?? page.title)}</a></li>`;
    }
    if (group) html += '</ul></details></li>';
    return html;
}

function build() {
    const pages = readJson(path.join(SOURCE, 'pages.json'));
    const catalog = readJson(path.join(SOURCE, 'reference.json'));
    const versions = [...read(WPF_PROJECT).matchAll(/<UwscWpfVersion>\s*([^<]+?)\s*<\/UwscWpfVersion>/g)];
    assert.equal(versions.length, 1, 'WPFプロジェクトに UwscWpfVersion を一つ設定してください');
    const version = versions[0][1];
    const components = /^(\d+\.\d+\.\d+)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$/.exec(version);
    assert(components, `WPFプロジェクトのバージョンが不正です: ${version}`);
    const outputs = {};
    const entries = [];
    for (const [index, page] of pages.entries()) {
        const { slug, title } = page;
        assert(/^[a-z][a-z-]*$/.test(slug), slug);
        const file = pagePath(slug);
        assert(!Object.hasOwn(outputs, file), `ページが重複しています: ${slug}`);
        const body = page.catalog ? referenceBody(catalog, page.catalog)
            : read(path.join(SOURCE, 'content', `${slug}.html`))
                .replaceAll('{{WPF_PRODUCT_VERSION}}', escape(version))
                .replaceAll('{{WPF_FILE_VERSION}}', `${components[1]}.0`);
        entries.push({ url: file, title, text: Array.from(document(body).text.join(' ')).slice(0, 600).join('') });
        for (const section of (' ' + body).split(/(?=<h2 id=")/).slice(1)) {
            const match = /^<h2 id="([^"]+)">(.*?)<\/h2>/.exec(section);
            if (match) entries.push({ url: `${file}#${match[1]}`, title: `${match[2]} — ${title}`, text: document(section).text.join(' ') });
        }
        const toc = [...body.matchAll(/<h2 id="([^"]+)">(.*?)<\/h2>/g)]
            .map(([, anchor, label]) => `<li><a href="#${escape(anchor)}">${label}</a></li>`).join('');
        const pager = [[pages[index - 1], '← '], [pages[index + 1], '次へ：']]
            .filter(([p]) => p).map(([p, label]) => `<a href="${relativePath(file, pagePath(p.slug))}">${label}${escape(p.title)}</a>`).join(' ');
        const nav = navigation(pages, slug);
        outputs[file] = render({ title, body, toc, pager, nav, root: slug === 'index' ? '' : '../' });
    }
    // 既存の索引と同じ表記を維持し、移行だけの差分を避けます。
    const json = '[' + entries.map(entry => '{' + Object.entries(entry)
        .map(([key, value]) => `${JSON.stringify(key)}: ${JSON.stringify(value)}`).join(', ') + '}').join(', ') + ']';
    outputs['assets/search-index.js'] = 'window.HELP_INDEX = ' + json.replace(/</g, '\\u003c') + ';\n';
    for (const asset of ['help.css', 'help.js']) outputs[`assets/${asset}`] = read(path.join(SOURCE, 'assets', asset));
    outputs['.nojekyll'] = '';
    validateLinks(outputs, entries.map(entry => entry.url));
    return outputs;
}

function validateLinks(outputs, searchLinks = []) {
    const documents = Object.fromEntries(Object.entries(outputs)
        .filter(([name]) => name.endsWith('.html')).map(([name, html]) => [name, document(html)]));
    for (const [name, doc] of Object.entries(documents)) {
        const links = doc.links.filter(link => !allowedExternalLinks[name]?.has(link));
        if (name === 'index.html') links.push(...searchLinks);
        for (const link of links) {
            assert(!/^[a-z][a-z\d+.-]*:|^\/\//i.test(link), `${name}: 外部リンク ${link}`);
            const match = /^([^?#]*)(?:\?[^#]*)?(?:#(.*))?$/.exec(link);
            const href = decodeURIComponent(match[1]);
            assert(!href.startsWith('/') && !href.includes('\\'), `${name}: 不正なパス ${link}`);
            const target = href ? path.posix.join(path.posix.dirname(name), href) : name;
            assert(Object.hasOwn(outputs, target), `${name}: リンク先がありません ${link}`);
            if (match[2]) assert(documents[target]?.ids.has(decodeURIComponent(match[2])), `${name}: アンカーがありません ${link}`);
        }
    }
}

function main() {
    const { values } = parseArgs({ options: { output: { type: 'string' }, check: { type: 'boolean' } } });
    const destination = path.resolve(values.output ?? path.join(ROOT, 'artifacts/help-site'));
    assert(destination !== SOURCE, '公開用出力先に原稿フォルダーは指定できません');
    const outputs = build();
    for (const [name, value] of Object.entries(outputs)) {
        for (const folder of [destination, SOURCE]) {
            const file = path.join(folder, name);
            if (values.check) assert.equal(read(file), value, `生成物が一致しません: ${file}`);
            else {
                fs.mkdirSync(path.dirname(file), { recursive: true });
                fs.writeFileSync(file, value, 'utf8');
            }
        }
    }
    for (const name of Object.keys(outputs).filter(name => name.startsWith('pages/'))) {
        for (const folder of [destination, SOURCE]) {
            const legacy = path.join(folder, path.posix.basename(name));
            if (values.check) assert(!fs.existsSync(legacy), `旧形式の生成物が残っています: ${legacy}`);
            else fs.rmSync(legacy, { force: true });
        }
    }
    const actual = fs.readdirSync(destination, { recursive: true, withFileTypes: true })
        .filter(entry => entry.isFile()).map(entry => path.relative(destination, path.join(entry.parentPath, entry.name)).split(path.sep).join('/'));
    assert.deepEqual(new Set(actual), new Set(Object.keys(outputs)), '公開出力に想定外のファイルがあります');
    console.log(`HTMLヘルプ: ${Object.keys(outputs).length}ファイル、リンク・アンカー検査成功`);
}

module.exports = { ROOT, SOURCE, read, readJson, decode, scanHtml, document, build, validateLinks };
if (require.main === module) main();

function render({ title, body, toc, pager, nav, root }) {
    return `<!doctype html>
<html lang="ja">
<head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<meta name="description" content="UWSCSharpの導入・操作・構文・制限を説明する利用者向けヘルプです。">
<title>${escape(title)} | UWSC# ヘルプ</title>
<link rel="stylesheet" href="${root}assets/help.css"><script src="${root}assets/search-index.js" defer></script>
<script src="${root}assets/help.js" defer></script></head>
<body><a class="skip" href="#main">本文へ移動</a>
<aside class="sidebar"><a class="brand" href="${root}index.html">UWSC# <span>ヘルプ</span></a>
<p class="version">UWSC# アプリ・コマンドライン版（プレビュー）</p>
<nav aria-label="ヘルプの目次"><ul>${nav}</ul></nav>
<p class="aside-note">UWSCSharp 利用ガイド<br>原版との完全互換は未確認です。</p></aside>
<div class="workspace"><header><label for="search">ヘルプ内を検索</label>
<input id="search" data-help-root="${root}" type="search" placeholder="例：保存、F8、PARAM_STR" autocomplete="off" aria-controls="results">
<p id="search-status" role="status" aria-live="polite"></p>
<noscript>検索にはJavaScriptが必要です。目次からも閲覧できます。</noscript>
<ol id="results" hidden></ol></header>
<div class="content"><main id="main" tabindex="-1"><p class="breadcrumb">UWSC# ヘルプ / ${escape(title)}</p>
<h1>${escape(title)}</h1>${body}<nav class="pager" aria-label="前後のページ">${pager}</nav>
<footer>確認基準日：2026-10-20 · プレビューの仕様・未確認事項を含みます。</footer></main>
<nav class="toc" aria-label="このページの目次"><strong>このページ</strong><ul>${toc}</ul></nav>
</div></div></body></html>
`;
}
