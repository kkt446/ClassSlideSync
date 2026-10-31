#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
ClassSlideSync —— LibreOffice Impress 页码适配器（Linux）

作用：连到正在运行的 LibreOffice（必须带 --accept 的 UNO socket），每秒读一次
「当前放映的课件 / 第几页 / 共几页 / 是否在放映」，写成 ClassSlideSync 插件读的
~/.local/share/classslidesync/current.json：

    {"file":"/home/u/课件.pptx","page":4,"total":10,"showing":true,
     "updated":"2026-10-31T07:00:00Z","app":"libreoffice"}

反方向：插件在卡片上点「从第 N 页接着讲」时，会写同目录的 command.json：

    {"page":3,"ts":"2026-10-31T07:00:00.123Z"}

本脚本每轮读一次；ts 比上次处理的新、page 在 1..total 内，就调
XSlideShowController.gotoSlide(XDrawPage)（注意必须传 XDrawPage，传整数会抛
CannotConvertException），然后立刻重写 current.json 让插件马上看到新页码。
命令超过 2 分钟未处理会被当成过期跳过。

为什么是「外部进程 + UNO」而不是 LibreOffice 扩展（.oxt）：
  · 扩展要装进用户配置、升级 LibreOffice 可能被清掉；
  · 外部脚本崩了只影响自己，不会把 Impress 带下水；
  · 老师不用改任何设置，装好脚本、用 run.sh 启动即可。

依赖：python3-uno（Ubuntu/Debian: sudo apt install python3-uno libreoffice-impress）。
注意：必须用系统自带的 /usr/bin/python3，不能是 conda/venv 里的 python —— pyuno
是编译进系统 python 的，换解释器会 `import uno` 失败。

用法：
    # 1) 先让 LibreOffice 监听 2002 端口（见 README.md）
    soffice --impress --accept="socket,host=127.0.0.1,port=2002;urp;" 课件.pptx
    # 2) 再启动本脚本
    ./run.sh
    # 或指定端口/输出/只读一次（调试用）
    python3 slidesync_lo.py --port 2002 --interval 1 --once

退出码：0 正常；2 依赖缺失；3 --once 时连不上/没有演示文档。
"""

import argparse
import json
import os
import sys
import time
import urllib.parse
from datetime import datetime, timezone

APP_NAME = "libreoffice"
DEFAULT_HOST = "127.0.0.1"
DEFAULT_PORT = 2002
DEFAULT_INTERVAL = 1.0
STALE_SECONDS = 10  # 仅用于日志提示；真正的过期判断在插件侧

# 反向通道：插件把「跳到第 N 页」写进 current.json 同目录的 command.json。
COMMAND_NAME = "command.json"
COMMAND_MAX_AGE_SECONDS = 120  # 超过 2 分钟的命令当成历史遗留，不执行


def log(message, quiet=False):
    if quiet:
        return
    stamp = datetime.now().strftime("%H:%M:%S")
    sys.stderr.write("[slidesync-lo %s] %s\n" % (stamp, message))
    sys.stderr.flush()


def default_output_path():
    """和插件 Core/LinuxSlideSource.cs 完全一致的路径规则。"""
    xdg = os.environ.get("XDG_DATA_HOME")
    if xdg:
        base = xdg
    else:
        base = os.path.join(os.path.expanduser("~"), ".local", "share")
    return os.path.join(base, "classslidesync", "current.json")


def iso_now():
    # 插件按 ISO-8601 解析；去掉毫秒，保持和协议示例一致。
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def command_path_for(out_path):
    """command.json 和 current.json 放同一个目录（插件 Core/LinuxSlideSource.cs 也是这么定）。"""
    return os.path.join(os.path.dirname(out_path) or ".", COMMAND_NAME)


def parse_ts(text):
    """解析插件写的 ISO-8601（可能带毫秒和 Z）。解析不了返回 None，不当成错误。"""
    if not text or not isinstance(text, str):
        return None
    value = text.strip()
    if value.endswith("Z") or value.endswith("z"):
        value = value[:-1] + "+00:00"
    try:
        parsed = datetime.fromisoformat(value)
    except ValueError:
        return None
    if parsed.tzinfo is None:
        parsed = parsed.replace(tzinfo=timezone.utc)
    return parsed.astimezone(timezone.utc)


def read_command(path, quiet=False):
    """读一次 command.json。没有文件返回 None；半截/坏 JSON 记一行日志后返回 None。"""
    if not os.path.exists(path):
        return None
    try:
        # utf-8-sig：插件写的是无 BOM UTF-8，但别人手写带 BOM 时也别崩。
        with open(path, "r", encoding="utf-8-sig") as handle:
            payload = json.load(handle)
        if not isinstance(payload, dict):
            log("command: %s is not a JSON object, ignored" % path, quiet)
            return None
        page = int(payload.get("page", 0))
        ts = payload.get("ts")
        return {"page": page, "ts": ts if isinstance(ts, str) else ""}
    except Exception as exc:
        log("command: cannot read %s: %s" % (path, exc), quiet)
        return None


def file_url_to_path(url):
    """file:///root/a%20b.pptx -> /root/a b.pptx；不是 file URL 就原样返回。"""
    if not url:
        return ""
    if not url.startswith("file:"):
        return url
    try:
        parsed = urllib.parse.urlparse(url)
        path = urllib.parse.unquote(parsed.path)
        # file://host/share 在 Linux 上用不到；真遇到就把 host 拼回去，至少不丢信息。
        if parsed.netloc and parsed.netloc not in ("", "localhost"):
            path = "//" + parsed.netloc + path
        return path
    except Exception:
        return url


def write_json_atomic(path, payload):
    """临时文件 + os.replace：插件永远读不到半截 JSON。"""
    directory = os.path.dirname(path) or "."
    os.makedirs(directory, exist_ok=True)
    tmp = "%s.tmp.%d" % (path, os.getpid())
    with open(tmp, "w", encoding="utf-8") as handle:
        json.dump(payload, handle, ensure_ascii=False, separators=(",", ":"))
        handle.write("\n")
        handle.flush()
        os.fsync(handle.fileno())
    os.replace(tmp, path)


class UnoBridge(object):
    """一条到 soffice 的 UNO 连接；断了由调用方重建。"""

    def __init__(self, host, port, quiet=False):
        import uno  # 延迟导入：没装 python3-uno 时给出人话错误
        self.uno = uno
        self.host = host
        self.port = port
        self.quiet = quiet
        self.ctx = None
        self.desktop = None

    def connect(self):
        uno = self.uno
        local = uno.getComponentContext()
        resolver = local.ServiceManager.createInstanceWithContext(
            "com.sun.star.bridge.UnoUrlResolver", local)
        url = "uno:socket,host=%s,port=%d;urp;StarOffice.ComponentContext" % (
            self.host, self.port)
        self.ctx = resolver.resolve(url)
        self.desktop = self.ctx.ServiceManager.createInstanceWithContext(
            "com.sun.star.frame.Desktop", self.ctx)
        # 触发一次调用，确认代理真的活着（resolve 成功但进程刚退的情况）。
        _ = self.desktop.Components
        return self

    def close(self):
        self.desktop = None
        self.ctx = None

    # ---- 找文档 ----

    def _presentations(self):
        found = []
        try:
            enum = self.desktop.Components.createEnumeration()
        except Exception:
            return found
        while enum.hasMoreElements():
            component = enum.nextElement()
            try:
                if component.supportsService(
                        "com.sun.star.presentation.PresentationDocument"):
                    found.append(component)
                    continue
            except Exception:
                pass
            # 兜底：某些 WPS/第三方实现没有注册服务名，但有 DrawPages。
            try:
                if component.getDrawPages() is not None:
                    found.append(component)
            except Exception:
                pass
        return found

    def _running_state(self, doc):
        """返回 (是否放映, XPresentation2 或 None)。"""
        try:
            presentation = doc.getPresentation()
        except Exception:
            presentation = None
        if presentation is None:
            return False, None

        # 多数版本 getPresentation() 返回的对象本身就支持 XPresentation2，
        # pyuno 的动态代理能直接调 isRunning()；失败再显式 queryInterface。
        try:
            return bool(presentation.isRunning()), presentation
        except Exception:
            pass
        try:
            presentation2 = self.uno.queryInterface(
                presentation, "com.sun.star.presentation.XPresentation2")
            if presentation2 is not None:
                return bool(presentation2.isRunning()), presentation2
        except Exception:
            pass
        try:
            type_obj = self.uno.getTypeByName(
                "com.sun.star.presentation.XPresentation2")
            presentation2 = presentation.queryInterface(type_obj)
            if presentation2 is not None:
                return bool(presentation2.isRunning()), presentation2
        except Exception:
            pass
        return False, None

    def pick_document(self):
        docs = self._presentations()
        if not docs:
            return None

        # 优先正在放映的那一份（可能同时开着好几个课件）。
        for doc in docs:
            try:
                running, _ = self._running_state(doc)
                if running:
                    return doc
            except Exception:
                continue

        # 其次当前窗口里的那一份。
        try:
            current = self.desktop.getCurrentComponent()
            for doc in docs:
                if doc == current:
                    return doc
        except Exception:
            pass
        return docs[0]

    # ---- 读状态 ----

    def _page_index(self, doc, page):
        try:
            pages = doc.getDrawPages()
            count = pages.getCount()
            for index in range(count):
                try:
                    if pages.getByIndex(index) == page:
                        return index
                except Exception:
                    continue
        except Exception:
            pass
        return -1

    def read_state(self, doc):
        """返回协议字典（不含 updated/app），读不出来抛异常由调用方重连。"""
        url = ""
        try:
            url = doc.getURL()
        except Exception:
            pass
        path = file_url_to_path(url)

        total = 0
        try:
            total = int(doc.getDrawPages().getCount())
        except Exception:
            total = 0

        showing, presentation2 = self._running_state(doc)
        page = 0

        if showing and presentation2 is not None:
            # 放映态优先问 XSlideShowController：它给的是「屏幕上正在放的那一页」。
            controller = None
            try:
                controller = presentation2.getController()
            except Exception:
                controller = None
            if controller is not None:
                try:
                    page = int(controller.getCurrentSlideIndex()) + 1
                except Exception:
                    page = 0
                try:
                    count = int(controller.getSlideCount())
                    if count > 0:
                        total = count
                except Exception:
                    pass

        if page <= 0:
            # 非放映态（或拿不到放映控制器）：退回编辑视图的当前页。
            try:
                controller = doc.getCurrentController()
                current_page = controller.getCurrentPage()
                index = self._page_index(doc, current_page)
                if index >= 0:
                    page = index + 1
            except Exception:
                page = 0

        if total <= 0:
            total = max(page, 0)

        return {
            "file": path,
            "page": page,
            "total": total,
            "showing": bool(showing),
        }

    # ---- 跳页（插件 → command.json → 这里） ----

    def goto_slide(self, doc, page):
        """跳到第 page 页（1 起）。返回 (ok, detail)，永不抛。

        关键：XSlideShowController.gotoSlide() 的参数是 XDrawPage 对象，
        传整数会抛 CannotConvertException（2026-10-31 在 WSL 实测）。
        """
        try:
            pages = doc.getDrawPages()
            total = int(pages.getCount())
        except Exception as exc:
            return False, "no draw pages: %s" % exc
        if page < 1 or page > total:
            return False, "page %d out of range 1..%d" % (page, total)
        try:
            target = pages.getByIndex(page - 1)  # 0-based 索引
        except Exception as exc:
            return False, "cannot get page %d: %s" % (page, exc)

        showing, presentation2 = self._running_state(doc)
        if not showing or presentation2 is None:
            # 没在放映：退到编辑视图跳页。老师通常点了按钮才想起来没按 F5，
            # 这样至少页码对得上，按 F5 就从这一页开始。
            try:
                controller = doc.getCurrentController()
                controller.setCurrentPage(target)
                return True, "edit view setCurrentPage(%d)" % page
            except Exception as exc:
                return False, "not showing, setCurrentPage failed: %s" % exc

        try:
            controller = presentation2.getController()
        except Exception as exc:
            return False, "no slide show controller: %s" % exc
        if controller is None:
            return False, "no slide show controller"
        try:
            controller.gotoSlide(target)
        except Exception as exc:
            return False, "%s: %s" % (type(exc).__name__, exc)
        return True, "gotoSlide(%d)" % page


def apply_command(bridge, doc, out_path, state, last_tag, quiet=False):
    """执行一轮跳页命令。返回 (最新 state, 去重用的 tag)。

    去重不能只看文件 mtime：command.json 会一直留着，插件下一次跳同一页时会
    写新的 ts。所以用 (page, ts) 作为「这条命令」的身份；处理过就不再执行，
    老师自己用键盘翻页也不会被同一条旧命令反复拽回去。
    """
    command = read_command(command_path_for(out_path), quiet)
    if command is None:
        return state, last_tag

    tag = (command["page"], command["ts"])
    if tag == last_tag:
        return state, last_tag
    last_tag = tag  # 先记账：这条命令只执行一次，失败也不无限重试

    written_at = parse_ts(command["ts"])
    if written_at is not None:
        age = (datetime.now(timezone.utc) - written_at).total_seconds()
        if age > COMMAND_MAX_AGE_SECONDS:
            log("command: ignore stale jump to %d (%.0fs old)"
                % (command["page"], age), quiet)
            return state, last_tag
    elif command["ts"]:
        log("command: unparsable ts %r, still try page %d"
            % (command["ts"], command["page"]), quiet)

    page = command["page"]
    total = int(state.get("total") or 0)
    if page < 1 or (total > 0 and page > total):
        log("command: jump to %d -> fail (page out of range 1..%d)"
            % (page, max(total, 0)), quiet)
        return state, last_tag

    ok, detail = bridge.goto_slide(doc, page)
    if ok:
        log("command: jump to %d -> ok" % page, quiet)
        # 立刻重读一次：current.json 这一轮就写新页码，卡片最多 500ms 就能看到，
        # 而不是等下一轮轮询（也避免刚跳完就写旧页、被插件当成「没跳成功」）。
        try:
            state = bridge.read_state(doc)
        except Exception as exc:
            log("command: re-read after jump failed: %s" % exc, quiet)
    else:
        log("command: jump to %d -> fail (%s)" % (page, detail), quiet)
    return state, last_tag


def parse_args(argv):
    parser = argparse.ArgumentParser(
        description="ClassSlideSync LibreOffice Impress 页码适配器")
    parser.add_argument("--host", default=DEFAULT_HOST,
                        help="UNO host（默认 127.0.0.1；用 localhost 在部分系统上会先解析到 ::1）")
    parser.add_argument("--port", type=int, default=DEFAULT_PORT,
                        help="UNO 端口（默认 2002，要和 soffice --accept 一致）")
    parser.add_argument("--interval", type=float, default=DEFAULT_INTERVAL,
                        help="轮询秒数（默认 1.0）")
    parser.add_argument("--out", default=None,
                        help="输出 JSON（默认 ~/.local/share/classslidesync/current.json）")
    parser.add_argument("--once", action="store_true",
                        help="只读一次就退出（排障用；没有放映也返回 0，只要有演示文档）")
    parser.add_argument("--quiet", action="store_true", help="不打印状态日志")
    return parser.parse_args(argv)


def main(argv=None):
    args = parse_args(argv or sys.argv[1:])
    out_path = args.out or default_output_path()

    try:
        bridge = UnoBridge(args.host, args.port, args.quiet)
    except ImportError:
        sys.stderr.write(
            "错误：当前 Python 没有 uno 模块。请安装 python3-uno，并用 /usr/bin/python3 运行。\n"
            "  Ubuntu/Debian: sudo apt install python3-uno libreoffice-impress\n")
        return 2

    log("output: " + out_path, args.quiet)
    log("connecting to uno:socket,host=%s,port=%d ..." % (args.host, args.port),
        args.quiet)

    if args.once:
        try:
            bridge.connect()
        except Exception as exc:
            sys.stderr.write("错误：连不上 LibreOffice 的 UNO socket：%s\n" % exc)
            sys.stderr.write(
                "请确认 LibreOffice 是这样启动的：\n"
                "  soffice --impress --accept=\"socket,host=127.0.0.1,port=%d;urp;\" 课件.pptx\n"
                % args.port)
            return 3
        try:
            doc = bridge.pick_document()
            if doc is None:
                sys.stderr.write("错误：连上了，但没有打开的演示文档（Impress）。\n")
                return 3
            state = bridge.read_state(doc)
            state["updated"] = iso_now()
            state["app"] = APP_NAME
            write_json_atomic(out_path, state)
            print(json.dumps(state, ensure_ascii=False))
            return 0
        except Exception as exc:
            sys.stderr.write("错误：读取幻灯片状态失败：%s\n" % exc)
            return 3

    connected = False
    next_attempt = 0.0
    last_key = None
    last_command_tag = None  # 跨重连保留：同一条命令不会因为断线被执行两次
    command_path = command_path_for(out_path)
    log("command in: " + command_path, args.quiet)
    while True:
        now = time.monotonic()
        if not connected and now >= next_attempt:
            try:
                bridge.connect()
                connected = True
                log("connected", args.quiet)
            except Exception as exc:
                next_attempt = now + 2.0
                log("connect failed (retry in 2s): %s" % exc, args.quiet)

        if connected:
            try:
                doc = bridge.pick_document()
                if doc is None:
                    # 没有演示文档：不写文件，10 秒后插件自然判定过期。
                    # 这样老师从 Impress 切到浏览器时，卡片会自己收起而不是停在旧页码。
                    if last_key is not None:
                        log("no presentation document; data will go stale", args.quiet)
                        last_key = None
                else:
                    state = bridge.read_state(doc)
                    # 先处理插件下发的跳页命令（可能改写 state），再写 current.json：
                    # 这样跳页和页码更新在同一轮里完成，卡片不会先看到旧页码。
                    state, last_command_tag = apply_command(
                        bridge, doc, out_path, state, last_command_tag, args.quiet)
                    state["updated"] = iso_now()
                    state["app"] = APP_NAME
                    write_json_atomic(out_path, state)
                    key = (state["file"], state["page"], state["total"],
                           state["showing"])
                    if key != last_key:
                        log("page %d/%d showing=%s file=%s" % (
                            state["page"], state["total"], state["showing"],
                            state["file"]), args.quiet)
                        last_key = key
            except Exception as exc:
                # UNO 断线（LibreOffice 退出/重启）会在这里集中爆出来：标记断开重连。
                log("lost connection: %s" % exc, args.quiet)
                bridge.close()
                connected = False
                next_attempt = time.monotonic() + 2.0
                last_key = None

        time.sleep(max(0.1, args.interval))


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        sys.exit(0)
