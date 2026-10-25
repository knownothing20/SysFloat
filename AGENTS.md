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
- 根目录 exe 的"永远最新"由 `tools\deploy.ps1` 保证：发布 → 时间戳备份 → 停止实例 → 替换 → 重启 → 自检，并在 `local\deploy-log.txt` 记录版本/git/哈希。改完代码一律跑它部署。

## 构建与验证

- 源码 .NET 5（SDK 5.0.408），`net5.0-windows` WinForms；验证程序共 **166 项断言**：
  ```powershell
  dotnet build .\src\verification\SysFloat.Verification.csproj -c Release -r win-x64
  .\src\verification\bin\Release\net5.0-windows\win-x64\SysFloat.Verification.exe
  ```
- **改任何代码后：先跑全量验证，全过才部署/提交。** 测试失败要查明原因，禁止跳过。
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
