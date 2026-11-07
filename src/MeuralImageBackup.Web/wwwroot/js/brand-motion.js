// Animates the logo (Components/Shared/BrandMark.razor). Four motions, picked at random for each
// play and never the same twice in a row:
//   copy    - the copy slides out from behind the picture to its place, the backup being made;
//   shine   - the sun rises over the hills and glows;
//   swing   - the picture swings on its nail and settles, like one just hung;
//   develop - the hills draw themselves and the sun fades in, like a photo developing.
// The header logo plays one motion when hovered or focused; a mark with data-motion="once" plays one
// shortly after it appears, and one with data-motion="loop" keeps playing while it's on the page.
// Nothing moves when the user asks for reduced motion. Only the Web Animations API is used: no
// attribute Blazor renders is changed.
(() => {
    const LOOP_PAUSE = 500;
    const reduced = matchMedia("(prefers-reduced-motion: reduce)");
    const playing = new WeakSet();
    const started = new WeakSet();
    let lastMotion = null;
    const backOut = "cubic-bezier(.34,1.56,.64,1)";

    const parts = svg => {
        const at = (el, origin) => { if (el) { el.style.transformBox = "fill-box"; el.style.transformOrigin = origin; } return el; };
        return {
            copy: at(svg.querySelector(".bm-copy"), "center"),
            front: at(svg.querySelector(".bm-front"), "center top"),
            hills: svg.querySelector(".bm-hills"),
            sun: at(svg.querySelector(".bm-sun"), "center"),
        };
    };

    const done = anims => Promise.all(anims.filter(Boolean).map(a => a.finished.catch(() => { })));

    const MOTIONS = {
        copy: m => done([
            m.copy.animate([
                { transform: "translate(-10px, 11px)", opacity: 0 },
                { transform: "translate(-10px, 11px)", opacity: 1, offset: 0.15 },
                { transform: "translate(0, 0)", opacity: 1 },
            ], { duration: 750, easing: backOut }),
            m.front.animate([{ transform: "scale(1)" }, { transform: "scale(.94)" }, { transform: "scale(1)" }], { duration: 450, easing: "ease-in-out" }),
        ]),
        shine: m => done([
            m.sun.animate([
                { transform: "translateY(9px) scale(.5)", opacity: 0 },
                { transform: "translateY(0) scale(1.35)", opacity: 1, offset: 0.6 },
                { transform: "translateY(0) scale(1)", opacity: 1 },
            ], { duration: 900, easing: "ease-out" }),
        ]),
        swing: m => done([
            m.front.animate([
                { transform: "rotate(0)" }, { transform: "rotate(-9deg)" }, { transform: "rotate(6deg)" },
                { transform: "rotate(-3deg)" }, { transform: "rotate(1.5deg)" }, { transform: "rotate(0)" },
            ], { duration: 1100, easing: "ease-in-out" }),
        ]),
        develop: m => {
            const length = m.hills.getTotalLength ? m.hills.getTotalLength() : 60;
            return done([
                m.hills.animate([
                    { strokeDasharray: `${length}`, strokeDashoffset: `${length}` },
                    { strokeDasharray: `${length}`, strokeDashoffset: "0" },
                ], { duration: 800, easing: "ease-in-out" }),
                m.sun.animate([{ opacity: 0, transform: "scale(.6)" }, { opacity: 1, transform: "scale(1)" }],
                    { duration: 500, delay: 550, easing: backOut, fill: "backwards" }),
            ]);
        },
    };

    const pick = () => {
        const names = Object.keys(MOTIONS).filter(n => n !== lastMotion);
        return lastMotion = names[Math.floor(Math.random() * names.length)];
    };

    async function play(svg) {
        if (reduced.matches || playing.has(svg) || !svg.isConnected) return;
        playing.add(svg);
        try { await MOTIONS[pick()](parts(svg)); }
        catch { }
        finally { playing.delete(svg); }
    }

    async function loop(svg) {
        while (svg.isConnected && svg.dataset.motion === "loop" && !reduced.matches) {
            await play(svg);
            await new Promise(r => setTimeout(r, LOOP_PAUSE));
        }
        started.delete(svg);
    }

    function scan() {
        for (const svg of document.querySelectorAll("svg.brand-mark[data-motion]")) {
            if (started.has(svg)) continue;
            started.add(svg);
            if (svg.dataset.motion === "loop") loop(svg);
            else setTimeout(() => play(svg), 350);
        }
    }

    const trigger = e => {
        const host = e.target.closest?.(".brand");
        // Only when the pointer comes in from outside, not when it moves between the logo's parts.
        if (!host || (e.relatedTarget && host.contains(e.relatedTarget))) return;
        const svg = host.querySelector("svg.brand-mark");
        if (svg) play(svg);
    };
    document.addEventListener("pointerover", trigger);
    document.addEventListener("focusin", trigger);

    new MutationObserver(scan).observe(document.documentElement, { childList: true, subtree: true });
    if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", scan); else scan();
})();
