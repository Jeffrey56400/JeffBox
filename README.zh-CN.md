> **中文说明** · [English](README.md)

# JeffBox

> 一个 450KB 的单文件 Windows 桌面工具箱 —— 待办 · 笔记 · 快捷启动器，三合一。

![.NET 9](https://img.shields.io/badge/.NET-9-512BD4) ![WPF](https://img.shields.io/badge/UI-WPF-0078D4) ![License](https://img.shields.io/badge/License-Apache--2.0-blue) ![Platform](https://img.shields.io/badge/Platform-Windows-0078D6) ![Dependencies](https://img.shields.io/badge/dependencies-zero-green)

**Todo · Notes · Launcher — three desktop essentials in one lightweight exe.**

| 待办（浅色） | 快捷启动 |
|:-:|:-:|
| ![Todo](docs/screenshots/todo-light.png) | ![Launcher](docs/screenshots/launcher-light.png) |

| 笔记 · 阅读模式 | 笔记 · 深色主题 |
|:-:|:-:|
| ![Notes](docs/screenshots/md-preview.png) | ![Dark](docs/screenshots/md-dark.png) |

## 为什么是 JeffBox

- **一个 exe，双击即用**：.NET 9 + WPF 原生开发，零外部依赖，单文件发布约 450KB
- **数据 100% 留在本机**：所有内容保存在 `%APPDATA%\JeffBox`，无账号、无云同步、无遥测
- **常驻后台，随叫随到**：托盘常驻 + 四路全局热键（主热键呼出窗口，另可为待办 / 笔记 / 启动器各绑一个直达热键）
- **性能优先**：3.5MB+ 的 Markdown 文档秒开（流式分块渲染），长待办列表 UI 虚拟化，空闲内存占用低
- **深色 / 浅色主题 + 中英双语**，界面细节按现代桌面规范打磨（圆角卡片、覆盖式细滚动条、流畅动画）

## 功能一览

### ✅ 待办 Todo

- 任务树：子任务无限嵌套，默认展开，一键全部展开/收起
- 优先级（无/低/中/高，列表彩条标识）、截止时间、到点提醒（应用内横幅）
- 详情支持 Markdown-lite，可直接 `Ctrl+V` 粘贴图片（自动检测剪贴板空白图并提示）
- 筛选（全部/待办/已完成）、显示完成进度、悬停卡片预览

### 📝 笔记 Notes（Markdown）

- 同屏编辑 ⇄ 预览切换，对标 Typora 的轻量阅读体验
- 双击 `.md` 文件可直接用 JeffBox 打开（可选注册文件关联，可随时还原）
- 完整语法：标题/表格/代码块/引用/任务列表/有序无序列表（多层）/分割线/删除线/==高亮==/脚注/行内与块级数学公式
- 大文件流式渲染：首屏 300 块，滚动到底自动续载；超大文件自动转只读预览
- 编码自适应：BOM → UTF-8 → GB18030，GBK 文档不乱码；保存统一 UTF-8，原子写盘不损坏

### ⚡ 快捷启动 Launcher

- 磁贴墙：**单击即启动**，自动提取系统图标（含 `.lnk`；目标被卸载时回退通用图标）
- **拖入即添加**：程序、快捷方式、文件、文件夹直接拖进窗口
- **多分类页签**：`＋` 新建，右键重命名/排序/删除；磁贴拖到页签上跨分类移动
- **拖动排序** 或按**启动频次自动排序**（悬停磁贴可见累计次数）
- 记住上次停留的分类与排序方式，旧版平铺数据自动迁移
- 笔记内置**最近打开文档**列表，一键回到上一份文档

### 全局能力

- 托盘常驻（可切换"关闭即退出"）、开机自启（可选）、单实例（二次启动传递参数）
- **可自定义全局热键**：设置中点输入框直接按组合即录入；注册失败时三层冲突识别——常见软件默认热键知识库（微信/QQ/钉钉/网易云/Snipaste 等，运行中的给强提示）→ **按键响应取证**（按下时哪个程序抢了前台就点名谁）→ 通用指引；系统保留组合（Alt+F4 等）自动拦截
- 窗口状态记忆：位置/尺寸/最大化，多显示器拔插不丢窗口

## 快速开始

**方式一：下载发布版**

到 [Releases](../../releases) 下载 `JeffBox.exe`，双击运行。需要 .NET Desktop Runtime 9.x（多数 Win10/11 已自带或自动提示安装）。

**方式二：从源码构建**

```bash
git clone https://github.com/Jeffrey56400/JeffBox.git
cd JeffBox

# 调试运行
dotnet run

# 发布单文件 exe（约 450KB）
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

要求：.NET SDK 9.0+，Windows 10 1809+。

## 默认快捷键

| 操作 | 默认 | 说明 |
| --- | --- | --- |
| 呼出 / 隐藏窗口 | `Ctrl+Alt+T` | 主热键，可在设置中自定义任意组合 |
| 直达待办 / 笔记 / 启动器 | 未绑定 | 设置中按组合即录入，如 `Ctrl+Alt+D` |
| 笔记：新建 / 打开 / 保存 / 编辑⇄预览 | `Ctrl+N / O / S / E` | |
| 启动器：编辑 / 删除磁贴 | `F2 / Delete` | 单击磁贴即启动 |

## 数据与隐私

- 全部数据保存在本机 `%APPDATA%\JeffBox`（`todos.json` / `tools.json` / `settings.json` / `attachments\`），删除该文件夹即完全卸载数据
- 无网络请求、无遥测、无自动更新；唯一的外部行为是你主动打开链接
- 保存均使用「临时文件 + 原子替换」，断电/崩溃不会损坏数据；检测到损坏时自动备份 `.corrupt-*` 并停止覆盖写入

## 项目结构

```
JeffBox/
├── MainWindow.xaml(.cs)      # 导航壳：自定义标题栏、托盘、热键、设置浮层
├── TodoViewModel.cs          # 待办树 ViewModel（子任务同步、筛选、面包屑）
├── MarkdownLite.cs           # 流式 Markdown 渲染器（惰性迭代器分块产出）
├── Models/TodoItem.cs        # 任务树数据模型（自动迁移旧格式）
├── Services/
│   ├── TodoStorage.cs        # 待办持久化（原子写、损坏保护、迁移）
│   ├── LauncherStorage.cs    # 启动器持久化（多分类结构）
│   ├── AppSettings.cs        # 设置持久化（窗口状态、热键、主题语言）
│   ├── Theme.cs / Loc.cs     # 深浅主题调色板 / 中英双语词条
│   ├── HotkeyInfo.cs         # 热键解析、保留组合校验、冲突知识库
│   ├── IconExtract.cs        # Shell 图标提取（含 .lnk 与缺失回退）
│   ├── Attachments.cs        # 待办图片附件（防路径逃逸、孤儿清理）
│   └── ...
└── Views/
    ├── MdToolView.*          # 笔记：编辑/预览同屏、分块续载、编码检测
    ├── LaunchToolView.*      # 启动器：磁贴、拖拽排序、多分类页签
    ├── HotkeyBox.*           # 可复用热键捕获框（低级键盘钩子）
    └── InputDialog.cs        # 轻量文本输入对话框
```

## 技术亮点

- **流式 Markdown 渲染**：`EnumerateBlocks()` 惰性迭代器按块产出 UI 元素，首屏只渲染 300 块、滚动近底续载，3.5MB+ 文档秒开且内存平稳
- **热键系统的三层防御**：`RegisterHotKey` 失败 → 知识库匹配（常见软件默认热键 × 运行进程佐证）→ **按键响应取证**（低级键盘钩子记录按键分发前后的前台变化，真实点名占用者）——Windows 不提供热键占用方查询 API，这是无侵入的近似定位方案
- **WPF 自定义标题栏的边角打磨**：WindowChrome 最大化时按系统指标（`SM_CXSIZEFRAME + SM_CXPADDEDBORDER`）动态补偿边距，避免内容被裁 8px 的经典问题
- **剪贴板图像防御链**：PNG 原始字节优先 → DIB 丢 alpha 检测（假透明图丢弃 alpha 通道）→ 全空白图用户提示
- **零依赖单文件**：所有功能（托盘、热键、Markdown、图标提取、编码检测）均由 BCL/P-Invoke 实现，无第三方 NuGet 包

## Roadmap

- [ ] 导出 / 导入（todos 备份、笔记批量）
- [ ] 待办日历视图
- [ ] 笔记侧边大纲与全文搜索
- [ ] 启动器磁贴图标自定义

欢迎在 Issues 提需求与反馈。

## 贡献

欢迎 PR。建议：改动附截图/复现步骤；新功能保持"零依赖 + 全本地"两条底线。

## License

[Apache-2.0](LICENSE) © 2026 Jeffrey56400
