# M2 设计：物件系统（卡牌 / Token / 骰子）

> 本文是 M2 的实现蓝图。动手前请先确认文末「待确认的 4 个决定」。

## 0. 与 M1 的接缝

M1 已经为 M2 预留好了挂载点，M2 不需要改任何 M1 代码结构，只需要「填进去」：

| M1 预留 | M2 怎么用 |
|---|---|
| `IWorldPicker.PickTopmost(worldPos)` | `ObjectManager` 实现它，决定一次左键按下是「拖物件」还是「点空白」 |
| `ViewportController.Picker` | 启动时赋值为 `ObjectManager` |
| `ViewportController.PrimaryPressed/Dragged/Released` | 物件拖拽全程 |
| `ViewportController.EmptyAreaClicked` | 取消选中；若拖动则是框选 |
| `ViewportController.ContextMenuRequested` | 弹上下文菜单 |
| `Main.ObjectsRoot` | 所有物件的父节点 |
| `GameConfig.DragThresholdPixels` | 「点」与「拖」的阈值，复用 |

`ViewportController` 已经内置了 `Space+左键 / 中键 / 右键拖动 = 平移画布`，
所以**左键可以放心地专用于物件交互**，不会和相机打架。

---

## 1. 数据模型

全部是纯 POCO（不继承 `Resource`），因为 M4 要用 `System.Text.Json` 序列化成
可读可手改的 JSON。`Color` / `Vector2` 走自定义 JsonConverter。

```csharp
// 卡牌定义 —— 模板合成的「模板」
public sealed class CardDefinition
{
    public string Id { get; set; } = "";          // 存档内唯一
    public string DisplayName { get; set; } = "";
    public string FaceImage { get; set; } = "";   // images/ 下文件名；空 = 纯色卡面
    public string BackImage { get; set; } = "";   // 空 = 程序化默认卡背
    public Color FaceTint { get; set; } = Colors.White;
    public Color BorderColor { get; set; } = new("#2e3440");
    public CardFaceTemplate Template { get; set; } = new();
    public List<CardField> Fields { get; set; } = new();
}

// 卡面上的一个字段
public sealed class CardField
{
    public string Key   { get; set; } = "";   // "cost"
    public string Label { get; set; } = "";   // "费用"；空则显示 Key
    public string Value { get; set; } = "";   // "3"
    public FieldSlot Slot { get; set; } = FieldSlot.Center;
    public int FontSize { get; set; }         // 0 = 用模板默认
    public Color? Color { get; set; }         // null = 用模板默认
    public bool ShowLabel { get; set; }
}

public enum FieldSlot
{
    Title, TypeLine,
    TopLeft, TopRight,
    CenterLeft, Center, CenterRight,
    BottomLeft, BottomCenter, BottomRight,
    Description,     // 自动换行
}

// 布局参数，整套可换 —— 这就是「自定义卡牌风格」
public sealed class CardFaceTemplate
{
    public float Padding            { get; set; } = 0.06f;   // 相对卡宽
    public int   TitleFontSize      { get; set; } = 34;
    public int   FieldFontSize      { get; set; } = 26;
    public int   DescriptionFontSize{ get; set; } = 20;
    public float CornerRadius       { get; set; } = 14f;
    public float BorderWidth        { get; set; } = 3f;
    public Color TitleColor         { get; set; } = new("#eceff4");
    public Color FieldColor         { get; set; } = new("#d8dee9");
    public Color DescriptionColor   { get; set; } = new("#c8ced9");
    public bool  ShowFrame          { get; set; } = true;
}
```

**为什么字段是 `List<CardField>` 而不是固定属性**：你要验证的是玩法，字段随玩法变
（今天是 cost/atk/hp，明天可能加「关键词」「阵营」）。用列表 + 布局槽位，
新字段不需要改代码。

`CardObject` 额外持有 `Dictionary<string,string> FieldOverrides` ——
**实例级覆盖**。改某一张卡的数值不会污染定义，这是「验证平衡性」时的核心操作。

---

## 2. 物件基类

```csharp
public abstract partial class TabletopObject : Node2D
{
    public string Uid { get; }                  // 稳定唯一 id，M4 存档引用
    public abstract ObjectKind Kind { get; }
    public Vector2 Size { get; set; }           // 本地坐标，以中心为原点
    public bool IsFaceDown { get; set; }
    public int  PileId { get; set; }            // 0 = 不在堆里
    public int  PileIndex { get; set; }

    public bool IsSelected { get; set; }
    public bool IsHovered  { get; set; }

    public Rect2 LocalRect => new(-Size / 2f, Size);

    /// 命中测试：ToLocal 会自动逆转旋转，所以旋转后的卡也点得准。
    public virtual bool ContainsWorldPoint(Vector2 world)
        => LocalRect.Grow(MinHitPadding).HasPoint(ToLocal(world));

    public abstract ObjectState CaptureState();
    public abstract void ApplyState(ObjectState state);

    protected abstract void DrawContent();      // 子类只画自己的内容

    public override void _Draw()                // 基类统一画选中/悬停描边
    {
        DrawContent();
        if (IsHovered)  DrawOutline(HoverColor, 3f);
        if (IsSelected) DrawOutline(SelectColor, 5f);
    }
}
```

关键点：**选中/悬停描边由基类统一画**，子类 `DrawContent()` 完全不管交互状态。

小物件（缩得很小时的骰子）给一个最小命中尺寸，否则点不中。

---

## 3. 卡面渲染

`CardFaceRenderer` 是静态无状态类，卡牌节点在 `_Draw()` 里调它。

**底板/边框/投影用缓存的 `StyleBoxFlat` + `DrawStyleBox()` 画**，
因为 `DrawRect` 不支持圆角，而 `StyleBoxFlat` 原生支持圆角、边框宽度、阴影偏移。
复用同一个实例、改 `BgColor` 后重画，避免每张卡分配新对象。

绘制顺序：

1. 投影（`StyleBoxFlat` 的 `shadow_size` / `shadow_offset`，黑色 25% alpha）
2. 底板（`FaceTint` 或纯色）
3. 底图（`FaceImage`，铺满卡面）
4. 字段文字
5. 边框

**字段槽位 → 矩形**（本地坐标，`W`/`H` 为卡宽高，`pad = W * Padding`）：

| 槽位 | 矩形 | 对齐 |
|---|---|---|
| `Title` | `(pad, pad, W-2pad, H*0.12)` | 居中 |
| `TypeLine` | `(pad, H*0.15, W-2pad, H*0.06)` | 居中 |
| `TopLeft` | `(pad, H*0.22, W*0.30, H*0.08)` | 左 |
| `TopRight` | `(W*0.62, H*0.22, W*0.32-pad, H*0.08)` | 右 |
| `CenterLeft` | `(pad, H*0.44, W*0.30, H*0.08)` | 左 |
| `Center` | `(pad, H*0.44, W-2pad, H*0.08)` | 居中 |
| `CenterRight` | `(W*0.62, H*0.44, W*0.32-pad, H*0.08)` | 右 |
| `BottomLeft` | `(pad, H*0.66, W*0.30, H*0.08)` | 左 |
| `BottomCenter` | `(pad, H*0.66, W-2pad, H*0.08)` | 居中 |
| `BottomRight` | `(W*0.62, H*0.66, W*0.32-pad, H*0.08)` | 右 |
| `Description` | `(pad, H*0.76, W-2pad, H*0.20)` | 左，自动换行 |

这是标准 TCG 版式，够用且不挤。

**文字绘制要处理三件 Godot 不给的事**：

1. **垂直居中**：`DrawString` 的 `pos` 是**基线**，不是左上角。需要
   `pos.Y = rect.Y + ascent + (rect.H - (ascent + descent)) / 2`。
2. **省略号截断**：固定槽位里长文本会溢出。用 `Font.GetStringSize` 二分查找
   出最长的可容纳前缀，加 `…`。
3. **换行**：`Description` 用 `DrawMultilineString`，给宽度 +
   `TextServer.LineBreakFlag.Mandatory | WordBoundary | GraphemeBoundary`。
   CJK 逐字可断，拉丁按词断。

---

## 4. 骰子（无物理）

按你的要求：**不碰物理引擎，只让数字滚动然后停下**。

```csharp
public sealed partial class DiceObject : TabletopObject
{
    public int Sides { get; set; } = 6;        // 4/6/8/10/12/20/100
    public int Count { get; set; } = 1;        // 多骰，如 3d6
    public int[] Values { get; set; };         // 每个骰的当前值
    private DicePhase _phase;                  // Idle / Rolling
}
```

**掷骰动画**（总时长 ≈ 1.2s）：

| 阶段 | 时长 | 表现 |
|---|---|---|
| 高速跳动 | 0–600ms | 每 40ms 换一组随机数 |
| 减速 | 600–1100ms | 间隔 40ms → 300ms 线性拉长 |
| 定格 | 1100–1200ms | 显示结果，缩放脉冲 1.0→1.12→1.0 强调 |

- 点击骰子即掷；右键菜单也有「掷」
- 多骰显示：上行 `3 + 5 + 2`（小字），下行 `= 10`（大字）
- 结果由 `RandomNumberGenerator` 产生并**保存 seed**，M4 存档后可复现
- 外形：统一的圆角方形轮廓 + 大号数字。**不按面数画不同多面体形状** ——
  俯视 2D 下 D20 的三角轮廓在 120px 里根本认不出，反而统一轮廓更清楚
  （如果你想要多面体外观，告诉我，我改）

---

## 5. 堆叠

`Pile` 是纯逻辑分组，**不新增节点**，只改成员的位置与绘制顺序。

```csharp
public sealed class Pile
{
    public int Id;
    public readonly List<TabletopObject> Members = new();  // 底 → 顶
    public Vector2 Position;
    public TabletopObject Top => Members[^1];
    public int Count => Members.Count;
}
```

**视觉**：
- 成员按索引做 **2px 阶梯偏移**（`+2*i, -2*i`），一眼能看出是「一叠」
- 只完整绘制最上面 3 张，更下面的只画轮廓，避免无谓开销
- 右上角一个圆角小标签显示 `×N`，仅当 `N ≥ 2`

**行为**：
- 拖拽整堆 → 所有成员一起移动
- `Shift` + 拖拽 → 只拖最上面一张（从堆中抽出）
- 松手时若中心落在另一个物件上 → 并入那个堆；若落在散件上 → 与该散件组成新堆
- 右键 → 「拆散这堆」

**绘制顺序**：同级节点的绘制顺序 = 子节点顺序。`ObjectManager` 在堆变化时调
`MoveChild` 重排：先按堆分组，堆内按 `PileIndex` 递增，保证同堆成员连续且
顶张画在最上。

---

## 6. 交互

| 操作 | 按键 |
|---|---|
| 拖拽物件 / 整堆 | 左键拖 |
| 框选 | 左键在**空白**按下并拖动 |
| 从堆中抽单张 | `Shift` + 拖拽 |
| 翻面 | `F` |
| 步进旋转 ±15° | `[` / `]`（`Q` / `E` 同义） |
| 无级旋转 | `Alt` + 滚轮 |
| 重置旋转 | 右键菜单 |
| 复制 | `Ctrl`+`D` |
| 删除 | `Delete` |
| 全选 / 取消选中 | `Ctrl`+`A` / `Esc` |
| 网格吸附开关 | `G` + HUD 按钮 |
| 掷骰 | 点击骰子 |
| 上下文菜单 | 右键**轻点**物件 |

> 注意 `Shift` 的用法：在 M1 里我提议过 `Shift` 做网格吸附，现在它改做「从堆中抽单张」。
> 网格吸附改成 `G` 键独立开关 —— 因为吸附是持续状态，用开关比用修饰键合理。
> **这条需要你确认**（见文末）。

旋转：`[` `]` 每次 ±15°（精确摆正），`Alt+滚轮` 连续（随意倾斜）。

---

## 7. 上下文菜单

用 Godot 的 `PopupMenu`，挂在 HUD 的 `CanvasLayer` 下（不受相机影响）。
右键轻点物件 → `PopupOnParent(...)`。菜单项按物件类型动态构建：

- **通用**：顺时针 90° / 逆时针 90° / 重置旋转 / 翻面 / 复制 / 删除
- **在堆里时**：从堆中取出 / 拆散这堆
- **骰子**：掷 / 面数 ▸ (D4 D6 D8 D10 D12 D20 D100) / 数量 ▸ (1 2 3 5)
- **卡牌**：检视 / 编辑字段（M5 才会真正打开编辑器，M2 先只读展示）

---

## 8. 框选

空白处左键按下 + 拖动 → 世界坐标下的选框，松手选中所有 AABB 相交的物件。

需要一个 `SelectionBox` 节点（`Node2D` + `_Draw`），层级在 `Objects` 之上、
`HUD` 之下。这个也是**待确认项之一**：M2 是否就做框选，还是留到 M3。

---

## 9. HUD 变化

顶栏增加：`选中 N` / `物件 N` / 网格吸附开关（按钮，按下态表示开启）。
底部提示条更新为新键位。

---

## 10. 怎么验证

复用 M1 的自检通道（`tools/dev/shot.ps1`，免窗口焦点），扩展两处：

**`DevReport` 增加 `objects` 段**：物件总数、类型分布、每个物件的
`position` / `rotation` / `face_down` / `pile_id` / `pile_index`。

**`DevInputSim` 增加端到端交互断言**（走真实输入管线）：

| 断言 | 期望 |
|---|---|
| 拖拽一张卡 | 位移 = 鼠标位移 / 缩放，误差 < 1px |
| 点空白后拖拽 | 不移动物件，产生框选 |
| `[` / `]` | `rotation` 恰好变化 ±15° |
| `F` | `IsFaceDown` 翻转 |
| 拖到另一张卡上松手 | 形成堆，成员数 = 2，位移一致 |
| `Shift`+拖堆 | 只抽出 1 张，原堆剩 1 张 |
| 点击骰子 | 值落在 `[1, Sides]`，动画结束后稳定不变 |

再加一个人工可复核的画面证据：`.dev/shots/m2_objects.png`。

---

## 11. 待确认的 4 个决定

1. **框选要不要在 M2 就做？** 做的话 M2 工作量大一点，但多选/批量旋转删除在
   验证玩法时很实用。不做的话 M2 只能单选。
2. **`Shift` 改做「从堆中抽单张」，网格吸附改成 `G` 开关** —— 同意吗？
3. **骰子外形统一圆角方形**（不按面数画多面体）—— 同意吗？
4. **卡面 11 个布局槽位**够不够？还是你想要更自由的（比如字段可给百分比坐标）？
