# WPS Linux 页码适配器（WPS JS 加载项）

> **⚠️ 这条路径没有在 WPS Linux 上实测过。**
> 开发/验证环境里没有 WPS Linux（只有 LibreOffice），所以本目录的 `main.js` / `ribbon.xml`
> 是按 WPS 公开的 JS 加载项 API 写的，**没有在真实 WPS Linux 进程里跑过**。
> 如果 WPS 版本不认这些 API（尤其 `wps.WppApplication()`、`SlideShowWindows`、
> `wps.Env.GetHomePath()`、`wps.FileSystem.writeFileString`），需要按实际版本调整；
> 排障方法见文末。LibreOffice 那条路是实测过的，WPS 这条请当作「待验证的参考实现」。

作用与 LibreOffice 适配器相同：每秒把当前演示文稿的页码写到

```
~/.local/share/classslidesync/current.json
```

插件（`Core/LinuxSlideSource.cs`）每 500ms 读一次，超过 10 秒没更新就认为没有数据。

跳页方向相反：插件点卡片按钮时写同目录的 `command.json`
（`{"page":3,"ts":"2026-10-31T07:00:00.123Z"}`），本加载项每拍读一次，发现新命令且正在放映时
调用 `Application.SlideShowWindows.Item(1).View.GotoSlide(page)`（1 起），然后立刻把新页码
写进这一拍的 `current.json`。协议细节与 LibreOffice 版一致，见
`../libreoffice/README.md` 第 5 节。**注意：`View.GotoSlide` 在 WPS Linux 上未实测**，
如果该版本没有这个方法，日志会打印 `command: jump to N -> fail (...)`，按第 4 节用调试器
确认实际方法名后改 `ClassSlideSyncApplyCommand()`。

## 1. 文件

| 文件 | 作用 |
| --- | --- |
| `main.js` | 全部逻辑：取 WPS 演示对象、读页码、写 JSON、1 秒定时器 |
| `ribbon.xml` | 功能区「课件页码」选项卡：开始 / 停止 / 立即写一次 |
| `publish.xml.example` | 加载项注册文件示例（复制到 jsaddons 根目录并改路径） |

## 2. 安装

WPS Linux 的 JS 加载项目录是：

```
~/.local/share/Kingsoft/wps/jsaddons/
```

以用户 `teacher` 为例：

```bash
mkdir -p ~/.local/share/Kingsoft/wps/jsaddons/ClassSlideSync
cp main.js ribbon.xml ~/.local/share/Kingsoft/wps/jsaddons/ClassSlideSync/
```

然后在 **jsaddons 根目录**（不是 `ClassSlideSync/` 里面）放注册文件
`publish.xml`。可以直接复制示例：

```bash
cp publish.xml.example ~/.local/share/Kingsoft/wps/jsaddons/publish.xml
# 编辑 publish.xml，把 /home/teacher 换成你的实际家目录
```

示例内容：

```xml
<?xml version="1.0" encoding="UTF-8"?>
<jsplugins>
  <jspluginonline enable="enable_dev"
                  type="wpp"
                  name="ClassSlideSync"
                  url="file:///home/teacher/.local/share/Kingsoft/wps/jsaddons/ClassSlideSync/" />
</jsplugins>
```

- `type="wpp"`：只加载到 WPS 演示（WPP）进程；文字/表格不会加载它。
- `enable="enable_dev"`：开发模式，允许从本地目录加载。
- `url` 必须指向**目录**且以 `/` 结尾；路径里有空格/中文要做 URL 编码。
- 部分 WPS 版本用 `jsplugins.xml` 而不是 `publish.xml`，根元素同样是 `<jsplugins>`；
  如果 `publish.xml` 不生效，按你那个版本的官方文档改成 `jsplugins.xml` 再试。
  新版 WPS 对 `jsplugins.xml` 有限制，优先 `publish.xml`。

安装后**完全退出 WPS 再启动**（包括托盘/后台进程），打开一个 `.pptx`：

- 功能区应出现「课件页码」选项卡；
- 不点按钮也会在加载 1.5 秒后自动开始同步（`main.js` 末尾的 `setTimeout`）；
- 也可以点「开始同步」。

## 3. 验证

打开一个 10 页的演示文稿，进入放映、翻到第 4 页，然后在终端：

```bash
cat ~/.local/share/classslidesync/current.json
```

期望：

```json
{"file":"/home/teacher/课件/第3节.pptx","page":4,"total":10,"showing":true,
 "updated":"2026-10-31T07:00:00Z","app":"wps"}
```

- 编辑态（没在放映）也会写，`showing` 为 `false`，`page` 是当前编辑窗口看到的那一页。
- 关掉演示文稿后不再写新文件；约 10 秒后插件判定过期，卡片收起页码。
- 写入路径遵循 `wps.Env.GetHomePath()`；若你的 WPS 返回的不是家目录，
  `main.js` 里的 `ClassSlideSyncDataDir()` 会拼错，可直接改成绝对路径
  `/home/你的用户名/.local/share/classslidesync`。

## 4. 排障

1. **ALT+F12** 打开 WPS 的 JS 调试器，看 `[ClassSlideSync]` 日志。
2. 在 Console 里逐条确认 API：

   ```js
   wps.Env.GetHomePath()
   wps.WppApplication()
   wps.WppApplication().ActivePresentation.FullName
   wps.WppApplication().Slides.Count
   wps.WppApplication().SlideShowWindows.Count
   wps.WppApplication().SlideShowWindows.Item(1).View.Slide.SlideIndex
   ```

   哪一条报 `undefined`/异常，就说明该版本 WPS 的字段名不同，需要改 `main.js`
   的 `ClassSlideSyncReadState()`。
3. 看加载项有没有被加载：WPS 的「工具 / 开发工具 / JS 加载项」里应能看到
   `ClassSlideSync`；没有就检查 `publish.xml` 的路径、`type`、`enable`。
4. 确认输出目录：

   ```bash
   ls -l ~/.local/share/classslidesync/
   ```

   目录不存在时 `main.js` 会尝试 `wps.FileSystem.Mkdir` 创建；失败会在日志里打印。

## 5. 已知限制 / 与 LibreOffice 实现的差异

- **没有原子写**：`wps.FileSystem` 没有 rename，`writeFileString` 直接覆盖目标文件。
  如果插件正好在半截 JSON 时读取，会解析失败并丢弃这一拍，500ms 后重试；
  不会抛异常，但理论上可能闪一帧「无页码」。
- **不写心跳**：没有打开的演示文稿时完全不写文件，让 `updated` 自然过期。
  这样老师切到浏览器后卡片不会停在旧页；代价是 WPS 崩溃/被杀后最多 10 秒才收起。
- **页码基准**：按 WPS 兼容 PowerPoint 的对象模型，`SlideIndex` 从 1 开始，和协议一致。
  如果某个 WPS 版本返回 0 基，需要 `+1`。
- **放映窗口**：用 `SlideShowWindows.Count > 0` 判断放映。WPS Linux 若把放映实现成
  另一个进程/窗口，这里可能拿不到；届时用 `Application.PresentationInShow` 之类
  的替代判断（不同版本字段不同）。
- **跳页未实测**：`command.json` 轮询与 `View.GotoSlide` 是按 PowerPoint 兼容对象模型写的，
  没有在 WPS Linux 里真跑过；LibreOffice 那条路的跳页是实测过的（见
  `../libreoffice/README.md`）。WPS 若只读不跳，先看日志里的 `command: jump to ... -> fail`。
- **WPS Linux 的加载项支持本身**：WPS for Linux 各发行版（社区版/专业版）对 JS 加载项
  的支持不一致，有的版本根本没有 `jsaddons` 目录。**这一点未实测**，装之前先用
  `ls ~/.local/share/Kingsoft/wps/` 确认存在 `jsaddons`，否则这条适配器无法使用，
  请改用 LibreOffice 或等 WPS 侧支持。

## 6. 卸载

```bash
rm -rf ~/.local/share/Kingsoft/wps/jsaddons/ClassSlideSync
rm -f  ~/.local/share/Kingsoft/wps/jsaddons/publish.xml   # 如果只给本加载项用过
```
