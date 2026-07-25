# macOS Dark Glass UI Implementation Plan

**Goal:** 将课堂同传助手改造成 macOS 深色玻璃风，同时保留全部功能和双栏效率。

**Architecture:** 通过 WPF 应用资源集中定义玻璃卡片、胶囊按钮和输入控件；主窗口仅重排视觉结构并使用 WindowChrome，自有业务事件与控件名称保持兼容。

**Tech Stack:** .NET 8、WPF、XAML、WindowChrome。

## Tasks

- [x] 为核心主题资源与 macOS 标题栏添加失败测试。
- [x] 建立颜色、文字、玻璃卡片、按钮和输入控件资源。
- [x] 重构双栏布局并接入红黄绿窗口控制。
- [x] 渲染截图，修正下拉框文字对比。
- [x] 修复异步初始化与截图之间的时序竞争。
- [x] 运行完整测试、构建、发布和启动验收。
