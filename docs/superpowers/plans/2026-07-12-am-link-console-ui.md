# AM-LINK Console UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use executing-plans to implement this plan task-by-task. Steps use checkbox syntax for tracking.

**Goal:** 将现有课堂同传助手替换为 AM-LINK 黑金设备控制台界面，并保留全部业务功能。

**Architecture:** 继续使用纯 WPF。主题令牌和控件模板集中在 `App.xaml`，主窗口在 `MainWindow.xaml` 中重排为左侧模块、中央信息和右侧信号架；现有控件名称与事件不变，避免修改业务流程。

**Tech Stack:** .NET 8、WPF、XAML、WindowChrome。

## Global Constraints

- 产品名只使用 `AM-LINK`，署名为 `Developed by AppleMccree`。
- 不得出现 `DG-LAB`、`Coyote` 或 `郊狼`。
- 不改变翻译、录音、PPT、SQLite、Markdown、凭据或保留策略。
- 所有输入框内容必须垂直居中；最小窗口 940×640 可操作。

### Task 1: 品牌与主题测试

**Files:** Modify `tests/ClassInterpreter.Tests/Program.cs`

- [ ] 添加失败测试，检查 AM-LINK、AppleMccree、黑金主题资源和禁用品牌词。
- [ ] 运行测试，预期因资源和品牌尚未实现而失败。

### Task 2: 黑金主题资源

**Files:** Modify `src/ClassInterpreter.App/App.xaml`

- [ ] 将玻璃主题替换为近黑背景、暗金强调色、虚线次按钮、控制台输入框和状态卡片。
- [ ] 保留 `PART_ContentHost` 的 `Margin="12,0" VerticalAlignment="Center"`，防止文字裁切回归。
- [ ] 运行测试和 Release 构建，预期通过且 0 警告。

### Task 3: 三栏控制台布局

**Files:** Modify `src/ClassInterpreter.App/MainWindow.xaml`

- [ ] 顶栏加入 `AM-LINK / CLASSROOM OS` 和 QWEN LINK 状态。
- [ ] 左栏加入五个视觉模块并承载现有设置控件。
- [ ] 中栏保留 PPT、译文、原文、语言状态和两个运行按钮。
- [ ] 右栏加入 CHANNEL A/B、输入电平、语言方向和系统状态。
- [ ] 底部加入低权重 `Developed by AppleMccree`。

### Task 4: 截图与可用性修正

**Files:** Modify XAML only when screenshot evidence shows a defect.

- [ ] 构建后连续运行两次 `--render-ui`，确认完整且稳定。
- [ ] 人工检查课程名、Workspace、API Key、下拉框、字幕、按钮和署名无裁切。
- [ ] 对发现的问题编写回归断言后再修复。

### Task 5: 发布安装

**Files:** Update portable output and D 盘 source mirror.

- [ ] 运行完整测试与自包含 publish。
- [ ] 使用管理员安装脚本覆盖 D 盘主 DLL/XAML/测试文件。
- [ ] 比对发布 DLL SHA-256，运行 5 秒启动烟雾测试。
- [ ] 从现有桌面快捷方式打开 AM-LINK。
