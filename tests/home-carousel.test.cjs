const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const source = fs.readFileSync(path.join(__dirname, '..', 'Jellyfin.Plugin.Mediarow',
    'Web', 'mediarow-home.js'), 'utf8');
const setupSource = source.slice(source.indexOf('    function setupCarousel('),
    source.indexOf('    function createRow('));

class Element {
    constructor(fragment = false) {
        this.fragment = fragment;
        this.children = [];
        this.dataset = {};
        this.events = {};
        this.scrollLeft = 0;
        this.clientWidth = 250;
        this.isConnected = true;
        this.captured = false;
        this.classes = new Set();
        this.classList = { add: value => this.classes.add(value), remove: value => this.classes.delete(value) };
    }
    get scrollWidth() { return this.children.length * 100 - 10; }
    get offsetLeft() { return this.parent.children.indexOf(this) * 100; }
    setAttribute() {}
    appendChild(child) {
        if (child.fragment) {
            [...child.children].forEach(card => this.appendChild(card));
        } else {
            child.parent = this;
            this.children.push(child);
        }
    }
    insertBefore(fragment, target) {
        const index = this.children.indexOf(target);
        fragment.children.forEach(child => { child.parent = this; });
        this.children.splice(index, 0, ...fragment.children);
    }
    remove() { this.parent.children.splice(this.parent.children.indexOf(this), 1); }
    querySelectorAll() { return this.children.filter(card => card.dataset.carouselCopy); }
    addEventListener(name, handler) { this.events[name] = handler; }
    setPointerCapture() { this.captured = true; }
    hasPointerCapture() { return this.captured; }
    releasePointerCapture() { this.captured = false; }
    emit(name, overrides = {}) {
        const event = {
            isPrimary: true, button: 0, pointerId: 1, clientX: 200, clientY: 10, detail: 1,
            preventDefault() { this.prevented = true; },
            stopImmediatePropagation() { this.stopped = true; },
            ...overrides
        };
        this.events[name]?.(event);
        return event;
    }
}

function carousel(count = 5) {
    const items = new Element();
    const catalog = Array.from({ length: count }, (_, Id) => ({ Id }));
    catalog.forEach(() => items.appendChild(new Element()));
    const previous = new Element();
    const next = new Element();
    let resize;
    const context = vm.createContext({
        document: { createDocumentFragment: () => new Element(true) },
        window: { ResizeObserver: true },
        ResizeObserver: class {
            constructor(callback) { resize = callback; }
            observe() {}
            disconnect() {}
        },
        requestAnimationFrame: callback => callback(),
        createCard: () => new Element()
    });
    vm.runInContext(setupSource, context);
    context.setupCarousel({}, items, catalog, previous, next);
    return { items, previous, next, resize: () => resize() };
}

test('overflowing rows start on the original cards with decorative loop copies', () => {
    const { items, previous, next } = carousel();
    assert.equal(items.scrollLeft, 500);
    assert.equal(items.children.length, 15);
    assert.equal(items.querySelectorAll().length, 10);
    assert.equal(previous.hidden, false);
    assert.equal(next.hidden, false);
    assert.ok(items.querySelectorAll().every(card => card.tabIndex === -1));
});

test('drag moves the whole row and suppresses navigation only after dragging', () => {
    const { items } = carousel();
    items.emit('pointerdown');
    items.emit('pointermove', { clientX: 195 });
    assert.equal(items.scrollLeft, 500);
    items.emit('pointermove', { clientX: 100 });
    assert.equal(items.scrollLeft, 600);
    assert.equal(items.captured, true);
    items.emit('pointerup');
    assert.equal(items.captured, false);
    assert.equal(items.emit('click').stopped, true);
    assert.equal(items.emit('click', { detail: 0 }).stopped, undefined);
    items.emit('pointerdown');
    items.emit('pointerup');
    assert.equal(items.emit('click').stopped, undefined);
});

test('drag wraps seamlessly forward and backward, even over multiple cycles', () => {
    const { items } = carousel();
    items.scrollLeft = 990;
    items.emit('pointerdown');
    items.emit('pointermove', { clientX: 180 });
    assert.equal(items.scrollLeft, 510);
    items.emit('pointerup');
    items.emit('pointerdown');
    items.emit('pointermove', { clientX: 230 });
    assert.equal(items.scrollLeft, 980);
    items.emit('pointermove', { clientX: 1430 });
    assert.equal(items.scrollLeft, 780);
});

test('buttons and keyboard wrap while vertical gestures remain page scrolling', () => {
    const { items, previous, next } = carousel();
    previous.emit('click');
    assert.equal(items.scrollLeft, 800);
    next.emit('click');
    assert.equal(items.scrollLeft, 500);
    const key = items.emit('keydown', { key: 'ArrowRight' });
    assert.equal(key.prevented, true);
    assert.equal(items.scrollLeft, 700);
    items.emit('pointerdown');
    const vertical = items.emit('pointermove', { clientY: 60 });
    assert.equal(vertical.prevented, undefined);
    assert.equal(items.scrollLeft, 700);
});

test('short rows do not repeat and resizing removes unneeded copies', () => {
    const short = carousel(2);
    assert.equal(short.items.children.length, 2);
    assert.equal(short.next.hidden, true);
    const full = carousel();
    full.items.clientWidth = 600;
    full.resize();
    assert.equal(full.items.children.length, 5);
    assert.equal(full.next.hidden, true);
    full.items.clientWidth = 250;
    full.resize();
    assert.equal(full.items.children.length, 15);
    assert.equal(full.items.scrollLeft, 500);
});

test('pointer cancellation releases drag state', () => {
    const { items } = carousel();
    items.emit('pointerdown');
    items.emit('pointermove', { clientX: 100 });
    items.emit('pointercancel');
    assert.equal(items.captured, false);
    assert.equal(items.classes.has('isDragging'), false);
    items.emit('pointermove', { clientX: 0 });
    assert.equal(items.scrollLeft, 600);
});

test('remote and keyboard arrows move focus between original cards and wrap', () => {
    const { items } = carousel(3);
    const originals = items.children.filter(card => !card.dataset.carouselCopy);
    let focused = null;
    originals.forEach((card, index) => {
        card.getAttribute = name => name === 'data-mediarow-index' ? String(index) : null;
        card.focus = () => { focused = card; };
    });
    const right = items.emit('keydown', { key: 'ArrowRight', target: originals[2] });
    assert.equal(right.prevented, true);
    assert.equal(focused, originals[0]);
    items.emit('keydown', { key: undefined, keyCode: 37, target: originals[0] });
    assert.equal(focused, originals[2]);
    const down = items.emit('keydown', { key: 'ArrowDown', target: originals[0] });
    assert.equal(down.prevented, undefined);
});

test('touch pointers keep native momentum scrolling', () => {
    const { items } = carousel();
    items.emit('pointerdown', { pointerType: 'touch' });
    const move = items.emit('pointermove', { pointerType: 'touch', clientX: 100 });
    assert.equal(move.prevented, undefined);
    assert.equal(items.scrollLeft, 500);
    assert.equal(items.captured, false);
});

test('injected script avoids syntax that older TV browsers cannot parse', () => {
    const code = source.replace(/\/\/.*$/gm, '').replace(/'(?:\\.|[^'\\])*'/g, "''");
    for (const pattern of [/=>/, /\?\./, /\?\?/, /\basync\b/, /\bawait\b/, /`/, /\blet\b/, /\bconst\b/, /\bclass\b/, /\.includes\(/, /\.closest\(/, /Object\.assign/, /\.\.\./]) {
        assert.doesNotMatch(code, pattern);
    }
});
