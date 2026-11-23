// ブラウザー操作を行わず、公開用の検索スクリプトを検査します。
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

class Element {
    constructor() {
        this.children = [];
        this.listeners = {};
        this.value = '';
        this.textContent = '';
        this.hidden = true;
    }

    addEventListener(name, listener) {
        this.listeners[name] = listener;
    }

    dispatchEvent(event) {
        this.listeners[event.type](event);
    }

    replaceChildren() {
        this.children = [];
    }

    append(...children) {
        this.children.push(...children);
    }
}

const root = path.resolve(__dirname, '..');
const elements = Object.fromEntries(['search', 'results', 'search-status']
    .map(name => [name, new Element()]));
elements.search.dataset = { helpRoot: '' };
const context = vm.createContext({
    window: {},
    document: {
        getElementById: name => elements[name],
        createElement: () => new Element(),
    },
    Event: class Event {
        constructor(type) {
            this.type = type;
        }
    },
});
const site = process.argv[2] || 'docs/user-help';
for (const file of ['search-index.js', 'help.js']) {
    vm.runInContext(fs.readFileSync(path.join(root,
        site, 'assets', file), 'utf8'), context);
}

function search(query) {
    elements.search.value = query;
    elements.search.dispatchEvent({ type: 'input' });
    return elements.results.children.map(item => item.children[0]);
}

assert.equal(search('GETID')[0]?.href, 'pages/functions-windows.html#getid');
elements.search.dataset.helpRoot = '../';
assert.equal(search('GETID')[0]?.href, '../pages/functions-windows.html#getid');
elements.search.dataset.helpRoot = '';
assert.equal(search('ＧＥＴＩＤ')[0]?.href, 'pages/functions-windows.html#getid');
assert.equal(search('PRINT')[0]?.href, 'pages/functions-display.html#print');
assert.equal(search('SENDSTR')[0]?.href, 'pages/functions-input.html#sendstr');
assert.equal(search('スクリプトを予約実行する')[0]?.href, 'pages/scheduler.html');
assert.equal(search('一回だけ実行する')[0]?.href, 'pages/scheduler.html#run-once');
assert(search('VAR 書き戻').some(link => link.href.startsWith('pages/routines.html#')));
assert(search('FOPEN UTF-8').some(link => link.href === 'pages/functions-files.html#fopen'));
assert(search('該当項目のない検索語abcdef').length === 0);
assert(elements['search-status'].textContent.includes('ありません'));
assert(search('<script>alert(1)</script>').length === 0);
search('GETID');
elements.search.listeners.keydown({ key: 'Escape' });
assert.equal(elements.search.value, '');
assert.equal(elements.results.hidden, true);
assert.equal(elements.results.children.length, 0);
console.log('検索ロジック: 関数名・ページ名・見出しの完全一致を優先。全角英字・複数語・該当なし・入力文字列・Escapeの検査成功');
