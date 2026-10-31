#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""WSL 验证驱动：连上已经带 --accept 启动的 LibreOffice，用 UNO 加载 10 页课件，
进入放映、翻到第 4 页，然后调用真正的 slidesync_lo.py --once，最后截图。

用法（在 WSL 里，DISPLAY 已指向 Xvfb）：
    /usr/bin/python3 test/lo_drive.py
"""

import os
import subprocess
import sys
import time

import uno

HOST = "127.0.0.1"
PORT = 2002
PPTX = "/root/plugin/test/纯页码课件.pptx"
ADAPTER = "/root/plugin/tools/linux-adapters/libreoffice/slidesync_lo.py"
OUT = "/root/.local/share/classslidesync/current.json"
SHOT = "/tmp/slidesync-shot.png"


def log(msg):
    print("[drive] " + msg, flush=True)


def main():
    local = uno.getComponentContext()
    resolver = local.ServiceManager.createInstanceWithContext(
        "com.sun.star.bridge.UnoUrlResolver", local)
    ctx = resolver.resolve(
        "uno:socket,host=%s,port=%d;urp;StarOffice.ComponentContext" % (HOST, PORT))
    desktop = ctx.ServiceManager.createInstanceWithContext(
        "com.sun.star.frame.Desktop", ctx)
    log("connected")

    url = uno.systemPathToFileUrl(PPTX)
    log("loading " + url)
    doc = desktop.loadComponentFromURL(url, "_blank", 0, ())
    if doc is None:
        log("loadComponentFromURL returned None")
        return 1
    log("loaded, pages=%d url=%s" % (doc.getDrawPages().getCount(), doc.getURL()))

    pres = doc.getPresentation()
    log("starting presentation")
    pres.start()

    # pyuno 没有 uno.queryInterface；对象本身是 XPresentation2 时可直接调 isRunning，
    # 否则用 queryInterface + getTypeByName。
    p2 = pres
    try:
        p2.isRunning()
    except AttributeError:
        p2 = pres.queryInterface(
            uno.getTypeByName("com.sun.star.presentation.XPresentation2"))
    if p2 is None:
        log("no XPresentation2")
        return 1

    deadline = time.time() + 10
    while time.time() < deadline:
        if p2.isRunning():
            break
        time.sleep(0.3)
    if not p2.isRunning():
        log("presentation did not start")
        return 1
    log("presentation running")

    controller = p2.getController()
    if controller is None:
        log("no controller")
        return 1
    log("slide count=%d index=%d" % (controller.getSlideCount(), controller.getCurrentSlideIndex()))
    # XSlideShowController.gotoSlide 要的是 XDrawPage，不是序号。
    # 第 4 页 = DrawPages 里 0 基的 index 3。
    controller.gotoSlide(doc.getDrawPages().getByIndex(3))
    time.sleep(1.5)
    log("after goto index=%d" % controller.getCurrentSlideIndex())

    verify_home = os.environ.get("VERIFY_HOME")
    out_path = OUT
    adapter_args = ["/usr/bin/python3", ADAPTER, "--once"]
    if verify_home:
        out_path = os.path.join(verify_home, "classslidesync", "current.json")
        adapter_args += ["--out", out_path]
    log("running adapter --once (defaults: 127.0.0.1:2002, out=%s)" % out_path)
    result = subprocess.run(adapter_args, capture_output=True, text=True)
    log("adapter rc=%d" % result.returncode)
    if result.stdout.strip():
        log("adapter stdout: " + result.stdout.strip())
    if result.stderr.strip():
        log("adapter stderr: " + result.stderr.strip())

    if os.path.exists(out_path):
        with open(out_path, "r", encoding="utf-8") as handle:
            log("current.json: " + handle.read().strip())
    else:
        log("current.json missing: " + out_path)

    if verify_home and os.environ.get("RUN_PLUGIN_PROBE") == "1":
        log("running LinuxSlideSource probe with XDG_DATA_HOME=" + verify_home)
        env = os.environ.copy()
        env["XDG_DATA_HOME"] = verify_home
        probe = subprocess.run(
            ["dotnet", "run", "--project", "/tmp/probe/LinuxSourceProbe.csproj"],
            capture_output=True, text=True, env=env, timeout=120)
        log("plugin probe rc=%d" % probe.returncode)
        log("plugin probe stdout:\n" + probe.stdout.strip())
        if probe.stderr.strip():
            log("plugin probe stderr: " + probe.stderr.strip()[-2000:])

    try:
        shot = subprocess.run(["import", "-window", "root", SHOT],
                              capture_output=True, text=True, timeout=20)
        log("screenshot rc=%d path=%s stderr=%s" % (
            shot.returncode, SHOT, shot.stderr.strip()))
    except Exception as exc:
        log("screenshot failed: %s" % exc)

    # 留一点时间给外部再截图；最后结束放映并关文档，让脚本能退出。
    hold = float(os.environ.get("HOLD_SECONDS", "0") or "0")
    if hold > 0:
        log("holding presentation for %.0fs for screenshot" % hold)
        time.sleep(hold)
    else:
        time.sleep(3)
    try:
        p2.end()
    except Exception:
        pass
    time.sleep(0.5)
    try:
        doc.close(False)
    except Exception:
        pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
