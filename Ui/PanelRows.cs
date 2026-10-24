using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ClassSlideSync.Plugin.Core;

namespace ClassSlideSync.Plugin.Ui
{
    /// <summary>
    /// 面板左栏的一行课件。只做展示，数据仍然来自 CardFileItem；
    /// Detail（“第 N 页 · 3 小时前”）是 CardFileList 算好的，面板不重新写一套。
    /// </summary>
    public sealed class PanelRow : INotifyPropertyChanged
    {
        bool selected;

        public PanelRow(CardFileItem item, string subject, bool isCurrent)
        {
            // 缺 item 说明调用方（面板 Rebuild）已经把 null 传进来了，早点炸比在界面上
            // 留一行空白、点进去 NullReference 更容易定位。空字符串仍然允许（老记录没科目/没名字）。
            if (item == null) throw new ArgumentNullException(nameof(item));
            Item = item;
            Name = item.DisplayName ?? "";
            Subject = subject ?? "";
            Detail = item.Detail ?? "";
            IsCurrent = isCurrent;
        }

        public CardFileItem Item { get; }
        public string Name { get; }
        public string Subject { get; }
        public string Detail { get; }
        /// <summary>左栏第二行：科目 + 页码/时间。分组仍按科目，但老师扫一行时也能看到科目。</summary>
        public string Subline
        {
            get
            {
                // Detail 为空时不能留下一个孤零零的“ · ”（空数据/老记录会出现）。
                if (string.IsNullOrEmpty(Detail)) return string.IsNullOrEmpty(Subject) ? "" : Subject;
                return string.IsNullOrEmpty(Subject) ? Detail : Subject + " · " + Detail;
            }
        }
        public bool IsCurrent { get; }
        public bool HasBadge { get { return IsCurrent; } }
        public string Badge { get { return IsCurrent ? "当前" : ""; } }

        /// <summary>管理模式下每行前面出现勾选框（控制面板「管理 → 删除选中」）。</summary>
        bool manageMode;
        bool picked;

        public bool ManageMode
        {
            get { return manageMode; }
            set
            {
                if (manageMode == value) return;
                manageMode = value;
                Notify(nameof(ManageMode));
            }
        }

        public bool IsPicked
        {
            get { return picked; }
            set
            {
                if (picked == value) return;
                picked = value;
                Notify(nameof(IsPicked));
            }
        }

        void Notify(string name)
        {
            var handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(name));
        }

        public bool IsSelected
        {
            get { return selected; }
            set
            {
                if (selected == value) return;
                selected = value;
                var handler = PropertyChanged;
                if (handler != null) handler(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>面板左栏的一个科目分组：标题（科目 · N 份）+ 该科目下的课件行。</summary>
    public sealed class PanelGroup
    {
        public PanelGroup(string subject, List<PanelRow> rows)
        {
            Subject = subject ?? "";
            // 空组也要能安全构造：Header 会读 Rows.Count，null 会直接 NRE。
            Rows = rows ?? new List<PanelRow>();
        }

        public string Subject { get; }
        public List<PanelRow> Rows { get; }
        public string Header { get { return Subject + " · " + Rows.Count + " 份"; } }
    }
}
