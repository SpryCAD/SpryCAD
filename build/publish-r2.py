#!/usr/bin/env python3
"""Publishes one build to Cloudflare R2: the apt and dnf repositories, the downloads and the update feed.

Bucket layout (all under the bucket's public URL, R2_PUBLIC_URL):
  apt/sprycad-archive-keyring.gpg                   the public key that signs the repository
  apt/dists/<channel>/{Release,Release.gpg,InRelease}  signed suite index (channel: stable or dev)
  apt/dists/<channel>/main/binary-amd64/Packages(.gz)
  apt/pool/<channel>/main/s/sprycad/sprycad_<debver>_amd64.deb
  rpm/RPM-GPG-KEY-sprycad                              the same key, armored, for rpm and dnf
  rpm/sprycad-<channel>.repo                           the repository's definition, for /etc/yum.repos.d
  rpm/<channel>/sprycad-<version>-<release>.x86_64.rpm  signed packages
  rpm/<channel>/repodata/repomd.xml(.asc)              signed index (and the metadata files it names)
  downloads/<channel>/<version>/                       the Windows installer and the Linux tarball
  downloads/<channel>.json                             every version still in downloads/<channel>/, newest first: the
                                                       website's version list (site/download.js reads it, by CORS)
  updates/<channel>.json                               the update feed: latest version, URLs, SHA-256s
  updates/<channel>.json.sig                           its signature: ECDSA P-256 over SHA-256, DER, base64

Each channel has its own pool: a commit on master and its tag both build version 0.2.0, and two different files
under one name would fail apt's checksums. Stable keeps every release; dev keeps the newest --keep-dev builds (the
older .debs and downloads are deleted). The indexes are written last (Packages, then the signed Release), so a
client never reads an index that names a file not yet uploaded. The dnf repository is rebuilt from the channel's
.rpms each run (createrepo_c), so they are downloaded first: up to --keep-dev of them on dev, every release on stable.

The feed is signed with UPDATE_SIGNING_KEY, whose public half is built into the app (src/SpryCad.App/Updater.cs):
the app installs only what a feed it can verify names, so the R2 keys alone cannot ship an update. The feed is
uploaded before its signature; an app reading between the two sees a mismatch and just checks again next time.

Usage: build/publish-r2.py --channel dev --deb X.deb --rpm X.rpm --tarball X.tar.gz --installer X.exe
  [--notes-out links.md]
Needs: aws (CLI v2), dpkg-scanpackages (dpkg-dev), apt-ftparchive (apt-utils), rpmsign (rpm), createrepo_c, gpg with
  the signing key imported, openssl.
Environment: R2_ENDPOINT, R2_BUCKET, R2_PUBLIC_URL, AWS_ACCESS_KEY_ID, AWS_SECRET_ACCESS_KEY (the R2 API token),
  UPDATE_SIGNING_KEY (PEM private key, P-256), and optionally APT_GPG_KEY_ID (default: the first secret key) and
  APT_GPG_PASSPHRASE.
"""
import argparse
import base64
import datetime
import functools
import gzip
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

ARCH = 'amd64'


def env(name, default=None):
    # an unset GitHub secret or variable arrives as an empty string
    v = os.environ.get(name) or default
    if not v:
        sys.exit(f'{name} is not set (or empty): add it under Settings > Secrets and variables > Actions')
    return v


BUCKET = env('R2_BUCKET')
SIGNING_KEY = env('UPDATE_SIGNING_KEY')  # read first: a missing key stops the run before anything is uploaded
# the bucket's S3 API URL as Cloudflare shows it (https://<account id>.r2.cloudflarestorage.com, perhaps with the
# bucket's name after it, which the CLI takes separately)
ENDPOINT = env('R2_ENDPOINT').rstrip('/').removesuffix('/' + BUCKET)
PUBLIC = env('R2_PUBLIC_URL').rstrip('/')


def run(*cmd, **kw):
    return subprocess.run(cmd, check=True, text=True, **kw)


def aws(*a, check=True):
    r = subprocess.run(['aws', *a, '--endpoint-url', ENDPOINT], text=True, capture_output=True)
    if check and r.returncode != 0:
        # the CLI's own message says why (AccessDenied, NoSuchBucket, InvalidAccessKeyId, ...)
        sys.exit(f"aws {' '.join(a[:2])} failed (exit {r.returncode}):\n{r.stderr.strip() or r.stdout.strip()}")
    return r


def put(local, key, content_type=None, no_cache=False):
    """Uploads a file. Indexes and the feed are no-cache so clients and the CDN always see the current one; the
    packages cache for an hour only, since pushing a tag again rebuilds its files under the same names."""
    cmd = ['s3', 'cp', str(local), f's3://{BUCKET}/{key}', '--only-show-errors']
    if content_type:
        cmd += ['--content-type', content_type]
    cmd += ['--cache-control', 'no-cache' if no_cache else 'public, max-age=3600']
    aws(*cmd)
    print(f'  put {key}')


def get(key, local):
    """Downloads a file; False when it does not exist. Any other failure stops the run: reading a missing index as
    empty on a network error would drop every package from it."""
    r = aws('s3api', 'get-object', '--bucket', BUCKET, '--key', key, str(local), check=False)
    if r.returncode == 0:
        return True
    if 'NoSuchKey' in r.stderr or '404' in r.stderr:
        return False
    sys.exit(f'reading {key} failed:\n{r.stderr}')


def delete(key, recursive=False):
    aws('s3', 'rm', f's3://{BUCKET}/{key}', '--only-show-errors', *(['--recursive'] if recursive else []))
    print(f'  deleted {key}')


def deb_cmp(a, b):
    if a == b:
        return 0
    return -1 if subprocess.run(['dpkg', '--compare-versions', a, 'lt', b]).returncode == 0 else 1


def version_cmp(a, b):
    """Orders package versions: 0.2.0-dev.3 sorts as the .deb's 0.2.0+dev.3, after 0.2.0 and before 0.2.1."""
    return deb_cmp(a.replace('-dev.', '+dev.'), b.replace('-dev.', '+dev.'))


def field(stanza, name):
    m = re.search(rf'^{name}: *(.+)$', stanza, re.M)
    return m.group(1).strip() if m else None


def sha256(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for chunk in iter(lambda: f.read(1 << 20), b''):
            h.update(chunk)
    return h.hexdigest()


def sign_feed(feed, sig, work):
    """Signs the feed's bytes as uploaded: ECDSA P-256 over SHA-256, DER, base64 (.NET ECDsa.VerifyData with
    DSASignatureFormat.Rfc3279DerSequence reads it)."""
    key = work / 'update-key.pem'
    key.touch(mode=0o600)
    key.write_text(SIGNING_KEY.strip() + '\n')
    der = work / 'feed.der'
    run('openssl', 'dgst', '-sha256', '-sign', str(key), '-out', str(der), str(feed))
    key.unlink()
    sig.write_text(base64.b64encode(der.read_bytes()).decode() + '\n')


def gpg_args():
    a = ['gpg', '--batch', '--yes']
    if os.environ.get('APT_GPG_PASSPHRASE'):
        a += ['--pinentry-mode', 'loopback', '--passphrase', os.environ['APT_GPG_PASSPHRASE']]
    key = os.environ.get('APT_GPG_KEY_ID')
    if not key:
        out = run('gpg', '--batch', '--list-secret-keys', '--with-colons', capture_output=True).stdout
        fprs = [l.split(':')[9] for l in out.splitlines() if l.startswith('fpr:')]
        if not fprs:
            sys.exit('no secret key in gpg to sign the repository with')
        key = fprs[0]
    return a + ['--local-user', key], key


def publish_apt(channel, deb, keep, work):
    """Adds the .deb to the channel's pool and index, prunes old dev builds, and re-signs the suite."""
    debver = run('dpkg-deb', '-f', str(deb), 'Version', capture_output=True).stdout.strip()
    pool = f'pool/{channel}/main/s/sprycad'
    put(deb, f'apt/{pool}/{deb.name}', 'application/vnd.debian.binary-package')

    # the new package's stanza, with Filename relative to the repository root
    scan = work / 'scan'
    (scan / pool).mkdir(parents=True)
    shutil.copy(deb, scan / pool / deb.name)
    stanza = run('dpkg-scanpackages', '--multiversion', pool, '/dev/null', cwd=scan,
                 capture_output=True).stdout.strip()

    # merge into the current index: replace the same version (a rebuilt tag), keep the rest
    dist = work / 'dists' / channel
    bin_dir = dist / 'main' / f'binary-{ARCH}'
    bin_dir.mkdir(parents=True)
    packages = bin_dir / 'Packages'
    old = []
    if get(f'apt/dists/{channel}/main/binary-{ARCH}/Packages', packages):
        old = [s.strip() for s in packages.read_text().split('\n\n') if s.strip()]
    entries = [s for s in old if field(s, 'Version') != debver] + [stanza]
    entries.sort(key=functools.cmp_to_key(lambda a, b: deb_cmp(field(a, 'Version'), field(b, 'Version'))))
    pruned = []
    if keep and len(entries) > keep:
        pruned, entries = entries[:-keep], entries[-keep:]

    packages.write_text('\n\n'.join(entries) + '\n')
    with open(packages, 'rb') as src, gzip.GzipFile(bin_dir / 'Packages.gz', 'wb', mtime=0) as dst:
        shutil.copyfileobj(src, dst)

    # Release (hashes of the indexes), signed both ways: InRelease (inline) and Release.gpg (detached)
    opts = {'Origin': 'SpryCAD', 'Label': 'SpryCAD', 'Suite': channel, 'Codename': channel,
            'Architectures': ARCH, 'Components': 'main', 'Description': f'SpryCAD {channel} builds'}
    cmd = ['apt-ftparchive']
    for k, v in opts.items():
        cmd += ['-o', f'APT::FTPArchive::Release::{k}={v}']
    release = run(*cmd, 'release', '.', cwd=dist, capture_output=True).stdout
    (dist / 'Release').write_text(release)
    gpg, key = gpg_args()
    run(*gpg, '--clearsign', '-o', str(dist / 'InRelease'), str(dist / 'Release'))
    run(*gpg, '--armor', '--detach-sign', '-o', str(dist / 'Release.gpg'), str(dist / 'Release'))
    keyring = work / 'sprycad-archive-keyring.gpg'
    keyring.write_bytes(subprocess.run(['gpg', '--batch', '--export', key], check=True, capture_output=True).stdout)

    put(keyring, 'apt/sprycad-archive-keyring.gpg', 'application/pgp-keys', no_cache=True)
    for name in [f'main/binary-{ARCH}/Packages', f'main/binary-{ARCH}/Packages.gz',
                 'Release', 'Release.gpg', 'InRelease']:
        put(dist / name, f'apt/dists/{channel}/{name}', 'text/plain' if '.gz' not in name else None, no_cache=True)

    for s in pruned:
        delete(f"apt/{field(s, 'Filename')}")
    return debver, f"{PUBLIC}/apt/{pool}/{deb.name}"


def rpm_version(name):
    """An .rpm's version as the .deb's, for ordering: sprycad-0.2.0-1.dev.3.x86_64.rpm is 0.2.0+dev.3, -1 is 0.2.0."""
    m = re.fullmatch(r'sprycad-(.+)-1(?:\.dev\.(\d+))?\.x86_64\.rpm', name)
    if not m:
        sys.exit(f'unexpected .rpm name: {name}')
    return m.group(1) + (f'+dev.{m.group(2)}' if m.group(2) else '')


def publish_rpm(channel, rpm, keep, work):
    """Signs the .rpm, adds it to the channel's dnf repository (rebuilt from all its .rpms), prunes old dev builds,
    and signs the index. Returns the .rpm's URL."""
    gpg, key = gpg_args()
    signed = work / rpm.name
    shutil.copy(rpm, signed)
    sign = ['rpmsign', '--addsign', '--define', f'_gpg_name {key}']
    if os.environ.get('APT_GPG_PASSPHRASE'):
        sign += ['--define', '_gpg_sign_cmd_extra_args --pinentry-mode loopback --passphrase '
                 + os.environ['APT_GPG_PASSPHRASE']]
    run(*sign, str(signed), capture_output=True)

    # the channel's packages, this one replacing a rebuild of the same version
    repo = work / 'repo'
    repo.mkdir()
    aws('s3', 'sync', f's3://{BUCKET}/rpm/{channel}/', str(repo), '--exclude', '*', '--include', '*.rpm',
        '--only-show-errors')
    shutil.copy(signed, repo / rpm.name)
    names = sorted((p.name for p in repo.glob('*.rpm')),
                   key=functools.cmp_to_key(lambda a, b: deb_cmp(rpm_version(a), rpm_version(b))))
    pruned = names[:-keep] if keep and len(names) > keep else []
    for n in pruned:
        (repo / n).unlink()

    # the index: gzip metadata (dnf on RHEL 8 reads no zstd), no sqlite (dnf does not use it), signed detached
    run('createrepo_c', '--general-compress-type=gz', '--no-database', '--quiet', str(repo))
    run(*gpg, '--armor', '--detach-sign', '-o', str(repo / 'repodata' / 'repomd.xml.asc'),
        str(repo / 'repodata' / 'repomd.xml'))

    # the key and the .repo file (both small, rewritten each run), the package, the metadata, the index last
    pubkey = work / 'RPM-GPG-KEY-sprycad'
    pubkey.write_bytes(subprocess.run(['gpg', '--batch', '--armor', '--export', key], check=True,
                                      capture_output=True).stdout)
    put(pubkey, 'rpm/RPM-GPG-KEY-sprycad', 'text/plain', no_cache=True)
    repofile = work / f'sprycad-{channel}.repo'
    repofile.write_text(f"""[sprycad-{channel}]
name=SpryCAD ({channel})
baseurl={PUBLIC}/rpm/{channel}
enabled=1
gpgcheck=1
repo_gpgcheck=1
gpgkey={PUBLIC}/rpm/RPM-GPG-KEY-sprycad
""")
    put(repofile, f'rpm/sprycad-{channel}.repo', 'text/plain', no_cache=True)
    put(signed, f'rpm/{channel}/{rpm.name}', 'application/x-rpm')
    meta = [p for p in (repo / 'repodata').iterdir() if p.name not in ('repomd.xml', 'repomd.xml.asc')]
    for p in meta:
        put(p, f'rpm/{channel}/repodata/{p.name}', no_cache=True)
    put(repo / 'repodata' / 'repomd.xml', f'rpm/{channel}/repodata/repomd.xml', 'text/xml', no_cache=True)
    put(repo / 'repodata' / 'repomd.xml.asc', f'rpm/{channel}/repodata/repomd.xml.asc', 'text/plain', no_cache=True)

    # then what the index no longer names: older metadata files, pruned packages
    current = {p.name for p in meta} | {'repomd.xml', 'repomd.xml.asc'}
    for line in aws('s3', 'ls', f's3://{BUCKET}/rpm/{channel}/repodata/', check=False).stdout.splitlines():
        name = line.split()[-1] if line.split() else ''
        if name and name not in current:
            delete(f'rpm/{channel}/repodata/{name}')
    for n in pruned:
        delete(f'rpm/{channel}/{n}')
    return f'{PUBLIC}/rpm/{channel}/{rpm.name}'


def prune_downloads(channel, keep):
    """Dev: deletes all but the newest `keep` version folders under downloads/dev/."""
    r = aws('s3', 'ls', f's3://{BUCKET}/downloads/{channel}/', check=False)
    versions = [l.split('PRE', 1)[1].strip().rstrip('/') for l in r.stdout.splitlines() if 'PRE ' in l]
    versions.sort(key=functools.cmp_to_key(version_cmp))
    for v in versions[:-keep] if len(versions) > keep else []:
        delete(f'downloads/{channel}/{v}/', recursive=True)


def publish_index(channel, entry, work):
    """Writes downloads/<channel>.json, the website's version list: every version folder still under
    downloads/<channel>/, newest first, each with the SHA-256s and date recorded when it was published (`entry` is
    this build's). A folder the index does not know (published before it existed) is listed from the bucket alone:
    its files, their sizes and the upload date. Written after the dev pruning, so it never names deleted files; one
    index per channel, as the two channels publish independently."""
    key = f'downloads/{channel}.json'
    local = work / 'index.json'
    known = {}
    if get(key, local):
        known = {e['version']: e for e in json.loads(local.read_text()).get('versions', [])}
    known[entry['version']] = entry

    found = {}  # version -> {file name: (size, upload time)}
    for line in aws('s3', 'ls', f's3://{BUCKET}/downloads/{channel}/', '--recursive').stdout.splitlines():
        parts = line.split(None, 3)  # 2026-10-09 16:53:38   44051627 downloads/dev/0.0.1-dev.180/SpryCAD_setup_...
        m = len(parts) == 4 and re.fullmatch(rf'downloads/{channel}/([^/]+)/([^/]+)', parts[3])
        if m:
            found.setdefault(m.group(1), {})[m.group(2)] = (int(parts[2]), f'{parts[0]}T{parts[1]}+00:00')

    versions = []
    for v, files in found.items():
        e = known.get(v)
        if e is None:
            e = {'version': v, 'published': min(t for _, t in files.values()), 'files': {}}
            for name, (size, _) in files.items():
                kind = 'windows' if name.endswith('.exe') else 'tarball' if name.endswith('.tar.gz') else None
                if kind:
                    e['files'][kind] = {'name': name, 'url': f'{PUBLIC}/downloads/{channel}/{v}/{name}', 'size': size}
        versions.append(e)
    versions.sort(key=functools.cmp_to_key(lambda a, b: version_cmp(b['version'], a['version'])))

    local.write_text(json.dumps({'channel': channel, 'versions': versions}, indent=2) + '\n')
    put(local, key, 'application/json', no_cache=True)


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument('--channel', choices=['dev', 'stable'], required=True)
    ap.add_argument('--deb', type=Path, required=True)
    ap.add_argument('--rpm', type=Path, required=True)
    ap.add_argument('--tarball', type=Path, required=True)
    ap.add_argument('--installer', type=Path, required=True)
    ap.add_argument('--keep-dev', type=int, default=15, help='dev builds to keep (stable keeps all)')
    ap.add_argument('--notes-out', type=Path, help='write Markdown download links here (for a GitHub release)')
    a = ap.parse_args()

    m = re.fullmatch(r'SpryCAD-(.+)-linux-x64\.tar\.gz', a.tarball.name)
    if not m:
        sys.exit(f'unexpected tarball name: {a.tarball.name}')
    version = m.group(1)
    keep = a.keep_dev if a.channel == 'dev' else 0
    print(f'== publishing {version} to the {a.channel} channel ==')

    # 1) downloads: immutable, one folder per version
    dl = f'downloads/{a.channel}/{version}'
    put(a.installer, f'{dl}/{a.installer.name}', 'application/vnd.microsoft.portable-executable')
    put(a.tarball, f'{dl}/{a.tarball.name}', 'application/gzip')

    # 2) the apt and dnf repositories
    with tempfile.TemporaryDirectory() as tmp:
        debver, deb_url = publish_apt(a.channel, a.deb, keep, Path(tmp))
    with tempfile.TemporaryDirectory() as tmp:
        rpm_url = publish_rpm(a.channel, a.rpm, keep, Path(tmp))

    # 3) the update feed, last: it names files that are all in place now
    def asset(p):
        return {'name': p.name, 'url': f'{PUBLIC}/{dl}/{p.name}', 'sha256': sha256(p), 'size': p.stat().st_size}
    feed = {
        'channel': a.channel,
        'version': version,
        'published': datetime.datetime.now(datetime.timezone.utc).isoformat(timespec='seconds'),
        'commit': os.environ.get('GITHUB_SHA'),
        'windows': {'installer': asset(a.installer)},
        'linux': {'tarball': asset(a.tarball),
                  'deb': {'name': a.deb.name, 'url': deb_url, 'sha256': sha256(a.deb), 'version': debver},
                  'rpm': {'name': a.rpm.name, 'url': rpm_url}},
    }
    with tempfile.TemporaryDirectory() as tmp:
        f = Path(tmp) / 'feed.json'
        f.write_text(json.dumps(feed, indent=2) + '\n')
        sig = Path(tmp) / 'feed.json.sig'
        sign_feed(f, sig, Path(tmp))
        put(f, f'updates/{a.channel}.json', 'application/json', no_cache=True)
        put(sig, f'updates/{a.channel}.json.sig', 'text/plain', no_cache=True)

    if keep:
        prune_downloads(a.channel, keep)

    # 4) the website's version list
    entry = {'version': version, 'published': feed['published'], 'commit': feed['commit'],
             'files': {'windows': feed['windows']['installer'], 'tarball': feed['linux']['tarball'],
                       'deb': {**feed['linux']['deb'], 'size': a.deb.stat().st_size},
                       'rpm': feed['linux']['rpm']}}
    with tempfile.TemporaryDirectory() as tmp:
        publish_index(a.channel, entry, Path(tmp))

    if a.notes_out:
        w, t = feed['windows']['installer'], feed['linux']['tarball']
        a.notes_out.write_text(f"""## Downloads

| Platform | File | SHA-256 |
|---|---|---|
| Windows (x64) | [{w['name']}]({w['url']}) | `{w['sha256']}` |
| Linux (x64), tarball | [{t['name']}]({t['url']}) | `{t['sha256']}` |
| Debian / Ubuntu (amd64) | [{a.deb.name}]({deb_url}) | `{feed['linux']['deb']['sha256']}` |
| Fedora / RHEL (x86_64) | [{a.rpm.name}]({rpm_url}) | signed with the repository key |

### Install with apt (gets updates with the system)

```bash
sudo install -d -m 0755 /etc/apt/keyrings
curl -fsSL {PUBLIC}/apt/sprycad-archive-keyring.gpg | sudo tee /etc/apt/keyrings/sprycad.gpg > /dev/null
echo "deb [arch=amd64 signed-by=/etc/apt/keyrings/sprycad.gpg] {PUBLIC}/apt {a.channel} main" | sudo tee /etc/apt/sources.list.d/sprycad.list
sudo apt update && sudo apt install sprycad
```

### Install with dnf (Fedora, RHEL, Rocky, Alma)

```bash
sudo curl -fsSL -o /etc/yum.repos.d/sprycad.repo {PUBLIC}/rpm/sprycad-{a.channel}.repo
sudo dnf install sprycad
```
""")
    print('== done ==')


if __name__ == '__main__':
    main()
