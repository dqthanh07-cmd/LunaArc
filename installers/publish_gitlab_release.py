"""Upload already verified release assets to GitLab without replacing existing files."""
import hashlib
import os
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path


def request(url, token, method='GET', data=None):
    req = urllib.request.Request(url, data=data, method=method, headers={'JOB-TOKEN': token})
    return urllib.request.build_opener(GitLabRedirectHandler()).open(req, timeout=60)


class GitLabRedirectHandler(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        target = urllib.parse.urlparse(newurl)
        if target.scheme != 'https' or target.hostname != 'gitlab.com':
            raise urllib.error.HTTPError(newurl, code, 'Refusing to forward CI credentials off GitLab', headers, fp)
        return super().redirect_request(req, fp, code, msg, headers, newurl)


def digest(path):
    hasher = hashlib.sha256()
    with path.open('rb') as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b''):
            hasher.update(chunk)
    return hasher.digest()


def main(directory):
    project_id = os.environ['CI_PROJECT_ID']
    token = os.environ['CI_JOB_TOKEN']
    version = os.environ['LUNAARC_RELEASE_VERSION']
    tag = os.environ['LUNAARC_RELEASE_TAG']
    if tag != f'v{version}':
        raise ValueError('Release tag and version do not match')
    root = Path(directory)
    assets = sorted(path for path in root.iterdir() if path.is_file())
    expected = {
        f'LunaArc-{version}-setup-win-x64.exe',
        f'LunaArc-{version}-portable-win-x64.zip',
        f'LunaArc-{version}-linux-x64.tar.gz',
        'SHA256SUMS.txt',
    }
    if {path.name for path in assets} != expected:
        raise ValueError('Release assets do not match the expected package set')

    base = f"{os.environ['CI_API_V4_URL']}/projects/{urllib.parse.quote(project_id, safe='') }"
    release_url = f'{base}/releases/{urllib.parse.quote(tag, safe="")}'
    try:
        with request(release_url, token):
            raise FileExistsError(f'Release {tag} already exists; it will not be replaced')
    except urllib.error.HTTPError as error:
        if error.code != 404:
            raise
    tag_url = f'{base}/repository/tags/{urllib.parse.quote(tag, safe="")}'
    try:
        with request(tag_url, token):
            raise FileExistsError(f'Git tag {tag} already exists; release promotion will not reuse it')
    except urllib.error.HTTPError as error:
        if error.code != 404:
            raise

    package_version = urllib.parse.quote(tag, safe='')
    package_name = urllib.parse.quote('lunaarc', safe='')
    urls = {
        path: f'{base}/packages/generic/{package_name}/{package_version}/{urllib.parse.quote(path.name, safe="")}'
        for path in assets
    }
    for path, url in urls.items():
        try:
            with request(url, token) as existing:
                hasher = hashlib.sha256()
                while chunk := existing.read(1024 * 1024):
                    hasher.update(chunk)
                if hasher.digest() != digest(path):
                    raise FileExistsError(f'Existing package asset differs and will not be replaced: {path.name}')
        except urllib.error.HTTPError as error:
            if error.code != 404:
                raise

    for path, url in urls.items():
        result = subprocess.run(
            ['curl', '--fail', '--silent', '--show-error', '--upload-file', str(path),
             '--header', f'JOB-TOKEN: {token}', url],
            check=False,
        )
        if result.returncode:
            # A concurrent promotion may have uploaded this exact file after preflight.
            with request(url, token) as existing:
                hasher = hashlib.sha256()
                while chunk := existing.read(1024 * 1024):
                    hasher.update(chunk)
                if hasher.digest() != digest(path):
                    raise RuntimeError(f'Package upload failed or conflicting asset exists: {path.name}')
    print(f'Uploaded or verified {len(assets)} immutable assets for {tag}.')


if __name__ == '__main__':
    main(Path(sys.argv[1]))
