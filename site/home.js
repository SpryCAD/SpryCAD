// The home page: the spur gear sample in 3D (img/gear.stl, exported from samples/demos/spur-gear.sprt by
// build/site-gear.cs) and the latest version on the download links.
(() => {
  'use strict';

  const BASE = 'https://download.sprycad.com';

  // ---- the latest version (stable, else dev) on the download links; they keep pointing at /download without it
  async function latest() {
    for (const ch of ['stable', 'dev']) {
      try {
        const r = await fetch(`${BASE}/downloads/${ch}.json`, { cache: 'no-store' });
        if (r.ok) {
          const v = (await r.json()).versions?.[0];
          if (v) return { channel: ch, version: v.version, windows: v.files?.windows };
        }
        const u = await fetch(`${BASE}/updates/${ch}.json`, { cache: 'no-store' });
        if (u.ok) {
          const f = await u.json();
          return { channel: ch, version: f.version, windows: f.windows?.installer };
        }
      } catch { /* next channel */ }
    }
    return null;
  }
  latest().then(l => {
    if (!l) return;
    for (const el of document.querySelectorAll('[data-latest="version"]')) el.textContent = l.version;
    for (const el of document.querySelectorAll('[data-latest="rev"]')) el.textContent = `Rev ${l.version} · ${l.channel} channel`;
    if (l.windows?.url) {
      document.getElementById('win-link').href = l.windows.url;
      document.getElementById('win-file').textContent = `${l.windows.name}${l.windows.size ? ` · ${Math.round(l.windows.size / 1048576)} MB` : ''}`;
    }
  });

  // ---- the model
  const stage = document.getElementById('stage'), canvas = document.getElementById('gl'), dim = document.getElementById('dim');
  if (!window.THREE || !THREE.STLLoader || !THREE.OrbitControls) return;  // no WebGL library: the frame stays empty
  let renderer;
  try { renderer = new THREE.WebGLRenderer({ canvas, antialias: true, alpha: true }); } catch { return; }
  renderer.setPixelRatio(Math.min(devicePixelRatio, 2));
  renderer.outputEncoding = THREE.sRGBEncoding;
  const scene = new THREE.Scene();
  const camera = new THREE.OrthographicCamera(-1, 1, 1, -1, -1000, 1000);
  scene.add(camera);
  scene.add(new THREE.HemisphereLight(0xffffff, 0x7d8a80, 0.85));
  const key = new THREE.DirectionalLight(0xffffff, 0.75);
  key.position.set(-1.2, 2, 3);
  camera.add(key);  // the light turns with the view, as in the app

  const R = 38, HALF = 43;  // the gear's outside radius, and half the view's height, in mm
  const spin = new THREE.Group(), tilt = new THREE.Group();
  tilt.rotation.x = -1.05;
  tilt.position.y = 7;  // a little up, leaving room for the dimension
  tilt.add(spin);
  scene.add(tilt);
  const controls = new THREE.OrbitControls(camera, canvas);
  controls.enableZoom = false;
  controls.enablePan = false;
  controls.enableDamping = true;
  let touched = false;
  controls.addEventListener('start', () => { touched = true; dim.style.opacity = 0; });

  // STL has one normal per triangle: smooth only across edges flatter than `deg`, so curved faces shade round and the
  // edges between faces stay sharp (three.js's later toCreasedNormals)
  function creased(g, deg) {
    const p = g.attributes.position, n = p.count, c = Math.cos(deg * Math.PI / 180), fn = new Float32Array(n * 3);
    const a = new THREE.Vector3(), b = new THREE.Vector3(), d = new THREE.Vector3(), u = new THREE.Vector3(), v = new THREE.Vector3();
    for (let i = 0; i < n; i += 3) {
      a.fromBufferAttribute(p, i); b.fromBufferAttribute(p, i + 1); d.fromBufferAttribute(p, i + 2);
      u.subVectors(d, b); v.subVectors(a, b); u.cross(v).normalize();
      for (let k = 0; k < 3; k++) fn.set([u.x, u.y, u.z], (i + k) * 3);
    }
    const groups = new Map();  // corners at one point
    for (let i = 0; i < n; i++) {
      const k = `${Math.round(p.getX(i) * 1e3)},${Math.round(p.getY(i) * 1e3)},${Math.round(p.getZ(i) * 1e3)}`;
      (groups.get(k) || groups.set(k, []).get(k)).push(i);
    }
    const out = new Float32Array(n * 3);
    for (const ids of groups.values()) for (const i of ids) {
      let x = 0, y = 0, z = 0;
      for (const j of ids)
        if (fn[i * 3] * fn[j * 3] + fn[i * 3 + 1] * fn[j * 3 + 1] + fn[i * 3 + 2] * fn[j * 3 + 2] >= c) {
          x += fn[j * 3]; y += fn[j * 3 + 1]; z += fn[j * 3 + 2];
        }
      const l = Math.hypot(x, y, z) || 1;
      out[i * 3] = x / l; out[i * 3 + 1] = y / l; out[i * 3 + 2] = z / l;
    }
    g.setAttribute('normal', new THREE.BufferAttribute(out, 3));
  }

  new THREE.STLLoader().load('/img/gear.stl', g => {
    g.computeBoundingBox();
    g.center();
    creased(g, 30);
    const mesh = new THREE.Mesh(g, new THREE.MeshStandardMaterial({
      color: 0xbfc6cd, metalness: 0.3, roughness: 0.42, polygonOffset: true, polygonOffsetFactor: 1, polygonOffsetUnits: 1 }));
    const edges = new THREE.LineSegments(new THREE.EdgesGeometry(g, 28), new THREE.LineBasicMaterial({ color: 0x1b2620 }));
    spin.add(mesh, edges);
  });

  // the outside diameter dimensioned under the gear, while the view is the starting one (the gear turns about its own
  // axis, so its outline holds still)
  function drawDim(w, h) {
    const s = h / (2 * HALF), cx = w / 2, x0 = cx - R * s, x1 = cx + R * s, cy = h / 2 - tilt.position.y * s;
    const y = Math.min(h - 18, cy + R * s * Math.cos(tilt.rotation.x) + 7 * s + 22), ink = '#1f2d26';
    dim.setAttribute('viewBox', `0 0 ${w} ${h}`);
    dim.innerHTML = `
      <g stroke="${ink}" stroke-width="1" fill="none">
        <path d="M${x0} ${cy} V${y + 6} M${x1} ${cy} V${y + 6}" opacity=".35"/>
        <path d="M${x0 + 9} ${y} H${x1 - 9}"/>
      </g>
      <g fill="${ink}"><path d="M${x0} ${y} l9 -3 v6z"/><path d="M${x1} ${y} l-9 -3 v6z"/></g>
      <rect x="${cx - 34}" y="${y - 10}" width="68" height="20" fill="#eef5e8"/>
      <text x="${cx}" y="${y + 4}" text-anchor="middle" font-family="IBM Plex Mono, monospace" font-size="12" font-weight="600" fill="${ink}">Ø76.00</text>`;
  }

  function size() {
    const w = stage.clientWidth, h = stage.clientHeight, a = w / h;
    renderer.setSize(w, h, false);
    camera.left = -HALF * a; camera.right = HALF * a; camera.top = HALF; camera.bottom = -HALF;
    camera.position.set(0, 0, 200);
    camera.lookAt(0, 0, 0);
    camera.updateProjectionMatrix();
    if (!touched) drawDim(w, h);
  }
  new ResizeObserver(size).observe(stage);
  size();

  // turn slowly, and draw only while the view is on screen
  const still = matchMedia('(prefers-reduced-motion: reduce)').matches;
  let visible = true;
  new IntersectionObserver(e => { visible = e[0].isIntersecting; }).observe(stage);
  (function frame() {
    if (visible) {
      if (!still) spin.rotation.z += 0.0035;
      controls.update();
      renderer.render(scene, camera);
    }
    requestAnimationFrame(frame);
  })();
})();
