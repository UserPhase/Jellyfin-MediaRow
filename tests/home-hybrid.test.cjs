const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const source = fs.readFileSync(path.join(__dirname, '..', 'Jellyfin.Plugin.Mediarow',
    'Web', 'mediarow-home.js'), 'utf8');
const context = vm.createContext({});
vm.runInContext(source.slice(source.indexOf('    function projectHomeSections('),
    source.indexOf('    function getApiClient(')), context);
const project = (sections, categories, view) =>
    JSON.parse(JSON.stringify(context.projectHomeSections(sections, categories, view)));
const genres = ['Action', 'Drama', 'Comedy', 'Horror', 'Romance', 'Thriller', 'Science Fiction', 'Mystery'];
const sections = [
    { Id: 'Mediarow_ContinueWatching', DisplayTitle: 'Continue Watching', Order: -1 },
    { Id: 'Mediarow_Popular', DisplayTitle: 'Popular', Order: 0 },
    ...genres.map((genre, index) => ({
        Id: 'Mediarow_' + genre.replaceAll(' ', ''), DisplayTitle: genre, AdditionalData: genre, Order: index + 1
    }))
];

test('hybrid Home splits Popular and interleaves six genres rather than grouping by type', () => {
    const rows = project(sections, ['Movies', 'Series'], 'Home');
    assert.equal(rows.length, 9);
    assert.deepEqual(rows.map(row => row.DisplayTitle), [
        'Continue Watching', 'Popular Movies', 'Popular Series',
        'Action Movies', 'Drama Series', 'Comedy Movies', 'Horror Series', 'Romance Movies', 'Thriller Series'
    ]);
    assert.deepEqual(rows.map(row => row.Order), rows.map((_, index) => index));
    assert.equal(rows[0].ContentCategory, null);
});

test('category views show the full configured genre lineup with only that category', () => {
    for (const category of ['Movies', 'Series']) {
        const rows = project(sections, ['Movies', 'Series'], category);
        assert.equal(rows.length, 10);
        assert.ok(rows.every(row => row.ContentCategory === category));
        assert.equal(rows.at(-1).DisplayTitle, 'Mystery ' + category);
    }
});

test('configured genre order is respected without mutating the source definitions', () => {
    const reordered = structuredClone(sections);
    reordered.find(row => row.DisplayTitle === 'Mystery').Order = .5;
    const before = JSON.stringify(reordered);
    const rows = project(reordered, ['Movies', 'Series'], 'Home');
    assert.equal(rows[3].DisplayTitle, 'Mystery Movies');
    assert.equal(rows[4].DisplayTitle, 'Action Series');
    assert.equal(JSON.stringify(reordered), before);
});

test('disabled categories stay absent and a single category is not duplicated', () => {
    const rows = project(sections, ['Series'], 'Home');
    assert.equal(rows.filter(row => row.Id === 'Mediarow_Popular').length, 1);
    assert.ok(rows.slice(1).every(row => row.ContentCategory === 'Series'));
    assert.deepEqual(project(sections, ['Movies'], 'Anime'), []);
    assert.deepEqual(project(sections, [], 'Home'), []);
});

test('Anime rows do not appear in ordinary movie/series filters', () => {
    const withAnime = sections.concat({
        Id: 'Mediarow_Anime', DisplayTitle: 'Anime', AdditionalData: 'Anime', Order: .5
    });
    assert.ok(!project(withAnime, ['Movies', 'Series'], 'Home').some(row => row.Id === 'Mediarow_Anime'));
    assert.ok(!project(withAnime, ['Movies', 'Series', 'Anime'], 'Movies').some(row => row.Id === 'Mediarow_Anime'));
    const animeRows = project(withAnime, ['Movies', 'Series', 'Anime'], 'Anime');
    assert.ok(animeRows.every(row => row.ContentCategory === 'Anime'));
    assert.ok(animeRows.some(row => row.DisplayTitle === 'Anime'));
    const homeRows = project(withAnime, ['Movies', 'Series', 'Anime'], 'Home');
    assert.ok(homeRows.some(row => row.DisplayTitle === 'Popular Anime'));
});

function createLoadHarness() {
    const pending = [];
    const warnings = [];
    class Element {
        constructor() {
            this.children = [];
            this.dataset = {};
            this.attributes = {};
            this.listeners = {};
            this.parent = null;
        }
        get isConnected() { return this === container || Boolean(this.parent?.isConnected); }
        appendChild(child) { child.parent = this; this.children.push(child); }
        remove() {
            this.parent.children = this.parent.children.filter(child => child !== this);
            this.parent = null;
        }
        setAttribute(name, value) { this.attributes[name] = value; }
        addEventListener(name, listener) { this.listeners[name] = listener; }
    }
    const container = new Element();
    const api = {
        getUrl: url => url,
        getJSON: url => {
            if (url.startsWith('Mediarow/Home/Sections?')) {
                return Promise.resolve({ Items: sections, ContentCategories: ['Movies', 'Series'] });
            }
            return new Promise(resolve => pending.push({ url, resolve }));
        }
    };
    const harnessContext = vm.createContext({
        window: { location: { hash: '#/home' } },
        document: {
            createElement: () => new Element(),
            querySelector: selector => selector === '[data-mediarow-home="true"]'
                ? container.children.find(child => child.dataset.mediarowHome === 'true') || null
                : selector === '.sections' ? container : null
        },
        console: { warn: (...args) => warnings.push(args) },
        setTimeout,
        getApiClient: () => api,
        getUserId: () => 'test-user',
        moveRecentlyAddedBelow: () => {},
        createRow: (_api, section, result) => result.Items.length
            ? Object.assign(new Element(), { section }) : null,
        insertRowInOrder: (parent, row) => {
            parent.appendChild(row);
            parent.children.sort((left, right) => left.section.Order - right.section.Order);
        }
    });
    vm.runInContext('var isLoading = false; var maxConcurrentSections = 4; var selectedCategory = "Home"; var loadVersion = 0;\n' +
        source.slice(source.indexOf('    function projectHomeSections('), source.indexOf('    function getApiClient(')) +
        source.slice(source.indexOf('    function loadRows('), source.indexOf('    var attempts =')), harnessContext);
    const root = () => container.children[0];
    return {
        pending, root, warnings,
        load: () => harnessContext.loadRows(),
        click: category => root().children[0].children.find(button => button.textContent === category).listeners.click(),
        rows: () => root().children[2].children,
        api
    };
}

const flush = () => new Promise(resolve => setImmediate(resolve));

test('Jellyfin Recently Added moves below Mediarow rows while other built-ins stay above', () => {
    const container = { children: [], appendChild(child) {
        this.children = this.children.filter(existing => existing !== child).concat(child);
    } };
    const element = className => ({
        className, parentElement: container,
        classList: { contains: name => className.split(' ').includes(name) },
        compareDocumentPosition(other) {
            return container.children.indexOf(other) < container.children.indexOf(this) ? 2 : 4;
        }
    });
    const myMedia = element('verticalSection section0');
    const recentlyAdded = element('section6');
    const root = element('mediarowHomeRows');
    const nextSection = element('verticalSection section7');
    container.children = [myMedia, recentlyAdded, root, nextSection];
    const moveContext = vm.createContext({ Node: { DOCUMENT_POSITION_PRECEDING: 2 } });
    vm.runInContext(source.slice(source.indexOf('    function moveRecentlyAddedBelow('),
        source.indexOf('    function loadRows(')), moveContext);
    moveContext.moveRecentlyAddedBelow(root);
    assert.deepEqual(container.children, [myMedia, root, nextSection, recentlyAdded]);
    moveContext.moveRecentlyAddedBelow(root);
    assert.deepEqual(container.children, [myMedia, root, nextSection, recentlyAdded]);
    moveContext.moveRecentlyAddedBelow(null);
});

test('rapid switching starts the newest view immediately and never inserts stale rows', async () => {
    const harness = createLoadHarness();
    const homeLoad = harness.load();
    await flush();
    assert.equal(harness.pending.length, 4);
    harness.click('Movies');
    await flush();
    assert.equal(harness.pending.length, 8);
    harness.click('Series');
    await flush();
    assert.equal(harness.pending.length, 12);
    const staleRequests = harness.pending.splice(0, 8);
    staleRequests.forEach(request => request.resolve({ Items: [{ Id: 'stale' }] }));
    await flush();
    assert.equal(harness.rows().length, 0);
    assert.equal(harness.pending.length, 4);
    while (harness.pending.length) {
        const requests = harness.pending.splice(0);
        assert.ok(requests.every(request => request.url.includes('contentCategory=Series')));
        requests.forEach(request => request.resolve({ Items: [{ Id: 'series' }] }));
        await flush();
    }
    await homeLoad;
    assert.equal(harness.rows().length, 10);
    assert.ok(harness.rows().every(row => row.section.ContentCategory === 'Series'));
    assert.equal(harness.root().children[1].hidden, true);
});

test('discovery failures display an alert instead of endlessly retrying', async () => {
    const harness = createLoadHarness();
    harness.api.getJSON = () => Promise.reject(new Error('catalog unavailable'));
    await harness.load();
    assert.equal(harness.root().children[0].attributes.role, 'alert');
    assert.match(harness.root().children[0].textContent, /could not load/);
    assert.equal(harness.warnings.length, 1);
    await harness.load();
    assert.equal(harness.root().children.length, 1);
});

test('empty and failed rows keep category controls and report the outcome', async () => {
    for (const fails of [false, true]) {
        const harness = createLoadHarness();
        const discovery = harness.api.getJSON;
        harness.api.getJSON = url => url.startsWith('Mediarow/Home/Sections?')
            ? discovery(url)
            : fails ? Promise.reject(new Error('row unavailable')) : Promise.resolve({ Items: [] });
        const pendingLoad = harness.load();
        await pendingLoad;
        assert.equal(harness.root().children[0].children.length, 3);
        assert.equal(harness.rows().length, 0);
        assert.equal(harness.root().children[1].hidden, false);
        if (fails) {
            assert.match(harness.root().children[1].textContent, /could not load/);
            assert.equal(harness.warnings.length, 9);
        } else {
            assert.match(harness.root().children[1].textContent, /No results/);
        }
    }
});

test('poster clicks navigate only to resolved IDs and surface pending or failed playback', async () => {
    const notices = [];
    const navigations = [];
    let outcome = { ItemId: 'native-movie' };
    const clickContext = vm.createContext({
        resolvePlaybackItem: async () => {
            if (outcome instanceof Error) throw outcome;
            return outcome;
        },
        navigateToDetails: (_api, id) => navigations.push(id),
        showPlaybackError: (_card, message) => notices.push(message),
        findAncestor: () => null,
        console: { warn() {} }
    });
    vm.runInContext(source.slice(source.indexOf('    function openResolvedDetails('),
        source.indexOf('    function showPlaybackError(')), clickContext);
    const card = { dataset: {}, setAttribute() {}, removeAttribute() {}, closest: () => null };
    const click = () => clickContext.openResolvedDetails({ preventDefault() {}, currentTarget: card }, {}, { Id: 'virtual-movie' });
    await click();
    assert.deepEqual(navigations, ['native-movie']);
    outcome = { Resolved: false, Message: 'Scan pending' };
    await click();
    assert.equal(notices.at(-1), 'Scan pending');
    outcome = new Error('not configured');
    await click();
    assert.match(notices.at(-1), /could not be opened/);
    assert.deepEqual(navigations, ['native-movie']);
    assert.equal(card.dataset.mediarowResolving, undefined);
    outcome = { ItemId: 'virtual-movie', BrowseOnly: true };
    await click();
    assert.deepEqual(navigations, ['native-movie', 'virtual-movie']);
});
