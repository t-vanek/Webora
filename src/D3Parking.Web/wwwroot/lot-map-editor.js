// Editor mapy parkoviště: veškerá práce s ukazatelem (výběr, tažení, změna velikosti, otáčení,
// posun a zoom plátna) běží tady v prohlížeči. Blazor vlastní model a ukládání — dostane až
// výsledek gesta, ne každý pohyb myši. Bez toho by tažení jednoho obdélníku znamenalo desítky
// round-tripů po SignalR za sekundu a na mapě o pěti stech tvarech by to bylo nepoužitelné.
//
// Dělba práce s Blazorem:
//   - Blazor vykresluje tvary (<g class="map-shape" data-*>) a nechává prázdné <g class="map-overlay">.
//   - JS do overlaye kreslí úchyty a gumičku výběru. Blazor o těch uzlech neví, takže mu je
//     překreslení nesmaže.
//   - Souřadnice tvaru drží data-atributy; jsou to tytéž hodnoty, které pošleme zpět při dokončení.

const SVG_NS = 'http://www.w3.org/2000/svg';

// Velikost úchytu v pixelech obrazovky — přepočítává se do jednotek mapy, aby úchyt zůstal
// stejně velký při jakémkoli zoomu.
const HANDLE_PX = 9;
const ROTATE_ARM_PX = 26;

const RESIZE_HANDLES = [
    { id: 'nw', fx: 1, fy: 1, dx: -1, dy: -1 },
    { id: 'n', fx: 0.5, fy: 1, dx: 0, dy: -1 },
    { id: 'ne', fx: 0, fy: 1, dx: 1, dy: -1 },
    { id: 'e', fx: 0, fy: 0.5, dx: 1, dy: 0 },
    { id: 'se', fx: 0, fy: 0, dx: 1, dy: 1 },
    { id: 's', fx: 0.5, fy: 0, dx: 0, dy: 1 },
    { id: 'sw', fx: 1, fy: 0, dx: -1, dy: 1 },
    { id: 'w', fx: 1, fy: 0.5, dx: -1, dy: 0 },
];

let state = null;

// --- geometrie (zrcadlí MapRect na serveru; server hodnoty po odeslání stejně sanitizuje) ---

const toRad = (deg) => (deg * Math.PI) / 180;

function corners(r) {
    const cx = r.x + r.w / 2;
    const cy = r.y + r.h / 2;
    const pts = [[r.x, r.y], [r.x + r.w, r.y], [r.x + r.w, r.y + r.h], [r.x, r.y + r.h]];
    if (Math.abs(r.rot) < 0.001) {
        return pts;
    }

    const sin = Math.sin(toRad(r.rot));
    const cos = Math.cos(toRad(r.rot));
    return pts.map(([px, py]) => {
        const dx = px - cx;
        const dy = py - cy;
        return [cx + dx * cos - dy * sin, cy + dx * sin + dy * cos];
    });
}

const pointsAttr = (r) => corners(r).map(([x, y]) => `${round(x)},${round(y)}`).join(' ');

// Bod (dx, dy) z lokálního rámce tvaru do světových souřadnic.
function localToWorld(rot, dx, dy) {
    if (Math.abs(rot) < 0.001) {
        return [dx, dy];
    }

    const sin = Math.sin(toRad(rot));
    const cos = Math.cos(toRad(rot));
    return [dx * cos - dy * sin, dx * sin + dy * cos];
}

const round = (v) => Math.round(v * 100) / 100;

function snap(value) {
    const grid = state?.grid ?? 0;
    return grid > 0 ? Math.round(value / grid) * grid : value;
}

// --- převod souřadnic ---

// Klientské pixely na jednotky mapy. getScreenCTM respektuje viewBox, CSS měřítko i odrolování
// libovolného předka, takže tohle sedí i uvnitř posuvného panelu.
function toMap(event) {
    const ctm = state.svg.getScreenCTM();
    if (!ctm) {
        return { x: 0, y: 0 };
    }

    const point = state.svg.createSVGPoint();
    point.x = event.clientX;
    point.y = event.clientY;
    const mapped = point.matrixTransform(ctm.inverse());
    return { x: mapped.x, y: mapped.y };
}

// Kolik jednotek mapy odpovídá jednomu pixelu obrazovky — pro úchyty konstantní velikosti.
function unitsPerPixel() {
    const ctm = state.svg.getScreenCTM();
    return ctm && ctm.a !== 0 ? 1 / ctm.a : 1;
}

// --- čtení a zápis tvarů ---

const shapeNodes = () => Array.from(state.svg.querySelectorAll('.map-shape'));

const shapeNode = (id) => state.svg.querySelector(`.map-shape[data-id="${CSS.escape(id)}"]`);

function readRect(node) {
    return {
        id: node.dataset.id,
        x: parseFloat(node.dataset.x),
        y: parseFloat(node.dataset.y),
        w: parseFloat(node.dataset.w),
        h: parseFloat(node.dataset.h),
        rot: parseFloat(node.dataset.rot) || 0,
    };
}

// Živý náhled: přepíše jen polygon a pozici popisku, data-atributy zůstanou původní až do
// dokončení gesta. Nedokončené tažení tak jde kdykoli zahodit prostým překreslením.
function preview(node, rect) {
    const polygon = node.querySelector('polygon');
    if (polygon) {
        polygon.setAttribute('points', pointsAttr(rect));
    }

    const text = node.querySelector('text');
    if (text) {
        const cx = rect.x + rect.w / 2;
        const cy = rect.y + rect.h / 2;
        text.setAttribute('x', round(cx));
        text.setAttribute('y', round(cy));
        text.setAttribute('transform', rect.rot ? `rotate(${round(rect.rot)} ${round(cx)} ${round(cy)})` : '');
    }
}

function commitRect(node, rect) {
    node.dataset.x = round(rect.x);
    node.dataset.y = round(rect.y);
    node.dataset.w = round(rect.w);
    node.dataset.h = round(rect.h);
    node.dataset.rot = round(rect.rot);
}

// --- výběr ---

const selectedIds = () => shapeNodes().filter((n) => n.classList.contains('is-selected')).map((n) => n.dataset.id);

function setSelection(ids) {
    const wanted = new Set(ids);
    shapeNodes().forEach((node) => node.classList.toggle('is-selected', wanted.has(node.dataset.id)));
    drawOverlay();
    state.dotNet.invokeMethodAsync('OnSelectionChanged', Array.from(wanted)).catch(() => undefined);
}

// --- overlay: úchyty a gumička ---

function clearOverlay() {
    while (state.overlay.firstChild) {
        state.overlay.removeChild(state.overlay.firstChild);
    }
}

function drawOverlay() {
    clearOverlay();
    if (state.tool !== 'select') {
        return;
    }

    const ids = selectedIds();
    // Úchyty jen u jednoho tvaru: u vícenásobného výběru by osm úchytů muselo znamenat změnu
    // velikosti skupiny, což je jiná operace — a posunout se dá výběr jakékoli velikosti i bez nich.
    if (ids.length !== 1) {
        return;
    }

    const node = shapeNode(ids[0]);
    if (!node) {
        return;
    }

    const rect = readRect(node);
    const size = HANDLE_PX * unitsPerPixel();
    const cx = rect.x + rect.w / 2;
    const cy = rect.y + rect.h / 2;

    RESIZE_HANDLES.forEach((handle) => {
        const localX = rect.x + rect.w * (1 - handle.fx) - cx;
        const localY = rect.y + rect.h * (1 - handle.fy) - cy;
        const [wx, wy] = localToWorld(rect.rot, localX, localY);
        state.overlay.appendChild(handleNode('resize', handle.id, cx + wx, cy + wy, size));
    });

    // Otáčecí rameno vychází z horní hrany, takže i u otočeného tvaru míří „nahoru od něj".
    const [ax, ay] = localToWorld(rect.rot, 0, -(rect.h / 2 + ROTATE_ARM_PX * unitsPerPixel()));
    const arm = document.createElementNS(SVG_NS, 'line');
    arm.setAttribute('class', 'map-handle__arm');
    arm.setAttribute('x1', round(cx));
    arm.setAttribute('y1', round(cy));
    arm.setAttribute('x2', round(cx + ax));
    arm.setAttribute('y2', round(cy + ay));
    state.overlay.appendChild(arm);
    state.overlay.appendChild(handleNode('rotate', 'rotate', cx + ax, cy + ay, size));
}

function handleNode(role, id, x, y, size) {
    const node = document.createElementNS(SVG_NS, 'rect');
    node.setAttribute('class', `map-handle map-handle--${role}`);
    node.setAttribute('x', round(x - size / 2));
    node.setAttribute('y', round(y - size / 2));
    node.setAttribute('width', round(size));
    node.setAttribute('height', round(size));
    if (role === 'rotate') {
        node.setAttribute('rx', round(size / 2));
    }

    node.dataset.handle = id;
    node.dataset.role = role;
    return node;
}

function marquee(a, b) {
    let node = state.overlay.querySelector('.map-marquee');
    if (!node) {
        node = document.createElementNS(SVG_NS, 'rect');
        node.setAttribute('class', 'map-marquee');
        state.overlay.appendChild(node);
    }

    node.setAttribute('x', round(Math.min(a.x, b.x)));
    node.setAttribute('y', round(Math.min(a.y, b.y)));
    node.setAttribute('width', round(Math.abs(b.x - a.x)));
    node.setAttribute('height', round(Math.abs(b.y - a.y)));
}

// Osově zarovnaná obálka tvaru — proti ní se testuje gumička (u otočených tvarů je to
// záměrné zjednodušení: výběr rámečkem má být rychlý, ne přesný na pixel).
function bounds(rect) {
    const pts = corners(rect);
    return {
        minX: Math.min(...pts.map((p) => p[0])),
        minY: Math.min(...pts.map((p) => p[1])),
        maxX: Math.max(...pts.map((p) => p[0])),
        maxY: Math.max(...pts.map((p) => p[1])),
    };
}

// --- gesta ---

function onPointerDown(event) {
    if (!state || event.button === 2) {
        return;
    }

    const point = toMap(event);
    const handle = event.target.closest?.('.map-handle');
    const shape = event.target.closest?.('.map-shape');

    // Prostřední tlačítko posouvá plátno v každém nástroji — jinak by se při kreslení muselo
    // pořád přepínat zpět na ruku.
    if (event.button === 1 || state.tool === 'pan') {
        state.drag = { kind: 'pan', origin: { x: event.clientX, y: event.clientY }, view: { ...state.view } };
    } else if (handle && handle.dataset.role === 'rotate') {
        const node = shapeNode(selectedIds()[0]);
        const rect = readRect(node);
        state.drag = {
            kind: 'rotate',
            node,
            start: rect,
            centre: { x: rect.x + rect.w / 2, y: rect.y + rect.h / 2 },
            startAngle: null,
        };
        state.drag.startAngle = angleTo(state.drag.centre, point) - rect.rot;
    } else if (handle) {
        const node = shapeNode(selectedIds()[0]);
        state.drag = { kind: 'resize', node, start: readRect(node), handle: handle.dataset.handle, origin: point };
    } else if (state.tool === 'draw') {
        state.drag = { kind: 'draw', origin: { x: snap(point.x), y: snap(point.y) } };
    } else if (shape) {
        // Tažení nevybraného tvaru ho nejdřív vybere — jinak by se musel klikat dvakrát.
        if (!shape.classList.contains('is-selected')) {
            setSelection(event.shiftKey ? [...selectedIds(), shape.dataset.id] : [shape.dataset.id]);
        } else if (event.shiftKey) {
            setSelection(selectedIds().filter((id) => id !== shape.dataset.id));
            return;
        }

        const nodes = selectedIds().map(shapeNode).filter(Boolean);
        state.drag = { kind: 'move', origin: point, nodes, starts: nodes.map(readRect) };
    } else {
        state.drag = { kind: 'marquee', origin: point, additive: event.shiftKey, base: event.shiftKey ? selectedIds() : [] };
        if (!event.shiftKey) {
            setSelection([]);
        }
    }

    state.svg.setPointerCapture(event.pointerId);
    state.moved = false;
    event.preventDefault();
}

const angleTo = (centre, point) => (Math.atan2(point.y - centre.y, point.x - centre.x) * 180) / Math.PI;

function onPointerMove(event) {
    if (!state?.drag) {
        return;
    }

    const drag = state.drag;
    state.moved = true;

    if (drag.kind === 'pan') {
        const scale = state.view.w / state.svg.clientWidth;
        state.view.x = drag.view.x - (event.clientX - drag.origin.x) * scale;
        state.view.y = drag.view.y - (event.clientY - drag.origin.y) * scale;
        applyView();
        return;
    }

    const point = toMap(event);

    if (drag.kind === 'move') {
        const dx = snap(point.x - drag.origin.x);
        const dy = snap(point.y - drag.origin.y);
        drag.nodes.forEach((node, i) => preview(node, { ...drag.starts[i], x: drag.starts[i].x + dx, y: drag.starts[i].y + dy }));
        drag.delta = { dx, dy };
    } else if (drag.kind === 'resize') {
        drag.rect = resized(drag, point, event.shiftKey);
        preview(drag.node, drag.rect);
    } else if (drag.kind === 'rotate') {
        let rot = angleTo(drag.centre, point) - drag.startAngle;
        // Shift drží násobky 15° — plány jsou skoro vždy kreslené v takových krocích.
        if (event.shiftKey) {
            rot = Math.round(rot / 15) * 15;
        }

        drag.rect = { ...drag.start, rot };
        preview(drag.node, drag.rect);
    } else if (drag.kind === 'draw') {
        drag.rect = normalize(drag.origin, { x: snap(point.x), y: snap(point.y) });
        drawPending(drag.rect);
    } else if (drag.kind === 'marquee') {
        marquee(drag.origin, point);
        const box = normalize(drag.origin, point);
        const hit = shapeNodes().filter((node) => {
            const b = bounds(readRect(node));
            return b.minX < box.x + box.w && b.maxX > box.x && b.minY < box.y + box.h && b.maxY > box.y;
        }).map((node) => node.dataset.id);
        const wanted = new Set([...drag.base, ...hit]);
        shapeNodes().forEach((node) => node.classList.toggle('is-selected', wanted.has(node.dataset.id)));
    }
}

const normalize = (a, b) => ({
    x: Math.min(a.x, b.x),
    y: Math.min(a.y, b.y),
    w: Math.abs(b.x - a.x),
    h: Math.abs(b.y - a.y),
    rot: 0,
});

// Změna velikosti probíhá v lokálním rámci tvaru, takže u otočeného obdélníku táhne úchyt
// „doprava" po jeho vlastní hraně a protilehlý roh zůstává na místě.
function resized(drag, point, keepRatio) {
    const start = drag.start;
    const spec = RESIZE_HANDLES.find((h) => h.id === drag.handle);
    const dxWorld = point.x - drag.origin.x;
    const dyWorld = point.y - drag.origin.y;

    // Světový posun zpět do lokálního rámce (rotace opačným směrem).
    const [dxLocal, dyLocal] = localToWorld(-start.rot, dxWorld, dyWorld);

    let w = start.w + dxLocal * spec.dx;
    let h = start.h + dyLocal * spec.dy;
    w = Math.max(1, snap(w));
    h = Math.max(1, snap(h));

    if (keepRatio && spec.dx !== 0 && spec.dy !== 0) {
        const ratio = start.w / start.h;
        h = Math.max(1, w / ratio);
    }

    // Kotva je protilehlý roh: jeho světová poloha se nesmí hnout, tak se dopočítá nový střed.
    const anchorLocalX = (spec.dx === 0 ? 0 : -spec.dx) * (start.w / 2);
    const anchorLocalY = (spec.dy === 0 ? 0 : -spec.dy) * (start.h / 2);
    const [ax, ay] = localToWorld(start.rot, anchorLocalX, anchorLocalY);
    const anchorX = start.x + start.w / 2 + ax;
    const anchorY = start.y + start.h / 2 + ay;

    const newAnchorLocalX = (spec.dx === 0 ? 0 : -spec.dx) * (w / 2);
    const newAnchorLocalY = (spec.dy === 0 ? 0 : -spec.dy) * (h / 2);
    const [nx, ny] = localToWorld(start.rot, newAnchorLocalX, newAnchorLocalY);

    return { ...start, w, h, x: anchorX - nx - w / 2, y: anchorY - ny - h / 2 };
}

function drawPending(rect) {
    let node = state.overlay.querySelector('.map-pending');
    if (!node) {
        node = document.createElementNS(SVG_NS, 'polygon');
        node.setAttribute('class', 'map-pending');
        state.overlay.appendChild(node);
    }

    node.setAttribute('points', pointsAttr(rect));
}

function onPointerUp(event) {
    if (!state?.drag) {
        return;
    }

    const drag = state.drag;
    state.drag = null;
    try {
        state.svg.releasePointerCapture(event.pointerId);
    } catch {
        // Ukazatel už mohl být uvolněn (např. při ztrátě fokusu) — na výsledku gesta to nic nemění.
    }

    if (drag.kind === 'pan') {
        return;
    }

    if (drag.kind === 'draw') {
        clearOverlay();
        if (drag.rect && drag.rect.w >= 1 && drag.rect.h >= 1) {
            state.dotNet.invokeMethodAsync('OnShapeDrawn', drag.rect.x, drag.rect.y, drag.rect.w, drag.rect.h)
                .catch(() => undefined);
        }

        return;
    }

    if (drag.kind === 'marquee') {
        clearOverlay();
        setSelection(selectedIds());
        return;
    }

    if (drag.kind === 'move' && drag.delta && (drag.delta.dx || drag.delta.dy)) {
        const updates = drag.nodes.map((node, i) => {
            const rect = { ...drag.starts[i], x: drag.starts[i].x + drag.delta.dx, y: drag.starts[i].y + drag.delta.dy };
            commitRect(node, rect);
            return payload(node.dataset.id, rect);
        });
        push(updates);
    } else if ((drag.kind === 'resize' || drag.kind === 'rotate') && drag.rect) {
        commitRect(drag.node, drag.rect);
        push([payload(drag.node.dataset.id, drag.rect)]);
    }

    drawOverlay();
}

const payload = (id, r) => ({
    shapeId: id,
    x: round(r.x),
    y: round(r.y),
    width: round(r.w),
    height: round(r.h),
    rotation: round(r.rot),
});

function push(updates) {
    if (updates.length > 0) {
        state.dotNet.invokeMethodAsync('OnShapesMoved', updates).catch(() => undefined);
    }
}

// --- zoom a posun plátna ---

function applyView() {
    const v = state.view;
    state.svg.setAttribute('viewBox', `${round(v.x)} ${round(v.y)} ${round(v.w)} ${round(v.h)}`);
    drawOverlay();
}

function onWheel(event) {
    if (!state) {
        return;
    }

    event.preventDefault();
    const point = toMap(event);
    const factor = event.deltaY > 0 ? 1.15 : 1 / 1.15;
    const w = Math.min(state.natural.w * 8, Math.max(state.natural.w / 40, state.view.w * factor));
    const scale = w / state.view.w;

    // Zoom k ukazateli: bod pod kurzorem zůstává na místě.
    state.view.x = point.x - (point.x - state.view.x) * scale;
    state.view.y = point.y - (point.y - state.view.y) * scale;
    state.view.w = w;
    state.view.h = state.natural.h * (w / state.natural.w);
    applyView();
}

function onKeyDown(event) {
    if (!state || event.target !== state.svg) {
        return;
    }

    if (event.key === 'Escape') {
        setSelection([]);
        event.preventDefault();
        return;
    }

    if (event.key === 'Delete' || event.key === 'Backspace') {
        const ids = selectedIds();
        if (ids.length > 0) {
            state.dotNet.invokeMethodAsync('OnDeleteRequested', ids).catch(() => undefined);
            event.preventDefault();
        }

        return;
    }

    // Šipky posouvají výběr o mřížku (s Shiftem desetkrát) — přesnější než myš.
    const nudge = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] }[event.key];
    if (!nudge) {
        return;
    }

    const nodes = selectedIds().map(shapeNode).filter(Boolean);
    if (nodes.length === 0) {
        return;
    }

    const stepSize = (state.grid > 0 ? state.grid : 1) * (event.shiftKey ? 10 : 1);
    const updates = nodes.map((node) => {
        const rect = readRect(node);
        rect.x += nudge[0] * stepSize;
        rect.y += nudge[1] * stepSize;
        commitRect(node, rect);
        preview(node, rect);
        return payload(node.dataset.id, rect);
    });

    push(updates);
    drawOverlay();
    event.preventDefault();
}

export function attach(svg, dotNet, options) {
    detach();
    if (!svg) {
        return;
    }

    const natural = { w: options.width, h: options.height };
    state = {
        svg,
        dotNet,
        overlay: svg.querySelector('.map-overlay'),
        tool: options.tool ?? 'select',
        grid: options.grid ?? 0,
        natural,
        view: { x: 0, y: 0, w: natural.w, h: natural.h },
        drag: null,
    };

    state.handlers = {
        down: onPointerDown,
        move: onPointerMove,
        up: onPointerUp,
        wheel: onWheel,
        key: onKeyDown,
    };

    svg.addEventListener('pointerdown', state.handlers.down);
    svg.addEventListener('pointermove', state.handlers.move);
    svg.addEventListener('pointerup', state.handlers.up);
    svg.addEventListener('pointercancel', state.handlers.up);
    svg.addEventListener('wheel', state.handlers.wheel, { passive: false });
    svg.addEventListener('keydown', state.handlers.key);
    svg.dataset.tool = state.tool;
    applyView();
}

// Volá se po každém překreslení Blazorem: nástroj a mřížka se mohly změnit a overlay (úchyty)
// je potřeba nakreslit znovu nad nové uzly tvarů.
export function sync(options) {
    if (!state) {
        return;
    }

    state.tool = options.tool ?? state.tool;
    state.grid = options.grid ?? state.grid;
    // Modul hlásí na plátně, který nástroj skutečně drží. Blazor si třídu plátna vykresluje sám,
    // ta ale říká jen „server o změně ví" — tohle říká „modul, který obsluhuje tažení, ji přijal".
    state.svg.dataset.tool = state.tool;
    if (options.width && options.height && (options.width !== state.natural.w || options.height !== state.natural.h)) {
        state.natural = { w: options.width, h: options.height };
        state.view = { x: 0, y: 0, w: options.width, h: options.height };
        applyView();
        return;
    }

    drawOverlay();
}

export function select(ids) {
    if (state) {
        setSelection(ids ?? []);
    }
}

export function zoom(factor) {
    if (!state) {
        return;
    }

    const centre = { x: state.view.x + state.view.w / 2, y: state.view.y + state.view.h / 2 };
    const w = Math.min(state.natural.w * 8, Math.max(state.natural.w / 40, state.view.w / factor));
    state.view.w = w;
    state.view.h = state.natural.h * (w / state.natural.w);
    state.view.x = centre.x - w / 2;
    state.view.y = centre.y - state.view.h / 2;
    applyView();
}

export function resetView() {
    if (state) {
        state.view = { x: 0, y: 0, w: state.natural.w, h: state.natural.h };
        applyView();
    }
}

export function detach() {
    if (!state) {
        return;
    }

    const { svg, handlers } = state;
    svg.removeEventListener('pointerdown', handlers.down);
    svg.removeEventListener('pointermove', handlers.move);
    svg.removeEventListener('pointerup', handlers.up);
    svg.removeEventListener('pointercancel', handlers.up);
    svg.removeEventListener('wheel', handlers.wheel);
    svg.removeEventListener('keydown', handlers.key);
    state = null;
}
