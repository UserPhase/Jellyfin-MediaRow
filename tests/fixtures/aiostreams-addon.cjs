// Local-only Stremio fixture. Serve generated test media, never third-party video.
const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');

const root = process.argv[2];
if (!root || !path.isAbsolute(root)) throw new Error('Pass an absolute folder containing generated compatible.mp4 and surround.mkv');
const base = 'http://127.0.0.1:18766';
const movie = {
    id: 'tt999999991', imdb_id: 'tt999999991', type: 'movie', name: 'Dynamic Playback Test',
    description: 'Generated colour bars and sine-wave audio for local playback verification.',
    releaseInfo: '2020', released: '2020-01-01T00:00:00Z', runtime: '1 min', genres: ['Drama', 'Action'],
    imdbRating: '8'
};
const series = {
    ...movie, id: 'tt999999992', imdb_id: 'tt999999992', type: 'series', name: 'Dynamic Episode Test',
    videos: [1, 2].map(episode => ({
        id: `tt999999992:1:${episode}`, season: 1, episode,
        title: `Generated episode ${episode}`, released: '2020-01-01T00:00:00Z'
    }))
};
const streams = [
    { name: 'Generated MP4 / AAC', url: `${base}/media/compatible.mp4`, behaviorHints: { filename: 'compatible.mp4' } },
    {
        name: 'Generated MKV / AC3', url: `${base}/media/surround.mkv`,
        behaviorHints: {
            filename: 'surround.mkv', notWebReady: true,
            proxyHeaders: { request: { Referer: `${base}/allowed` } }
        }
    },
    {
        name: 'Generated HLS / AAC', url: `${base}/media/playlist.m3u8`,
        behaviorHints: { filename: 'playlist.m3u8', proxyHeaders: { request: { Referer: `${base}/allowed` } } }
    },
    { name: 'Unsupported raw torrent', infoHash: 'fixture-only' }
];
function json(response, data, status = 200) {
    response.writeHead(status, { 'Content-Type': 'application/json', 'Access-Control-Allow-Origin': '*' });
    response.end(JSON.stringify(data));
}
http.createServer((request, response) => {
    const pathname = new URL(request.url, base).pathname;
    if (pathname === '/manifest.json') {
        return json(response, {
            id: 'local.dynamic.playback.fixture', version: '1.0.0', name: 'Generated Playback Fixture',
            resources: ['catalog', 'meta', 'stream'], types: ['movie', 'series'],
            catalogs: [{ type: 'movie', id: 'top' }, { type: 'series', id: 'top' }]
        });
    }
    if (pathname.startsWith('/catalog/')) return json(response, { metas: [pathname.includes('/series/') ? series : movie] });
    if (pathname.startsWith('/meta/')) return json(response, { meta: pathname.includes('/series/') ? series : movie });
    if (pathname.startsWith('/stream/')) {
        console.log('stream request', pathname);
        return json(response, { streams });
    }
    if (/^\/media\/(?:compatible\.mp4|surround\.mkv|playlist\.m3u8|seg\d+\.ts)$/.test(pathname)) {
        if (!pathname.endsWith('.mp4') && request.headers.referer !== `${base}/allowed`) {
            return json(response, { error: 'Referer required for fixture MKV' }, 403);
        }
        const file = path.join(root, path.basename(pathname));
        const size = fs.statSync(file).size;
        const range = /^bytes=(\d+)-(\d*)$/.exec(request.headers.range || '');
        const start = range ? Number(range[1]) : 0;
        const end = range && range[2] ? Math.min(Number(range[2]), size - 1) : size - 1;
        if (start >= size || start > end) {
            response.writeHead(416, { 'Content-Range': `bytes */${size}` });
            return response.end();
        }
        const headers = {
            'Content-Type': pathname.endsWith('.m3u8') ? 'application/vnd.apple.mpegurl'
                : pathname.endsWith('.ts') ? 'video/mp2t' : pathname.endsWith('.mp4') ? 'video/mp4' : 'video/x-matroska',
            'Content-Length': end - start + 1, 'Accept-Ranges': 'bytes', 'Access-Control-Allow-Origin': '*'
        };
        if (range) headers['Content-Range'] = `bytes ${start}-${end}/${size}`;
        response.writeHead(range ? 206 : 200, headers);
        if (request.method === 'HEAD') return response.end();
        fs.createReadStream(file, { start, end }).pipe(response);
        return;
    }
    json(response, { error: 'Unknown fixture endpoint' }, 404);
}).listen(18766, '127.0.0.1', () => console.log('Generated playback fixture ready at', base));
