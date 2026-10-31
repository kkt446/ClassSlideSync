/* --- 探针（临时）：加载时先落一个标记，用来判断 WPS 到底有没有执行本文件 --- */
(function () {
    try {
        var lines = [];
        lines.push("at=" + new Date().toISOString());
        lines.push("typeof wps=" + (typeof wps));
        try { lines.push("wps keys=" + Object.keys(wps).join(",")); } catch (e1) { lines.push("wps keys err " + e1); }
        try { lines.push("home=" + wps.Env.GetHomePath()); } catch (e2) { lines.push("home err " + e2); }
        try { lines.push("fs=" + (typeof wps.FileSystem)); } catch (e3) { lines.push("fs err " + e3); }
        try {
            wps.FileSystem.writeFileString("/tmp/css-addin-beacon.txt", lines.join("\n") + "\n");
        } catch (e4) {
            try { wps.FileSystem.writeFileString("/root/css-addin-beacon.txt", lines.join("\n") + "\n" + "first write err " + e4); } catch (e5) { }
        }
    } catch (e) { }
})();
/*
 * ClassSlideSync —— WPS Linux 页码适配器（WPS JS 加载项）
 *
 * ⚠️ 这条路径**没有在 WPS Linux 上实测过**（开发环境里没有 WPS Linux）。
 *    代码按 WPS 加载项公开 API 写：wps.WppApplication()、Application.ActivePresentation、
 *    Application.SlideShowWindows、wps.FileSystem.writeFileString、wps.Env.GetHomePath()。
 *    不同 WPS 版本/发行版可能字段名不同，请先用调试器（ALT+F12）确认。
 *
 * 作用：每秒把「当前演示文稿 / 第几页 / 共几页 / 是否在放映」写到
 *    ~/.local/share/classslidesync/current.json
 * 格式与 LibreOffice 适配器完全一致：
 *    {"file":"/home/u/课件.pptx","page":4,"total":10,"showing":true,
 *     "updated":"2026-10-31T07:00:00Z","app":"wps"}
 *
 * 反向通道（跳页）：插件点卡片按钮时写同目录 command.json：
 *      {"page":3,"ts":"2026-10-31T07:00:00.123Z"}
 * 本脚本每拍读一次；发现新命令且正在放映，就调
 *      Application.SlideShowWindows.Item(1).View.GotoSlide(page)   // 1 起
 * 然后原地刷新 state.page，让 current.json 这一拍就写新页码。
 *
 * 安装：见同目录 README.md。加载后功能区出现「课件页码」选项卡；
 *       也可以不点按钮，脚本会在加载 1.5 秒后自动开始同步。
 */

var CLASS_SLIDESYNC_INTERVAL_MS = 1000;
var CLASS_SLIDESYNC_TIMER = null;
var CLASS_SLIDESYNC_LAST = "";
var CLASS_SLIDESYNC_LAST_COMMAND = "";

function ClassSlideSyncLog(message) {
    try {
        console.log("[ClassSlideSync] " + message);
    } catch (e) {
        /* 调试器没开时 console 可能不可用，静默 */
    }
}

function ClassSlideSyncDataDir() {
    // Linux: ~/.local/share/classslidesync；和插件 Core/LinuxSlideSource.cs 保持一致。
    // 优先 XDG_DATA_HOME（加载项里通常读不到环境变量，所以主要靠 HOME）。
    var home = "";
    try {
        home = wps.Env.GetHomePath() || "";
    } catch (e) {
        home = "";
    }
    if (!home) {
        // 最后兜底：WPS 的临时目录上一级通常不是 home，这里只用于给出可读错误。
        home = ".";
    }
    if (home.charAt(home.length - 1) !== "/") {
        home += "/";
    }
    return home + ".local/share/classslidesync";
}

function ClassSlideSyncJsonPath() {
    return ClassSlideSyncDataDir() + "/current.json";
}

function ClassSlideSyncCommandPath() {
    // 插件 Core/LinuxSlideSource.cs 的 TryWriteCommand 写的就是这个路径。
    return ClassSlideSyncDataDir() + "/command.json";
}

function ClassSlideSyncEnsureDir() {
    var dir = ClassSlideSyncDataDir();
    try {
        var fs = wps.FileSystem;
        if (!fs.Exists(dir)) {
            fs.Mkdir(dir);
        }
        return true;
    } catch (e) {
        ClassSlideSyncLog("建目录失败: " + e);
        return false;
    }
}

function ClassSlideSyncIsoNow() {
    // 2026-10-31T07:00:00Z（去掉毫秒，和协议示例一致）
    return new Date().toISOString().replace(/\.\d{3}Z$/, "Z");
}

function ClassSlideSyncPathFromFullName(fullName) {
    if (!fullName) {
        return "";
    }
    var path = String(fullName);
    if (path.indexOf("file:") === 0) {
        // file:///home/u/a%20b.pptx -> /home/u/a b.pptx
        path = path.replace(/^file:\/\//, "");
        // file:/// 去掉后可能只剩 /home/...；file://localhost/... 也一起处理
        path = path.replace(/^localhost/, "");
        try {
            path = decodeURIComponent(path);
        } catch (e) {
            /* 保留原样 */
        }
    }
    return path;
}

function ClassSlideSyncWrite(payload) {
    if (!ClassSlideSyncEnsureDir()) {
        return false;
    }
    var text = JSON.stringify(payload);
    var path = ClassSlideSyncJsonPath();
    try {
        // WPS 的文件 API 没有 rename，无法像 LibreOffice 适配器那样原子替换。
        // 插件对半截 JSON 是容错的（丢弃这一拍，500ms 后重试），所以直接写。
        wps.FileSystem.writeFileString(path, text);
        return true;
    } catch (e) {
        ClassSlideSyncLog("写 " + path + " 失败: " + e);
        return false;
    }
}

function ClassSlideSyncReadState() {
    var app = null;
    try {
        app = wps.WppApplication();
    } catch (e) {
        return null; // 不在演示进程里（比如加载项被文字/表格进程误加载）
    }
    if (!app) {
        return null;
    }

    var presentation = null;
    try {
        presentation = app.ActivePresentation;
    } catch (e) {
        presentation = null;
    }
    if (!presentation) {
        return null; // 没有打开的演示文稿：不写文件，10 秒后插件判定过期
    }

    var total = 0;
    try {
        total = presentation.Slides.Count;
    } catch (e) {
        total = 0;
    }

    var file = "";
    try {
        file = ClassSlideSyncPathFromFullName(presentation.FullName);
    } catch (e) {
        file = "";
    }
    if (!file) {
        return null;
    }

    var showing = false;
    var page = 0;

    // 放映态：SlideShowWindows 里有一个窗口就说明在放。
    try {
        if (app.SlideShowWindows.Count > 0) {
            showing = true;
            page = app.SlideShowWindows.Item(1).View.Slide.SlideIndex;
        }
    } catch (e) {
        showing = false;
        page = 0;
    }

    if (!showing) {
        // 编辑态：当前窗口正在看的那一页。
        try {
            page = app.ActiveWindow.View.Slide.SlideIndex;
        } catch (e) {
            page = 0;
        }
    }

    // 放映刚开始的一瞬间 SlideIndex 可能还是 0：这一拍先不写，下一拍（1 秒后）就有了。
    if (showing && (!page || page < 1)) {
        return null;
    }

    if (!total || total < 1) {
        total = page > 0 ? page : 0;
    }

    return {
        file: file,
        page: page,
        total: total,
        showing: showing,
        updated: ClassSlideSyncIsoNow(),
        app: "wps"
    };
}

function ClassSlideSyncReadCommand() {
    var path = ClassSlideSyncCommandPath();
    try {
        if (!wps.FileSystem.Exists(path)) {
            return null;
        }
        var text = wps.FileSystem.readFileString(path);
        if (!text) {
            return null;
        }
        var payload = JSON.parse(text);
        if (!payload) {
            return null;
        }
        var page = parseInt(payload.page, 10);
        if (!page || page < 1) {
            return null;
        }
        return {
            page: page,
            ts: payload.ts === undefined || payload.ts === null ? "" : String(payload.ts)
        };
    } catch (e) {
        // 可能正好读到插件写了一半的文件，或者文件被删了：下一拍再试。
        ClassSlideSyncLog("读 " + path + " 失败（下一拍重试）: " + e);
        return null;
    }
}

function ClassSlideSyncApplyCommand(state) {
    var command = ClassSlideSyncReadCommand();
    if (!command) {
        return state;
    }

    // command.json 会一直留着；用 (page|ts) 当身份，处理过就不再执行，
    // 老师自己用键盘翻页不会被同一条旧命令反复拽回去。
    var tag = command.page + "|" + command.ts;
    if (tag === CLASS_SLIDESYNC_LAST_COMMAND) {
        return state;
    }
    CLASS_SLIDESYNC_LAST_COMMAND = tag;

    var writtenAt = Date.parse(command.ts);
    if (!isNaN(writtenAt) && (new Date().getTime() - writtenAt) > 120000) {
        ClassSlideSyncLog("command: ignore stale jump to " + command.page);
        return state;
    }

    if (!state.showing) {
        ClassSlideSyncLog("command: jump to " + command.page + " -> fail (not showing)");
        return state;
    }
    if (state.total > 0 && command.page > state.total) {
        ClassSlideSyncLog("command: jump to " + command.page +
            " -> fail (page out of range 1.." + state.total + ")");
        return state;
    }

    var app = null;
    try {
        app = wps.WppApplication();
    } catch (e) {
        app = null;
    }
    if (!app) {
        ClassSlideSyncLog("command: jump to " + command.page + " -> fail (no WppApplication)");
        return state;
    }

    var ok = false;
    var detail = "";
    try {
        // 与 PowerPoint 对象模型同款：GotoSlide 的参数从 1 起。
        var view = app.SlideShowWindows.Item(1).View;
        // 直接调，不判 typeof：WPS 的 COM 代理有时是属性而不是 JS 函数，
        // 但照样可调用；真不可用时会在 catch 里留下 detail。
        view.GotoSlide(command.page);
        ok = true;
        detail = "GotoSlide(" + command.page + ")";
    } catch (e) {
        detail = String(e);
    }

    if (ok) {
        ClassSlideSyncLog("command: jump to " + command.page + " -> ok");
        // 立刻把新页码带进这一拍要写的 JSON，卡片不用等下一轮。
        try {
            state.page = app.SlideShowWindows.Item(1).View.Slide.SlideIndex;
        } catch (e) {
            state.page = command.page;
        }
        state.updated = ClassSlideSyncIsoNow();
    } else {
        ClassSlideSyncLog("command: jump to " + command.page + " -> fail (" + detail + ")");
    }
    return state;
}

function ClassSlideSyncTick() {
    try {
        var state = ClassSlideSyncReadState();
        if (!state) {
            if (CLASS_SLIDESYNC_LAST !== "") {
                ClassSlideSyncLog("没有可写的演示状态（未打开演示文稿），等待数据过期");
                CLASS_SLIDESYNC_LAST = "";
            }
            return;
        }
        // 先执行插件下发的跳页命令（可能改写 state.page），再算 key、写文件。
        state = ClassSlideSyncApplyCommand(state);
        var key = state.file + "|" + state.page + "|" + state.total + "|" + state.showing;
        if (ClassSlideSyncWrite(state)) {
            if (key !== CLASS_SLIDESYNC_LAST) {
                ClassSlideSyncLog("page " + state.page + "/" + state.total +
                    " showing=" + state.showing + " file=" + state.file);
                CLASS_SLIDESYNC_LAST = key;
            }
        }
    } catch (e) {
        // 定时器回调里绝不能把异常抛给 WPS 的 JS 引擎。
        ClassSlideSyncLog("tick 失败: " + e);
    }
}

function ClassSlideSyncStart(control) {
    if (CLASS_SLIDESYNC_TIMER !== null) {
        ClassSlideSyncLog("已经在同步");
        return;
    }
    ClassSlideSyncLog("启动，输出 " + ClassSlideSyncJsonPath());
    ClassSlideSyncTick(); // 立即写一次，不用等 1 秒
    CLASS_SLIDESYNC_TIMER = setInterval(ClassSlideSyncTick, CLASS_SLIDESYNC_INTERVAL_MS);
}

function ClassSlideSyncStop(control) {
    if (CLASS_SLIDESYNC_TIMER !== null) {
        clearInterval(CLASS_SLIDESYNC_TIMER);
        CLASS_SLIDESYNC_TIMER = null;
        ClassSlideSyncLog("已停止（current.json 会在 10 秒后过期）");
    }
}

function ClassSlideSyncOnce(control) {
    ClassSlideSyncTick();
    ClassSlideSyncLog("手动写了一次，见 " + ClassSlideSyncJsonPath());
}

function ClassSlideSyncOnLoad(ribbonUI) {
    // 功能区加载完成后再启动，避免 wps 对象还没注入。
    setTimeout(function () {
        if (CLASS_SLIDESYNC_TIMER === null) {
            ClassSlideSyncStart(null);
        }
    }, 1500);
    return true;
}

// 不点按钮也自动开始：老师不该为了页码去点一个开关。
setTimeout(function () {
    if (CLASS_SLIDESYNC_TIMER === null) {
        ClassSlideSyncStart(null);
    }
}, 1500);
