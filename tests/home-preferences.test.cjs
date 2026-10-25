const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const source = fs.readFileSync(path.join(__dirname, '..', 'Jellyfin.Plugin.Mediarow',
    'Web', 'mediarow-home.js'), 'utf8');
const slice = (start, end) => source.slice(source.indexOf('    function ' + start + '('),
    source.indexOf(end));

test('moveInList moves one row and never mutates or overflows', () => {
    const context = vm.createContext({});
    vm.runInContext(slice('moveInList', '    var sectionKindLabels'), context);
    const rows = ['Popular', 'Action', 'Drama'];
    assert.deepEqual([...context.moveInList(rows, 2, -1)], ['Popular', 'Drama', 'Action']);
    assert.deepEqual([...context.moveInList(rows, 0, 1)], ['Action', 'Popular', 'Drama']);
    assert.deepEqual([...context.moveInList(rows, 0, -1)], rows);
    assert.deepEqual([...context.moveInList(rows, 2, 1)], rows);
    assert.deepEqual(rows, ['Popular', 'Action', 'Drama']);
});

function createDismissHarness(postOutcome) {
    const calls = [];
    const toasts = [];
    const removed = [];
    let refreshes = 0;
    let releasePost;
    const context = vm.createContext({
        Promise,
        console: { warn() {} },
        getUserId: () => 'user-a',
        removeTitleEverywhere: (_api, id) => removed.push(id),
        showToast: (message, label, action) => toasts.push({ message, label, action }),
        refreshHome: () => { refreshes++; },
        sendJson: (_api, method, url) => {
            calls.push(method + ' ' + url);
            if (method === 'POST') {
                return new Promise((resolve, reject) => {
                    releasePost = () => (postOutcome === 'fail' ? reject(new Error('offline')) : resolve({}));
                });
            }
            return Promise.resolve(null);
        }
    });
    vm.runInContext(slice('preferencesUrl', '    // Removes a title from every rendered row') +
        slice('dismissTitle', '    var toastTimer'), context);
    const api = { getUrl: url => '/' + url };
    return {
        calls, toasts, removed,
        refreshes: () => refreshes,
        releasePost: () => releasePost(),
        dismiss: () => context.dismissTitle(api, { Id: 'title-1', Name: 'Heat' })
    };
}

const flush = () => new Promise(resolve => setImmediate(resolve));

test('Not interested removes the title at once and saves it for the current user', async () => {
    const harness = createDismissHarness('ok');
    harness.dismiss();
    assert.deepEqual(harness.removed, ['title-1']);
    assert.deepEqual(harness.calls, ['POST /Plugins/AIOStreams/NotInterested/title-1?userId=user-a']);
    assert.match(harness.toasts[0].message, /Heat won.t be suggested again/);
    assert.equal(harness.toasts[0].label, 'Undo');
});

test('Undo waits for the save so the DELETE can never overtake the POST', async () => {
    const harness = createDismissHarness('ok');
    harness.dismiss();
    harness.toasts[0].action();
    await flush();
    assert.equal(harness.calls.length, 1, 'DELETE must wait for the POST');
    harness.releasePost();
    await flush();
    assert.deepEqual(harness.calls, [
        'POST /Plugins/AIOStreams/NotInterested/title-1?userId=user-a',
        'DELETE /Plugins/AIOStreams/NotInterested/title-1?userId=user-a'
    ]);
    assert.equal(harness.refreshes(), 1);
});

test('a failed save restores Home and Undo then sends nothing', async () => {
    const harness = createDismissHarness('fail');
    harness.dismiss();
    harness.toasts[0].action();
    harness.releasePost();
    await flush();
    assert.deepEqual(harness.calls, ['POST /Plugins/AIOStreams/NotInterested/title-1?userId=user-a']);
    assert.ok(harness.toasts.some(toast => /Could not hide Heat/.test(toast.message)));
    assert.ok(harness.refreshes() >= 1);
});
