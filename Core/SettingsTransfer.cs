using System;
using System.Reflection;
using System.Text.Json;

namespace ClassSlideSync.Plugin.Core
{
    /// <summary>
    /// 导出/导入插件设置（只针对 SyncSettings；台账是另一个文件，不跟着进进出出）。
    /// 导入走「逐属性覆盖」而不是直接换对象：SyncSettings 的实例被服务、设置页、控制面板一起持有，
    /// 换掉引用会让那几处继续看着旧对象，改了也不生效。
    /// </summary>
    public static class SettingsTransfer
    {
        static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true,
            // 中文科目名不转义成 \uXXXX，导出的文件老师能直接看懂、也能手改。
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        /// <summary>把当前设置序列化成 JSON 文本。</summary>
        public static string ToJson(SyncSettings? settings)
        {
            if (settings == null) return "{}";
            try { return JsonSerializer.Serialize(settings, Options); }
            catch (Exception ex) { return "{\"error\":\"" + ex.Message.Replace("\"", "'") + "\"}"; }
        }

        /// <summary>把 JSON 覆盖到现有设置上。返回 null = 成功，否则是给老师看的中文原因。</summary>
        public static string? Apply(SyncSettings? settings, string? json)
        {
            if (settings == null) return "插件设置还没准备好";
            if (string.IsNullOrWhiteSpace(json)) return "这个文件是空的";
            SyncSettings? loaded;
            try { loaded = JsonSerializer.Deserialize<SyncSettings>(json); }
            catch (Exception ex) { return "这个文件不是课件续页的配置文件（" + ex.Message + "）"; }
            if (loaded == null) return "这个文件里没有可用的设置";

            int applied = 0;
            foreach (var p in typeof(SyncSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanWrite) continue;
                if (p.GetIndexParameters().Length > 0) continue;
                try
                {
                    object? v = p.GetValue(loaded);
                    if (v == null && p.PropertyType != typeof(string)) continue;
                    p.SetValue(settings, v);
                    applied++;
                }
                catch { }
            }
            return applied == 0 ? "文件里没有能识别的设置项" : null;
        }

        /// <summary>导入用的文件默认名，和导出保持一致，老师一次能认出来。</summary>
        public static string SuggestedFileName(DateTime now)
        {
            return "课件续页-设置-" + now.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture) + ".json";
        }
    }
}
