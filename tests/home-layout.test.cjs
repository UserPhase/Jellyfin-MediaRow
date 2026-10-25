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
    scrollIntoView() { this.scrolledIntoView = true; }
    contains(element) { return this.children.includes(element); }
    async emit(name, event = {}) {
        await this.events[name]?.({ preventDefault() {}, ...event });
    }
}

async function editor(config, api = {}) {
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
            updatePluginConfiguration: async (_, value) => { saved = value; },
            ...api
        } },
        Dashboard: {
            showLoadingMsg() {}, hideLoadingMsg() {}, processPluginConfigurationUpdateResult() {},
            alert: message => alerts.push(message)
        },
        console: { log() {}, error: console.error }, setTimeout: handler => handler(), clearTimeout() {}
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

test('desktop settings navigation and provider setup stay inside the plugin page', async () => {
    const ui = await editor({ CatalogProvider: 'Direct', StreamProvider: 'AIOStreams' });
    assert.equal(ui.get('metadataApiSettings').open, true);
    assert.equal(ui.get('playbackSetupNote').hidden, false);
    await ui.get('navLanguage').emit('click');
    assert.equal(ui.get('mrLanguage').scrolledIntoView, true);
    ui.get('chkEnablePersistence').checked = true;
    await ui.get('chkEnablePersistence').emit('change');
    assert.equal(ui.get('playbackSetupNote').hidden, true);
    ui.get('chkTriggerLibraryScan').checked = false;
    await ui.get('chkTriggerLibraryScan').emit('change');
    assert.equal(ui.get('playbackSetupNote').hidden, false);
});

test('all scalar settings round-trip through the desktop form, including disabled switches', async () => {
    const settings = {
        Enabled: ['chkEnabled', false], CreateMediaOnView: ['chkCreateMediaOnView', true],
        TvdbApiKey: ['txtTvdbApiKey', 'fixture-tvdb-key'], TmdbApiKey: ['txtTmdbApiKey', 'fixture-tmdb-key'],
        EmbedarrUrl: ['txtEmbedarrUrl', 'https://embedarr.test'], EmbedarrApiKey: ['txtEmbedarrApiKey', 'fixture-embedarr-key'],
        CatalogProvider: ['selCatalogProvider', 'Direct'], StreamProvider: ['selStreamProvider', 'AIOStreams'],
        StremioCatalogUrl: ['txtStremioCatalogUrl', 'https://catalog.test'], AIOStreamsUrl: ['txtAIOStreamsUrl', 'https://streams.test/addon'],
        MovieApiSource: ['selMovieApiSource', 'Tvdb'], TvShowApiSource: ['selTvShowApiSource', 'Tmdb'],
        MaxMovieResults: ['txtMaxMovieResults', 37], MaxTvShowResults: ['txtMaxTvShowResults', 41], CacheTtlMinutes: ['txtCacheTtlMinutes', 95],
        DirectMovieUrlTemplate: ['txtDirectMovieUrl', 'https://streams.test/movie/{imdb}'],
        DirectTvUrlTemplate: ['txtDirectTvUrl', 'https://streams.test/tv/{imdb}/{season}/{episode}'],
        DirectAnimeUrlTemplate: ['txtDirectAnimeUrl', 'https://streams.test/anime/{anilist}/{audio}'],
        ShowUnreleasedStreams: ['chkShowUnreleasedStreams', true], EnableAnimeAudioVersions: ['chkEnableAnimeAudioVersions', true],
        AnimeAudioTracks: ['txtAnimeAudioTracks', 'sub,dub,original'], PreferOriginalLanguageStreams: ['chkPreferOriginalLanguageStreams', false],
        EnableStreamRecovery: ['chkEnableStreamRecovery', false], EnableOnlineSkipSegments: ['chkEnableOnlineSkipSegments', false],
        NetflixStylePlayerButtons: ['chkNetflixStylePlayerButtons', false],
        LanguageMode: ['selLanguageMode', 'Override'], PreferredLanguage: ['selPreferredLanguage', 'nld'],
        MoviePreferredId: ['selMoviePreferredId', 'Tmdb'], TvShowPreferredId: ['selTvShowPreferredId', 'Tvdb'], AnimePreferredId: ['selAnimePreferredId', 'AniList'],
        EnableSubtitles: ['chkEnableSubtitles', true], OpenSubtitlesApiKey: ['txtOpenSubtitlesApiKey', 'fixture-subtitle-key'],
        SubtitleLanguages: ['txtSubtitleLanguages', 'en,nl'], MaxSubtitlesPerLanguage: ['txtMaxSubtitlesPerLanguage', 2],
        SubtitleCachePath: ['txtSubtitleCachePath', '/test/subtitles'], UseJellyfinOpenSubtitlesCredentials: ['chkUseJellyfinOSCredentials', false],
        EnablePersistence: ['chkEnablePersistence', true], MoviesLibraryPath: ['txtMoviesLibraryPath', '/test/movies'],
        SeriesLibraryPath: ['txtSeriesLibraryPath', '/test/series'], AnimeLibraryPath: ['txtAnimeLibraryPath', '/test/anime'],
        TriggerLibraryScan: ['chkTriggerLibraryScan', false], CreateUnreleasedMedia: ['chkCreateUnreleasedMedia', true],
        CheckForUpdatesOnView: ['chkCheckForUpdatesOnView', false], CleanupWatchedMovies: ['chkCleanupWatchedMovies', true],
        CleanupUnsavedItems: ['chkCleanupUnsavedItems', true], UnsavedItemGraceMinutes: ['txtUnsavedItemGraceMinutes', 17],
        CleanupCompletedSeries: ['chkCleanupCompletedSeries', true], EnableEpisodeSync: ['chkEnableEpisodeSync', false],
        EpisodeSyncIntervalHours: ['txtEpisodeSyncIntervalHours', 17], EpisodeSyncConcurrency: ['txtEpisodeSyncConcurrency', 5],
        EnableHomeRows: ['chkEnableHomeRows', false], EnableAppLibraries: ['chkEnableAppLibraries', false], ShowAppLibrariesInWeb: ['chkShowAppLibrariesInWeb', true],
        HomeRowItemCount: ['txtHomeRowItemCount', 39], HomeRowShuffle: ['chkHomeRowShuffle', false],
        HomeRowShuffleRefreshMinutes: ['txtHomeRowShuffleRefreshMinutes', 87], HomeRowMinimumRating: ['txtHomeRowMinimumRating', 7.4],
        CorrectTracksAfterPlaybackStart: ['chkCorrectTracksAfterPlaybackStart', false]
    };
    const ui = await editor({ EnableHlsProbing: true, FutureSetting: { retained: true } });
    for (const [id, value] of Object.values(settings)) {
        ui.get(id)[typeof value === 'boolean' ? 'checked' : 'value'] = value;
    }
    await ui.get('mediarowForm').emit('change');
    assert.equal(ui.get('configSaveStatus').textContent, 'You have unsaved changes.');
    const saved = await ui.save();
    for (const [key, [, expected]] of Object.entries(settings)) assert.equal(saved[key], expected, key);
    assert.equal(saved.EnableHlsProbing, true, 'Legacy preferences must survive editing active settings');
    assert.deepEqual(saved.FutureSetting, { retained: true });
    const reopened = await editor(saved);
    for (const [id, value] of Object.values(settings)) {
        assert.equal(reopened.get(id)[typeof value === 'boolean' ? 'checked' : 'value'], value, id);
    }
    assert.equal(ui.get('btnSave').disabled, false);
    assert.match(ui.get('configSaveStatus').textContent, /Settings saved/);
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

test('per-user language preferences round-trip and keep other users', async () => {
    const alice = '11111111-2222-3333-4444-555555555555';
    const bob = 'aaaaaaaabbbbccccddddeeeeeeeeeeee';
    const ui = await editor({
        UserLanguagePreferences: [{ UserId: alice.replace(/-/g, ''), Enabled: true, PreferOriginalAudio: false,
            AudioLanguages: ['ger'], SubtitleLanguages: ['ger'], SubtitleMode: 'Off',
            SeriesOverrides: [{ SeriesId: 'abc', SeriesName: 'Lost', AudioLanguages: ['eng'], SubtitleLanguages: [],
                SubtitleMode: 'Off', PreferOriginalAudio: false, Learned: true }] }]
    }, {
        getUsers: async () => [{ Id: alice, Name: 'Alice' }, { Id: bob, Name: 'Bob' }],
        getCultures: async () => [{ DisplayName: 'Japanese', ThreeLetterISOLanguageName: 'jpn' }],
        getCurrentUserId: () => alice,
        getItems: async (_, query) => ({ Items: query.SearchTerm === 'small'
            ? [{ Id: '99999999-0000-0000-0000-000000000001', Name: 'Smallville', ProductionYear: 2001 }] : [] })
    });
    assert.equal(ui.get('chkCorrectTracksAfterPlaybackStart').checked, true);
    assert.equal(ui.get('selLanguageUser').value, alice.replace(/-/g, ''));
    assert.equal(ui.get('chkLanguageUserEnabled').checked, true);
    assert.equal(ui.get('selAudioMode').value, 'specific');
    assert.equal(ui.get('selSubtitleMode').value, 'Off');
    assert.equal(ui.get('subtitleLanguageBlock').style.display, 'none');
    assert.equal(ui.get('audioLanguageList').children.length, 1);
    const summary = ui.get('languageUserSummary').children;
    assert.equal(summary.length, 2);
    assert.equal(summary[0].children[1].textContent, 'German audio · No subtitles · 1 series');
    assert.equal(summary[1].children[1].textContent, 'Jellyfin defaults');
    assert.equal(ui.get('seriesOverrideList').children[0].children[0].children[0].textContent, 'Remembered');

    // Bob: original language with English subtitles always; choosing him from the overview and editing enables him.
    await summary[1].emit('click');
    assert.equal(ui.get('selLanguageUser').value, bob);
    assert.equal(ui.get('chkLanguageUserEnabled').checked, false);
    ui.get('selSubtitleMode').value = 'Always';
    await ui.get('selSubtitleMode').emit('change');
    assert.equal(ui.get('chkLanguageUserEnabled').checked, true);
    assert.equal(ui.get('forcedWhenAudioMatchesContainer').style.display, 'none');
    for (const code of ['eng', 'jpn', 'eng']) {
        ui.get('selAudioLanguageAdd').value = code;
        await ui.get('btnAddAudioLanguage').emit('click');
    }
    ui.get('selSubtitleLanguageAdd').value = 'eng';
    await ui.get('btnAddSubtitleLanguage').emit('click');
    const jpnRow = ui.get('audioLanguageList').children[1];
    await jpnRow.children.find(child => child.title === 'Move jpn up').emit('click');
    ui.get('chkLanguagePreferNonForced').checked = false;
    await ui.get('chkLanguagePreferNonForced').emit('change');
    ui.get('chkRememberSeriesChoices').checked = false;
    await ui.get('chkRememberSeriesChoices').emit('change');
    assert.equal(ui.get('languageUserSummary').children[1].children[1].textContent,
        'Original audio, else Japanese, English · Always English subtitles');

    ui.get('txtLanguageSeriesSearch').value = 'small';
    await ui.get('txtLanguageSeriesSearch').emit('keydown', { key: 'Enter' });
    await ui.get('languageSeriesResults').children[0].emit('click');
    assert.equal(ui.get('languageSeriesResults').children.length, 0);
    const override = ui.get('seriesOverrideList').children[0];
    const [audioInput] = override.children.filter(child => child.tag === 'input');
    audioInput.value = 'ENG, eng ,fre';
    await audioInput.emit('change');
    const modeSelect = override.children.find(child => child.tag === 'select');
    modeSelect.value = 'ForcedOnly';
    await modeSelect.emit('change');
    ui.get('chkCorrectTracksAfterPlaybackStart').checked = false;

    const saved = await ui.save();
    assert.equal(saved.CorrectTracksAfterPlaybackStart, false);
    assert.equal(saved.UserLanguagePreferences.length, 2);
    const alicePrefs = saved.UserLanguagePreferences[0];
    assert.deepEqual(alicePrefs.AudioLanguages, ['ger']);
    assert.equal(alicePrefs.SubtitleMode, 'Off');
    assert.equal(alicePrefs.AudioMode, undefined);
    assert.deepEqual(alicePrefs.SeriesOverrides[0], { SeriesId: 'abc', SeriesName: 'Lost', AudioLanguages: ['eng'],
        SubtitleLanguages: [], SubtitleMode: 'Off', PreferOriginalAudio: false, Learned: true });
    const bobPrefs = saved.UserLanguagePreferences[1];
    assert.equal(bobPrefs.UserId, bob);
    assert.equal(bobPrefs.Enabled, true);
    assert.equal(bobPrefs.PreferOriginalAudio, true);
    assert.equal(bobPrefs.SubtitleMode, 'Always');
    assert.equal(bobPrefs.PreferNonForcedSubtitles, false);
    assert.equal(bobPrefs.RememberSeriesChoices, false);
    assert.deepEqual(bobPrefs.AudioLanguages, ['jpn', 'eng']);
    assert.deepEqual(bobPrefs.SubtitleLanguages, ['eng']);
    assert.deepEqual(bobPrefs.SeriesOverrides, [{ SeriesId: '99999999000000000000000000000001', SeriesName: 'Smallville',
        AudioLanguages: ['eng', 'fre'], SubtitleLanguages: [], SubtitleMode: 'ForcedOnly', Learned: false, PreferOriginalAudio: false }]);
});

test('audio mode "Jellyfin default" clears the audio list and disabling keeps settings', async () => {
    const id = 'aaaaaaaabbbbccccddddeeeeeeeeeeee';
    const ui = await editor({
        UserLanguagePreferences: [{ UserId: id, Enabled: true, PreferOriginalAudio: true, AudioLanguages: ['eng'], SubtitleMode: 2 }]
    }, { getUsers: async () => [{ Id: id, Name: 'Sam' }] });
    assert.equal(ui.get('selAudioMode').value, 'original');
    assert.equal(ui.get('selSubtitleMode').value, 'Always');
    ui.get('selAudioMode').value = 'default';
    await ui.get('selAudioMode').emit('change');
    assert.equal(ui.get('audioLanguageBlock').style.display, 'none');
    ui.get('chkLanguageUserEnabled').checked = false;
    await ui.get('chkLanguageUserEnabled').emit('change');
    const [saved] = (await ui.save()).UserLanguagePreferences;
    assert.equal(saved.Enabled, false);
    assert.equal(saved.PreferOriginalAudio, false);
    assert.deepEqual(saved.AudioLanguages, []);
    assert.equal(saved.SubtitleMode, 'Always');
});

test('language section tolerates servers without user or culture APIs', async () => {
    const ui = await editor({});
    assert.equal(ui.get('languageUserSettings').style.display, 'none');
    const saved = await ui.save();
    assert.deepEqual(saved.UserLanguagePreferences, []);
    assert.equal(saved.CorrectTracksAfterPlaybackStart, true);
});

test('catalog rows load from HomeCatalogs, keep their place, and save with the row order', async () => {
    const ui = await editor({
        HomeSections: [
            { Id: 'Mediarow_ContinueWatching', DisplayTitle: 'Continue Watching', Order: 0, Enabled: true },
            { Id: 'MediarowCatalog_c1', DisplayTitle: 'Top Horror', Order: 1, Enabled: true },
            { Id: 'Mediarow_Popular', DisplayTitle: 'Popular', Order: 2, Enabled: true }
        ],
        HomeCatalogs: [
            { Id: 'c1', Title: 'Top Horror', Source: 'Tmdb', MediaType: 'Movie', Genre: 'Horror', Sort: 'TopRated', Enabled: true },
            { Id: 'c2', Title: 'Unlisted', Source: 'Auto', MediaType: 'All', Sort: 'Popular', Enabled: true }
        ]
    });
    assert.deepEqual(ui.order(), ['Mediarow_ContinueWatching', 'MediarowCatalog_c1', 'Mediarow_Popular', 'MediarowCatalog_c2']);
    const saved = await ui.save();
    assert.deepEqual(saved.HomeCatalogs.map(row => [row.Id, row.Genre, row.Sort, row.Order]),
        [['c1', 'Horror', 'TopRated', 0], ['c2', undefined, 'Popular', 1]]);
    const catalogSection = saved.HomeSections.find(section => section.Id === 'MediarowCatalog_c1');
    assert.equal(catalogSection.Order, 1);
    assert.equal(catalogSection.Enabled, true);
    assert.equal(catalogSection.AdditionalData, null);
    assert.ok(!saved.HomeRowGenres.includes('Top Horror'));
});

test('adding and removing a catalog row updates the list and the saved catalogs', async () => {
    const ui = await editor({ HomeSections: [{ Id: 'Mediarow_Popular', DisplayTitle: 'Popular', Order: 0, Enabled: true }] });
    ui.get('selCatalogRowType').value = 'Series';
    ui.get('selCatalogRowGenre').value = 'Comedy';
    ui.get('selCatalogRowSort').value = 'Newest';
    ui.get('txtCatalogRowYear').value = '2024';
    ui.get('txtCatalogRowRating').value = '7';
    await ui.get('btnAddCatalogRow').emit('click');
    const added = ui.order().at(-1);
    assert.match(added, /^MediarowCatalog_c[0-9a-z]+$/);
    let saved = await ui.save();
    assert.equal(saved.HomeCatalogs.length, 1);
    assert.deepEqual(
        (({ Source, MediaType, Genre, Sort, Year, MinimumRating, Title }) => ({ Source, MediaType, Genre, Sort, Year, MinimumRating, Title }))(saved.HomeCatalogs[0]),
        { Source: 'Auto', MediaType: 'Series', Genre: 'Comedy', Sort: 'Newest', Year: 2024, MinimumRating: 7, Title: 'New Comedy Series (2024)' });
    const remove = ui.row(added).querySelectorAll('button').find(button => button.textContent === 'Remove');
    await remove.emit('click');
    assert.ok(!ui.order().includes(added));
    saved = await ui.save();
    assert.deepEqual(saved.HomeCatalogs, []);
    assert.ok(!saved.HomeSections.some(section => section.Id === added));
});

test('episode sync settings default on and are clamped when saved', async () => {
    const ui = await editor({});
    assert.equal(ui.get('chkEnableEpisodeSync').checked, true);
    assert.equal(ui.get('txtEpisodeSyncIntervalHours').value, 6);
    ui.get('txtEpisodeSyncIntervalHours').value = '500';
    ui.get('txtEpisodeSyncConcurrency').value = '0';
    ui.get('chkEnableEpisodeSync').checked = false;
    const saved = await ui.save();
    assert.equal(saved.EnableEpisodeSync, false);
    assert.equal(saved.EpisodeSyncIntervalHours, 168);
    assert.equal(saved.EpisodeSyncConcurrency, 3);
});

test('unsaved cleanup is optional and its custom grace period survives saving and reload', async () => {
    const ui = await editor({});
    assert.equal(ui.get('chkCleanupUnsavedItems').checked, false);
    assert.equal(ui.get('txtUnsavedItemGraceMinutes').value, 5);
    assert.equal(ui.get('txtUnsavedItemGraceMinutes').disabled, true);
    ui.get('chkCleanupUnsavedItems').checked = true;
    await ui.get('chkCleanupUnsavedItems').emit('change');
    assert.equal(ui.get('txtUnsavedItemGraceMinutes').disabled, false);
    ui.get('txtUnsavedItemGraceMinutes').value = '17';
    const saved = await ui.save();
    assert.equal(saved.CleanupUnsavedItems, true);
    assert.equal(saved.UnsavedItemGraceMinutes, 17);
    assert.equal(saved.CleanupWatchedMovies, false);
    assert.equal(saved.CleanupCompletedSeries, false);
    const reloaded = await editor(saved);
    assert.equal(reloaded.get('txtUnsavedItemGraceMinutes').value, 17);
    reloaded.get('chkCleanupUnsavedItems').checked = false;
    await reloaded.get('chkCleanupUnsavedItems').emit('change');
    const disabled = await reloaded.save();
    assert.equal(disabled.CleanupUnsavedItems, false);
    assert.equal(disabled.UnsavedItemGraceMinutes, 17);
});
