#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""1.2.1.5 发版：改 manifest / README 更新日志，然后用 GitHub Git Data API 推 main + 打 tag。"""
import base64
import json
import os
import subprocess
import sys
import urllib.error
import urllib.request

REPO = "KKT446/ClassSlideSync"
API = "https://api.github.com"
ROOT = r"C:\Users\xzq\Documents\deepseek-harness\default-workspace\ClassSlideSync.Plugin"
VERSION = "1.2.1.5"
TODAY = "2026-10-24"

TOKEN = open(os.path.expanduser(r"~\.dsh\github-token.txt"), "r", encoding="utf-8").read().strip()


def api(method, path, body=None):
    data = None
    headers = {
        "Authorization": "Bearer " + TOKEN,
        "Accept": "application/vnd.github+json",
        "X-GitHub-Api-Version": "2022-11-28",
        "User-Agent": "ClassSlideSync-release",
    }
    if body is not None:
        data = json.dumps(body, ensure_ascii=False).encode("utf-8")
        headers["Content-Type"] = "application/json; charset=utf-8"
    req = urllib.request.Request(API + path, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=90) as r:
            raw = r.read()
            return json.loads(raw) if raw else {}
    except urllib.error.HTTPError as e:
        detail = e.read().decode("utf-8", "replace")
        raise RuntimeError(f"{method} {path} -> {e.code}: {detail[:600]}")


def git(*args):
    return subprocess.run(
        ["git"] + list(args), cwd=ROOT, capture_output=True, text=True, encoding="utf-8"
    ).stdout


# ---------- 1. 改版本号与更新日志 ----------
man_path = os.path.join(ROOT, "manifest.yml")
raw = open(man_path, "rb").read()
bom = raw.startswith(b"\xef\xbb\xbf")
text = raw.decode("utf-8-sig")
old = "version: 1.2.0.0"
if "version: " + VERSION in text:
    print("manifest 已是", VERSION, "跳过")
else:
    assert old in text, "manifest 里没有 1.2.0.0"
    text = text.replace(old, "version: " + VERSION, 1)
    open(man_path, "wb").write((b"\xef\xbb\xbf" if bom else b"") + text.encode("utf-8"))
    print("manifest ->", VERSION)

rd_path = os.path.join(ROOT, "README.md")
raw = open(rd_path, "rb").read()
bom = raw.startswith(b"\xef\xbb\xbf")
rd = raw.decode("utf-8-sig")
crlf = "\r\n" in rd
rd = rd.replace("\r\n", "\n")

if f"### {VERSION}（" in rd:
    print("README 已有", VERSION, "一节，跳过")
else:
    anchor = "## 更新日志\n\n### 1.2.0.0"
    assert anchor in rd, "README 里没找到更新日志锚点"
    section = (
        "## 更新日志\n\n"
        f"### {VERSION}（{TODAY}）\n\n"
        "- 修复若干 bug。\n\n"
        "### 1.2.0.0"
    )
    rd = rd.replace(anchor, section, 1)
    if crlf:
        rd = rd.replace("\n", "\r\n")
    open(rd_path, "wb").write((b"\xef\xbb\xbf" if bom else b"") + rd.encode("utf-8"))
    print("README -> 追加", VERSION, "一节")

# ---------- 2. 收集要提交的文件 ----------
status = git("status", "--porcelain")
changes = []
for line in status.splitlines():
    if not line.strip():
        continue
    code, path = line[:2], line[3:].strip()
    if path.startswith("shots/") or path.startswith("bin/") or path.startswith("obj/") or path.startswith("cipx/"):
        continue
    if " -> " in path:  # rename
        path = path.split(" -> ", 1)[1]
    changes.append(path.replace("\\", "/"))
print("待提交文件:")
for c in changes:
    print("   ", c)
if not changes:
    print("没有改动，退出")
    sys.exit(1)

# ---------- 3. 推 main ----------
ref = api("GET", f"/repos/{REPO}/git/ref/heads/main")
base_sha = ref["object"]["sha"]
base_commit = api("GET", f"/repos/{REPO}/git/commits/{base_sha}")
base_tree = base_commit["tree"]["sha"]
print("远端 main:", base_sha[:10], "tree:", base_tree[:10])

tree_items = []
for rel in changes:
    full = os.path.join(ROOT, rel.replace("/", os.sep))
    if not os.path.isfile(full):
        continue
    data = open(full, "rb").read()
    blob = api("POST", f"/repos/{REPO}/git/blobs",
               {"content": base64.b64encode(data).decode("ascii"), "encoding": "base64"})
    tree_items.append({"path": rel, "mode": "100644", "type": "blob", "sha": blob["sha"]})
    print(f"  blob {blob['sha'][:10]} {len(data):>9,} B  {rel}")

tree = api("POST", f"/repos/{REPO}/git/trees", {"base_tree": base_tree, "tree": tree_items})
message = (
    f"发布 {VERSION}\n\n"
    "- 修复若干 bug。\n"
    "- 设置页的科目改为从课表已添加的科目里选，路径改为文件夹选择器。\n"
    "- 课件库面板按卡片本身的观感重排；卡片收成小条时固定 50% 透明。\n"
    "- 记录页码取推过的最远页；连开同名课件不再混桶；课件改名能认回原记录。\n"
)
commit = api("POST", f"/repos/{REPO}/git/commits",
             {"message": message, "tree": tree["sha"], "parents": [base_sha]})
print("commit:", commit["sha"])

api("PATCH", f"/repos/{REPO}/git/refs/heads/main", {"sha": commit["sha"], "force": False})
print("main 已更新")

# ---------- 4. 打 tag（触发 CI 出 Release） ----------
try:
    api("POST", f"/repos/{REPO}/git/refs", {"ref": f"refs/tags/{VERSION}", "sha": commit["sha"]})
    print("tag 已创建:", VERSION)
except RuntimeError as e:
    if "already exists" in str(e) or "422" in str(e):
        api("PATCH", f"/repos/{REPO}/git/refs/tags/{VERSION}", {"sha": commit["sha"], "force": True})
        print("tag 已存在，已指向新提交:", VERSION)
    else:
        raise

print("\n完成。commit =", commit["sha"])
print("CI: https://github.com/KKT446/ClassSlideSync/actions")
print("Release 页面会在 CI 跑完后出现:", f"https://github.com/KKT446/ClassSlideSync/releases/tag/{VERSION}")
