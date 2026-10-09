// The download page: reads the version lists from R2 (downloads/<channel>.json, written by build/publish-r2.py; the
// bucket allows this site's origin by CORS), lets the visitor pick a channel, version and system, and shows that
// version's files and install steps. The choice is kept in the URL (?channel=dev&version=0.2.0-dev.3&os=linux), so a
// link names one build.
(() => {
  'use strict';

  const BASE = 'https://download.sprycad.com';

  const $ = id => document.getElementById(id);
  const esc = s => String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const mb = n => n ? `${(n / 1048576).toFixed(1)} MB` : '';
  const day = iso => iso ? new Date(iso).toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' }) : '';
  // a dev build 0.2.0-dev.3 is the .deb 0.2.0+dev.3 (build/make-linux-package.sh)
  const debVersion = v => v.replace('-dev.', '+dev.');

  let data = { stable: [], dev: [] };
  const params = new URLSearchParams(location.search);
  const state = {
    channel: params.get('channel'),
    version: params.get('version'),
    os: params.get('os') || (/Linux|X11|CrOS/.test(navigator.userAgent) && !/Android/.test(navigator.userAgent) ? 'linux' : 'windows'),
  };

  // A channel's versions, newest first. Before its first publish with a version list there is only the update feed:
  // the latest version alone. A channel with neither has no versions; a network error rejects.
  async function channel(ch) {
    const r = await fetch(`${BASE}/downloads/${ch}.json`, { cache: 'no-store' });
    if (r.ok) return (await r.json()).versions || [];
    const u = await fetch(`${BASE}/updates/${ch}.json`, { cache: 'no-store' });
    if (!u.ok) return [];
    const f = await u.json();
    return [{ version: f.version, published: f.published, commit: f.commit,
              files: { windows: f.windows?.installer, tarball: f.linux?.tarball, deb: f.linux?.deb } }];
  }

  function setSeg(el, value) {
    for (const b of el.querySelectorAll('button')) b.setAttribute('aria-pressed', String(b.dataset.value === value));
  }

  function pre(cmd, shell = 'bash') {
    return `<div class="code"><div class="bar"><span>${shell}</span><button class="copy" type="button">Copy</button></div><pre><code>${esc(cmd)}</code></pre></div>`;
  }

  function fileRow(title, f, note) {
    if (!f) return '';
    return `<div class="file">
      <div class="what"><strong>${esc(title)}</strong><span class="name">${esc(f.name)}${f.size ? ' · ' + mb(f.size) : ''}${note ? ' · ' + esc(note) : ''}</span></div>
      <a class="btn primary" href="${esc(f.url)}" download>Download</a>
      ${f.sha256 ? `<div class="sha">SHA-256 ${esc(f.sha256)}</div>` : ''}
    </div>`;
  }

  function windows(rel, latest) {
    const w = rel.files.windows;
    if (!w) return `<div class="notice warn">This version has no Windows installer.</div>`;
    return `
      <div class="files">${fileRow('Windows installer (64-bit)', w)}</div>
      <h2>Install on Windows</h2>
      <ol class="steps">
        <li>Download <code>${esc(w.name)}</code> and run it.</li>
        <li>If Windows SmartScreen says <em>Windows protected your PC</em>, choose <strong>More info</strong> &rarr; <strong>Run anyway</strong>
          (the installer is not code-signed yet).</li>
        <li>SpryCAD installs for your user account without administrator rights. To install for everyone on the PC
          (into <code>Program Files</code>), pick <em>Install for all users</em> when the setup asks.</li>
        <li>Start SpryCAD from the Start menu. <code>.sprt</code> parts and <code>.sasm</code> assemblies open with it.</li>
      </ol>
      <p>SpryCAD checks for new ${rel.channel === 'dev' ? 'dev builds' : 'releases'} when it starts and from
        <strong>Help &rarr; Check for Updates</strong>; an update installs once SpryCAD closes.${latest ? '' :
        ' Since this is not the newest version, it will offer to update.'}</p>
      <h3>Silent install</h3>
      <p>For scripted installs (add <code>/ALLUSERS</code> from an administrator prompt to install for everyone):</p>
      ${pre(`.\\${w.name} /VERYSILENT /SUPPRESSMSGBOXES`, 'powershell')}
      ${w.sha256 ? `<h3>Check the download</h3><p>In PowerShell, the hash should match the SHA-256 shown above:</p>
      ${pre(`(Get-FileHash .\\${w.name} -Algorithm SHA256).Hash`, 'powershell')}` : ''}
      <h3>Uninstall</h3>
      <p>Settings &rarr; Apps &rarr; Installed apps &rarr; SpryCAD &rarr; Uninstall.</p>`;
  }

  function linux(rel, latest) {
    const base = BASE;
    const debver = rel.files.deb?.version || debVersion(rel.version);
    const deb = rel.files.deb || {
      name: `sprycad_${debver}_amd64.deb`,
      url: `${base}/apt/pool/${rel.channel}/main/s/sprycad/sprycad_${debver}_amd64.deb`,
    };
    const rpm = rel.files.rpm;  // builds before the dnf repository have none
    const t = rel.files.tarball;
    const dir = t ? t.name.replace(/\.tar\.gz$/, '') : '';
    const apt = [
      'sudo install -d -m 0755 /etc/apt/keyrings',
      `curl -fsSL ${base}/apt/sprycad-archive-keyring.gpg | sudo tee /etc/apt/keyrings/sprycad.gpg > /dev/null`,
      `echo "deb [arch=amd64 signed-by=/etc/apt/keyrings/sprycad.gpg] ${base}/apt ${rel.channel} main" | sudo tee /etc/apt/sources.list.d/sprycad.list`,
      latest ? 'sudo apt update && sudo apt install sprycad'
             : `sudo apt update && sudo apt install sprycad=${debver}`,
    ];
    if (!latest) apt.push('sudo apt-mark hold sprycad   # stay on this version; apt-mark unhold sprycad to follow updates again');

    return `
      <div class="files">
        ${fileRow('Debian / Ubuntu package (.deb)', deb, 'amd64')}
        ${fileRow('Fedora / RHEL package (.rpm)', rpm, 'x86_64')}
        ${fileRow('Linux tarball (any distribution)', t, 'x86-64')}
      </div>

      <h2>Debian and Ubuntu: from the apt repository</h2>
      <p>Recommended: SpryCAD then updates along with the rest of the system. Ubuntu 22.04 or newer, Debian 12 or newer.
        ${rel.channel === 'dev' ? 'This adds the <strong>dev</strong> channel (every build of the main branch).' : ''}</p>
      ${pre(apt.join('\n'))}
      <p class="small muted">Start it from the applications menu or with <code>sprycad</code>. Remove it with
        <code>sudo apt remove sprycad</code>${latest ? '' : ' (and <code>apt-mark unhold</code> first if you held it)'}.</p>

      <h3>Or install the .deb file only</h3>
      <p>No repository and no automatic updates:</p>
      ${pre(`curl -fLO ${deb.url}\nsudo apt install ./${deb.name}`)}

      ${rpm ? `<h2>Fedora, RHEL, Rocky and Alma: from the dnf repository</h2>
      <p>Packages are signed with the same key as the apt repository; dnf asks once to import it.</p>
      ${pre([`sudo curl -fsSL -o /etc/yum.repos.d/sprycad.repo ${base}/rpm/sprycad-${rel.channel}.repo`,
             latest ? 'sudo dnf install sprycad' : `sudo dnf install ${rpm.name.replace(/\.rpm$/, '')}`].join('\n'))}
      <p class="small muted">Remove it with <code>sudo dnf remove sprycad</code>.${latest ? '' :
        ' <code>dnf upgrade</code> will move it to the newest version.'}</p>` : ''}

      ${t ? `<h2>Any distribution: the tarball</h2>
      <p>Installs for your user only (no root) into <code>~/.local/share/sprycad</code>, with a menu entry, file types and a
        <code>sprycad</code> command in <code>~/.local/bin</code>. Needs X11 or XWayland and OpenGL.</p>
      ${pre(`curl -fLO ${t.url}\ntar xzf ${t.name}\ncd ${dir}\n./install.sh`)}
      <p>To update, run the same steps with a newer tarball. To uninstall (your settings in <code>~/.config/SpryCAD</code> are kept):</p>
      ${pre('~/.local/share/sprycad/uninstall.sh')}
      ${t.sha256 ? `<h3>Check the download</h3><p>The hash should match the SHA-256 shown above:</p>${pre(`sha256sum ${t.name}`)}` : ''}` : ''}`;
  }

  // The change logs (changelog/<channel>.json, written by build/publish-r2.py): each build's commit subjects. Loaded
  // beside the version lists and shown when they arrive; a channel without one shows no changes.
  let logs = { stable: [], dev: [] };
  async function changelog(ch) {
    const r = await fetch(`${BASE}/changelog/${ch}.json`, { cache: 'no-store' });
    return r.ok ? (await r.json()).versions || [] : [];
  }

  function changes(rel, ch) {
    const entry = logs[ch].find(e => e.version === rel.version);
    if (!entry) return '';
    const items = entry.changes.map(c => `<li>${esc(c.subject)} <span class="small muted">${esc(c.commit.slice(0, 7))}</span></li>`);
    const list = items.length ? `<ul>${items.slice(0, 15).join('')}</ul>` +
        (items.length > 15 ? `<details><summary>${items.length - 15} more</summary><ul>${items.slice(15).join('')}</ul></details>` : '')
      : '<p class="muted">No changes listed.</p>';
    return `<h2>What's new in ${esc(rel.version)}</h2>${list}`;
  }

  function render() {
    const stableOk = data.stable.length > 0, devOk = data.dev.length > 0;
    if (state.channel !== 'stable' && state.channel !== 'dev') state.channel = stableOk ? 'stable' : 'dev';
    if (state.os !== 'windows' && state.os !== 'linux') state.os = 'windows';
    setSeg($('channel'), state.channel);
    setSeg($('os'), state.os);

    const list = data[state.channel];
    const sel = $('version');
    sel.innerHTML = list.length
      ? list.map((r, i) => `<option value="${esc(r.version)}">${esc(r.version)}${i === 0 ? ' (latest)' : ''}</option>`).join('')
      : '<option>None yet</option>';
    sel.disabled = !list.length;
    let rel = list.find(r => r.version === state.version);
    if (!rel) { rel = list[0]; state.version = rel?.version || null; }
    if (rel) sel.value = rel.version;

    const notes = [];
    if (state.channel === 'stable' && !stableOk)
      notes.push(`<div class="notice">There is no stable release yet. ${devOk ? '<a href="#" data-go="dev">Try a dev build</a>: the newest build of the main branch.' : ''}</div>`);
    if (state.channel === 'dev' && rel)
      notes.push(`<div class="notice warn">Dev builds are made from every change to the main branch: the newest features, less tested. Older dev builds are removed after a while.</div>`);
    if (state.channel === 'dev' && !devOk)
      notes.push(`<div class="notice">No dev builds are available right now.</div>`);
    $('notice').innerHTML = notes.join('');

    const commit = rel?.commit ? ` · ${esc(rel.commit.slice(0, 7))}` : '';
    $('meta').innerHTML = rel ? `Published ${esc(day(rel.published))}${commit}` : '';

    if (rel) rel.channel = state.channel;
    $('content').innerHTML = rel ? (state.os === 'windows' ? windows(rel, rel === list[0]) : linux(rel, rel === list[0])) + changes(rel, state.channel) : '';

    const q = new URLSearchParams({ channel: state.channel, os: state.os });
    if (rel && rel !== list[0]) q.set('version', rel.version);
    history.replaceState(null, '', `${location.pathname}?${q}`);
  }

  $('channel').addEventListener('click', e => {
    const v = e.target.closest('button')?.dataset.value;
    if (v && v !== state.channel) { state.channel = v; state.version = null; render(); }
  });
  $('os').addEventListener('click', e => {
    const v = e.target.closest('button')?.dataset.value;
    if (v) { state.os = v; render(); }
  });
  $('version').addEventListener('change', e => { state.version = e.target.value; render(); });
  document.addEventListener('click', e => {
    const go = e.target.closest('[data-go]');
    if (go) { e.preventDefault(); state.channel = go.dataset.go; state.version = null; render(); return; }
    const copy = e.target.closest('.copy');
    if (copy) {
      const text = copy.closest('.code').querySelector('pre').textContent;
      navigator.clipboard?.writeText(text).then(() => {
        copy.textContent = 'Copied';
        setTimeout(() => { copy.textContent = 'Copy'; }, 1500);
      });
    }
  });

  let listed = false;
  Promise.all([changelog('stable'), changelog('dev')]).then(([stable, dev]) => {
    logs = { stable, dev };
    if (listed) render();
  }, () => {});
  Promise.all([channel('stable'), channel('dev')]).then(([stable, dev]) => {
    data = { stable, dev };
    listed = true;
    render();
  }, () => {
    $('version').innerHTML = '<option>Unavailable</option>';
    $('notice').innerHTML = '<div class="notice warn">The version list could not be loaded. Try again in a moment.</div>';
  });
})();
