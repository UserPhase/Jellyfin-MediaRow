const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const source = fs.readFileSync(path.join(__dirname, '..', 'Jellyfin.Plugin.Mediarow',
    'Web', 'mediarow-home.js'), 'utf8');
const context = vm.createContext({});
vm.runInContext(source.slice(source.indexOf('    var NEXT_EPISODE_COUNTDOWN_SECONDS'),
    source.indexOf('    function watchNextEpisodeCountdown(')), context);
const tick = (state, native, now, paused = false) =>
    JSON.parse(JSON.stringify(context.nextEpisodeCountdown(state, native, now, paused)));

test('prompt opened at the credits counts down 10 seconds and then starts the next episode once', () => {
    const state = {};
    assert.deepEqual(tick(state, 640, 0), { seconds: 10, progress: 0, start: false });
    assert.deepEqual(tick(state, Infinity, 4000), { seconds: 6, progress: 40, start: false });
    assert.deepEqual(tick(state, Infinity, 10000), { seconds: 0, progress: 100, start: true });
    assert.equal(tick(state, Infinity, 10250).start, false);
});

test('pausing playback pauses the credits countdown', () => {
    const state = {};
    tick(state, 300, 0);
    tick(state, Infinity, 3000);
    tick(state, Infinity, 20000, true);
    assert.equal(tick(state, Infinity, 20000).seconds, 7);
});

test('Jellyfin\'s own end-of-episode prompt keeps its countdown and never starts early', () => {
    const state = {};
    assert.deepEqual(tick(state, 40, 0), { seconds: 40, progress: 0, start: false });
    assert.deepEqual(tick(state, 10, 30000), { seconds: 10, progress: 75, start: false });
    assert.equal(tick(state, 0, 40000).start, false);
});

test('a missing countdown (autoplay turned off) does nothing', () => {
    assert.equal(context.nextEpisodeCountdown({}, NaN, 0, false), null);
});
