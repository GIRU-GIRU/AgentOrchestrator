// The canvas under the cards. The page owns the view and every drag, because a round trip per pointer move would crawl;
// .NET is told where things landed and renders that. Blazor never writes a style on #canvas or #canvas-world.
window.canvas = (() => {
    const MIN_ZOOM = 0.2;
    const MAX_ZOOM = 1.75;
    const FIT_MAX_ZOOM = 1;
    const WHEEL_RATE = 0.0016;
    const DRAG_THRESHOLD = 6;
    const FIT_PADDING = 48;
    // A category's name stands above its box, at its own size however far out the view is.
    const FIT_TOP_PADDING = 96;
    const REVEAL_MARGIN = 40;
    const REPORT_MS = 300;
    const EASE_MS = 340;
    const MIN_BOX_WIDTH = 220;
    const MIN_BOX_HEIGHT = 140;
    const DOT_SPACING = 30;
    // Below this the canvas is an overview: category boxes shout and the strip above them is the thing to read.
    const FAR_ZOOM = 0.62;
    // A layer scaled without re-rasterising is what makes a gesture smooth; dropping it this long after the last step is what brings the text back sharp.
    const SETTLE_MS = 160;
    const NOT_DRAGGABLE = '.corner, button, a, input, textarea, .rename-box, .canvas-dock';

    let dotnet = null;
    let root = null;
    let world = null;
    let panX = 0;
    let panY = 0;
    let zoom = 1;
    let action = null;
    let swallowClick = false;
    let reportTimer = 0;
    let easeTimer = 0;
    let settleTimer = 0;
    let frame = 0;
    let dragFrame = 0;
    let dots = null;
    let shownZoom = 0;
    let zoomRule = null;

    function init(reference, view) {
        dotnet = reference;
        root = document.getElementById('canvas');
        world = document.getElementById('canvas-world');
        dots = document.getElementById('canvas-dots');
        // A rule of its own, so a zoom step recalculates the boxes and each card's far mark, X and launch ring, not every node in every card under a shared variable.
        const sheet = document.createElement('style');
        sheet.textContent = '.cat-box, .far-mark, .xmark, .launch-portal { --z: 1; }';
        document.head.appendChild(sheet);
        zoomRule = sheet.sheet.cssRules[0];
        if (view?.hasView) {
            panX = view.panX;
            panY = view.panY;
            zoom = clampZoom(view.zoom);
        }

        apply();
        root.addEventListener('pointerdown', onDown);
        root.addEventListener('wheel', onWheel, { passive: false });
        document.addEventListener('pointermove', onMove);
        document.addEventListener('pointerup', onUp);
        document.addEventListener('pointercancel', onUp);
        document.addEventListener('keydown', onKeyDown, true);
        window.addEventListener('click', e => {
            if (swallowClick) {
                swallowClick = false;
                e.stopImmediatePropagation();
                e.preventDefault();
            }
        }, true);
        new ResizeObserver(report).observe(root);
    }

    function clampZoom(value) {
        return Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, value || 1));
    }

    // Whole pixels, or text at rest lands between them and goes soft.
    function apply() {
        const x = Math.round(panX);
        const y = Math.round(panY);
        world.style.transform = `translate(${x}px, ${y}px) scale(${zoom})`;
        const spacing = DOT_SPACING * zoom;
        dots.style.transform = `translate(${x % spacing}px, ${y % spacing}px)`;
        if (zoom !== shownZoom) {
            shownZoom = zoom;
            dots.style.backgroundSize = `${spacing}px ${spacing}px`;
            zoomRule.style.setProperty('--z', zoom);
            root.toggleAttribute('data-far', zoom < FAR_ZOOM);
        }
    }

    // A burst of pointer or wheel events costs one write per frame.
    function schedule() {
        if (!frame) {
            frame = requestAnimationFrame(() => {
                frame = 0;
                apply();
            });
        }
    }

    function moving() {
        world.classList.add('canvas-moving');
        clearTimeout(settleTimer);
        settleTimer = setTimeout(() => world.classList.remove('canvas-moving'), SETTLE_MS);
    }

    function report() {
        clearTimeout(reportTimer);
        reportTimer = setTimeout(() => dotnet?.invokeMethodAsync('CanvasViewJs', panX, panY, zoom, root.clientWidth, root.clientHeight), REPORT_MS);
    }

    function ease() {
        world.classList.add('canvas-easing');
        clearTimeout(easeTimer);
        easeTimer = setTimeout(() => world.classList.remove('canvas-easing'), EASE_MS);
    }

    function setBox(el, left, top) {
        el.style.left = `${Math.round(left)}px`;
        el.style.top = `${Math.round(top)}px`;
    }

    // A drag moves a layer and never touches layout: a translate per frame while it lasts, and the real position once, on drop.
    function shift(el, dx, dy) {
        el.style.translate = `${dx.toFixed(2)}px ${dy.toFixed(2)}px`;
    }

    function settle(el, left, top) {
        el.style.translate = '';
        setBox(el, left, top);
        window.bridges?.track(el);
    }

    // A fast mouse sends several moves a frame; only the last one is drawn.
    function scheduleDrag() {
        if (!dragFrame) {
            dragFrame = requestAnimationFrame(() => {
                dragFrame = 0;
                reapply();
            });
        }
    }

    function cards() {
        return [...world.querySelectorAll(':scope > .card')];
    }

    function centreIn(card, box) {
        const x = card.offsetLeft + card.offsetWidth / 2;
        const y = card.offsetTop + card.offsetHeight / 2;
        return x >= box.offsetLeft && x <= box.offsetLeft + box.offsetWidth && y >= box.offsetTop && y <= box.offsetTop + box.offsetHeight;
    }

    function start(kind, e, el, extra) {
        action = { kind, el, pointerId: e.pointerId, startX: e.clientX, startY: e.clientY, isActive: false, ...extra };
    }

    function onDown(e) {
        if (action?.isActive) {
            finish(false);
        }

        action = null;
        if (e.button === 1) {
            e.preventDefault();
            start('pan', e, root, { fromX: panX, fromY: panY });
            return;
        }

        const target = e.target;
        if (e.button !== 0 || e.ctrlKey || target.closest(NOT_DRAGGABLE)) {
            return;
        }

        const handle = target.closest('.cat-resize');
        if (handle) {
            const box = handle.parentElement;
            start('resize', e, box, { fromWidth: box.offsetWidth, fromHeight: box.offsetHeight });
            return;
        }

        const head = target.closest('.cat-head');
        if (head) {
            const box = head.parentElement;
            start('box', e, box, { fromLeft: box.offsetLeft, fromTop: box.offsetTop });
            return;
        }

        const card = target.closest('.canvas-world > .card');
        if (card) {
            start('card', e, card, { key: card.dataset.flip, fromLeft: card.offsetLeft, fromTop: card.offsetTop });
            return;
        }

        start('pan', e, root, { fromX: panX, fromY: panY });
    }

    function activate() {
        action.isActive = true;
        document.body.classList.add('dragging-card');
        try {
            action.el.setPointerCapture(action.pointerId);
        } catch {
        }

        if (action.kind === 'card') {
            action.el.dataset.dragging = '1';
        } else if (action.kind === 'box') {
            action.carried = cards().filter(card => centreIn(card, action.el)).map(card => ({ card, fromLeft: card.offsetLeft, fromTop: card.offsetTop }));
            action.el.classList.add('canvas-carried');
            action.carried.forEach(item => item.card.classList.add('canvas-carried'));
        } else if (action.kind === 'pan') {
            root.classList.add('canvas-panning');
        }
    }

    function onMove(e) {
        if (!action || e.pointerId !== action.pointerId) {
            return;
        }

        const dx = e.clientX - action.startX;
        const dy = e.clientY - action.startY;
        if (!action.isActive) {
            if (Math.hypot(dx, dy) < DRAG_THRESHOLD) {
                return;
            }

            activate();
        }

        action.worldDx = dx / zoom;
        action.worldDy = dy / zoom;
        if (action.kind === 'pan') {
            panX = action.fromX + dx;
            panY = action.fromY + dy;
            moving();
            schedule();
            return;
        }

        scheduleDrag();
    }

    // Blazor rewrites a card's whole style attribute when any part of it changes, so a drag in progress is put back.
    function reapply() {
        if (!action?.isActive) {
            return;
        }

        const { kind, el, worldDx: dx, worldDy: dy } = action;
        if (kind === 'card') {
            shift(el, dx, dy);
            window.bridges?.track(el);
        } else if (kind === 'box') {
            shift(el, dx, dy);
            for (const item of action.carried) {
                shift(item.card, dx, dy);
                window.bridges?.track(item.card);
            }
        } else if (kind === 'resize') {
            el.style.width = `${Math.max(MIN_BOX_WIDTH, Math.round(action.fromWidth + dx))}px`;
            el.style.height = `${Math.max(MIN_BOX_HEIGHT, Math.round(action.fromHeight + dy))}px`;
        }
    }

    function onUp(e) {
        if (action && e.pointerId === action.pointerId) {
            finish(false);
        }
    }

    function onKeyDown(e) {
        if (e.key === 'Escape' && action?.isActive) {
            e.preventDefault();
            e.stopImmediatePropagation();
            finish(true);
        }
    }

    function finish(isCancel) {
        const done = action;
        action = null;
        if (!done?.isActive) {
            return;
        }

        cancelAnimationFrame(dragFrame);
        dragFrame = 0;
        document.body.classList.remove('dragging-card');
        root.classList.remove('canvas-panning');
        delete done.el.dataset.dragging;
        done.el.classList.remove('canvas-carried');
        (done.carried ?? []).forEach(item => item.card.classList.remove('canvas-carried'));
        swallowClick = true;
        setTimeout(() => { swallowClick = false; }, 300);
        if (done.kind === 'pan') {
            if (isCancel) {
                panX = done.fromX;
                panY = done.fromY;
                apply();
            }

            report();
            return;
        }

        if (isCancel) {
            undo(done);
            return;
        }

        commit(done);
        send(done);
    }

    function undo(done) {
        if (done.kind === 'resize') {
            done.el.style.width = `${done.fromWidth}px`;
            done.el.style.height = `${done.fromHeight}px`;
            return;
        }

        settle(done.el, done.fromLeft, done.fromTop);
        for (const item of done.carried ?? []) {
            settle(item.card, item.fromLeft, item.fromTop);
        }
    }

    function commit(done) {
        if (done.kind !== 'card' && done.kind !== 'box') {
            return;
        }

        settle(done.el, done.fromLeft + done.worldDx, done.fromTop + done.worldDy);
        for (const item of done.carried ?? []) {
            settle(item.card, item.fromLeft + done.worldDx, item.fromTop + done.worldDy);
        }
    }

    function moveOf(card) {
        return { key: card.dataset.flip, x: card.offsetLeft, y: card.offsetTop };
    }

    function send(done) {
        const id = done.el.dataset.cat;
        if (done.kind === 'card') {
            dotnet?.invokeMethodAsync('CanvasCardMovedJs', moveOf(done.el));
        } else if (done.kind === 'box') {
            dotnet?.invokeMethodAsync('CanvasBoxMovedJs', id, done.el.offsetLeft, done.el.offsetTop, done.carried.map(item => moveOf(item.card)));
        } else if (done.kind === 'resize') {
            dotnet?.invokeMethodAsync('CanvasBoxResizedJs', id, done.el.offsetWidth, done.el.offsetHeight);
        }
    }

    function onWheel(e) {
        if (e.ctrlKey) {
            return;
        }

        e.preventDefault();
        const bounds = root.getBoundingClientRect();
        const pointerX = e.clientX - bounds.left;
        const pointerY = e.clientY - bounds.top;
        const next = clampZoom(zoom * Math.exp(-e.deltaY * WHEEL_RATE));
        panX = pointerX - (pointerX - panX) * (next / zoom);
        panY = pointerY - (pointerY - panY) * (next / zoom);
        zoom = next;
        moving();
        schedule();
        report();
    }

    function worldAt(clientX, clientY) {
        const bounds = root.getBoundingClientRect();
        return { x: (clientX - bounds.left - panX) / zoom, y: (clientY - bounds.top - panY) / zoom };
    }

    // Space: everything in view, never zoomed in past life size however few cards there are.
    function fit(isInstant) {
        const items = world.querySelectorAll(':scope > .card, :scope > .cat-box');
        if (items.length === 0) {
            return;
        }

        let left = Infinity;
        let top = Infinity;
        let right = -Infinity;
        let bottom = -Infinity;
        for (const el of items) {
            left = Math.min(left, el.offsetLeft);
            top = Math.min(top, el.offsetTop);
            right = Math.max(right, el.offsetLeft + el.offsetWidth);
            bottom = Math.max(bottom, el.offsetTop + el.offsetHeight);
        }

        // The room right of the dock, so what Space fits is never left underneath it.
        const dock = document.getElementById('canvas-dock');
        const dockRight = dock ? dock.offsetLeft + dock.offsetWidth : 0;
        const width = Math.max(1, root.clientWidth - dockRight);
        const roomX = Math.max(1, width - 2 * FIT_PADDING);
        const roomY = Math.max(1, root.clientHeight - FIT_TOP_PADDING - FIT_PADDING);
        zoom = Math.min(FIT_MAX_ZOOM, clampZoom(Math.min(roomX / (right - left), roomY / (bottom - top))));
        panX = dockRight + (width - (right - left) * zoom) / 2 - left * zoom;
        panY = FIT_TOP_PADDING + (roomY - (bottom - top) * zoom) / 2 - top * zoom;
        if (!isInstant) {
            ease();
        }

        apply();
        report();
    }

    // An arrow key can select a card off-screen; only move as far as it takes to see it.
    function reveal(key) {
        const card = cards().find(el => el.dataset.flip === key);
        if (!card) {
            return;
        }

        const view = root.getBoundingClientRect();
        const box = card.getBoundingClientRect();
        const moveX = box.left < view.left + REVEAL_MARGIN ? view.left + REVEAL_MARGIN - box.left : box.right > view.right - REVEAL_MARGIN ? view.right - REVEAL_MARGIN - box.right : 0;
        const moveY = box.top < view.top + REVEAL_MARGIN ? view.top + REVEAL_MARGIN - box.top : box.bottom > view.bottom - REVEAL_MARGIN ? view.bottom - REVEAL_MARGIN - box.bottom : 0;
        if (moveX !== 0 || moveY !== 0) {
            panX += moveX;
            panY += moveY;
            ease();
            apply();
            report();
        }
    }

    return { init, fit, reveal, worldAt, reapply, zoomOf: () => zoom };
})();
