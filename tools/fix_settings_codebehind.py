# -*- coding: utf-8 -*-
import io

P = r"C:\Users\xzq\Documents\deepseek-harness\default-workspace\ClassSlideSync.Plugin\Views\SettingsPages\SlideSyncSettingsPage.xaml.cs"
t = io.open(P, encoding="utf-8", newline="").read()

# 1) 页面级 SubjectInput 换成「把开关写到每一行上」
old_prop = """    /// <summary>
    /// 科目这一列该显示下拉还是手填框。课表读不出来（没加载、服务缺失、异常）时退回手填框，
    /// 见 <see cref="SubjectInputState"/>。
    /// </summary>
    public SubjectInputState SubjectInput { get; } = new();
"""
new_prop = """    /// <summary>
    /// 科目这一列该显示下拉还是手填框。课表读不出来（没加载、服务缺失、异常）时退回手填框。
    /// 开关写在每一行（<see cref="SubjectFolder.UseDropdown"/>）而不是放在页面上：模板里
    /// $parent[…] 那种写法要 Avalonia 在运行时按程序集名加载类型，插件是动态加载的会直接崩，
    /// 详见 SlideSyncSettingsPage.axaml 里行模板的注释。
    /// </summary>
    void ApplySubjectInput(bool useDropdown)
    {
        foreach (var folder in Settings.SubjectFolders)
        {
            if (folder == null) continue;
            folder.Options = SubjectOptions;
            folder.UseDropdown = useDropdown;
        }
    }
"""
assert old_prop in t, "prop block not found"
t = t.replace(old_prop, new_prop, 1)

# 2) 下拉开关改走 ApplySubjectInput
old_flag = "            SubjectInput.UseDropdown = loaded && fromProfile.Count > 0;"
new_flag = "            var useDropdown = loaded && fromProfile.Count > 0;"
assert old_flag in t, "flag line not found"
t = t.replace(old_flag, new_flag, 1)

old_tail = """            foreach (var name in all)
            {
                if (!SubjectOptions.Contains(name))
                {
                    SubjectOptions.Add(name);
                }
            }
        }"""
new_tail = """            foreach (var name in all)
            {
                if (!SubjectOptions.Contains(name))
                {
                    SubjectOptions.Add(name);
                }
            }

            // 选项备齐之后再把「显示下拉」的开关写到每一行上（行可能是在选项之前建好的）。
            ApplySubjectInput(useDropdown);
        }"""
assert old_tail in t, "tail block not found"
t = t.replace(old_tail, new_tail, 1)

# 3) 异常分支
old_catch = """            // 档案还没就绪或者服务拿不到：退化成原来的可编辑文本框，页面不能崩。
            SubjectInput.UseDropdown = false;"""
new_catch = """            // 档案还没就绪或者服务拿不到：退化成原来的可编辑文本框，页面不能崩。
            ApplySubjectInput(false);"""
assert old_catch in t, "catch block not found"
t = t.replace(old_catch, new_catch, 1)

# 4) 删掉不再被引用的 SubjectInputState 类
start = t.find("/// <summary>\r\n/// 科目这一列用下拉还是手填框。")
if start < 0:
    start = t.find("/// <summary>\n/// 科目这一列用下拉还是手填框。")
assert start > 0, "SubjectInputState block not found"
end = t.rfind("}")
t = t[:start].rstrip() + "\r\n}\r\n"
print("SubjectInputState removed; file now", len(t), "chars")
print("SubjectInput left:", t.count("SubjectInput."), "| SubjectInputState left:", t.count("SubjectInputState"))

io.open(P, "w", encoding="utf-8", newline="").write(t)
print("written")