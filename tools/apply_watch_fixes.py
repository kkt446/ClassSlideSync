# -*- coding: utf-8 -*-
"""Apply the two WATCH fixes: SessionEngine publish lock + SeewoBridge circuit breaker."""
import io
import sys

BASE = r"C:\Users\xzq\Documents\deepseek-harness\default-workspace\ClassSlideSync.Plugin\Core"


def patch(name, pairs):
    path = BASE + "\\" + name
    text = io.open(path, encoding="utf-8", newline="").read()
    applied = 0
    for idx, (old, new) in enumerate(pairs):
        if old not in text:
            print("%s: pattern %d NOT FOUND" % (name, idx + 1))
            continue
        text = text.replace(old, new, 1)
        applied += 1
    io.open(path, "w", encoding="utf-8", newline="").write(text)
    print("%s: %d/%d patch(es) applied" % (name, applied, len(pairs)))
    return applied == len(pairs)


engine_field_old = "        readonly object gate = new object();\n"
engine_field_new = (
    "        readonly object gate = new object();\n"
    "        // 发布锁：Push 把 BuildState 的结果发给卡片，而 Push 可能同时来自 worker（250ms 循环）\n"
    "        // 和 UI 线程（点跳转 -> SetStatus -> Push）。没有它 lastPushHash 会被两边踩，\n"
    "        // 出现「该刷的没刷」，displayed 也可能被另一线程改成半截状态。\n"
    "        readonly object pushGate = new object();\n"
)

engine_push_old = (
    "        void Push(bool isShowing, int currentSlide, int count)\n"
    "        {\n"
    "            var st = BuildState(isShowing, currentSlide, count);\n"
    "            int h = st.Hash();\n"
    "            if (h == lastPushHash) return;\n"
    "            lastPushHash = h;\n"
    "            var ev = StateChanged;\n"
)
engine_push_new = (
    "        void Push(bool isShowing, int currentSlide, int count)\n"
    "        {\n"
    "            CardState st;\n"
    "            lock (pushGate)\n"
    "            {\n"
    "                st = BuildState(isShowing, currentSlide, count);\n"
    "                int h = st.Hash();\n"
    "                if (h == lastPushHash) return;\n"
    "                lastPushHash = h;\n"
    "            }\n"
    "            // 事件在锁外抛：卡片的事件处理器会回头调 RequestJumpTo / ClearStatus，\n"
    "            // 在锁里调虽然 Monitor 可重入，但没必要冒这个险。\n"
    "            var ev = StateChanged;\n"
)

ok1 = patch(
    "SessionEngine.cs",
    [(engine_field_old, engine_field_new), (engine_push_old, engine_push_new)],
)

seewo_field_old = "        int lastSlideCount;\n"
seewo_field_new = (
    "        int lastSlideCount;\n"
    "\n"
    "        // 熔断：希沃卡死（TCP 连着但不应答）时一次 GetState 最坏 3 x 1500ms = 4.5 秒，\n"
    "        // 足够把引擎线程拖住、卡片跟着不刷新。对照 COM 那条路的 NoteComCost。\n"
    "        const int SlowStateMs = 1200;\n"
    "        const int CooldownSeconds = 6;\n"
    "        DateTime cooldownUntil = DateTime.MinValue;\n"
)

seewo_get_old = (
    "            var now = DateTime.UtcNow;\n"
    "            if (cachedState != null && (now - cachedAt).TotalMilliseconds < 500) return cachedState;\n"
    "            var st = GetStateUncached();\n"
    "            cachedState = st;\n"
    "            cachedAt = DateTime.UtcNow;\n"
    "            return st;\n"
    "        }\n"
)
seewo_get_new = (
    "            var now = DateTime.UtcNow;\n"
    "            // 熔断期间一个 HTTP 都不发，直接拿上一次的结果顶着（卡片不卡，页码暂停刷新）。\n"
    "            if (now < cooldownUntil) return cachedState != null ? cachedState : new SeewoState();\n"
    "            if (cachedState != null && (now - cachedAt).TotalMilliseconds < 500) return cachedState;\n"
    "            var sw = System.Diagnostics.Stopwatch.StartNew();\n"
    "            var st = GetStateUncached();\n"
    "            sw.Stop();\n"
    "            cachedState = st;\n"
    "            cachedAt = DateTime.UtcNow;\n"
    "            if (sw.ElapsedMilliseconds > SlowStateMs) NoteSlowState(sw.ElapsedMilliseconds);\n"
    "            return st;\n"
    "        }\n"
    "\n"
    "        void NoteSlowState(long ms)\n"
    "        {\n"
    "            cooldownUntil = DateTime.UtcNow.AddSeconds(CooldownSeconds);\n"
    "            lastError = \"希沃响应慢（\" + ms + \" ms）\";\n"
    "            try { ledger.Log(\"seewo: GetState took \" + ms + \" ms -- cooldown \" + CooldownSeconds + \"s\"); }\n"
    "            catch { }\n"
    "        }\n"
)

ok2 = patch(
    "SeewoBridge.cs",
    [(seewo_field_old, seewo_field_new), (seewo_get_old, seewo_get_new)],
)

print("RESULT", "OK" if (ok1 and ok2) else "PARTIAL")
sys.exit(0 if (ok1 and ok2) else 1)
