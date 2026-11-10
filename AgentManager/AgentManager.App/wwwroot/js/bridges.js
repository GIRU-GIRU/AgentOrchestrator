// The traces between bridged cards. BridgeRules.Links decides which pairs exist; this only draws them, into the two
// .bridge-layer SVGs Blazor renders empty. Every coordinate is in canvas units inside #canvas-world, so panning and zooming
// move the drawing with no script, and they come from the cards' offsets, which a drag writes directly.
window.bridges = (() => {
    const SVG = 'http://www.w3.org/2000/svg';
    const reduceMotion = matchMedia('(prefers-reduced-motion: reduce)').matches;
    const RIGHT = 1;
    const LEFT = -1;
    const PORT_PITCH = 14;
    const PORT_PAD = 7;
    // Two cards side by side have only the gap's width of wire, so their pads carry the link instead: a bracket across the gap.
    const SHORT_LINK = 40;
    const SHORT_LINK_PAD = 26;
    const NODE_SIZE = 5;
    const LANES = [0, -3, 3, -6, 6];
    const LANE_CLEARANCE = 6;
    const CHROME = '.legend, .badge, .bridge, .talk, .corner, .master-cpu';
    // How far a wire runs out of a card's side before turning, when the two cards are stacked and it has to go round.
    const STUB = 30;
    const BLEND_MS = 240;
    const NEW_MS = 1100;
    const LABEL_HEIGHT = 13;
    const LABEL_PADDING = 5;
    const LABEL_MARGIN = 10;
    const LABEL_MIN_CHARS = 8;
    const LABEL_RECHECK_TICKS = 10;
    const PULSE_LENGTH = 24;
    const PULSE_PX_PER_SECOND = 220;
    // The share of a cycle the pulse is on the wire, the 72% in app.css's bt-pulse; the rest is a pause before the next.
    const PULSE_MOVING_SHARE = 0.72;
    const MIN_TRAVEL_SECONDS = 1.6;

    const traces = new Map();
    let wrap = null;
    let under = null;
    let over = null;
    let watcher = null;
    let frame = 0;
    let stamp = '';
    let hovered = null;
    let ticks = 0;

    function root() {
        return document.getElementById('canvas');
    }

    function make(tag, attributes, parent) {
        const el = document.createElementNS(SVG, tag);
        for (const [name, value] of Object.entries(attributes)) {
            el.setAttribute(name, value);
        }

        parent.appendChild(el);
        return el;
    }

    function update(links) {
        wrap = document.getElementById('canvas-world');
        under = document.getElementById('bridge-under');
        over = document.getElementById('bridge-over');
        if (!wrap || !under || !over) {
            return;
        }

        const wanted = new Set();
        for (const link of links) {
            const [a, b] = [link.from, link.to].sort();
            const id = `${link.channel}|${a}|${b}`;
            wanted.add(id);
            const trace = traces.get(id) ?? add(id, link.channel, a, b);
            for (const layer of [trace.wires, trace.ports]) {
                layer.classList.toggle('bt-active', link.isActive);
                layer.classList.toggle('bt-reverse', link.from !== a);
            }
        }

        for (const [id, trace] of traces) {
            if (!wanted.has(id)) {
                trace.wires.remove();
                trace.ports.remove();
                traces.delete(id);
            }
        }

        watch(traces.size > 0);
        stamp = '';
        paintHover();
        reflow();
    }

    function add(id, channel, a, b) {
        const wires = make('g', { class: 'bt-trace bt-new' }, under);
        const halo = make('path', { class: 'bt-halo' }, wires);
        const wire = make('path', { class: 'bt-wire', pathLength: 1 }, wires);
        const pulse = make('path', { class: 'bt-pulse' }, wires);
        pulse.style.setProperty('--pulse', PULSE_LENGTH);
        const label = make('g', { class: 'bt-label' }, wires);
        const plate = make('rect', { class: 'bt-plate', y: -LABEL_HEIGHT / 2, height: LABEL_HEIGHT }, label);
        const text = make('text', { class: 'bt-name', 'text-anchor': 'middle', 'dominant-baseline': 'central' }, label);
        text.textContent = channel;
        const ports = make('g', { class: 'bt-trace bt-new' }, over);
        const trace = {
            id, channel, a, b, wires, ports, halo, wire, pulse, label, plate, text,
            portA: port(ports), portB: port(ports), textWidth: text.getComputedTextLength(),
            d: '', length: 0, shown: null, blend: null, endA: '', endB: '', route: '', spot: null, isPlaced: false, isShort: false,
        };
        traces.set(id, trace);
        setTimeout(() => {
            wires.classList.remove('bt-new');
            ports.classList.remove('bt-new');
        }, NEW_MS);
        return trace;
    }

    function port(parent) {
        const group = make('g', { class: 'bt-port' }, parent);
        make('path', { class: 'bt-pad', d: `M0 ${-PORT_PAD}V${PORT_PAD}` }, group);
        make('rect', { class: 'bt-node', x: -NODE_SIZE / 2, y: -NODE_SIZE / 2, width: NODE_SIZE, height: NODE_SIZE }, group);
        return group;
    }

    function watch(isOn) {
        if (isOn && !watcher) {
            watcher = new ResizeObserver(() => reflow());
            watcher.observe(root());
            root().addEventListener('pointerover', onHover);
            root().addEventListener('pointerleave', onLeave);
        } else if (!isOn && watcher) {
            watcher.disconnect();
            watcher = null;
            root().removeEventListener('pointerover', onHover);
            root().removeEventListener('pointerleave', onLeave);
            hovered = null;
            paintHover();
        }
    }

    function onHover(e) {
        const key = e.target.closest?.('.canvas-world > .card')?.dataset.flip ?? null;
        if (key !== hovered) {
            hovered = key;
            paintHover();
        }
    }

    function onLeave() {
        hovered = null;
        paintHover();
    }

    // Lighting the partner is what says "these two" when the wire is long, or runs off the screen.
    function paintHover() {
        const partners = new Set();
        for (const trace of traces.values()) {
            const isLit = trace.a === hovered || trace.b === hovered;
            trace.wires.classList.toggle('bt-lit', isLit);
            trace.ports.classList.toggle('bt-lit', isLit);
            if (isLit) {
                partners.add(trace.a === hovered ? trace.b : trace.a);
            }
        }

        for (const card of wrap.querySelectorAll(':scope > .card')) {
            if (partners.has(card.dataset.flip)) {
                card.dataset.bridgePartner = '1';
            } else if (card.dataset.bridgePartner) {
                delete card.dataset.bridgePartner;
            }
        }
    }

    function cardsByKey() {
        const cards = new Map();
        for (const card of wrap.querySelectorAll(':scope > .card')) {
            cards.set(card.dataset.flip, card);
        }

        return cards;
    }

    // A card being dragged keeps its layout position and moves by `translate` (canvas.js), so that is added here.
    function slotOf(card) {
        const shift = card.style.translate ? card.style.translate.split(' ').map(parseFloat) : [0, 0];
        const left = card.offsetLeft + (shift[0] || 0);
        const top = card.offsetTop + (shift[1] || 0);
        const width = card.offsetWidth;
        const height = card.offsetHeight;
        return { left, top, right: left + width, bottom: top + height, width, height };
    }

    function isMovingCard(card) {
        return card.dataset.dragging !== undefined;
    }

    // Free cards: out of the sides facing each other, to a run halfway between them. Stacked cards leave the same side and go round it.
    function plan(cards) {
        const placed = [];
        for (const trace of traces.values()) {
            trace.cardA = cards.get(trace.a);
            trace.cardB = cards.get(trace.b);
            const isPlaced = !!(trace.cardA && trace.cardB);
            if (isPlaced !== trace.isPlaced) {
                trace.isPlaced = isPlaced;
                trace.wires.style.display = isPlaced ? '' : 'none';
                trace.ports.style.display = isPlaced ? '' : 'none';
            }

            if (isPlaced) {
                placed.push(trace);
            }
        }

        placed.sort((x, y) => (x.id < y.id ? -1 : 1));
        for (const trace of placed) {
            trace.slotA = slotOf(trace.cardA);
            trace.slotB = slotOf(trace.cardB);
            const isStacked = trace.slotA.left < trace.slotB.right && trace.slotB.left < trace.slotA.right;
            trace.sideA = isStacked || trace.slotA.left < trace.slotB.left ? RIGHT : LEFT;
            trace.sideB = isStacked ? RIGHT : -trace.sideA;
        }

        assignPorts(placed, STUB);
        const columnLanes = new Map();
        for (const trace of placed) {
            route(trace, columnLanes);
        }

        return placed;
    }

    // Two stubs leaving opposite cards at one height would read as a wire joining those two cards, so a gap never holds two
    // traces' stubs level with each other.
    function assignPorts(placed, gapX) {
        const sides = new Map();
        const addEnd = (key, side, trace, isA) => {
            const id = `${key}|${side}`;
            (sides.get(id) ?? sides.set(id, []).get(id)).push({ trace, isA, side });
        };
        for (const trace of placed) {
            addEnd(trace.a, trace.sideA, trace, true);
            addEnd(trace.b, trace.sideB, trace, false);
        }

        const inGap = new Map();
        for (const ends of sides.values()) {
            const otherTop = end => (end.isA ? end.trace.slotB : end.trace.slotA).top;
            ends.sort((x, y) => otherTop(x) - otherTop(y));
            ends.forEach((end, index) => {
                const slot = end.isA ? end.trace.slotA : end.trace.slotB;
                const edge = end.side === RIGHT ? slot.right : slot.left;
                const gap = Math.round(edge + end.side * gapX / 2);
                const stubs = inGap.get(gap) ?? inGap.set(gap, []).get(gap);
                let offset = (index - (ends.length - 1) / 2) * PORT_PITCH;
                const y = () => slot.top + slot.height / 2 + offset;
                while (stubs.some(s => s.trace !== end.trace && Math.abs(s.y - y()) < PORT_PITCH / 2)) {
                    offset += PORT_PITCH;
                }

                stubs.push({ trace: end.trace, y: y() });
                end.trace[end.isA ? 'portYA' : 'portYB'] = y();
                end.trace[end.isA ? 'portOffsetA' : 'portOffsetB'] = offset;
            });
        }
    }

    function route(trace, columnLanes) {
        const { slotA: a, slotB: b, sideA, sideB } = trace;
        const ax = sideA === RIGHT ? a.right : a.left;
        const bx = sideB === RIGHT ? b.right : b.left;
        const ay = trace.portYA;
        const by = trace.portYB;
        const run = sideA !== sideB ? (ax + bx) / 2 : Math.max(ax, bx) + STUB;
        const x = run + laneOf(columnLanes, run, Math.min(ay, by), Math.max(ay, by));
        trace.target = { ax, ay, xA: x, yH: by, xB: x, by, bx };
        trace.nextEndA = `${sideA}${trace.portOffsetA}`;
        trace.nextEndB = `${sideB}${trace.portOffsetB}`;
        trace.nextRoute = `${Math.round(x)}`;
    }

    function laneOf(table, at, from, to) {
        const key = Math.round(at);
        const used = table.get(key) ?? table.set(key, []).get(key);
        for (let index = 0; index < LANES.length; index++) {
            const isFree = !used.some(u => u.index === index && from < u.to + LANE_CLEARANCE && u.from < to + LANE_CLEARANCE);
            if (isFree) {
                used.push({ index, from, to });
                return LANES[index];
            }
        }

        return LANES[0];
    }

    function snap(value) {
        return Math.round(value) + 0.5;
    }

    // A reorder can change the route in one frame. The run through the gaps slides to the new one, while an end stays on its
    // gliding card unless it has to move to another side of it.
    function draw(trace, now) {
        let shown = trace.target;
        const isEndAMoved = trace.nextEndA !== trace.endA;
        const isEndBMoved = trace.nextEndB !== trace.endB;
        const isChanged = isEndAMoved || isEndBMoved || trace.nextRoute !== trace.route;
        if (isChanged && trace.shown && !reduceMotion) {
            trace.blend = { from: trace.shown, at: now, isEndAMoved, isEndBMoved };
        }

        trace.endA = trace.nextEndA;
        trace.endB = trace.nextEndB;
        trace.route = trace.nextRoute;
        if (trace.blend) {
            const progress = (now - trace.blend.at) / BLEND_MS;
            if (progress >= 1) {
                trace.blend = null;
            } else {
                const eased = 1 - Math.pow(1 - progress, 3);
                const { from } = trace.blend;
                const mix = name => from[name] + (trace.target[name] - from[name]) * eased;
                shown = { ...trace.target, xA: mix('xA'), yH: mix('yH'), xB: mix('xB') };
                if (trace.blend.isEndAMoved) {
                    shown.ax = mix('ax');
                    shown.ay = mix('ay');
                }

                if (trace.blend.isEndBMoved) {
                    shown.bx = mix('bx');
                    shown.by = mix('by');
                }
            }
        }

        trace.shown = shown;
        const { ax, ay, xA, yH, xB, by, bx } = shown;
        const d = `M${Math.round(ax)} ${snap(ay)}H${snap(xA)}V${snap(yH)}H${snap(xB)}V${snap(by)}H${Math.round(bx)}`;
        if (d !== trace.d) {
            trace.d = d;
            trace.halo.setAttribute('d', d);
            trace.wire.setAttribute('d', d);
            trace.pulse.setAttribute('d', d);
            trace.portA.setAttribute('transform', `translate(${Math.round(ax) - (trace.sideA === RIGHT ? 0.5 : -0.5)} ${snap(ay)})`);
            trace.portB.setAttribute('transform', `translate(${Math.round(bx) - (trace.sideB === RIGHT ? 0.5 : -0.5)} ${snap(by)})`);
            const isShort = Math.abs(bx - ax) + Math.abs(by - ay) < SHORT_LINK;
            if (isShort !== trace.isShort) {
                trace.isShort = isShort;
                const pad = isShort ? SHORT_LINK_PAD : PORT_PAD;
                for (const end of [trace.portA, trace.portB]) {
                    end.firstChild.setAttribute('d', `M0 ${-pad}V${pad}`);
                }
            }
        }
    }

    function setPulse(trace) {
        const { ax, ay, xA, yH, xB, by, bx } = trace.target;
        const length = Math.round(Math.abs(xA - ax) + Math.abs(yH - ay) + Math.abs(xB - xA) + Math.abs(by - yH) + Math.abs(bx - xB));
        if (length !== trace.length) {
            trace.length = length;
            const travel = Math.max(length / PULSE_PX_PER_SECOND, MIN_TRAVEL_SECONDS) / PULSE_MOVING_SHARE;
            trace.pulse.style.setProperty('--end', -length);
            trace.pulse.style.setProperty('--travel', `${travel.toFixed(2)}s`);
        }
    }

    function signature(cards) {
        const parts = [];
        for (const trace of traces.values()) {
            for (const card of [cards.get(trace.a), cards.get(trace.b)]) {
                parts.push(card ? `${card.offsetLeft},${card.offsetTop},${card.style.translate},${card.offsetWidth},${card.offsetHeight}` : '-');
            }
        }

        return parts.join(';');
    }

    function reflow() {
        if (traces.size === 0 || frame) {
            return;
        }

        const cards = cardsByKey();
        const next = signature(cards);
        if (next === stamp) {
            ticks++;
            if (ticks >= LABEL_RECHECK_TICKS) {
                ticks = 0;
                placeLabels(cards);
            }
            return;
        }

        stamp = next;
        ticks = 0;
        const placed = plan(cards);
        const now = performance.now();
        let isBlending = false;
        for (const trace of placed) {
            draw(trace, now);
            setPulse(trace);
            isBlending ||= trace.blend !== null;
            trace.spot = null;
        }

        if (isBlending) {
            schedule();
        } else {
            placeLabels(cards);
        }
    }

    function track(card) {
        if (card.dataset.flip && [...traces.values()].some(t => t.a === card.dataset.flip || t.b === card.dataset.flip)) {
            schedule();
        }
    }

    function schedule() {
        if (!frame) {
            frame = requestAnimationFrame(step);
        }
    }

    function step(now) {
        frame = 0;
        const cards = cardsByKey();
        const placed = plan(cards);
        let isBusy = false;
        for (const trace of placed) {
            const isMoving = isMovingCard(trace.cardA) || isMovingCard(trace.cardB);
            draw(trace, now);
            isBusy ||= isMoving || trace.blend !== null;
        }

        under.classList.toggle('bt-moving', isBusy);
        if (isBusy) {
            schedule();
            return;
        }

        for (const trace of placed) {
            setPulse(trace);
            trace.spot = null;
        }

        stamp = signature(cards);
        placeLabels(cards);
    }

    function boxInWrap(el) {
        const box = el.getBoundingClientRect();
        const origin = wrap.getBoundingClientRect();
        const zoom = window.canvas?.zoomOf() ?? 1;
        const left = (box.left - origin.left) / zoom;
        const top = (box.top - origin.top) / zoom;
        return { left, top, right: left + box.width / zoom, bottom: top + box.height / zoom };
    }

    // What a label must not sit on, read only when a label is placed or rechecked: the layout is already clean by then.
    function obstacles(cards) {
        const boxes = [];
        for (const card of cards.values()) {
            const slot = slotOf(card);
            boxes.push({ left: slot.left + 1, top: slot.top + 1, right: slot.right - 1, bottom: slot.bottom - 1 });
            for (const el of card.querySelectorAll(CHROME)) {
                boxes.push(boxInWrap(el));
            }
        }

        return boxes;
    }

    function hits(box, boxes) {
        return boxes.some(o => box.left < o.right && o.left < box.right && box.top < o.bottom && o.top < box.bottom);
    }

    // The widest stretch of [from, to] that the boxes leave clear, along one axis.
    function widestGap(from, to, blocks) {
        let best = null;
        let cursor = from;
        for (const [start, end] of blocks.slice().sort((x, y) => x[0] - y[0])) {
            const gapEnd = Math.min(start, to);
            if (gapEnd > cursor && (!best || gapEnd - cursor > best.to - best.from)) {
                best = { from: cursor, to: gapEnd };
            }

            cursor = Math.max(cursor, end);
            if (cursor >= to) {
                break;
            }
        }

        if (cursor < to && (!best || to - cursor > best.to - best.from)) {
            best = { from: cursor, to };
        }

        return best;
    }

    function verticalRuns(trace) {
        const { ay, xA, yH, xB, by } = trace.target;
        const runs = Math.abs(xA - xB) < 1
            ? [{ x: xA, from: Math.min(ay, by), to: Math.max(ay, by) }]
            : [{ x: xA, from: Math.min(ay, yH), to: Math.max(ay, yH) }, { x: xB, from: Math.min(by, yH), to: Math.max(by, yH) }];
        return runs.filter(run => run.to - run.from > 2 * LABEL_MARGIN);
    }

    function spotBox(spot) {
        const halfLength = spot.length / 2;
        const halfHeight = LABEL_HEIGHT / 2;
        return spot.isUpright
            ? { left: spot.x - halfLength, right: spot.x + halfLength, top: spot.y - halfHeight, bottom: spot.y + halfHeight }
            : { left: spot.x - halfHeight + 2, right: spot.x + halfHeight - 2, top: spot.y - halfLength, bottom: spot.y + halfLength };
    }

    function findSpot(trace, boxes) {
        const { ax, ay, xA, xB, by, bx } = trace.target;
        const name = trace.channel;
        const whole = trace.textWidth + 2 * LABEL_PADDING;
        const stretches = [{ from: Math.min(ax, xA), to: Math.max(ax, xA), y: ay }, { from: Math.min(bx, xB), to: Math.max(bx, xB), y: by }];
        stretches.sort((p, q) => (q.to - q.from) - (p.to - p.from));
        for (const run of stretches) {
            const near = boxes.filter(o => run.y - LABEL_HEIGHT / 2 < o.bottom && o.top < run.y + LABEL_HEIGHT / 2);
            const gap = widestGap(run.from + LABEL_MARGIN, run.to - LABEL_MARGIN, near.map(o => [o.left, o.right]));
            if (gap && gap.to - gap.from >= whole) {
                return { x: (gap.from + gap.to) / 2, y: run.y, isUpright: true, length: whole, text: name };
            }
        }

        const runs = verticalRuns(trace);
        if (trace.isShort) {
            return besideShortLink(trace, boxes, whole);
        }

        let best = null;
        for (const run of runs) {
            const near = boxes.filter(o => run.x - LABEL_HEIGHT / 2 + 2 < o.right && o.left < run.x + LABEL_HEIGHT / 2 - 2);
            const gap = widestGap(run.from + LABEL_MARGIN, run.to - LABEL_MARGIN, near.map(o => [o.top, o.bottom]));
            if (gap && (!best || gap.to - gap.from > best.gap.to - best.gap.from)) {
                best = { x: run.x, gap };
            }
        }

        if (!best) {
            return null;
        }

        const room = best.gap.to - best.gap.from;
        const charWidth = trace.textWidth / name.length;
        const chars = room >= whole ? name.length : Math.floor((room - 2 * LABEL_PADDING) / charWidth);
        if (chars < LABEL_MIN_CHARS) {
            return null;
        }

        const text = chars === name.length ? name : name.slice(0, chars - 1) + '…';
        return { x: best.x, y: (best.gap.from + best.gap.to) / 2, isUpright: false, length: Math.min(whole, chars * charWidth + 2 * LABEL_PADDING), text };
    }

    // No wire long enough to sit on, so the name stands in the gap directly above the bracket.
    function besideShortLink(trace, boxes, whole) {
        const { ay, by, xA } = trace.target;
        const near = boxes.filter(o => xA - LABEL_HEIGHT / 2 + 2 < o.right && o.left < xA + LABEL_HEIGHT / 2 - 2);
        const bottom = Math.min(ay, by) - SHORT_LINK_PAD - LABEL_PADDING;
        const gap = widestGap(Math.min(trace.slotA.top, trace.slotB.top), bottom, near.map(o => [o.top, o.bottom]));
        if (!gap || gap.to !== bottom || gap.to - gap.from < whole) {
            return null;
        }

        return { x: xA, y: bottom - whole / 2, isUpright: false, length: whole, text: trace.channel };
    }

    function placeLabels(cards) {
        const boxes = obstacles(cards);
        const named = new Set();
        const ordered = [...traces.values()].filter(t => t.isPlaced).sort((x, y) => (x.id < y.id ? -1 : 1));
        for (const trace of ordered) {
            if (trace.spot && !named.has(trace.channel) && !hits(spotBox(trace.spot), boxes)) {
                boxes.push(spotBox(trace.spot));
                named.add(trace.channel);
            } else if (trace.spot) {
                trace.spot = null;
            }
        }

        for (const trace of ordered) {
            if (trace.spot) {
                continue;
            }

            const spot = named.has(trace.channel) ? null : findSpot(trace, boxes);
            trace.spot = spot;
            trace.label.classList.toggle('bt-placed', spot !== null);
            if (!spot) {
                continue;
            }

            named.add(trace.channel);
            boxes.push(spotBox(spot));
            const halfPlate = spot.length / 2;
            trace.plate.setAttribute('x', -halfPlate);
            trace.plate.setAttribute('width', spot.length);
            trace.text.textContent = spot.text;
            trace.label.setAttribute('transform', `translate(${snap(spot.x)} ${snap(spot.y)})${spot.isUpright ? '' : ' rotate(-90)'}`);
        }
    }

    return { update, reflow, track };
})();
