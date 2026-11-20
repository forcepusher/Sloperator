namespace Sloperator.Core.Render;

/// <summary>
/// The script put into every page the renderer opens, before the page's own: it stops the page's
/// clock, so each frame is drawn at its own moment however long it takes to draw (a heavy Three.js
/// scene at 3 frames a second still makes a smooth 30 fps clip). performance.now, Date and
/// requestAnimationFrame run on that clock; SMIL is seeked with setCurrentTime and CSS and Web
/// Animations by their currentTime. <c>__scene.seek(t)</c> moves everything to t seconds and
/// draws one frame.
/// </summary>
internal static class SceneClock
{
    public const string Script = """
        (() => {
          if (window.__scene) return;
          let now = 0, nextId = 1, queue = [];
          const epoch = Date.UTC(2026, 0, 1, 12, 0, 0);
          const errors = [];
          const RealDate = Date;
          window.requestAnimationFrame = cb => { const id = nextId++; queue.push({ id, cb }); return id; };
          window.cancelAnimationFrame = id => { queue = queue.filter(e => e.id !== id); };
          performance.now = () => now;
          class SceneDate extends RealDate {
            constructor(...a) { if (a.length === 0) super(epoch + now); else super(...a); }
            static now() { return epoch + now; }
          }
          window.Date = SceneDate;
          // three.js tells a devtools hook of every scene it makes: the one to save as a .glb.
          const scenes = [];
          const hook = new EventTarget();
          hook.addEventListener('observe', e => { if (e.detail && e.detail.isScene) scenes.push(e.detail); });
          window.__THREE_DEVTOOLS__ = hook;
          window.addEventListener('error', e => errors.push(String(e.message || e.error || e)));
          window.addEventListener('unhandledrejection', e => errors.push('Unhandled promise rejection: ' + String(e.reason && (e.reason.stack || e.reason.message) || e.reason)));

          const svgs = () => Array.from(document.querySelectorAll('svg')).filter(s => typeof s.setCurrentTime === 'function');
          function smilEnd() {
            // The longest finite run of a SMIL animation, else one loop of the longest repeating one.
            let end = 0, loop = 0, loops = [];
            for (const a of document.querySelectorAll('animate, animateTransform, animateMotion, set, animateColor')) {
              let start = 0, dur = 0;
              try { start = a.getStartTime(); } catch { start = 0; }
              try { dur = a.getSimpleDuration(); } catch { dur = 0; }
              if (!isFinite(dur) || dur <= 0) continue;
              const rc = a.getAttribute('repeatCount'), rd = a.getAttribute('repeatDur');
              if (rc === 'indefinite' || rd === 'indefinite') { loops.push(dur); loop = Math.max(loop, start + dur); continue; }
              const times = rc ? parseFloat(rc) : 1;
              end = Math.max(end, start + dur * (isFinite(times) && times > 0 ? times : 1));
            }
            return { end, loop, loops };
          }
          function cssEnd() {
            let end = 0, loops = [];
            for (const a of document.getAnimations()) {
              const t = a.effect && a.effect.getComputedTiming ? a.effect.getComputedTiming() : null;
              if (!t) continue;
              if (isFinite(t.endTime)) end = Math.max(end, t.endTime / 1000);
              else if (t.duration > 0) loops.push(t.duration / 1000 + (t.delay || 0) / 1000);
            }
            return { end, loops };
          }
          window.__scene = {
            errors,
            frames: 0,
            seek(t) {
              now = t * 1000;
              for (const s of svgs()) { try { s.pauseAnimations(); s.setCurrentTime(t); } catch (e) { } }
              for (const a of document.getAnimations()) { try { a.pause(); a.currentTime = t * 1000; } catch (e) { } }
              const q = queue; queue = [];
              for (const e of q) { try { e.cb(now); } catch (err) { errors.push(String(err && (err.stack || err.message) || err)); } }
              this.frames += q.length;
              return q.length;
            },
            info() {
              const root = document.documentElement;
              const svg = root instanceof SVGSVGElement;
              let width = 0, height = 0;
              if (svg) {
                const vb = root.viewBox && root.viewBox.baseVal;
                const w = root.getAttribute('width'), h = root.getAttribute('height');
                const abs = v => v && !/%$/.test(v.trim());
                width = abs(w) ? root.width.baseVal.value : (vb && vb.width) || 0;
                height = abs(h) ? root.height.baseVal.value : (vb && vb.height) || 0;
                if (!abs(w) && abs(h) && vb && vb.height) width = height * vb.width / vb.height;
                if (abs(w) && !abs(h) && vb && vb.width) height = width * vb.height / vb.width;
              }
              const smil = smilEnd(), css = cssEnd();
              const canvas = document.querySelector('canvas');
              return { svg, width, height, smilEnd: smil.end, smilLoop: smil.loop, smilLoops: smil.loops, cssEnd: css.end, cssLoops: css.loops,
                       animationFrames: queue.length > 0, canvas: !!canvas, title: document.title || '' };
            },
            async glb(fallback) {
              // The scene with the most in it (a page may make a small one for its environment map).
              let best = null, most = -1;
              for (const s of scenes) { let n = 0; s.traverse(() => n++); if (n > most) { most = n; best = s; } }
              if (!best) throw new Error('The page made no three.js scene to save.');
              let mod;
              try { mod = await import('three/addons/exporters/GLTFExporter.js'); }
              catch (e) { mod = await import(fallback); }
              const data = await new mod.GLTFExporter().parseAsync(best, { binary: true, onlyVisible: true, animations: best.animations || [] });
              const bytes = new Uint8Array(data);
              let text = '';
              for (let i = 0; i < bytes.length; i += 0x8000) text += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
              return btoa(text);
            },
            fit(w, h) {
              const root = document.documentElement;
              if (!(root instanceof SVGSVGElement)) return;
              if (!root.getAttribute('viewBox')) {
                const ow = root.width.baseVal.value, oh = root.height.baseVal.value;
                if (ow > 0 && oh > 0 && !/%$/.test(root.getAttribute('width') || '%')) root.setAttribute('viewBox', `0 0 ${ow} ${oh}`);
              }
              root.setAttribute('width', w);
              root.setAttribute('height', h);
            },
          };
        })();
        """;
}
