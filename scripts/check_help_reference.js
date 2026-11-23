// 収録範囲・階層目次・CLI掲載例をPythonなしで検査します。
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const { parseArgs } = require('node:util');
const { ROOT, SOURCE, read, readJson, scanHtml, build } = require('./build_help');

function validateNavigation(outputs) {
    const pages = readJson(path.join(SOURCE, 'pages.json'));
    const expected = new Map(pages.map(page => [page.slug === 'index' ? 'index.html' : `pages/${page.slug}.html`, page.group ?? null]));
    for (const [name, expectedGroup] of expected) {
        let inside = false;
        let summary = false;
        let group = null;
        const groups = [];
        const links = [];
        scanHtml(outputs[name], {
            start(tag, attrs) {
                if (tag === 'nav' && attrs['aria-label'] === 'ヘルプの目次') inside = true;
                if (!inside) return;
                if (tag === 'details') {
                    group = { title: '', open: Object.hasOwn(attrs, 'open') };
                    groups.push(group);
                }
                if (tag === 'summary') summary = true;
                if (tag === 'a') links.push({ href: path.posix.join(path.posix.dirname(name), attrs.href), group, current: attrs['aria-current'] === 'page' });
            },
            end(tag) {
                if (tag === 'nav') inside = false;
                if (tag === 'summary') summary = false;
                if (tag === 'details') group = null;
            },
            text(text) { if (inside && summary) group.title += text; },
        });
        assert.deepEqual(groups.map(group => group.title), ['使い方', '言語リファレンス', 'スクリプト関数', '困ったとき'], name);
        assert.equal(links.length, expected.size, name);
        assert.deepEqual(new Set(links.map(link => link.href)), new Set(expected.keys()), name);
        assert.deepEqual(links.filter(link => link.current).map(link => link.href), [name], name);
        assert.deepEqual(groups.filter(group => group.open).map(group => group.title), expectedGroup ? [expectedGroup] : [], name);
        for (const link of links) assert.equal(link.group?.title ?? null, expected.get(link.href), name);
    }
    console.log(`階層目次検査成功: ${pages.length}ページの所属・選択・自動展開`);
}

function validateCatalog() {
    const catalog = readJson(path.join(SOURCE, 'reference.json'));
    const core = path.join(ROOT, 'src/UWSCSharp.Core');
    const constants = Object.fromEntries([...read(path.join(core, 'Builtins/BuiltinFunctionNames.cs'))
        .matchAll(/const string (\w+) = "([A-Z0-9_]+)"/g)].map(([, key, value]) => [key, value]));
    const registered = new Set();
    const arities = {};
    for (const file of ['Builtins/BuiltinFunctions.cs', 'Abstractions/IScriptFiles.cs', 'Abstractions/WindowsFunctions.cs']) {
        for (const [, key, min, max] of read(path.join(core, file))
            .matchAll(/\[BuiltinFunctionNames\.(\w+)\] = (?:new)?\((\d+), (\d+|int\.MaxValue)(?:, ValidateBeforeEvaluation: (?:true|false))?\)/g)) {
            assert(constants[key], key);
            registered.add(constants[key]);
            arities[constants[key]] = [Number(min), max === 'int.MaxValue' ? 2147483647 : Number(max)];
        }
    }
    // 状態を伴う関数は共通の名前・実行種別対応表に登録されています。
    const special = read(path.join(core, 'Builtins/StatefulBuiltins.cs'));
    for (const [, key] of special.matchAll(/\[BuiltinFunctionNames\.(\w+)\] = StatefulBuiltin\.\w+/g)) {
        assert(constants[key], key);
        registered.add(constants[key]);
    }
    Object.assign(arities, { CTRLTRD: [2, 2], EVAL: [1, 1], LOGPRINT: [1, 5], SLEEP: [1, 1], GETTIME: [0, 2], RANDOM: [0, 1], TOKEN: [2, 4], RESIZE: [1, 2], SETCLEAR: [1, 2], SHIFTARRAY: [2, 2], QSORT: [1, 10] });
    arities[constants.StopForm] = [1, 3];
    arities[constants.SetHotkey] = [1, 3];
    arities[constants.Set8087Cw] = [0, 1];
    arities[constants.OleEvent] = [1, 4];
    arities[constants.Speak] = [0, 3];
    arities[constants.RecoState] = [0, 37];
    arities[constants.Dictate] = [0, 37];
    const names = catalog.entries.map(entry => entry.name);
    assert.equal(names.length, new Set(names).size, '関数名が重複しています');
    assert.deepEqual(new Set(names.filter(name => name !== 'PRINT')), registered, '収録名と登録名が一致しません');
    const ledger = readJson(path.join(SOURCE, 'capabilities.json'));
    const features = Object.fromEntries(ledger.features.map(feature => [feature.id, feature]));
    const categories = new Set(catalog.categories.map(category => category.id));
    for (const entry of catalog.entries) {
        const feature = features[entry.feature];
        assert(feature && feature.implementation !== '未着手', entry.name);
        assert(categories.has(entry.category), entry.name);
        assert(/^[a-z][a-z0-9-]*$/.test(entry.anchor), entry.anchor);
        if (entry.name !== 'PRINT') {
            const [actualMin, actualMax] = arities[entry.name];
            // 複数の呼出し形式を併記した項目は、形式ごとに引数数を検査します。
            for (const signature of entry.signature.split(' / ')) {
                assert(signature.startsWith(entry.name + '('), entry.name);
                const parameters = signature.slice(signature.indexOf('(') + 1).replace(/\)+$/, '');
                const required = parameters.split('[')[0].replace(/[, ]+$/, '');
                const minimum = required ? required.split(',').length : 0;
                const maximum = parameters ? parameters.split(',').length : 0;
                assert(actualMin <= minimum && minimum <= maximum && maximum <= actualMax, entry.name);
            }
        }
        for (const key of ['signature', 'description', 'returns', 'notes']) {
            assert(entry[key] && entry[key].length, `${entry.name}: ${key}`);
        }
        const evidence = entry.evidence;
        for (const file of [...evidence.sources, ...evidence.tests]) assert(fs.existsSync(path.join(ROOT, file)), `${entry.name}: ${file}`);
    }
    console.log(`収録検査成功: 実装登録${registered.size}関数とPRINT、${categories.size}分類`);
    return catalog;
}

function collectSamples(catalog) {
    const samples = catalog.entries.filter(entry => entry.example && Object.hasOwn(entry.example, 'expected'))
        .map(entry => ({ name: entry.anchor, ...entry.example }));
    for (const file of fs.readdirSync(path.join(SOURCE, 'content')).filter(file => file.endsWith('.html')).sort()) {
        let current;
        scanHtml(read(path.join(SOURCE, 'content', file)), {
            start(tag, attrs) {
                if (tag === 'pre' && attrs['data-example'] === 'verified') {
                    current = { name: attrs['data-name'], code: '', expected: attrs['data-expected'] };
                }
            },
            end(tag) {
                if (tag === 'pre' && current) { samples.push(current); current = undefined; }
            },
            text(text) { if (current) current.code += text; },
        });
    }
    samples.push({ name: 'call', code: 'CALL child.uws("太郎")', expected: '太郎\n', files: { 'child.uws': 'PRINT PARAM_STR[0]' } });
    return samples;
}

function runSamples(catalog, cli) {
    const validation = path.join(ROOT, 'artifacts/help-validation');
    fs.mkdirSync(validation, { recursive: true });
    const folder = fs.mkdtempSync(path.join(validation, 'samples-'));
    const results = [];
    try {
        for (const sample of collectSamples(catalog)) {
            assert(/^[a-z][a-z0-9-]*$/.test(sample.name), sample.name);
            const script = path.join(folder, `${sample.name}.uws`);
            fs.writeFileSync(script, sample.code, 'utf8');
            for (const [name, content] of Object.entries(sample.files ?? {})) {
                assert(path.basename(name) === name, name);
                fs.writeFileSync(path.join(folder, name), content, 'utf8');
            }
            const result = spawnSync('dotnet', [cli, script], { cwd: folder, encoding: 'utf8', timeout: 30000, windowsHide: true });
            const stdout = (result.stdout ?? '').replace(/\r\n/g, '\n');
            const stderr = result.stderr ?? '';
            const passed = !result.error && result.status === 0 && stdout === sample.expected && !stderr;
            results.push({ name: sample.name, passed, exit: result.status, stdout, stderr, ...(result.error ? { error: result.error.message } : {}) });
        }
    } finally {
        // 作成した一時フォルダーが検査用領域内にあることを確かめてから削除します。
        assert(path.dirname(path.resolve(folder)) === path.resolve(validation));
        assert(path.basename(folder).startsWith('samples-'));
        fs.rmSync(folder, { recursive: true });
    }
    fs.writeFileSync(path.join(validation, 'reference-samples.json'), JSON.stringify(results, null, 2) + '\n', 'utf8');
    const failures = results.filter(result => !result.passed);
    assert.equal(failures.length, 0, JSON.stringify(failures, null, 2));
    console.log(`掲載例検査成功: ${results.length}件（副作用のない例と一時ファイルのCALL）`);
}

function main() {
    const { values } = parseArgs({ options: { cli: { type: 'string' } } });
    const catalog = validateCatalog();
    validateNavigation(build());
    if (values.cli) {
        const cli = path.resolve(values.cli);
        assert(fs.statSync(cli).isFile(), cli);
        runSamples(catalog, cli);
    }
}
module.exports = { validateNavigation, validateCatalog, collectSamples };
if (require.main === module) main();
