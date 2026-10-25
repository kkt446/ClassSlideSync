# -*- coding: utf-8 -*-
"""把设置页行模板里的 $parent[local:…] 绑定换成绑定到行对象自身。

背景：插件是动态加载的，Avalonia 解析 $parent[local:SlideSyncSettingsPage] 时会走
XamlTypeResolver.Resolve → Assembly.Load("ClassSlideSync.Plugin")，在 ClassIsland 里
抛 FileNotFoundException，把整个设置页打崩（2026-10-24 真机崩溃报告）。
绑定到 x:DataType 指明的 SubjectFolder 成员则是编译期绑定，不需要运行时解析。
"""
import io
import sys

P = r"C:\Users\xzq\Documents\deepseek-harness\default-workspace\ClassSlideSync.Plugin\Views\SettingsPages\SlideSyncSettingsPage.axaml"

PAIRS = [
    ("{Binding $parent[local:SlideSyncSettingsPage].SubjectInput.UseDropdown}", "{Binding UseDropdown}"),
    ("{Binding $parent[local:SlideSyncSettingsPage].SubjectOptions}", "{Binding Options}"),
    ("{Binding $parent[local:SlideSyncSettingsPage].SubjectInput.UseTextBox}", "{Binding UseTextBox}"),
]

OLD_NOTE = (
    "课表没加载/读不出来时，SubjectInput.UseDropdown 为 false，",
    "这里就退回原来的可编辑文本框，绝不把旧值弄丢。",
    "往页面上取数据一律走 $parent[…]（DataTemplate 有自己的名字域，",
    "#PageRoot 这种元素名绑定在模板里解析不到）。 -->",
)
NEW_NOTE = (
    "课表没加载/读不出来时，SubjectFolder.UseDropdown 为 false，",
    "这里就退回原来的可编辑文本框，绝不把旧值弄丢。",
    "注意：绑定只能落在 x:DataType 指明的 SubjectFolder 成员上。",
    "以前这里写 $parent[local:SlideSyncSettingsPage].…，Avalonia 会在运行时",
    "按程序集名 Assembly.Load 解析类型，而插件是动态加载的，直接抛",
    "FileNotFoundException 把整个设置页打崩（2026-10-24 真机崩溃）。 -->",
)

text = io.open(P, encoding="utf-8", newline="").read()

for old, new in PAIRS:
    n = text.count(old)
    text = text.replace(old, new)
    print("binding replaced x%d: %s" % (n, old[:56]))

# 注释块：按行匹配（缩进不固定，所以逐行替换）
lines = text.split("\n")
out = []
replaced_note = 0
i = 0
while i < len(lines):
    if i + len(OLD_NOTE) <= len(lines) and all(OLD_NOTE[k] in lines[i + k] for k in range(len(OLD_NOTE))):
        indent = lines[i][: len(lines[i]) - len(lines[i].lstrip())]
        for k, seg in enumerate(NEW_NOTE):
            out.append(indent + seg + ("\r" if lines[i].endswith("\r") else ""))
        i += len(OLD_NOTE)
        replaced_note += 1
        continue
    out.append(lines[i])
    i += 1
text = "\n".join(out)
print("note block replaced x%d" % replaced_note)

io.open(P, "w", encoding="utf-8", newline="").write(text)
print("remaining $parent[ occurrences:", text.count("$parent["))
sys.exit(0)
