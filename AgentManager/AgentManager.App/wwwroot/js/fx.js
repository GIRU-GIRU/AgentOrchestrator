window.fx = (() => {
    const reduceMotion = matchMedia('(prefers-reduced-motion: reduce)').matches;
    const NOISE = '█▓▒░<>/\\|=+*#%$&!?';
    const KEYS = ['Escape', 'ArrowRight', 'ArrowLeft', 'ArrowDown', 'ArrowUp', 'Enter', 'z', ' ', 'm', 'r', 'p', 'Delete', 'c', 't', 'a', 'b', 'B', 'F2'];
    const ORIGIN_OPEN_MS = 260;
    const ORIGIN_CLOSE_MS = 220;
    const ORIGIN_GAP = 3;
    const ORIGIN_MARGIN = 8;
    let originAnchor = null;
    let audio = null;
    let lastPointer = null;
    // A looping CSS animation makes the page draw at the display's full rate for ever, which keeps the CPU busy at rest, so looping ones are held and stepped from a timer. One-shot animations and transitions stay smooth.
    const LOOP_STEP_MS = 50;
    const loopStarts = new WeakMap();
    // The Master Agent's crackle. A SMIL animate on this seed is costly whatever its rate: it services every display frame and re-runs the noise filter each time.
    const ELECTRIC_STEPS = 12;
    const ELECTRIC_EVERY_LOOP_STEPS = 3;
    let loopStep = 0;
    // Uncapped (App.xaml.cs), requestAnimationFrame runs as fast as the machine can; rolling text needs no more than this.
    const TEXT_FRAME_MS = 1000 / 60;
    const scrambles = new WeakMap();
    const counts = new WeakMap();
    let dotnet = null;

    function init(reference) {
        dotnet = reference;
        window.addEventListener('error', e => report(`${e.message} at ${e.filename}:${e.lineno}:${e.colno}`));
        window.addEventListener('unhandledrejection', e => report(`unhandled rejection: ${e.reason?.stack ?? e.reason}`));
        document.addEventListener('keydown', onKey);
        new MutationObserver(onMutations).observe(document.body, {
            subtree: true, childList: true, attributes: true, attributeFilter: ['data-text', 'data-value', 'style'],
        });
        document.querySelectorAll('.scr').forEach(el => scramble(el, true));
        const unlockAudio = () => {
            audio ??= new AudioContext();
            audio.resume?.();
        };
        document.addEventListener('pointerdown', unlockAudio, { once: true });
        document.addEventListener('keydown', unlockAudio, { once: true });
        document.addEventListener('pointerdown', e => {
            lastPointer = { x: e.clientX, y: e.clientY };
        });
        document.querySelectorAll('.num').forEach(el => count(el, true));
        setInterval(stepLoops, LOOP_STEP_MS);
    }

    function stepElectric() {
        const noise = document.getElementById('master-electric-noise');
        noise?.setAttribute('seed', String(loopStep / ELECTRIC_EVERY_LOOP_STEPS % ELECTRIC_STEPS + 1));
    }

    function stepLoops() {
        loopStep++;
        if (loopStep % ELECTRIC_EVERY_LOOP_STEPS === 0) {
            stepElectric();
        }

        if (document.hidden) {
            return;
        }

        // Every read before the first write: a read after a write recalculated the page's style once per animation.
        const now = performance.now();
        const loops = [];
        const fresh = [];
        for (const animation of document.getAnimations()) {
            if (loopStarts.has(animation)) {
                loops.push(animation);
            } else if (animation.playState !== 'idle' && animation.effect?.getComputedTiming().iterations === Infinity) {
                loopStarts.set(animation, now - (animation.currentTime ?? 0));
                loops.push(animation);
                fresh.push(animation);
            }
        }

        fresh.forEach(animation => animation.pause());
        for (const animation of loops) {
            animation.currentTime = now - loopStarts.get(animation);
        }
    }

    function nextTextFrame(frame) {
        setTimeout(() => frame(performance.now()), TEXT_FRAME_MS);
    }

    function report(message) {
        try {
            dotnet?.invokeMethodAsync('OnJsError', String(message));
        } catch {
        }
    }

    function onKey(e) {
        const target = e.target;
        const isTyping = target && (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || target.isContentEditable);
        if (isTyping) {
            if (e.key === 'Escape') {
                target.blur();
            }
            return;
        }

        if (e.key === '/') {
            e.preventDefault();
            document.getElementById('filter')?.focus();
            return;
        }

        if (e.ctrlKey && !e.altKey && !e.metaKey && !e.shiftKey && e.key.toLowerCase() === 'z') {
            e.preventDefault();
            dotnet?.invokeMethodAsync('UndoRemovalJs');
            return;
        }

        if (!KEYS.includes(e.key) || e.ctrlKey || e.altKey || e.metaKey) {
            return;
        }

        e.preventDefault();
        dotnet?.invokeMethodAsync('OnKey', e.key);
    }

    // Blazor rewrites a card's whole style attribute, so a drag in progress is put back, and the wires follow any change in size.
    function onMutations(records) {
        let isReflow = false;
        for (const record of records) {
            if (record.type === 'attributes' && record.attributeName === 'style') {
                isReflow ||= record.target.classList.contains('card');
                continue;
            }

            if (record.type === 'attributes') {
                if (record.target.classList.contains('scr')) {
                    scramble(record.target, false);
                } else if (record.target.classList.contains('num')) {
                    count(record.target, false);
                }
                continue;
            }

            isReflow ||= record.target.id === 'canvas-world';
            for (const node of record.addedNodes) {
                if (node.nodeType !== 1) {
                    continue;
                }
                if (node.classList.contains('scr')) {
                    scramble(node, true);
                }
                if (node.classList.contains('num')) {
                    count(node, true);
                }
                node.querySelectorAll?.('.scr').forEach(el => scramble(el, true));
                node.querySelectorAll?.('.num').forEach(el => count(el, true));
            }
        }

        if (isReflow) {
            flip();
        }
    }

    // Text that decodes into place, left to right. Blazor renders only data-text; this owns the element's text.
    function scramble(el, isFirst) {
        const text = el.dataset.text ?? '';
        const previous = el.dataset.shown ?? '';
        if (previous === text && el.textContent === text) {
            return;
        }

        el.dataset.shown = text;
        if (reduceMotion) {
            el.textContent = text;
            return;
        }

        const length = Math.max(previous.length, text.length);
        const duration = isFirst ? 700 : 450;
        const start = performance.now();
        const token = {};
        scrambles.set(el, token);
        const frame = now => {
            if (scrambles.get(el) !== token) {
                return;
            }

            const progress = Math.min(1, (now - start) / duration);
            let out = '';
            for (let i = 0; i < length; i++) {
                const settleAt = 0.25 + (i / length) * 0.75;
                if (progress >= settleAt) {
                    out += text[i] ?? '';
                } else if (progress >= settleAt - 0.4) {
                    out += text[i] === ' ' ? ' ' : NOISE[(Math.random() * NOISE.length) | 0];
                } else {
                    out += isFirst ? '' : (previous[i] ?? '');
                }
            }

            el.textContent = out;
            if (progress < 1) {
                nextTextFrame(frame);
            } else {
                el.textContent = text;
            }
        };
        nextTextFrame(frame);
    }

    // Numbers that roll to their new value.
    function count(el, isFirst) {
        const target = Number(el.dataset.value ?? 0);
        const from = isFirst ? 0 : (counts.get(el)?.value ?? target);
        if (from === target && !isFirst && el.textContent) {
            return;
        }

        const state = { value: target };
        counts.set(el, state);
        if (reduceMotion || from === target) {
            el.textContent = tokens(target);
            return;
        }

        const start = performance.now();
        const duration = isFirst ? 900 : 600;
        const frame = now => {
            if (counts.get(el) !== state) {
                return;
            }

            const progress = Math.min(1, (now - start) / duration);
            const eased = 1 - Math.pow(1 - progress, 3);
            el.textContent = tokens(Math.round(from + (target - from) * eased));
            if (progress < 1) {
                nextTextFrame(frame);
            }
        };
        nextTextFrame(frame);
    }

    function tokens(value) {
        if (value <= 0) return '—';
        if (value < 1e3) return String(value);
        if (value < 1e6) return trim((value / 1e3).toFixed(1)) + 'k';
        if (value < 1e9) return trim((value / 1e6).toFixed(2)) + 'M';
        return trim((value / 1e9).toFixed(2)) + 'B';
    }

    function trim(text) {
        return text.includes('.') ? text.replace(/0+$/, '').replace(/\.$/, '') : text;
    }

    // Opening an agent: a ripple from the click, a press and pop, and the outline opening out like a portal.
    function launch(key) {
        const card = cardOf(key);
        if (!card || reduceMotion) {
            return;
        }

        const box = card.getBoundingClientRect();
        const inside = lastPointer && lastPointer.x >= box.left && lastPointer.x <= box.right && lastPointer.y >= box.top && lastPointer.y <= box.bottom;
        const x = inside ? lastPointer.x - box.left : box.width / 2;
        const y = inside ? lastPointer.y - box.top : box.height / 2;
        const size = Math.hypot(Math.max(x, box.width - x), Math.max(y, box.height - y)) * 2;

        const clip = document.createElement('div');
        clip.className = 'launch-clip';
        const ring = document.createElement('div');
        ring.className = 'launch-ring';
        ring.style.left = x + 'px';
        ring.style.top = y + 'px';
        ring.style.width = ring.style.height = size + 'px';
        clip.appendChild(ring);
        const portal = document.createElement('div');
        portal.className = 'launch-portal';
        card.append(clip, portal);

        card.classList.remove('launching');
        void card.offsetWidth;
        card.classList.add('launching');
        setTimeout(() => {
            clip.remove();
            portal.remove();
            card.classList.remove('launching');
        }, 750);
    }

    /** The page drops a closed card in one frame, so a copy of it plays the collapse where it stood. */
    function vanish(key) {
        const card = document.querySelector(`[data-flip="${CSS.escape(key)}"]`);
        if (!card) {
            return;
        }

        const rect = card.getBoundingClientRect();
        const holder = document.createElement('div');
        holder.className = 'card-ghost-holder';
        holder.style.left = `${rect.left}px`;
        holder.style.top = `${rect.top}px`;
        holder.style.transform = `scale(${card.closest('.canvas-dock') ? 1 : window.canvas?.zoomOf() ?? 1})`;
        const ghost = card.cloneNode(true);
        ghost.removeAttribute('data-flip');
        ghost.classList.add('card-ghost');
        ghost.style.cssText = `width:${card.offsetWidth}px; height:${card.offsetHeight}px; --gold:${card.style.getPropertyValue('--gold')}`;
        holder.appendChild(ghost);
        document.body.appendChild(holder);
        ghost.addEventListener('animationend', event => {
            if (event.target === ghost) {
                holder.remove();
            }
        });
    }

    function shake(key) {
        const card = cardOf(key);
        if (!card || reduceMotion) {
            return;
        }

        card.classList.remove('shaking');
        void card.offsetWidth;
        card.classList.add('shaking');
        setTimeout(() => card.classList.remove('shaking'), 450);
    }

    function cardOf(key) {
        return [...document.querySelectorAll('.card')].find(c => c.dataset.flip === key);
    }

    // Two soft notes made here, no sound files: rising when an agent waits on the user, falling when it is done.
    // The page may only start audio after the user's first click or key press in it (unlockAudio).
    function sound(kind) {
        try {
            audio ??= new AudioContext();
            audio.resume?.();
            const notes = kind === 'await' ? [587.33, 783.99] : [659.25, 523.25];
            const type = kind === 'await' ? 'sine' : 'triangle';
            const now = audio.currentTime;
            notes.forEach((frequency, i) => {
                const start = now + i * 0.13;
                const oscillator = audio.createOscillator();
                const gain = audio.createGain();
                oscillator.type = type;
                oscillator.frequency.value = frequency;
                gain.gain.setValueAtTime(0, start);
                gain.gain.linearRampToValueAtTime(0.045, start + 0.02);
                gain.gain.exponentialRampToValueAtTime(0.0001, start + (kind === 'await' ? 0.35 : 0.6));
                oscillator.connect(gain).connect(audio.destination);
                oscillator.start(start);
                oscillator.stop(start + 0.7);
            });
        } catch (error) {
            report('sound: ' + error);
        }
    }

    function noteEditor(id) {
        const editor = document.getElementById(id);
        if (!editor) {
            return;
        }

        editor.focus();
        if (editor.dataset.bound) {
            return;
        }

        editor.dataset.bound = '1';
        editor.addEventListener('keydown', e => {
            if (!e.ctrlKey || e.altKey) {
                return;
            }

            const kind = { b: 'bold', i: 'italic' }[e.key.toLowerCase()];
            if (kind) {
                e.preventDefault();
                format(id, kind);
            }
        });
    }

    // Very basic Markdown formatting: wraps the selection, or toggles a prefix on the selected lines.
    function format(id, kind) {
        const el = document.getElementById(id);
        if (!el) {
            return;
        }

        const start = el.selectionStart;
        const end = el.selectionEnd;
        const value = el.value;
        const wraps = { bold: ['**', '**'], italic: ['_', '_'], strike: ['~~', '~~'], code: ['`', '`'] };
        if (wraps[kind]) {
            const [left, right] = wraps[kind];
            const selected = value.slice(start, end);
            const isWrapped = selected.startsWith(left) && selected.endsWith(right) && selected.length >= left.length + right.length;
            const body = isWrapped ? selected.slice(left.length, selected.length - right.length) : (selected || kind);
            const replacement = isWrapped ? body : left + body + right;
            el.setRangeText(replacement, start, end, 'select');
            if (!isWrapped && !selected) {
                el.setSelectionRange(start + left.length, start + left.length + body.length);
            }
        } else {
            const lineStart = value.lastIndexOf('\n', start - 1) + 1;
            const nextBreak = value.indexOf('\n', end);
            const lineEnd = nextBreak < 0 ? value.length : nextBreak;
            const lines = value.slice(lineStart, lineEnd).split('\n');
            const prefix = { heading: '# ', list: '- ', check: '- [ ] ' }[kind];
            const out = lines.map(line => {
                if (kind === 'check') {
                    if (line.startsWith('- [ ] ')) return '- [x] ' + line.slice(6);
                    if (line.startsWith('- [x] ')) return line.slice(6);
                    return '- [ ] ' + line.replace(/^- /, '');
                }
                return line.startsWith(prefix) ? line.slice(prefix.length) : prefix + line;
            }).join('\n');
            el.setRangeText(out, lineStart, lineEnd, 'end');
        }

        el.dispatchEvent(new Event('input', { bubbles: true }));
        el.focus();
    }

    function flip() {
        window.canvas?.reapply();
        window.bridges?.reflow();
    }

    function chatStick(el, isFirst) {
        if (!el) {
            return;
        }

        if (!el.dataset.bound) {
            el.dataset.bound = '1';
            el.addEventListener('scroll', () => {
                el.dataset.atBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 60 ? '1' : '0';
            });
        }

        if (isFirst || el.dataset.atBottom !== '0') {
            el.scrollTo({ top: el.scrollHeight, behavior: isFirst ? 'auto' : 'smooth' });
        }
    }

    function fitMenu(id) {
        const menu = document.getElementById(id);
        if (!menu) {
            return;
        }

        const box = menu.getBoundingClientRect();
        if (box.right > innerWidth - 8) {
            menu.style.left = Math.max(8, innerWidth - box.width - 8) + 'px';
        }
        if (box.bottom > innerHeight - 8) {
            menu.style.top = Math.max(8, innerHeight - box.height - 8) + 'px';
        }
    }

    function focus(id) {
        document.getElementById(id)?.focus();
    }

    function focusSelected(id) {
        const box = document.getElementById(id);
        box?.focus();
        box?.select();
    }

    function originHover(root, reference) {
        let timer = 0;
        const after = (ms, then) => {
            clearTimeout(timer);
            timer = setTimeout(then, ms);
        };
        const close = () => {
            clearTimeout(timer);
            if (originAnchor && root.isConnected) {
                reference.invokeMethodAsync('HideOriginJs');
            }
            originAnchor = null;
        };

        root.addEventListener('mouseover', e => {
            if (e.target.closest?.('#origin-pop')) {
                clearTimeout(timer);
                return;
            }

            const name = e.target.closest?.('[data-origin-key]');
            if (!name) {
                if (originAnchor) {
                    after(ORIGIN_CLOSE_MS, close);
                } else {
                    clearTimeout(timer);
                }
                return;
            }

            if (name === originAnchor) {
                clearTimeout(timer);
                return;
            }

            after(ORIGIN_OPEN_MS, () => {
                if (root.isConnected) {
                    originAnchor = name;
                    reference.invokeMethodAsync('ShowOriginJs', name.dataset.originKey, name.dataset.originOther ?? null, name.dataset.originName ?? '');
                }
            });
        });
        root.addEventListener('mouseout', e => {
            if (!root.contains(e.relatedTarget)) {
                after(ORIGIN_CLOSE_MS, close);
            }
        });
        root.addEventListener('wheel', e => {
            if (!e.target.closest?.('#origin-pop')) {
                close();
            }
        }, { passive: true });
    }

    // Under the name, or over it where that has no room: most names being read are at the bottom of the chat.
    function fitOrigin(id) {
        const pop = document.getElementById(id);
        if (!pop || !originAnchor) {
            return;
        }

        const anchor = originAnchor.getBoundingClientRect();
        const below = anchor.bottom + ORIGIN_GAP;
        const hasRoomBelow = below + pop.offsetHeight <= innerHeight - ORIGIN_MARGIN;
        pop.style.top = (hasRoomBelow ? below : Math.max(ORIGIN_MARGIN, anchor.top - pop.offsetHeight - ORIGIN_GAP)) + 'px';
        pop.style.left = Math.max(ORIGIN_MARGIN, Math.min(anchor.left, innerWidth - pop.offsetWidth - ORIGIN_MARGIN)) + 'px';
        pop.style.visibility = 'visible';
    }

    return { init, flip, chatStick, fitMenu, sound, noteEditor, format, launch, shake, vanish, focus, focusSelected, originHover, fitOrigin };
})();
