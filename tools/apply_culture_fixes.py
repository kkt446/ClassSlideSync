# -*- coding: utf-8 -*-
"""FIX-6 (corrupt ledger backup) + FIX-7 (invariant culture for every timestamp).

The runtime harness reported that under a non-Gregorian CurrentCulture (ar-SA /
UmAlQura) a one-day-old record was pruned: timestamps are written and parsed with
DateTime.ToString/TryParse and no culture, so the text round-trip depends on the
ambient culture. Pinning both sides to InvariantCulture removes the whole class.

B10: a corrupt ledger.json was silently dropped and the next Save overwrote it,
so the history was lost for good. Back the file up before starting clean.
"""
import io

BASE = r"C:\Users\xzq\Documents\deepseek-harness\default-workspace\ClassSlideSync.Plugin\Core"
INV = "System.Globalization.CultureInfo.InvariantCulture"
STY = "System.Globalization.DateTimeStyles.None"


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
    print("%s: %d/%d applied" % (name, applied, len(pairs)))
    return applied == len(pairs)


# ---------------------------------------------------------------- Ledger.cs
ledger_pairs = [
    # log line
    (
        'DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + message + Environment.NewLine',
        'DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ", ' + INV + ') + message + Environment.NewLine',
    ),
    # parse side
    (
        "            if (!string.IsNullOrEmpty(r.EndedAt) && DateTime.TryParse(r.EndedAt, out t)) return t;\n"
        "            if (!string.IsNullOrEmpty(r.StartedAt) && DateTime.TryParse(r.StartedAt, out t)) return t;\n",
        "            if (!string.IsNullOrEmpty(r.EndedAt)\n"
        "                && DateTime.TryParse(r.EndedAt, " + INV + ", " + STY + ", out t)) return t;\n"
        "            if (!string.IsNullOrEmpty(r.StartedAt)\n"
        "                && DateTime.TryParse(r.StartedAt, " + INV + ", " + STY + ", out t)) return t;\n",
    ),
    # corrupt ledger backup
    (
        "            catch\n"
        "            {\n"
        "                // A corrupt ledger must never stop ClassIsland from starting; begin clean.\n"
        "                l.ByFile = new Dictionary<string, List<SessionRecord>>();\n"
        "            }\n",
        "            catch\n"
        "            {\n"
        "                // A corrupt ledger must never stop ClassIsland from starting; begin clean.\n"
        "                // 但坏文件不能就这么算了：下一次 Save 会把它整个覆盖，讲了一学期的历史就永久没了。\n"
        "                // 先改名留档，万一只是一行坏了，老师还能人工拣回几十条。\n"
        "                try\n"
        "                {\n"
        "                    if (File.Exists(l.fileName))\n"
        "                        File.Move(l.fileName, l.fileName + \".corrupt-\"\n"
        "                            + DateTime.Now.ToString(\"yyyyMMdd-HHmmss\", " + INV + "));\n"
        "                }\n"
        "                catch { }\n"
        "                l.ByFile = new Dictionary<string, List<SessionRecord>>();\n"
        "            }\n",
    ),
]

# ---------------------------------------------------------------- CardFileList.cs
card_pairs = [
    (
        '            if (r != null && !string.IsNullOrEmpty(r.StartedAt) && DateTime.TryParse(r.StartedAt, out t)) return t;\n'
        '            if (r != null && !string.IsNullOrEmpty(r.EndedAt) && DateTime.TryParse(r.EndedAt, out t)) return t;\n',
        '            if (r != null && !string.IsNullOrEmpty(r.StartedAt)\n'
        '                && DateTime.TryParse(r.StartedAt, ' + INV + ', ' + STY + ', out t)) return t;\n'
        '            if (r != null && !string.IsNullOrEmpty(r.EndedAt)\n'
        '                && DateTime.TryParse(r.EndedAt, ' + INV + ', ' + STY + ', out t)) return t;\n',
    ),
    (
        '            if (!DateTime.TryParse(s ?? "", out t)) return -1;',
        '            if (!DateTime.TryParse(s ?? "", ' + INV + ', ' + STY + ', out t)) return -1;',
    ),
]

# ---------------------------------------------------------------- SessionEngine.cs
engine_pairs = [
    (
        '                StartedAt = startAt.ToString("yyyy-MM-dd HH:mm:ss"),',
        '                StartedAt = startAt.ToString("yyyy-MM-dd HH:mm:ss", ' + INV + '),',
    ),
    (
        '            current.EndedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");',
        '            current.EndedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", ' + INV + ');',
    ),
    (
        '            if (!DateTime.TryParse(r.EndedAt ?? r.StartedAt, out t)) return -1;',
        '            if (!DateTime.TryParse(r.EndedAt ?? r.StartedAt, ' + INV + ', ' + STY + ', out t)) return -1;',
    ),
    (
        '            if (DateTime.TryParse(r.StartedAt, out t))',
        '            if (DateTime.TryParse(r.StartedAt, ' + INV + ', ' + STY + ', out t))',
    ),
]

results = [patch("Ledger.cs", ledger_pairs), patch("CardFileList.cs", card_pairs)]
# EndedAt appears twice in SessionEngine: patch both occurrences.
engine_path = BASE + "\\SessionEngine.cs"
text = io.open(engine_path, encoding="utf-8", newline="").read()
old_ended = '            current.EndedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");'
new_ended = '            current.EndedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", ' + INV + ');'
n_ended = text.count(old_ended)
text = text.replace(old_ended, new_ended)
io.open(engine_path, "w", encoding="utf-8", newline="").write(text)
print("SessionEngine.cs: EndedAt x%d replaced" % n_ended)

# The remaining three engine patterns.
results.append(patch("SessionEngine.cs", engine_pairs))
print("RESULT", "OK" if all(results) else "PARTIAL")
