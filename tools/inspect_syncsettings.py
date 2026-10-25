# -*- coding: utf-8 -*-
"""FIX-8: SyncSettings.Normalize 保留 #RRGGBBAA 的 alpha；FIX-9: 学科文件夹两侧归一匹配。

harness 报的两条：
  B06 (中) Core/SyncSettings.cs:403  Normalize 把 #RRGGBBAA 截成 6 位，自定义底色 alpha 永远失效
  B04/B05 (低) SyncSettings.cs:348-365  存「劳动/英语」时用「劳动」或「英语」查不到（拆段只在查询侧）
"""
import io
import re

BASE = r"C:\Users\xzq\Documents\deepseek-harness\default-workspace\ClassSlideSync.Plugin\Core"
path = BASE + "\\SyncSettings.cs"
text = io.open(path, encoding="utf-8", newline="").read()

# 先看看当前实现长什么样，避免盲改
m = re.search(r"static string Normalize\(string s\)\s*\{.*?\n        \}", text, re.S)
print("=== Normalize 原文 ===")
print(m.group(0) if m else "NOT FOUND")

m2 = re.search(r"public SubjectFolder\? FindSubjectFolder\(string subject\)\s*\{.*?\n        \}", text, re.S)
print("=== FindSubjectFolder 原文 ===")
print(m2.group(0) if m2 else "NOT FOUND")

m3 = re.search(r"bool MatchFolder\(SubjectFolder f, string subject\)\s*\{.*?\n        \}", text, re.S)
print("=== MatchFolder 原文 ===")
print(m3.group(0) if m3 else "NOT FOUND")
