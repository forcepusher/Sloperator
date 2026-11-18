# Drawing scenes (Sloperator's guide)
Pictures and clips come from three things, each for what it does best:
- **ComfyUI** (the comfyui tool): raster pictures (photos, paintings, illustrations, characters) and animating them.
- **SVG** for 2D vector art: icons, logos, diagrams, charts, flat illustrations, titles and motion graphics, moving with SMIL.
- **Three.js** in an HTML page for 3D: objects, product shots, characters and places built from shapes, scenes, and 3D model files (.glb) saved from them.

Write the scene with write_file, then call render with it. A scene that doesn't move becomes a PNG; one that moves becomes an MP4 clip. Every frame is drawn at its exact moment on a stopped clock, so a heavy scene still makes a smooth clip.

## SVG and SMIL
- Start with `<svg xmlns="http://www.w3.org/2000/svg" width="1920" height="1080" viewBox="0 0 1920 1080">`: its size is the picture's (a small one, such as a 24-pixel icon, is drawn at 1024 along its longer side). For a clip, use the video's frame (1920x1080, 1080x1920, 1080x1080).
- Motion: `<animate>` (any attribute: x, r, opacity, fill, d), `<animateTransform type="translate|rotate|scale">`, `<animateMotion path="...">` (with `rotate="auto"` to turn along it), `<set>`. Time them with `begin`, `dur` and `repeatCount`; chain them with `begin="intro.end+0.3s"`; hold the end with `fill="freeze"`. Ease with `calcMode="spline" keyTimes="0;1" keySplines="0.4 0 0.2 1"`.
- The clip is as long as the last animation that ends; with only repeating ones (`repeatCount="indefinite"`), one loop of them all. `seconds` sets another length.
- CSS `@keyframes` in a `<style>` work too. No script is needed.
- Text uses the fonts on this PC (Segoe UI, Arial, Georgia, Consolas, Impact); keep it as text, not paths, so it stays sharp. A picture beside the file: `<image href="photo.png" .../>`.
- Morph a shape by animating `d` between paths with the same commands; draw a line on with `stroke-dasharray` and an animated `stroke-dashoffset`.

## Three.js
A page Sloperator serves three.js to itself (offline, r187); the import map is as three.js's docs write it, and a page without one gets it:
```html
<!doctype html><html><head><style>html,body{margin:0;overflow:hidden;background:#000}</style>
<script type="importmap">{"imports":{"three":"https://cdn.jsdelivr.net/npm/three@0.187.0/build/three.module.js","three/addons/":"https://cdn.jsdelivr.net/npm/three@0.187.0/examples/jsm/"}}</script>
</head><body><script type="module">
import * as THREE from 'three';
import { RoomEnvironment } from 'three/addons/environments/RoomEnvironment.js';
const renderer = new THREE.WebGLRenderer({ antialias: true });
renderer.setSize(innerWidth, innerHeight);
renderer.toneMapping = THREE.ACESFilmicToneMapping;
document.body.appendChild(renderer.domElement);
const scene = new THREE.Scene();
scene.environment = new THREE.PMREMGenerator(renderer).fromScene(new RoomEnvironment()).texture;
const camera = new THREE.PerspectiveCamera(40, innerWidth / innerHeight, 0.1, 100);
camera.position.set(0, 1.2, 5);
const knot = new THREE.Mesh(new THREE.TorusKnotGeometry(1, 0.3, 200, 32), new THREE.MeshStandardMaterial({ color: 0xff6a3d, metalness: 0.3, roughness: 0.35 }));
scene.add(knot);
renderer.setAnimationLoop(ms => { const t = ms / 1000; knot.rotation.y = t * 0.8; renderer.render(scene, camera); });
</script></body></html>
```
- The window is the picture's size (1280x720 unless width and height say otherwise): size the renderer and the camera's aspect from `innerWidth` and `innerHeight`.
- Animate by time: the time `setAnimationLoop` and `requestAnimationFrame` give, `performance.now()` or `THREE.Clock`. Make a loop come back to where it started at the clip's end (t / seconds × 2π) so it plays round smoothly.
- A scene drawn once (`renderer.render` without a loop, or a loop that changes nothing) is a picture.
- Files beside the page load by their names: `new GLTFLoader().load('model.glb', ...)`, textures, HDR environments. While something loads, set `window.sceneReady` to a promise of it (`window.sceneReady = loader.loadAsync('model.glb').then(g => scene.add(g.scene))`), and drawing waits for it.
- Light what you draw: MeshStandardMaterial and MeshPhysicalMaterial are black without lights or an environment (RoomEnvironment above). Shadows: `renderer.shadowMap.enabled = true`, `castShadow`/`receiveShadow`.
- Add-ons come from `three/addons/` (controls, loaders, geometries, post-processing such as EffectComposer and UnrealBloomPass, environments). WebGL only: `three/webgpu` and TSL aren't served offline.
- A 3D model file: build it in the page (shapes, `ExtrudeGeometry` from an SVG path or text, `LatheGeometry`, groups, materials, an `AnimationClip` in `scene.animations`) and render with output=name.glb: the biggest scene the page makes is saved as it is at `at` (0 unless given), and the look shows it from four sides. A .glb beside the page loads with GLTFLoader.
- A clear background (a 3D logo over a video): `new THREE.WebGLRenderer({ alpha: true })`, no scene.background, and render with transparent=true to a .webm, .mov or .png.

## Checking
The result comes with a look at it (a clip as a sheet of its frames) and the errors the page's scripts threw or files it couldn't find. Fix every error, look at the result as the user will, and render again until it is right. A still of a moving scene at a moment: at=2.5.
