"""全 Release の package.json と zip から VPM リスティング (index.json) を作る。"""
import hashlib, json, os, sys, urllib.request

repo = os.environ["GITHUB_REPOSITORY"]
token = os.environ.get("GH_TOKEN", "")
owner, name = repo.split("/")
out = sys.argv[1] if len(sys.argv) > 1 else "site"


def get(url, raw=False):
    req = urllib.request.Request(url, headers={"Authorization": f"Bearer {token}"} if token else {})
    if not raw:
        req.add_header("Accept", "application/vnd.github+json")
    with urllib.request.urlopen(req) as r:
        data = r.read()
    return data if raw else json.loads(data)


releases, page = [], 1
while True:
    chunk = get(f"https://api.github.com/repos/{repo}/releases?per_page=100&page={page}")
    if not chunk:
        break
    releases += chunk
    page += 1

packages = {}
for rel in releases:
    if rel.get("draft"):
        continue
    assets = {a["name"]: a["browser_download_url"] for a in rel["assets"]}
    if "package.json" not in assets:
        continue
    manifest = json.loads(get(assets["package.json"], raw=True))
    zip_name = f"{manifest['name']}-{manifest['version']}.zip"
    if zip_name not in assets:
        continue
    manifest["url"] = assets[zip_name]
    manifest["zipSHA256"] = hashlib.sha256(get(assets[zip_name], raw=True)).hexdigest()
    packages.setdefault(manifest["name"], {"versions": {}})["versions"][manifest["version"]] = manifest

listing = {
    "name": "日向's VPM Repository",
    "id": f"io.github.{owner.lower()}.{name.lower()}",
    "url": f"https://{owner.lower()}.github.io/{name}/index.json",
    "author": "日向",
    "packages": packages,
}

os.makedirs(out, exist_ok=True)
with open(os.path.join(out, "index.json"), "w", encoding="utf-8") as f:
    json.dump(listing, f, ensure_ascii=False, indent=2)
print(f"{sum(len(p['versions']) for p in packages.values())} versions")
