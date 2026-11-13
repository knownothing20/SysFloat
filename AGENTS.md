# SysFloat 项目工作约定（Agent 必读）

本文件是 AI 助手（ZCode 等）在本仓库工作时的强制约定。人工开发者也可参考。

## 语言与交流

- 与用户交流用中文；代码注释解释"为什么"而非"是什么"。

## 目录约定（单一工作区，勿改结构）

```
SysFloat.exe   根目录 exe = 部署目标，必须永远保持为最新构建
src\           源码（SysFloat 主项目 + verification 验证程序）
release\       发布输出（gitignored）
backup\        历史 exe/源码备份（gitignored）
local\         私有内容：内部任务记录、部署日志（gitignored，绝不提交）
docs\          公开文档与截图（进 git）
tools\         deploy.ps1 / package-folder.ps1 / privacy-check.ps1 / publish.bat
```

- 隐私红线：`local\`、`backup\`、`release\` 已被 .gitignore 排除；**任何推送前必须运行 `tools\privacy-check.ps1` 且 PASS**。
- 后续开发默认使用 GitHub Actions 编译和打包，根目录 exe 从对应提交的成功构建产物更新。部署按下载与校验 → 时间戳备份 → 停止实例 → 替换 → 重启 → 自检执行，在 `local\deploy-log.txt` 记录提交、构建运行及产物哈希。`tools\deploy.ps1` 当前包含本地编译步骤，使用云构建产物时需采用相应的部署入口。

## 备份与空间纪律（用户明确要求）

- **`backup\` 只保留最近 2 个部署备份**：`deploy.ps1` 生成新备份后自动删除更旧的，使目录恒 ≤2 项。旧版本回溯一律走 GitHub（Release 附件 + 提交历史），不在本机囤积。
- 构建中间产物（`src\*\bin`、`src\*\obj`、`release\packaging`、`release\win-x64-self-contained`）**用完即清**：它们随时可由一条命令重建，长期保留只浪费空间。`release\` 长期只留当前版本的 ZIP + sha256。
- 每隔一段时间用 `du -sh` 检查总量，异常膨胀（>500MB）时按上述规则清理。

## 构建与验证

- **默认线上构建**。这项项目约定适用于后续新会话。开始构建前读取当前 Actions 配置及运行状态，依据本次任务的提交、推送和构建授权执行。
- **首次接入状态**。2026-11-13 已确认仓库尚无 Actions 工作流。下一次需要构建时，先配置并验证 Windows 云端编译、打包及产物下载，再采用该流程交付，记录首次运行结果。
- **触发频率**。一轮代码修改和必要静态检查完成后，集中触发一次。文档、截图、项目规则调整跳过构建。提交前的小改动集中处理，构建结果通过提交 SHA 对应到当前源码。
- **结果复用与重试**。同一提交先查询已有运行，成功则复用产物，运行中则跟进至结束。失败先检查日志和原因，再进行有依据的修复或重试。云构建不可用时说明具体问题和可行回退方式，结合当前任务授权处理。
- **控制资源**。工作流采用显式触发或与代码相关的路径过滤，合并同一分支的重复运行，并设置合理的超时与产物保留期。
- **验收**。云端执行编译、打包和适配云环境的检查，本机负责真实显卡传感器、桌面交互及部署验收。现有完整验证含本机双显卡断言，云端运行前需要适配环境依赖，汇报中列明实际执行范围。交付前应完成对应提交的云端构建和所需本机验收。
- 源码 .NET 5（SDK 5.0.408），`net5.0-windows` WinForms。当前本机完整验证共 **575 项检查**，保留以下命令作为本机验证入口，按需要使用已有或下载的验证产物：
  ```powershell
  dotnet build .\src\verification\SysFloat.Verification.csproj -c Release -r win-x64
  .\src\verification\bin\Release\net5.0-windows\win-x64\SysFloat.Verification.exe
  ```
- 代码修改后按线上流程验证，必要时先将源码提交到当前任务允许的分支供云端构建。验证失败时查明原因，修复后重新验证；部署与交付依据已完成的构建和验收结果。
- 注意 shell 管道会吞退出码：验证命令单独执行并显式检查 `$LASTEXITCODE`，不要 `| tail` 后接 `&&`。

## 发布流程

1. 版本号策略：**升级 csproj Version 必须先获得用户明确确认**（有钩子拦截），两处 About 文本同步。
2. Release 附件两种：`SysFloat.exe`（146MB 直链）+ `SysFloat-vX.Y.Z.zip`（44.5MB 文件夹版，由 `tools\package-folder.ps1` 生成——含 WPF 剥离与 deps.json 修剪）+ 各自 sha256。
3. `gh release create` 后必须核对服务端 digest 与本地 SHA-256 一致。

## UI/代码约定

- 深色主题色板集中在 `UiTheme`（SysFloat 为 WidgetRenderer/DarkMenuRenderer 常量）；界面文案全部走 `UIStrings`（中/英双语键），新增用户可见文本必须加中英两键。
- 右键菜单条目一律 Tag=键名 驱动，勾选状态按 Tag 匹配，不按文字匹配。
- 高 DPI：进程 `SetProcessDPIAware`（Program.cs）+ 纯代码窗体在构建完成后按 `min(DPI 系数, 屏幕适配)` 手动 `Scale()`；运行期动态尺寸用同一系数换算。
- WinForms 停靠：内容(Fill) 最先加入、标题栏最后加入（停靠按后加先处理，顺序错=内容盖住边栏）。
- 窗口位置变化时钳回当前屏工作区，防内容出屏。

## 验证证据的纪律

- 优先客观手段：像素探针（预期坐标比对设计色值）、EnumWindows/GetWindowRect 窗口几何、PrintWindow 抓窗口内容、真实输入模拟（SendInput/mouse_event + 置前）。
- 图像分析服务可作参考但会误判：结论必须与代码事实/像素数据交叉核对，矛盾时以客观证据为准并如实报告。
- 如实汇报：失败就说失败，包括自己的失误（参数写反、管道吞退出码等教训都在本仓库发生过并记录于 local/tasks）。
