#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""补提交 Assets/ 与 test/ 下的这三个文件：上一个提交只处理了单文件，目录被跳过了。"""
import base64
import json
import os
import urllib.error
import urllib.request

REPO = "KKT446/ClassSlideSync"
API = "https://api.github.com"
ROOT = r"C:\Users\xzq\Documents\deepseek-harness\default-workspace\ClassSlideSync.Plugin"
VERSION = "1.2.1.5"
TOKEN = open(os.path.expanduser(r"~\.dsh\github-token.txt"), "r", encoding="utf-8").read().strip()

FILES = [
    "Assets/Tutorials/getStarted.json",
    "test/测试课件.pptx",
    "test/纯页码课件.pptx",
]


def api(method, path, body=None):
    headers = {
        "Authorization": "Bearer " + TOKEN,
        "Accept": "application/vnd.github+json",
        "X-GitHub-Api-Version": "2022-11-28",
        "User-Agent": "ClassSlideSync-release",
    }
    data = None
    if body is not None:
        data = json.dumps(body, ensure_ascii=False).encode("utf-8")
        headers["Content-Type"] = "application/json; charset=utf-8"
    req = urllib.request.Request(API + path, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=120) as r:
            raw = r.read()
            return json.loads(raw) if raw else {}
    except urllib.error.HTTPError as e:
        raise RuntimeError(f"{method} {path} -> {e.code}: {e.read().decode('utf-8', 'replace')[:600]}")


ref = api("GET", f"/repos/{REPO}/git/ref/heads/main")
base_sha = ref["object"]["sha"]
base_tree = api("GET", f"/repos/{REPO}/git/commits/{base_sha}")["tree"]["sha"]
print("base:", base_sha[:10])

items = []
for rel in FILES:
    full = os.path.join(ROOT, rel.replace("/", os.sep))
    assert os.path.isfile(full), "缺文件: " + rel
    data = open(full, "rb").read()
    blob = api("POST", f"/repos/{REPO}/git/blobs",
               {"content": base64.b64encode(data).decode("ascii"), "encoding": "base64"})
    items.append({"path": rel, "mode": "100644", "type": "blob", "sha": blob["sha"]})
    print(f"  blob {blob['sha'][:10]} {len(data):>8,} B  {rel}")

tree = api("POST", f"/repos/{REPO}/git/trees", {"base_tree": base_tree, "tree": items})
msg = (
    f"补上 {VERSION} 漏提交的新手引导资源与示例课件\n\n"
    "assets/Tutorials/getStarted.json、test/ 下两份课件。上一个提交走的是单文件"
    "清单，目录被跳过了。\n"
)
commit = api("POST", f"/repos/{REPO}/git/commits",
             {"message": msg, "tree": tree["sha"], "parents": [base_sha]})
print("commit:", commit["sha"])

api("PATCH", f"/repos/{REPO}/git/refs/heads/main", {"sha": commit["sha"], "force": False})
api("PATCH", f"/repos/{REPO}/git/refs/tags/{VERSION}", {"sha": commit["sha"], "force": True})
print("main 与 tag", VERSION, "都已指向", commit["sha"][:10])
