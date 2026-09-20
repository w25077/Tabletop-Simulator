# Tabletop Simulator — 2D 桌游玩法原型验证工作台

一个**单人自用**的 2D 桌游玩法原型工具。不是要发布给玩家的产品，目标只有一个：
**快速搭出一个桌游玩法原型，然后反复试玩、改数值、回退，验证它好不好玩。**

因此取舍是刻意为之的：

| 优先 | 不做 |
|---|---|
| 造卡快（文本/数值驱动，不必做美术） | 网络联机 |
| 布桌快、重开快 | 玩家权限 / 隐藏信息校验 |
| 改一个数值 → 立刻试玩 | 物理引擎（骰子不用碰撞） |
| 操作错了能立刻回退 | 美术打磨、发布打包 |

---

## 当前状态

**M1 已完成并通过端到端验证**（详见下方「怎么验证」）。

| 里程碑 | 内容 | 状态 |
|---|---|---|
| **M1** | 工程骨架、相机缩放平移、桌面背景+网格、中文字体 | ✅ 完成 |
| M2 | 物件系统：卡牌 / Token / 骰子，拖拽 / 旋转 / 翻转 / 堆叠，模板合成卡面，无物理骰子 | 待开始 |
| M3 | 区域系统：牌库 / 手牌 / 弃牌堆、自动堆叠、抽牌洗牌、盖放显示卡背 | 待开始 |
| M4 | 命令模式撤销 / 操作日志 / `user://saves` 存档管理 | 待开始 |
| M5 | 运行时编辑器（F1）：导入图片 / 造卡 / 组卡组 / 设桌面 / 画区域 | 待开始 |
| M6 | 打磨：示例存档、快捷键总览、性能 | 待开始 |

---

## 怎么跑

**编辑器里**：用 Godot 4.7.2 **.NET（mono）版**打开本目录，按 F5。

**命令行**：

```powershell
dotnet build TabletopSimulator.csproj
& "D:\SteamLibrary\steamapps\common\Godot Engine\Godot_v4.7.2-stable_mono_win64_console.exe" --path .
```

> 必须用 **.NET 版** Godot。标准版跑不了 C#。

### 键鼠操作（M1 已可用）

| 操作 | 按键 |
|---|---|
| 缩放（以鼠标位置为锚点） | 滚轮 |
| 平移画布 | 中键拖拽 / 右键拖拽 / `空格`+左键拖拽 |
| 适配整张桌面 | `Home` |
| 复位到 100% | `Ctrl`+`Home` |

> `tt_rotate_cw` / `tt_flip` / `tt_undo` / `tt_save` 等动作已在 InputMap 里登记好，
> M2–M4 直接接上即可，目前按下无效果。

---

## 怎么验证

不靠肉眼看图，靠**可复现的机械断言**：

```powershell
powershell -File tools/dev/shot.ps1 -Name m1_board -Frames 40
```

它会拉起游戏、等 N 帧、截图、跑自检、退出（**不需要窗口焦点**，可反复执行）。产出三份东西：

- `.dev/shots/m1_board.png` —— 画面
- `...png.meta.json` —— 分辨率 / 采样颜色数 / 平均亮度
- `...png.report.json` —— 自检报告

报告里的证据分四类：

1. **字体**：用 `Font.HasChar` 逐字检查中文码位是否有字形 → `verdict: CJK_OK`
2. **像素**：分区域统计主色与颜色数 → 桌面主色应精确等于 `BoardTheme.BackgroundColor`
3. **UI**：关键 `Control` 的实际矩形与文本 → 检查有没有跑出屏幕、有没有被压成 0 宽
4. **输入**：用 `Input.ParseInputEvent` 往真实输入管线里塞伪造鼠标事件，验证
   滚轮缩放（锚点漂移应为 0）、中键拖拽平移（位移误差应 < 1px）、
   左键点空白 / 右键轻点出菜单的阈值区分

**M1 实测结果**：`verdict: CJK_OK`（微软雅黑，探针 30+ 中文字零缺字形）；
缩放锚点漂移 `0.0 px`；平移误差 `0.00008 px`；点击/菜单判定各触发 1 次；全部 `pass`。

---

## 目录结构

```
scripts/
  Core/                     运行时的骨架
    GameConfig.cs           所有常量（尺寸 / 缩放范围 / 阈值）
    AppPaths.cs             user:// 路径约定：一个存档 = 一个自包含目录
    Fonts.cs                字体的唯一来源（解决 CJK 方块问题）
    BoardTheme.cs           桌面主题数据（纯 POCO，可 JSON 序列化）
    Board.cs                桌面绘制：底色 / 背景图 / 主次网格 / 边界
    BoardCamera.cs          相机：平移、以屏幕点为锚缩放、框取适配
    ViewportController.cs   输入路由 + 手势状态机（点 vs 拖 的阈值区分）
    IWorldPicker.cs         「这个坐标下有什么」—— M2 物件拾取接口
    Hud.cs                  常驻界面：状态条 / 提示条 / 吐司
    Main.cs                 根节点，装配以上一切
  Dev/                      开发期工具（不影响正常运行）
    CmdLine.cs              命令行参数读取
    DevCapture.cs           `--shot` 截图 + 自检，无需窗口焦点
    DevReport.cs            四类证据的自检报告
    DevInputSim.cs          合成输入探针（走真实输入管线）
scenes/Main.tscn            主场景
assets/theme/
  cjk_font.tres             SystemFont，按名字向系统要中文字体
  default_theme.tres        项目主题（字体 + Nord 配色 + 控件样式）
tools/dev/
  shot.ps1                  截图 + 自检一条龙
  focus_game.ps1            把游戏窗口提到前台（MCP 运行时断言前用）
```

---

## 几个关键设计决定

**1. 数据用 POCO + JSON，不用 Godot `Resource`/.tres**
存档要可读、可手改、可 diff —— 对调试工具这比二进制化重要得多。
`Color` / `Vector2` 走自定义 JsonConverter，JSON 里就是 `"#3b4252"` 这种能直接改的写法。

**2. 卡面用 `_Draw()` 合成，不用 `Control` 节点树**（M2 落地）
一张卡 = 1 个 `Node2D`。几百张卡时 Control 的 layout 开销会炸，
而 `Node2D` 天然支持自由旋转缩放，和"像素级自由拖拽"的交互模型完全契合。

**3. 相机目标值与当前值分离**
所有数学（尤其是"鼠标下的世界点保持不动"）作用在目标值上，
每帧用指数平滑逼近当前值。这样既平滑，又不会因为 Godot 相机变换晚一帧生效而算错锚点。

**4. HUD 用 VBox + 弹性 Spacer，不用锚点的 `minsize` 模式贴底**
M1 实测踩过：用 `anchor_preset=bottom_wide` 的 `minsize` 模式时，
容器最小尺寸还没算出来，偏移量按 0 计算，提示条被推到 `y=1080`（屏幕外）、
吐司被压成 1px 宽。声明式布局免疫这类时序问题。

**5. .ps1 脚本一律纯 ASCII**
Windows PowerShell 5.1 对无 BOM 的 UTF-8 文件按 ANSI 解码，
中文会变乱码并撑破字符串引号导致解析失败。C#/GDScript/文档里的中文不受影响。

---

## 已知环境约束

- **`user://` 在沙箱下不可写**：从受限 shell 启动的进程无法写
  `%APPDATA%\Godot\app_userdata\`。编辑器启动的游戏和导出版不受影响。
  M4 会加 `--save-root` 开关，让自动化测试能把存档写进工作区。
- **工作区外不可写**：截图只能写到 `.dev/`（已在 `.gitignore` 里）。
