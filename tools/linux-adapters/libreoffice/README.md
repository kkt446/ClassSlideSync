# LibreOffice Impress 页码适配器（Linux）

ClassSlideSync 插件在 Linux 上没有 COM，也没有希沃 MCP，所以页码改由**适配器读演示软件、
写一个 JSON 文件**提供。本目录的脚本负责 LibreOffice Impress 这一路：

```
LibreOffice Impress ──UNO socket(2002)──> slidesync_lo.py ──每秒写──> ~/.local/share/classslidesync/current.json
          ▲                                    │                                  │
          │                                    │ <──读── command.json <──写── 插件卡片按钮
          └──────── UNO gotoSlide ─────────────┘        （每轮轮询）      （点「从第 N 页接着讲」）
```

## 1. 安装

Ubuntu / Debian：

```bash
sudo apt update
sudo apt install -y libreoffice-impress python3-uno
```

- `python3-uno` 提供 `import uno`，必须装在**系统 Python** 里；
- 如果你用 conda / venv / pyenv，`/usr/bin/python3` 仍然能 import uno，但 `python3` 可能是别的解释器。
  所以本目录的 `run.sh` 写死了 `/usr/bin/python3`。

确认：

```bash
/usr/bin/python3 -c "import uno; print('uno ok')"
```

## 2. 让 LibreOffice 监听 UNO socket

**关键点：`--accept` 只在 LibreOffice 启动时生效。** 如果 LibreOffice 已经在运行，再执行
带 `--accept` 的命令只会把文件丢给已有实例，不会新开监听端口。

推荐先全部退出，再用一条命令同时打开课件并监听：

```bash
pkill -f soffice || true
soffice --impress \
  --accept="socket,host=127.0.0.1,port=2002;urp;" \
  "/home/teacher/课件/第3节.pptx"
```

如果希望保留自己的 LibreOffice 配置、又不想和日常实例抢锁，用独立 profile：

```bash
soffice --impress \
  -env:UserInstallation=file:///tmp/lo-slidesync-profile \
  --accept="socket,host=127.0.0.1,port=2002;urp;" \
  "/home/teacher/课件/第3节.pptx"
```

验证端口通了：

```bash
ss -ltnp | grep 2002
# 或
/usr/bin/python3 - <<'PY'
import uno
local = uno.getComponentContext()
r = local.ServiceManager.createInstanceWithContext(
    "com.sun.star.bridge.UnoUrlResolver", local)
ctx = r.resolve("uno:socket,host=127.0.0.1,port=2002;urp;StarOffice.ComponentContext")
print("connected", ctx is not None)
PY
```

> **为什么用 `127.0.0.1` 而不是 `localhost`**：LibreOffice 的 socket 只监听 IPv4
> `127.0.0.1`，而部分系统（包括本项目的 WSL Ubuntu 24.04）把 `localhost` 先解析成 `::1`，
> 适配器会报 `Connection refused`。`--accept` 和适配器两边都写 `127.0.0.1` 最稳；
> 只想用 `localhost` 的话，给适配器加 `--host 127.0.0.1`。

> 以 root 跑（例如本项目的 WSL 验证环境）时，加上 `--norestore --nologo` 更稳，
> 并且要确保 `HOME` 已设置。日常教师桌面不需要。

## 3. 启动适配器

```bash
cd tools/linux-adapters/libreoffice
./run.sh
# 或
/usr/bin/python3 slidesync_lo.py
```

默认：

- 连 `127.0.0.1:2002`（`--host` 可改；不要依赖 `localhost` 的 IPv6 解析）；
- 每 1 秒写一次 `~/.local/share/classslidesync/current.json`；
- 同时尊重 `$XDG_DATA_HOME`（和插件一致）。

常用参数：

```bash
./run.sh --port 2003                 # 换端口
./run.sh --interval 0.5              # 轮询间隔
./run.sh --out /tmp/current.json     # 换输出文件（排障）
./run.sh --once                      # 只读一次并打印 JSON，然后退出
./run.sh --quiet                     # 不打印状态
```

`--once` 很适合排障：

```bash
./run.sh --once
# {"file":"/root/plugin/test/纯页码课件.pptx","page":4,"total":10,"showing":true,"updated":"...","app":"libreoffice"}
```

看到上面的输出，就说明协议这一端是通的；再让插件读同一个文件即可。

## 4. 输出协议

```json
{"file":"/home/teacher/第3节.pptx","page":4,"total":10,"showing":true,
 "updated":"2026-10-31T07:00:00Z","app":"libreoffice"}
```

| 字段 | 含义 |
| --- | --- |
| `file` | 课件绝对路径（已把 `file:///...` 转成系统路径） |
| `page` | 当前页，从 1 开始；0 = 还没拿到 |
| `total` | 总页数 |
| `showing` | true = 正在全屏放映；false = 只是打开了文档 |
| `updated` | 写文件的 UTC 时间，插件超过 10 秒没看到新值就当没数据 |
| `app` | 固定 `libreoffice` |

写入用「临时文件 + `os.replace`」，插件不会读到半截 JSON。

## 5. 跳页：command.json（插件 → 适配器）

`current.json` 是**适配器 → 插件**的只读通道；跳页是反方向，走同目录的 `command.json`：

```json
{"page":3,"ts":"2026-10-31T07:00:00.123Z"}
```

| 字段 | 含义 |
| --- | --- |
| `page` | 目标页，从 1 开始；必须在 `1..total` 内 |
| `ts` | 插件写命令的 UTC 时间（ISO-8601，带毫秒，`Z` 结尾） |

适配器（`slidesync_lo.py`，watch 模式）每一轮：

1. 读 `command.json`；没有文件就跳过，什么也不做；
2. 用 `(page, ts)` 判断是不是「新命令」——同一个文件会一直留着，处理过的命令不再执行，
   所以老师自己用键盘/翻页器翻页不会被一条旧命令反复拽回去；
3. `ts` 超过 **2 分钟**的命令当成历史遗留，只记一行日志、不执行；
4. `page` 不在 `1..total` 内 → 记 `command: jump to N -> fail (...)`，不跳；
5. 否则调用 `XSlideShowController.gotoSlide(XDrawPage)`：
   - **必须传 XDrawPage 对象**（`pages.getByIndex(page - 1)`）。传整数（如 `gotoSlide(3)`）
     会抛 `com.sun.star.lang.IllegalArgumentException: CannotConvertException`，这是实测过的坑；
   - 没在放映（没按 F5）时退回编辑视图 `controller.setCurrentPage(XDrawPage)`，
     页码同样会变，老师再按 F5 就从这一页开始。
6. 成功后**立刻重读并重写 `current.json`**（刷新 `updated`、写新 `page`），
   插件的卡片在 500ms 内就能看到「跳过去了」，不用等下一轮 1 秒。

日志（stderr）长这样：

```
[slidesync-lo 16:42:10] command: jump to 3 -> ok
[slidesync-lo 16:42:10] page 3/10 showing=true file=/root/plugin/test/纯页码课件.pptx
```

排障时手动写一条命令即可（`ts` 用当前 UTC 时间，过期会被跳过）：

```bash
python3 - <<'PY'
import json, os, datetime
d = os.path.expanduser("~/.local/share/classslidesync")
os.makedirs(d, exist_ok=True)
ts = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.000Z")
json.dump({"page": 3, "ts": ts}, open(os.path.join(d, "command.json"), "w"))
PY
```

插件侧的写入口是 `Core/LinuxSlideSource.cs` 的 `TryWriteCommand(page, out error)`：
临时文件 + `File.Move(overwrite: true)`，适配器读不到半截 JSON；写失败会把原因交给
`SlideSyncService`，日志记 `linux jump: 下发失败（原因）`、卡片状态行同时保留提示。

> 注意：适配器只跳**当前窗口里那份**演示文稿。插件从历史里点另一份课件时会直接拒绝
> （`Linux 上只能跳当前放映的课件`），不会写命令 —— Linux 上没有 COM 去开新文档。

## 6. 行为细节 / 限制

- **放映态**：通过 `XPresentation2.isRunning()` 判断；当前页优先用
  `XSlideShowController.getCurrentSlideIndex()`，拿不到时退回编辑视图的当前页。
- **没有打开的演示文档**：不写新文件。10 秒后插件判定数据过期，卡片收起页码——
  这是故意的，避免老师切到浏览器后卡片还停在旧页。
- **LibreOffice 退出/重启**：脚本每 2 秒重连一次，不需要手工重启适配器。
- **只能连「启动时带 `--accept`」的实例**。如果老师习惯双击文件打开 Impress，
  那个实例没有 socket，适配器连不上。解决办法：用一个包装器/桌面快捷方式统一带
  `--accept` 启动（见上面的命令），或者接受「先启动 LibreOffice 再打开课件」的流程。
- **不要用 pip 的 `unoconv` 之类的包替代本脚本**：`unoconv` 是文档转换工具，不提供
  实时页码；`python3-uno` 才是需要的那个。
- VCL 插件：WSL/无显示器环境要配 Xvfb（见项目报告里的验证步骤）；正常桌面不用管。
- **跳页只在 watch 轮询模式生效**：`--once` 只读一次、不处理 `command.json`（排障用）。

## 7. 开机自启（可选）

如果希望登录桌面后适配器常驻，可以放一个 systemd user unit：

```ini
# ~/.config/systemd/user/classslidesync-lo.service
[Unit]
Description=ClassSlideSync LibreOffice page bridge
After=graphical-session.target

[Service]
ExecStart=%h/ClassSlideSync.Plugin/tools/linux-adapters/libreoffice/slidesync_lo.py
Restart=on-failure
RestartSec=2

[Install]
WantedBy=default.target
```

```bash
systemctl --user daemon-reload
systemctl --user enable --now classslidesync-lo.service
journalctl --user -u classslidesync-lo -f
```

注意 `%h/ClassSlideSync.Plugin` 要换成实际安装路径；`ExecStart` 里的脚本需要可执行权限
（`chmod +x slidesync_lo.py`）。
