// HTMLの読取りと生成物の不整合検出を検査します。
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const { ROOT, SOURCE, read, decode, document, build, validateLinks } = require('./build_help');

const parsed = document('<!-- <a href="missing.html">非表示</a> --><h2 id="a&#45;b">A &amp; B</h2><a href="index.html?a=>b#x">リンク</a>');
assert.deepEqual([...parsed.ids], ['a-b']);
assert.deepEqual(parsed.links, ['index.html?a=>b#x']);
assert.equal(parsed.text.join(' '), 'A & B リンク');
assert.equal(decode('&lt;&gt;&quot;&apos;&nbsp;&#x1f600;&#10;'), '<>"\'\u00a0😀\n');
assert.throws(() => document('<h2 id="same"></h2><p id="same"></p>'), /IDが重複/);
assert.throws(() => decode('&unknown;'), /未対応の文字参照/);
validateLinks({ 'index.html': '<h2 id="a"></h2><a href="#a">節</a>' });
for (const [page, href] of [
    ['index.html', 'https://github.com/Kawayan/uwscsharp-public/issues/new?template=bug-report.yml'],
    ['index.html', 'https://github.com/sponsors/Kawayan'],
    ['pages/getting-started.html', 'https://github.com/Kawayan/uwscsharp-public/releases'],
    ['pages/getting-started.html', 'https://dotnet.microsoft.com/ja-jp/download/dotnet/11.0'],
]) {
    const link = `<a href="${href}">リンク</a>`;
    validateLinks({ [page]: link });
    assert.throws(() => validateLinks({ 'pages/test.html': link }), /外部リンク/);
    assert.throws(() => validateLinks({ 'index.html': '' }, [href]), /外部リンク/);
    assert.throws(() => validateLinks({ [page]: `<a href="${href}/other">リンク</a>` }), /外部リンク/);
}
for (const href of ['missing.html', '#missing', 'https://example.com/', '//example.com/', 'javascript:alert(1)']) {
    assert.throws(() => validateLinks({ 'index.html': `<a href="${href}">リンク</a>` }), undefined, href);
}

const outputs = build();
const artifacts = path.join(ROOT, 'artifacts');
fs.mkdirSync(artifacts, { recursive: true });
const temporary = fs.mkdtempSync(path.join(artifacts, 'help-build-check-'));
const run = (...args) => spawnSync(process.execPath, [path.join(__dirname, 'build_help.js'), ...args],
    { cwd: temporary, encoding: 'utf8', windowsHide: true,
        env: Object.fromEntries(Object.entries(process.env).map(([key, value]) =>
            [key, key.toLowerCase() === 'path' ? '' : value])) });
try {
    fs.writeFileSync(path.join(temporary, 'editor.html'), '旧形式の生成物');
    const generated = run('--output', temporary);
    assert.equal(generated.status, 0, generated.stderr);
    assert(!fs.existsSync(path.join(temporary, 'editor.html')));
    const checked = run('--output', temporary, '--check');
    assert.equal(checked.status, 0, checked.stderr);
    for (const [name, value] of Object.entries(outputs)) assert.equal(read(path.join(temporary, name)), value, name);
    fs.appendFileSync(path.join(temporary, 'index.html'), '不一致');
    assert.notEqual(run('--output', temporary, '--check').status, 0);
    fs.writeFileSync(path.join(temporary, 'index.html'), outputs['index.html']);
    fs.writeFileSync(path.join(temporary, 'unexpected.txt'), '公開対象外');
    assert.notEqual(run('--output', temporary, '--check').status, 0);
    assert.notEqual(run('--output', SOURCE).status, 0);
    assert.notEqual(run('--unknown').status, 0);

    // WPFプロジェクトを参照し、Node.jsだけで生成します。
    const isolated = path.join(temporary, 'isolated');
    fs.mkdirSync(path.join(isolated, 'scripts'), { recursive: true });
    fs.copyFileSync(path.join(__dirname, 'build_help.js'), path.join(isolated, 'scripts/build_help.js'));
    fs.cpSync(SOURCE, path.join(isolated, 'docs/user-help'), { recursive: true });
    const project = 'src/UWSCSharp.Wpf/UWSCSharp.Wpf.csproj';
    fs.mkdirSync(path.dirname(path.join(isolated, project)), { recursive: true });
    fs.copyFileSync(path.join(ROOT, project), path.join(isolated, project));
    const isolatedRun = (...args) => spawnSync(process.execPath,
        [path.join(isolated, 'scripts/build_help.js'), ...args], {
            cwd: temporary, encoding: 'utf8', windowsHide: true,
            env: Object.fromEntries(Object.entries(process.env).map(([key, value]) =>
                [key, key.toLowerCase() === 'path' ? '' : value])),
        });
    const isolatedBuild = isolatedRun();
    assert.equal(isolatedBuild.status, 0, isolatedBuild.stderr);
    assert.equal(isolatedRun('--check').status, 0);
    const isolatedProject = path.join(isolated, project);
    fs.writeFileSync(isolatedProject, read(isolatedProject).replace(
        /<UwscWpfVersion>[^<]+<\/UwscWpfVersion>/,
        '<UwscWpfVersion>17.21.34-preview.1</UwscWpfVersion>'));
    assert.notEqual(isolatedRun('--check').status, 0);
    assert.equal(isolatedRun().status, 0);
    const index = read(path.join(isolated, 'docs/user-help/index.html'));
    assert(index.includes('<code>17.21.34-preview.1</code>'));
    assert(index.includes('<code>17.21.34.0</code>'));
    assert(read(path.join(isolated, 'docs/user-help/assets/search-index.js')).includes('17.21.34-preview.1'));
    fs.appendFileSync(path.join(isolated, 'docs/user-help/index.html'), '古いローカル生成物');
    assert.notEqual(isolatedRun('--check').status, 0);
} finally {
    // 検査自身が作った領域だけを削除します。
    assert(path.dirname(path.resolve(temporary)) === path.resolve(artifacts));
    assert(path.basename(temporary).startsWith('help-build-check-'));
    fs.rmSync(temporary, { recursive: true });
}
console.log('生成検査成功: HTML文字参照・属性・コメント・重複ID・不正リンク・別出力先・不一致・余分なファイル・引数');
