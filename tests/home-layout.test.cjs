const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const html = fs.readFileSync(path.join(__dirname, '..', 'Jellyfin.Plugin.Mediarow',
    'Configuration', 'configPage.html'), 'utf8');
const script = html.match(/<script type="text\/javascript">([\s\S]*?)<\/script>/)[1];

class Element {
    constructor(tag = 'div') {
        this.tag = tag;
        this.children = [];
        this.events = {};
        this.style = {};
        this.dataset = {};
        this.attributes = {};
        this.value = '';
        this.checked = false;
        this.classList = { add() {}, remove() {} };
    }
    appendChild(child) { this.children.push(child); }
    get firstChild() { return this.children[0] || null; }
    removeChild(child) { this.children.splice(this.children.indexOf(child), 1); }
    setAttribute(key, value) { this.attributes[key] = value; }
    getAttribute(key) { return this.attributes[key]; }
    addEventListener(name, handler) { this.events[name] = handler; }
    querySelectorAll(tag) {
        return this.children.flatMap(child =>
            (child.tag === tag ? [child] : []).concat(child.querySelectorAll(tag)));
    }
    querySelector(tag) { return this.querySelectorAll(tag)[0]; }
    focus() {}
    contains(element) { return this.children.includes(element); }
    async emit(name, event = {}) {
        await this.events[name]?.({ preventDefault() {}, ...event });
    }
}

async function editor(config) {
    const elements = new Map([...html.matchAll(/id="([^"]+)"/g)]
        .map(match => [match[1], new Element()]));
    let saved;
    const alerts = [];
    vm.runInNewContext(script, {
        document: {
            querySelector: selector => elements.get(selector.slice(1)),
            createElement: (tag, extension) => {
                if (extension && typeof extension !== 'string') {
                    throw new TypeError('Jellyfin custom-element wrapper requires a string extension');
                }
                return new Element(tag);
            }
        },
        window: { ApiClient: {
            getPluginConfiguration: async () => structuredClone(config),
            updatePluginConfiguration: async (_, value) => { saved = value; }
        } },
        Dashboard: {
            showLoadingMsg() {}, hideLoadingMsg() {}, processPluginConfigurationUpdateResult() {},
            alert: message => alerts.push(message)
        },
        console: { log() {}, error: console.error }, setTimeout: handler => handler()
    });
    const get = id => elements.get(id);
    await get('mediarowConfigPage').emit('viewshow');
    const row = id => get('homeSectionChoices').children.find(child => child.dataset.sectionId === id);
    return {
        get, row, alerts,
        order: () => get('homeSectionChoices').children
            .filter(child => child.querySelector('input').checked).map(child => child.dataset.sectionId),
        save: async () => {
            await get('mediarowForm').emit('submit');
            assert.deepEqual(alerts, []);
            assert.ok(saved, 'Configuration should be saved');
            return JSON.parse(JSON.stringify(saved));
        }
    };
}

test('defaults select movies and series, with anime opt-in', async () => {
    const ui = await editor({ HomeRowGenres: ['Action', 'Drama'] });
    assert.equal(ui.get('chkHomeMovies').checked, true);
    assert.equal(ui.get('chkHomeSeries').checked, true);
    assert.equal(ui.get('chkHomeAnime').checked, false);
    const saved = await ui.save();
    assert.deepEqual(saved.HomeRowContentCategories, ['Movies', 'Series']);
});

test('watched cleanup defaults off and movie/series switches save independently', async () => {
    const ui = await editor({});
    assert.equal(ui.get('chkCleanupWatchedMovies').checked, false);
    assert.equal(ui.get('chkCleanupCompletedSeries').checked, false);
    ui.get('chkCleanupWatchedMovies').checked = true;
    const saved = await ui.save();
    assert.equal(saved.CleanupWatchedMovies, true);
    assert.equal(saved.CleanupCompletedSeries, false);
    const reloaded = await editor({ CleanupWatchedMovies: false, CleanupCompletedSeries: true });
    assert.equal(reloaded.get('chkCleanupWatchedMovies').checked, false);
    assert.equal(reloaded.get('chkCleanupCompletedSeries').checked, true);
});

test('explicit library paths migrate legacy roots and save exact destinations', async () => {
    const ui = await editor({ PersistentLibraryPath: 'D:\\Media\\Mediarow' });
    assert.equal(ui.get('txtMoviesLibraryPath').value, 'D:\\Media\\Mediarow\\movies');
    assert.equal(ui.get('txtSeriesLibraryPath').value, 'D:\\Media\\Mediarow\\tv');
    assert.equal(ui.get('txtAnimeLibraryPath').value, 'D:\\Media\\Mediarow\\anime');
    ui.get('txtMoviesLibraryPath').value = '/data/film';
    ui.get('txtSeriesLibraryPath').value = '/data/shows';
    ui.get('txtAnimeLibraryPath').value = '/data/anime';
    await ui.get('txtMoviesLibraryPath').emit('input');
    assert.equal(ui.get('moviesPath').textContent, '/data/film');
    assert.equal(ui.get('showsPath').textContent, '/data/shows');
    const saved = await ui.save();
    assert.equal(saved.MoviesLibraryPath, '/data/film');
    assert.equal(saved.SeriesLibraryPath, '/data/shows');
    assert.equal(saved.AnimeLibraryPath, '/data/anime');
    assert.equal(saved.PersistentLibraryPath, 'D:\\Media\\Mediarow');
    const reloaded = await editor(saved);
    assert.equal(reloaded.get('txtMoviesLibraryPath').value, '/data/film');
});

test('enabled persistence refuses relative paths with an actionable error', async () => {
    const ui = await editor({ EnablePersistence: true });
    ui.get('txtMoviesLibraryPath').value = 'relative-movies';
    await ui.get('mediarowForm').emit('submit');
    assert.match(ui.alerts.at(-1), /Movies path must be an absolute/);
});
test('embedded clients without replaceChildren load genres and Stremio settings', async () => {
    assert.equal(typeof Element.prototype.replaceChildren, 'undefined');
    const ui = await editor({
        CatalogProvider: 'StremioAddon',
        StremioCatalogUrl: 'https://v3-cinemeta.strem.io',
        HomeRowGenres: ['Action', 'Drama']
    });
    assert.deepEqual(ui.alerts, []);
    assert.ok(ui.row('Mediarow_Action'));
    assert.ok(ui.row('Mediarow_Drama'));
    assert.equal(ui.get('txtStremioCatalogUrl').value, 'https://v3-cinemeta.strem.io');
    assert.equal(ui.get('stremioCatalogSettings').style.display, 'block');
    await ui.get('mediarowConfigPage').emit('viewshow');
    assert.equal(ui.get('homeSectionChoices').children.filter(row =>
        row.dataset.sectionId === 'Mediarow_Action').length, 1);
});

test('dragging changes visible order and persisted order, and reload preserves it', async () => {
    const ui = await editor({ HomeRowGenres: ['Action', 'Drama'] });
    const dataTransfer = { setData() {} };
    await ui.row('Mediarow_Drama').children[0].emit('dragstart', { dataTransfer });
    await ui.row('Mediarow_Popular').emit('drop');
    assert.deepEqual(ui.order(), [
        'Mediarow_ContinueWatching', 'Mediarow_Drama',
        'Mediarow_Popular', 'Mediarow_Action'
    ]);
    const saved = await ui.save();
    assert.deepEqual(saved.HomeSections.filter(section => section.Enabled).map(section => section.Id), ui.order());
    const reloaded = await editor(saved);
    assert.deepEqual(reloaded.order(), ui.order());
});

test('checkboxes and keyboard buttons update selection and order', async () => {
    const ui = await editor({ HomeRowGenres: ['Action', 'Drama'] });
    const action = ui.row('Mediarow_Action').querySelector('input');
    action.checked = false;
    await action.emit('change');
    assert.equal(ui.order().includes('Mediarow_Action'), false);
    const up = ui.row('Mediarow_Drama').querySelectorAll('button')[0];
    await up.emit('click');
    assert.equal(ui.order()[1], 'Mediarow_Drama');
    ui.get('chkHomeMovies').checked = false;
    ui.get('chkHomeSeries').checked = false;
    ui.get('chkHomeAnime').checked = true;
    const saved = await ui.save();
    assert.deepEqual(saved.HomeRowContentCategories, ['Anime']);
    assert.equal(saved.HomeSections.find(section => section.Id === 'Mediarow_Action').Enabled, false);
});

test('custom genre IDs and advanced provider fields survive saving', async () => {
    const ui = await editor({
        HomeRowGenres: ['Korean Drama'],
        HomeSections: [
            { Id: 'Mediarow_KoreanDrama', DisplayTitle: 'Korean Drama', AdditionalData: 'Korean Drama', Enabled: true, Order: 0 },
            { Id: 'custom-provider', DisplayTitle: 'Custom', Route: '/custom', Parameters: [{ Key: 'key', Value: 'value' }], Enabled: true, Order: 5 }
        ]
    });
    assert.ok(ui.row('Mediarow_KoreanDrama'));
    ui.get('txtNewHomeGenre').value = 'Mystery';
    await ui.get('btnAddHomeGenre').emit('click');
    const saved = await ui.save();
    assert.ok(saved.HomeSections.find(section => section.Id === 'Mediarow_Mystery').Enabled);
    const advanced = saved.HomeSections.find(section => section.Id === 'custom-provider');
    assert.equal(advanced.Route, '/custom');
    assert.deepEqual(advanced.Parameters, [{ Key: 'key', Value: 'value' }]);
});
