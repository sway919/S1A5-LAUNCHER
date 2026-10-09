# S1A5 启动器本体

S1A5 单机版的一键启动器（WPF · net48 单文件 EXE 约 1.8 MB，运行时用 Windows 自带的
.NET Framework 4.8，玩家零安装）。双击即自动定位服务端与客户端目录，
【开始游戏】= 起服务端 → 等端口就绪 → 拉起客户端进游戏。

仅供学习与研究，许可见 [LICENSE](LICENSE)（GNU AGPL-3.0），立场见 [NOTICE.md](NOTICE.md)。

> 本仓库是从 S1A5-oss 主仓库的 `启动器源码\` 拆出的独立副本（验收脚本在主仓库
> `tests\`，未随本仓库分发）。主仓库 `启动器和GM工具.md` 与 `交接文档.md` 有
> 完整的功能说明、启动速度数据与 UI 细节。

## 目录结构

```
启动器本体\
├── S1a5-Launcher.csproj     net48 WPF（Costura/Fody 把托管依赖内嵌进单文件 exe）
├── App.xaml(.cs)            入口；首次运行的使用协议门禁（防倒狗：逐字输入一句话）
├── MainWindow.xaml(.cs)     主窗（首页指挥台 / 状态板 / 日志胶囊 / 60US 式主题系统）
├── Core\                    AppSys（目录自动定位/自检）、ArtPalette+ColorScience
│                            （从背景原画实时取色的设计系统）、Flows、Ui、Converters
├── Services\
│   ├── ServerControl.cs     服务端进程识别（按命令行）、启停（Ctrl+C 优雅退出）、
│   │                        jar/jdk 探测（包内 jdk25 → PATH → JAVA_HOME）
│   ├── DllExtService.cs     DLL 扩展页的服务层：挂载/撤下 dll_ext.ini 插件、
│   │                        备份还原语义、extensions.json
│   ├── LauncherSettings.cs  本地设置（S1a5启动器.config.json，原子写）
│   └── ArchiveService.cs    存档三件套备份/还原/重置
├── Views\                   首页/存档/设置/说明 + 协议窗 + DLL 扩展页（DllExtView）
├── Themes\                  主题令牌与控件样式（Brushes/Fluent 深·浅两套）
├── BgDefault.jpg            内嵌默认背景原画（界面配色从它实时推导）
├── build.cmd                构建（→ bin\Release\net48\S1a5-Launcher.exe）
└── app.manifest / 图标EXE.ico / icon128.png / FodyWeavers.xml(.xsd)
```

## 构建

```bat
build.cmd                    :: dotnet build -f net48 -c Release
```

- 需要 .NET SDK（net48 目标）；Fody/Costura 由 NuGet 还原。
- 老的"拷进成品包"步骤改成了**可选**：旁边若有含 `server\*.jar` 的成品目录就拷过去，
  否则 exe 留在 `bin\Release\net48\`。
- 可选"秒开"版（自包含 ~74 MB，首窗 1.3s vs net48 的 3.4s）：

```bat
dotnet publish S1a5-Launcher.csproj -f net10.0-windows -c Release
```

## 运行需要什么

- **服务端 jar**（「服务端本体」仓库产物；放在 `server\` 或 `target\` 子目录都能被定位）
- **ACT5 客户端目录**（`DNF.exe + Script.pvf + A5.dll`；客户端补丁见「DLL扩展」仓库）
- **Java ≥ 25**：包内便携 jdk25 → PATH → JAVA_HOME → Program Files，都没有就
  引导安装包内 MSI
- 两种目录布局都支持（一键端式 / 整合包式），目录定位向上 6 级 + 横向 2 层。

## 功能一览

一键启动（服务端 → 客户端）/ 停止·重启服务端 / 拉起网页 GM（不随一键启动自动开）/
账号配置（默认 123456789）/ 存档备份·还原·重置 / DLL 扩展页（挂载·撤下·导入插件）/
使用协议门禁 / 主题与外观（深浅色、玻璃、原画取色）。

## 已知限制

- 服务端进程的**停止**依赖 AttachConsole+Ctrl+C；若 PATH 上的 java 是 shim
  （如 `javapath\java.exe`，它再套一层真 JVM 子进程），Ctrl+C 可能到不了真身，
  停止会失效（此时用任务管理器结束 java 进程树）。
- net48 首窗有 ~2.4-2.8s 的框架 JIT 地板，代码层救不回来（要快用 net10 发布）。
