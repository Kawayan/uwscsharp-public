(() => {
    const input = document.getElementById('search');
    const results = document.getElementById('results');
    const status = document.getElementById('search-status');
    const normalize = value => value.normalize('NFKC').toLocaleLowerCase('ja');
    input.addEventListener('input', () => {
        const query = normalize(input.value.trim());
        results.replaceChildren();
        results.hidden = !query;
        status.textContent = '';
        if (!query) return;
        const terms = query.split(/\s+/u);
        const matches = window.HELP_INDEX.filter(page =>
            terms.every(term => normalize(page.title + ' ' + page.text).includes(term)));
        const exactTitle = page => {
            const title = normalize(page.title);
            return title === query || title.startsWith(query + ' — ');
        };
        matches.sort((left, right) => Number(exactTitle(right)) - Number(exactTitle(left)));
        status.textContent = matches.length ? `${matches.length}件のページが見つかりました` : '一致するページがありません。別の語で検索してください。';
        for (const page of matches) {
            const item = document.createElement('li');
            const link = document.createElement('a');
            link.href = input.dataset.helpRoot + page.url;
            link.textContent = page.title;
            const excerpt = document.createElement('p');
            const text = page.text.replace(/\s+/gu, ' ').trim();
            const position = normalize(text).indexOf(terms[0]);
            const start = Math.max(0, position - 30);
            excerpt.textContent = (start ? '…' : '') + text.slice(start, start + 160) + '…';
            item.append(link, excerpt);
            results.append(item);
        }
    });
    input.addEventListener('keydown', event => {
        if (event.key === 'Escape') {
            input.value = '';
            input.dispatchEvent(new Event('input'));
        }
    });
})();
