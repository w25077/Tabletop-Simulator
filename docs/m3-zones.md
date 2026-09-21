# M3 设计：区域系统（牌库 / 手牌 / 弃牌堆 / 出牌区 / 公共区）

> **状态：已实现。** 本文保留为设计记录，下面「实现后的偏差」列出与原始设计的实际差异。
> 自检：`powershell -File tools/dev/shot.ps1 -Name <名字> -Frames 50`
> → 报告在 `.dev/shots/<名字>.png.report.json`，节节全绿。

## 实现后的偏差

| 项 | 设计 | 实际 | 原因 |
|---|---|---|---|
| 手牌区尺寸 | 1500 × 480 | **3168 × 480**，间距 18 → 12 | 1500 只够 4 张卡，第 5 张就换行掉到区域外。**会"长满"的区域必须按上限反推尺寸**，不能按"差不多"估 |
| 双击判定 | 用 `InputEventMouseButton.DoubleClick` | **自建判定**（两次未拖动的点击 + 时间 + 位移阈值） | 那个标志由 DisplayServer 层填写，而自检走 `Input.ParseInputEvent`，根本到不了那一层 —— 用它会让"双击抽牌"变成自检永远覆盖不到的盲区 |
| 落点被拒 | 只说"拒绝进入" | **退回原处**（记住拖拽起点所在的区域） | 起点就已脱离区域，被拒后物件会变成无归属的散件掉在桌上 —— 用户看到的是"我手滑了一下，牌没了归属" |
| 脱离区域的时机 | 未写明 | **拖拽起点**（`BeginDrag`） | 落点可能不在任何区域里；在落点才脱离会留下「还在牌库里、位置却在桌面」的半截状态 |
| 区域菜单优先级 | 只说"右键区域弹区域菜单" | **Stack 区域优先于物件**；其它排版仍然物件优先 | 一摞牌的正确操作单位是"这一摞"（洗牌/抽牌/全部翻开），不是"恰好被点到的第 7 张牌" |
| 双击优先级 | 未写明 | **区域优先于物件**（与右键相反） | 牌库最上面那 3 张牌正好压在自己的矩形中心，双击"牌库"命中的几乎总是顶牌。按"物件优先"的话抽牌永远触发不了 |
| 手牌重叠断言 | 相邻两张比较 X 差 | **AABB 两两相交，且只对 `Row` 生效** | 比较 X 差在换行时假失败（第 5 张换行后 X 回到最左边）；而 `Fan` 排版是**故意**叠压的，拿同一条断言去卡它就是测试在说谎 |
| 探针"跑不了"时 | 未区分 | **标记 `skipped` 且不写 `pass`** | 近景视角下桌面大半在视口外，断言会因"找不到靶子"报红 —— 那是没条件跑，不是产品有 bug |

### 用户实测提出的第三批要求（已实现）

**桌面上已排好位置的几张牌，框选后拖拽应当「仅移动」**，不该自动合并。

这与第一批要求（手牌全选拖到桌面 → 应当叠成一摞）**在动作上完全一样**，
只能靠「从哪来」区分。所以 `TryMergeAfterDrop` 现在分四种情况：

| 拖的是什么 | 落在空地 | 压在别的牌上 |
|---|---|---|
| **单张** | 脱离原堆，变散件 | 并进那一堆 |
| **整堆**（整组恰好是已有的一个自由堆） | 只换位置，堆不变 | 并堆 |
| **从区域里抓的一把** | **叠成一摞** | 只移动 |
| **桌面上框选的一把散牌** | **仅移动，保持排列** | **仅移动** |

判据是 `ObjectManager._draggingFromZones` —— 必须在 `ZoneManager.BeginDrag`
摘除归属**之前**记录，因为摘完之后所有 `ZoneId` 都空了，就再也问不出来。

**代价说明**：第一条要求（区域→成摞）与第三条（桌面→仅移动）是同一个手势的两种意图，
光看动作分辨不了，只能按来源分。如果以后发现"从区域里抓一把出来、但想摊开摆"
也需要支持，那就该加一个修饰键，而不是继续猜。

**断言**（回退验证过会红）：

- `loose_drag_did_not_merge`：框选 3 张桌面散牌拖走 → `PileId` 全为 0、堆数不变。
  回退后实测 3 张全进新堆（堆数 2→3）。
- `loose_drag_keeps_arrangement`：**每张的位移必须一致**（组内相对位置不变）。
  回退后实测组内偏差 670 px。少查这一条的话，"整组塌到一点"也能算"移动了"。
- `loose_drag_actually_moved`：确实移动了（否则"没合并"可能是因为压根没动）。
- 同时保住 `multi_formed_one_pile` 等三条：从手牌抓一把仍然成摞。

### 用户实测提出的第二批要求（已实现）

1. **指着一摞牌按 F 应当把整摞反过来**，而不是"只翻恰好被指到的那一张"。
2. **一摞牌要能洗牌**，快捷键 `R`（**完全替掉原来的 `S`**）。

**"整摞反过来"是两件事同时发生**，缺一不可：

| | 结果 |
|---|---|
| 只逐张翻面（原行为） | 顶牌还是原来那张、只是翻了面 —— 用户看到的是"牌堆没动" |
| 只反转次序 | 盖着的一摞反转之后画面**毫无变化**，像没反应 |
| **次序反转 + 每张正反面翻转** | 底牌变顶牌，且整摞朝向翻转 —— 这才是"物理上拿起来翻个面" |

**目标解析也改了**：`ResolveActionTargets` 的规则 1（悬停物件）多加一层 ——
悬停的物件属于"一摞"时，目标是**整摞**（自由堆按 `PileId` 认，叠放区域按 `ZoneId` 认；
后者的成员 `PileId` 恒为 0，只查 `PileId` 会漏掉整个牌库）。
想只操作一张：**先点它一下**，那时走的是"悬停对象在选中集里"那条分支，目标就是它自己。

**一个容易漏的配套改动**：画面上谁压谁完全由 `ObjectManager._drawOrder` 决定。
翻转/洗牌会改成员次序，所以必须同步 `_drawOrder`（`SyncDrawOrderToStack`），
否则新的顶牌会被画在其它牌底下 —— 看起来像"翻转没生效"。

**断言**（16 条，覆盖自由堆与叠放区域各一遍；都回退验证过会红）：

- `*_flip_reverses_order` / `*_flip_toggles_faces` / `*_flip_keeps_members` / `*_flip_is_involutive`
  （翻两次必须还原 —— 做不到就说明次序与正反面没同步）
- `*_shuffle_keeps_members` / `*_shuffle_changed_order` / `*_shuffle_seed_recorded`
- `single_card_still_flippable` —— 先点一下之后，只翻那一张、次序不许动。
  **这条是防退化用的**：少了它，"整摞翻转"很容易顺手把"单独翻一张"这条路堵死。

### 用户实测发现的 bug（已修）

**症状 1**：把一张牌放进牌库、再拖出来，它右上角仍挂着牌库的张数（用户报的是「30」）。

**症状 2**：手牌 5 张全选拖到桌面，期望"一堆牌、右上角写 5"，实际"确实像一堆、但没有数字"。

这两个症状是**两个独立的 bug 叠在一起**，而第二个恰好掩盖了第一个：

**bug A —— 拖出区域时没清叠放残留。**
`Zone.RemoveMember` 只清了 `ZoneId`，没清 `PileIndex` / `PileCount`。
而张数徽章的绘制条件恰好是「`PileCount >= 2` 且 `PileIndex` 是最后一个」——
从牌库顶拖出来的那张正好还满足条件，于是徽章留了下来。
这是一个**数据全对、只是画错**的 bug。

**bug B —— 从横排区域里批量拖动会把整组塌成一点。**
`ZoneManager.BeginDrag` 原来对每个成员调一次 `LeaveCurrentZone`，
而后者每次都会让源区域**重新排版**。横排排版总是从**槽位 0** 开始铺：

| 步骤 | 剩余成员的排布 | 被摘掉那张的位置 |
|---|---|---|
| 移除 H0 | H1→槽位0, H2→槽位1, … | H0 留在槽位 0 |
| 移除 H1 | H2→槽位0, H3→槽位1, … | **H1 已经在槽位 0** |
| … | … | **每一张都是在槽位 0 被摘掉的** |

于是 10 张牌会先全部塌到手牌最左边那一格、再一起跟着鼠标走。
**用户看到的"确实是一堆卡片"就是这个假象**——它们叠在一起，却没有真的成堆，
所以没有徽章，而且拖拽时也跟不上光标（实测差 2808 世界单位）。

**bug C —— 多选落在空地时完全不成堆。**
`TryMergeAfterDrop` 对多张落空地是**什么都不做**。用户明确要求的是
"一次手势拿起 N 张、放下就是 N 张一摞"，所以改成 `GroupIntoPile`，
锚点取离松手位置最近的那张（成形在你放手的地方）。

**修法与断言**（每条都验证过"回退修复后确实会红"）：

| bug | 修法 | 断言 |
|---|---|---|
| A | `TabletopObject.ClearStackVisual()` 统一收口"回到普通散件" | `roundtrip_badge_cleared` + 不变量 `badge_only_when_stacked` |
| B | `ZoneManager.LeaveZones()` 批量摘除，<b>最后只排版一次</b> | `multi_kept_shape_during_drag`（拖动中整组必须保持原形）、`multi_grabbed_follows_cursor` |
| C | `TryMergeAfterDrop` 增加"多张落空地 → `GroupIntoPile`" | `multi_formed_one_pile` / `multi_pile_count_is_group_size` / `multi_exactly_one_badge` / `multi_pile_forms_at_drop_point` |

**另外补了一条一直没有的检查**：`badge_render`（像素级）。
已有的物件探针全都在比对"卡面主色"，而**徽章画不画对主色毫无影响**——
所以 bug A 能一路穿过所有断言。现在成对取样：堆顶卡的右上角应当有成片徽章填充色，
一张**正面朝上的**散件卡应当几乎没有。
（对照组必须是正面卡：卡背底色 `#2b3242` 与徽章填充色 `#2e3440` 距离只有 `0.008`，
拿盖放的牌当对照，它整片卡背都会被判成"徽章像素"。）

> **一个反直觉但很重要的发现**：`multi_span_before_px = 2808` 是这条断言有牙齿的前提。
> 如果手牌本来就是叠在一起的，那"拖出去成了一堆"就是本来就成立的，
> 断言再绿也说明不了任何事。所以这一节刻意加了一组**按下之前**的观察点
> （跨度、PileId 种类、徽章数），先证明起点是摊开的、各自独立的。
> **两段式断言如果只写后半段，就会变成永远绿的假断言。**

**bug A 的补充细节**：影响面比"牌库→桌面"更广 —— 凡是"物件不再处于任何叠放组"
的路径都得清，包括自由堆解散、区域移出、以及**横排/自由排版**。
后两处原来也没清 `PileCount`，只是横排下 `PileIndex` 恰好为 0、
徽章条件不成立而**碰巧**看不出来 —— 换个排版就会冒出来。

**顺带排查出两处同源隐患**（同一个根因，只是还没被触发）：

- `ZoneManager.LeaveCurrentZone` 的兜底分支（区域已不存在时）只清了 `ZoneId`。
- `Zone.ClearMembers()` 只清成员表、不清物件 —— 而 `ZoneManager.ClearAll()` 就是用它
  换存档的，M4 一上来就会在桌面上留下一批孤儿声明。

两处都改成走 `ClearStackVisual()`，并新增第 8 节收尾检查 `zone_teardown`
（在所有模拟跑完之后单独跑一次拆桌，断言不留孤儿声明与幽灵徽章）。

> `zone_teardown` 刻意**不**放进 `zone_simulation`：试过，一放进去后面的
> `object_simulation` / `sequence_simulation` 就没了区域可测、顺序断言直接变红。
> **探针之间不该互相拆台** —— 破坏性检查一律排在最后。

> **一个认知盲点**：`zones.invariants` 是在**所有操作探针运行之前**取的**开局快照**，
> 所以它抓不到"只有操作过才会出现"的问题 —— 回退验证时它照样全绿，
> 真正报红的是 `zone_simulation.invariants_after`。
> 报告里已加 `snapshot` 字段说明这件事，运行期由 `DevZoneSim` 跑完一整套操作后再查一遍。

另外四处值得记下来：

- **`Zone.Members` 与 `Object.ZoneId` 是双簿记**，所以自检里专门有八条不变量
  （`DevReport.ZoneProbe`）。聚合判定直接把整个不变量字典与一遍，不逐个写键名 ——
  从根上杜绝 M2 那次「子项写 `dup_ok`、聚合读 `duplicate_ok`」的漂移。
- **`DevCapture` 原来用 `_ = CaptureAsync(...)` 发射后不管**，异常被 Task 吞掉，
  表现为"自检永远挂住、什么都不打印"。已加 `RunAsync` 包一层 try/catch。
- **不要按进程名去 kill 自己启动的程序 —— 这条栽过两次。**
  Godot 的**游戏进程与编辑器进程可执行文件名完全一样**，
  `ProcessName -like '*Godot*'` 会精确命中用户正开着的编辑器；
  第一次误杀后写了教训、**下一个命令里又原样犯了一遍**，
  说明"写进文档"不构成约束。现已工具化：跑 Godot 一律走
  `tools/dev/godot_run.ps1`（`-PassThru` 只停自己启动的 PID，动手前核对 `StartTime`）。
- **探针找"空白点"从"取第一个通过的"改成"全局找最空的"**（`FindEmptiestScreenPoint`）。
  旧写法扫不到就**猜**一个 `(中心, 视口92%)`；M3 加了下带手牌区之后那个猜出来的点
  正好落在手牌区里且上面压着卡，一次性让三处互不相关的断言同时变红。

---

> 以下为原始设计蓝图。

M3 的唯一目标：**跑通「洗牌 → 抽 5 张 → 打出 → 弃牌」的完整循环。**

这是第一次让桌面上的物件**有归属**。M2 里所有物件都是平权的散件，
位置由人决定；M3 里物件可以属于某个区域，然后区域**替你决定**它的位置、
正反面、能不能再进来。

---

## 0. 与 M2 的接缝（这些不改）

| 约定 | 为什么 M3 也不能破 |
|---|---|
| 输入只有一处入口（`ViewportController`） | 区域要接管双击与右键，**更要**守这条。两个节点各自抢 `_UnhandledInput`，谁先拿到事件取决于节点顺序 —— 那种 bug 极难查 |
| 绘制次序唯一真相是 `_drawOrder` | 区域**不参与**这个列表（见 §3） |
| 堆是纯逻辑分组，不新增节点 | 区域是**要**新增节点的，但成员仍然不是区域的子节点（见 §3） |
| 数据用 POCO + JSON，不用 Godot `Resource` | M4 存档要可读、可手改、可 diff。`ZoneDefinition` 同样是 POCO |
| HUD 用 VBox + 弹性 Spacer | 不新增贴底控件，只改提示条文本 |
| `.ps1` 一律纯 ASCII | 不新增 .ps1；改的是 .cs 里的提示文本 |

**唯一新增的信号**：`ViewportController.PrimaryDoubleClicked`。
Godot 的 `InputEventMouseButton.DoubleClick` 已经在事件里带了这个标志，
只是在 `HandlePress` 里被丢掉了 —— 补一条分支即可，不引入任何计时器。

---

## 1. 数据模型（`scripts/Data/ZoneData.cs`）

```csharp
public enum ZoneKind { Deck, Hand, Discard, Play, Public, Custom }

/// 区域怎么摆它的成员
public enum ZoneSortMode
{
    Free,    // 不排版 —— 拖进来放哪就是哪（出牌区 / 公共区）
    Stack,   // 叠成一摞（牌库 / 弃牌堆）
    Row,     // 横排、超宽换行（手牌）
    Fan,     // 横排 + 按扇形旋转（手牌，像真手牌）
}

/// 物件进区后强制成什么朝向
public enum FaceOnEnter
{
    Unchanged,  // 不动（出牌区 / 公共区）
    FaceUp,     // 强制翻开（手牌 / 弃牌堆）
    FaceDown,   // 强制盖放（牌库）
}

public sealed class ZoneDefinition
{
    public string Id;                 // 存档内唯一，成员用 ZoneId 引用
    public string Name;               // 显示名（画在区域左上角）
    public ZoneKind Kind;
    public Rect2 Rect;                // 世界坐标，左上角原点

    public ZoneSortMode SortMode;
    public FaceOnEnter FaceOnEnter;

    public bool SnapOnDrop;           // 拖入即归位（false = 放哪算哪）
    public int MaxCards;              // 0 = 无上限；超限拒绝进入
    public bool DrawOnDoubleClick;    // 双击抽牌
    public string DrawTargetId;       // 抽到哪个区域；空 = 抽到桌面
    public int DrawCount;             // 双击一次抽几张（默认 1）
    public bool Enabled;              // 关掉后不接受拖入、不响应双击
    public Color Tint;                // 半透明底色
    public Color BorderColor;
}
```

`ZoneDefaults.For(ZoneKind)` 给每种类型一套出厂参数，这样 DemoContent
和 M5 的「画区域」都不用逐项填：

| Kind | 中文名 | SortMode | FaceOnEnter | SnapOnDrop | 双击抽牌 | 默认尺寸 |
|---|---|---|---|---|---|---|
| `Deck` | 牌库 | `Stack` | `FaceDown` | ✔ | ✔（→手牌） | 300 × 460 |
| `Hand` | 手牌 | `Row` | `FaceUp` | ✔ | — | 1500 × 480 |
| `Discard` | 弃牌堆 | `Stack` | `FaceUp` | ✔ | — | 300 × 460 |
| `Play` | 出牌区 | `Free` | `Unchanged` | ✘ | — | 900 × 620 |
| `Public` | 公共区 | `Free` | `Unchanged` | ✘ | — | 700 × 460 |
| `Custom` | 自定义 | `Free` | `Unchanged` | ✘ | — | 500 × 400 |

> **与 `HANDOFF.md` 的偏差**：那里写的是 `AutoFaceDown` 布尔。
> 换成三态 `FaceOnEnter` 是因为布尔表达不了「强制翻开」——
> 而手牌区正需要它：牌库抽出来的牌必须翻开，否则手里全是卡背。
> 一个布尔只能表达「进区盖放」，三态能同时表达牌库的盖和手牌的翻。

---

## 2. 区域节点（`scripts/Core/Objects/Zone.cs`）

一个 `Zone` = 一个 `Node2D`，**只画自己**，不管成员的生命周期。

```csharp
public partial class Zone : Node2D
{
    public ZoneDefinition Definition { get; }
    public List<TabletopObject> Members { get; }   // 从底到顶

    public Vector2 StackAnchor { get; }            // 叠放的锚点（矩形中心）
    public int LastShuffleSeed { get; private set; }  // 洗牌可复现

    public bool ContainsWorldPoint(Vector2 world); // 矩形命中
    public override void _Draw();                  // 底色 / 边框 / 名称 / 张数
}
```

**`_Draw()` 画什么**

- 半透明底色（`Tint`）+ 2px 边框（`BorderColor`）
- 左上角：`"{Name}  {Count}"`，例如 `牌库 24`
- 空区域：边框改虚线感的低透明度，并显示 `"{Name}（空）"`
- 超限：边框转警示色（`MaxCards` 满了）
- **不画**任何成员 —— 成员自己画自己

**为什么成员不是区域的子节点**

和 M2「堆不新增节点」同一条理由：成员留在 `Objects` 容器下，
绘制次序由 `_drawOrder` 统一管。如果成员变成 `Zone` 的子节点，就会出现
「牌库整体压在桌上所有物件之上」这种区域层级污染全局层级的问题，
而且拖出区域时要做 `Reparent`，带来生命周期与释放顺序的坑。

代价是区域自己的绘制层级是固定的（`Zones` 节点 `z_index = -500`，
桌面是 `-1000`，物件是 `0`）—— 区域永远在物件底下、桌面之上。这正是想要的。

---

## 3. 成员关系的唯一真相

**`TabletopObject.ZoneId`（int，0 = 不属于任何区域）是唯一的真相。**
`Zone.Members` 是它的有序镜像，Zones 的每个改动都只经过 `ZoneManager` 一处。

**`PileId` 与 `ZoneId` 互斥**：一个物件不可能同时属于自由堆和区域叠。
拖出区域时先清 `ZoneId`，再走 M2 原有的堆逻辑。

### 关键决策：Stack 类区域**不复用** `Pile` 对象

HANDOFF 里写的是「卡进去自动堆叠对齐」，直觉上应该复用 `Pile`。
但把两者的生命周期接起来会在四个地方打架：

| 冲突点 | `Pile` 现在的行为 | 接了区域会怎样 |
|---|---|---|
| `DetachFromPile` | 成员 ≤ 1 就**解散这个堆** | 牌库只剩 1 张牌时堆被销毁 —— 但区域还在，得重建 |
| 拖整堆 | 拖任一成员 = 拖整堆 | 对牌库正确，对手牌区错误 |
| `MergeInto` | 任意两个物件都能成堆 | 会把牌库成员并进一个**桌面上**的自由堆，`ZoneId` 与 `PileId` 同时非 0 |
| 散堆/拆堆 | 会 `ResetPileFields`，包括 `Visible = true` | 会把牌库成员的可见性改回 true，直接违反「只画最上面 3 张」 |

所以改成：**区域自己排版，但复用徽章绘制。**
`TabletopObject.DrawPileBadge()` 只读 `PileCount` / `PileIndex` 两个字段，
区域把这两个字段当成「在同一处叠放中的位置/总数」来填，徽章就白拿了。
真正要重写的排版逻辑只有 ~15 行（位置阶梯 + 可见深度），从 `LayoutPile` 抄。

于是语义被明确成：

> `PileIndex` / `PileCount` = 「在同一处叠放中的位置 / 总数」（自由堆和 Stack 区域共用）
> `PileId` = 「自由堆的 id」，**Stack 区域的成员恒为 0**

`LayoutPile` 里两处直接改 `PileId` 的地方要顺手确认不会碰到区域成员
（区域成员的 `PileId` 恒 0，`DetachFromPile` 第一行就返回，安全）。

### 排版规则

| SortMode | 位置 | 可见性 | 旋转 |
|---|---|---|---|
| `Stack` | `StackAnchor + Pile.OffsetFor(i)` | 只画最上面 `PileVisibleDepth` 张 | 归零 |
| `Row` | 从矩形左侧内边距开始横排，超宽换行 | 全可见 | 归零 |
| `Fan` | 横排 + 每张按中心对称旋转（±`FanMaxDegrees`） | 全可见 | 扇形角 |
| `Free` | **不动** | 全可见 | 不动 |

`Stack` 区域的 `Visible` 处理要在**移出区域时恢复成 `true`**——
这是最容易漏、且症状最隐蔽的一处（卡片"消失"但数据还在）。

---

## 4. 区域管理器（`scripts/Core/Objects/ZoneManager.cs`）

挂在场景里已有的 `Zones` 节点上（现在是空 `Node2D`，`z_index = -500`）。
它是物件管理器的**区域对偶物**，两者平级、互不持有对方的内部状态。

职责：
- 持有全部 `Zone`，`ZonesRoot.AddChild(zone)`
- 命中测试：`ZoneAtWorld(Vector2)` —— 从上到下取第一个 `Enabled` 的区域
- 把区域的改动广播成信号（`ZonesChanged` / `ZoneCountsChanged`），HUD 与自检订阅
- 提供全部区域命令：`DrawFrom` / `DrawAll` / `Shuffle` / `MoveInto` / `MoveOut` / `FlipZone`

### 与物件系统的接缝：`IZoneInteraction`

沿用 M2 已经验证过的「被问，而不是抢」模式（`IWorldPicker` / `IWheelHandler`
就是这么做的）。新增一个接口，由 `ObjectManager` 持有：

```csharp
public interface IZoneInteraction
{
    // 右键：物件没命中时，问区域要不要接管。返回 true = 菜单已弹出
    bool TryHandleContextMenu(Vector2 worldPos, Vector2 screenPos);

    // 双击：物件没命中时问区域。返回 true = 已消费
    bool TryHandleDoubleClick(Vector2 worldPos);

    // 松手落点：拖拽结束时问区域。返回 true = 已归入区域（不再走自由堆逻辑）
    bool TryHandleDrop(IReadOnlyList<TabletopObject> dropped, Vector2 worldPos);

    // 键盘：区域有落点时的按键（S 洗牌 / D 抽牌）。返回 true = 已消费
    bool TryHandleKey(Key key);
}
```

`ObjectManager` 只依赖这个接口，不依赖 `ZoneManager` 具体类型 ——
和 `IWorldPicker` 一样，编译期单向、可替换、可空（不接区域时区域功能整体关闭）。

### 边界情况（每一处都要有明确行为）

| 情况 | 行为 |
|---|---|
| 拖入超过 `MaxCards` 的区域 | **拒绝整体进入**，物件留在原地，吐司提示「手牌已满（10/10）」 |
| 拖入 `Enabled = false` 的区域 | 同上，提示「区域已锁定」 |
| 拖入的物件已在目标区域内 | 只重排，不重复添加 |
| 区域内拖出到桌面 | 清 `ZoneId`、恢复 `Visible = true`、走原有堆逻辑 |
| 区域为空时拖出 | 无事发生 |
| 删除区域内的物件 | 先从区域摘掉，再删（否则 `Members` 留悬空引用） |
| `DrawTargetId` 指向不存在的区域 | 退回「抽到桌面」，吐司警告一次 —— 不静默 |
| 牌库为空时抽牌 | 吐司「牌库是空的」，不报错 |
| 区域矩形互相重叠 | 取**最后添加**的（视觉上画在上面的那个）；DemoContent 里刻意不重叠 |

---

## 5. 交互

### 落点规则（在 M2「悬停即选中」之上扩展）

M2 的规则是「悬停物件 → 操作它」。M3 加上区域后，落点优先级是：

1. **悬停物件** → 操作物件（M2 规则不变）
2. **悬停区域**（且没悬停物件）→ 操作区域
3. **都没有** → 操作选中集

`Zones` 的 `z_index = -500` 只是**绘制**层级，不影响命中优先级 ——
命中判定是显式的：先问物件，物件没有才问区域。

### 各种鼠标动作

| 动作 | 结果 |
|---|---|
| 左键拖物件到区域上方松手 | 归入该区域（位置/朝向/正反面按定义处理） |
| 左键拖区域内的牌出来 | 离开区域，落在桌上走自由堆逻辑；**整组都来自区域**时落空地会叠成一摞（见 §「实现后的偏差 · 第三批要求」） |
| `Shift`+拖牌库/弃牌堆的顶牌 | 只抽那一张出来（M2 已有语义，不变） |
| 左键拖**手牌区**里的某张 | 只拖那一张（不是整个手牌区） |
| **双击牌库** | 抽 `DrawCount` 张到 `DrawTargetId`（默认手牌区） |
| 双击其它区域 | 无事发生（避免误触） |
| 右键区域（无物件时） | 弹区域菜单（下表） |
| 右键物件 | M2 的物件菜单，不变 |
| 右键空白 | M2 的「全选」，不变 |
| 左键点区域内空白 | 仍算点空白 → 取消选中 / 起框选。**区域不拦截普通左键** |
| 框选 | 框到区域内的牌也照常选中（它们仍是普通物件） |

「左键点区域内空白仍算空白」这条很重要：区域是**位置**不是**容器**，
它不该像面板一样吞掉点击，否则在出牌区里框选就废了。

### 区域右键菜单

菜单项按 `Kind` 裁剪，不显示用不上的项：

```
抽 1 张 / 抽 3 张 / 抽 5 张     ← 仅 DrawOnDoubleClick 的区域（牌库）
洗牌                            ← 仅 SortMode = Stack
──────────────
全部翻开
全部盖放
──────────────
把选中物件移入此区域
把此区域全部牌移到 <目标区域>     ← DrawTargetId 存在时
──────────────
查看区域（把成员摊开到桌面）      ← SortMode = Stack 且成员 ≥ 2
```

「查看区域」= M2 已有的 `DissolvePile` 的语义，摊开看牌库里有什么 ——
验玩法时这是除洗牌外最常用的操作。

### 键盘

| 键 | 动作 | 备注 |
|---|---|---|
| `D` | 对悬停区域抽牌（等同双击） | `Ctrl+D` 是复制，不冲突 |
| `S` | 对悬停的 Stack 区域洗牌 | `Ctrl+S` 是 M4 的存档，不冲突 |

两个键都走 `TryHandleKey`，区域没落点时**不消费**，让 M2 的逻辑继续处理。

---

## 6. 隐藏信息

M3 的「隐藏信息」在单人工具里的意义是**验证玩法手感**（我能不能一眼看出
牌库剩几张、手里有什么），不是防作弊。所以：

- 牌库/盖放的区域 → 成员 `IsFaceDown = true` → 走 M2 已有的卡背渲染
- 进区 `FaceOnEnter.FaceDown` / `FaceUp` 强制设定正反面
- `Stack` 区域只画最上面 `PileVisibleDepth`（3）张 —— 牌库看起来就是"一摞"
- 区域左上角直接显示张数，**不靠数牌**
- 自检报告里记录每个区域的 `face_down` 计数，用来断言"牌库确实是盖着的"

---

## 7. 接缝改造清单（改哪些既有文件）

| 文件 | 改动 | 为什么必须改 |
|---|---|---|
| `TabletopObject.cs` | 加 `ZoneId`；`CaptureState`/`ApplyState` 带上它 | 成员关系的载体；M4 存档要用 |
| `ObjectState.cs` | 加 `ZoneId` 字段 | 同上 |
| `ViewportController.cs` | 加 `PrimaryDoubleClicked` 信号（用事件自带的 `DoubleClick`） | 双击抽牌；必须留在唯一输入入口里 |
| `ObjectManager.cs` | 持有 `IZoneInteraction`；三处问它：右键兜底、双击、落点 | 区域接管落点的唯一路径 |
| `Main.cs` | 装配 `ZoneManager`，注入 `Objects` 与 `Hud` | 与现有 `Objects.Bind(...)` 同构 |
| `GameConfig.cs` | 区域的配色、内边距、扇形角度等常量 | 所有魔法数字集中在一处（既有约定） |
| `DemoContent.cs` | 布置牌库/手牌/弃牌堆/出牌区 + 24 张牌进牌库 | 没有内容就没法验证 |
| `Hud.cs` + `Main.tscn` | 提示条文本补上新按键 | 用户看得见的操作说明 |
| `project.godot` | `tt_draw`(D) / `tt_shuffle`(S) | InputMap |
| `DevReport.cs` | 加 `zones` 段 | 机械断言区域结构 |
| `DevSequenceSim.cs` | 加区域顺序断言 | 机械断言完整循环 |

**不改**：`Pile.cs`、`CardObject.cs`、`TokenObject.cs`、`DiceObject.cs`、
`CardFaceRenderer.cs`、`Board.cs`、`BoardCamera.cs`、`SelectionBox.cs`、
`BoardTheme.cs`、`AppPaths.cs`、`Fonts.cs`、`TextureStore.cs`、`DevInputSim.cs`。

---

## 8. HUD 与快捷键

顶部状态条加一个区域计数（`牌库 24 · 手牌 5 · 弃牌 2`），
数据源是 `ZoneManager.ZoneCountsChanged` 信号。提示条补上：

```
… · 拖牌到区域上方松手 归入区域 · 双击牌库 抽牌 · S 洗牌 · D 抽牌
```

区域自己不占 HUD 空间 —— 名称和张数直接画在区域的矩形上（§2）。

---

## 9. 怎么验证

不靠看图，靠机械断言。复用 `tools/dev/shot.ps1` 那条免焦点通道，扩展两处。

### `DevReport` 增加 `zones` 段

```
zones: {
  found, count,
  items: [ { id, kind, name, rect, member_count, face_down, enabled,
             sort_mode, max_cards, stack_pile_ids, last_shuffle_seed } ],
  invariants: {                       ← 这些才是真正的断言
    zone_id_matches_membership,       // 每个成员的 ZoneId 都指向真的含有它的区域
    no_member_in_two_zones,           // 没有成员同时出现在两个区域
    stack_zones_contiguous,           // Stack 区域成员 PileIndex 恰好是 0..n-1
    stack_visible_depth_ok,           // 只有最上面 PileVisibleDepth 张 Visible
    pile_zone_exclusive,              // 没有物件同时 PileId != 0 且 ZoneId != 0
    member_counts_match              // Zone.Members.Count == 实际 ZoneId 指向它的物件数
  },
  pass: <六条全绿>
}
```

`invariants` 六条是从 §3 的「唯一真相」直接翻译过来的不等式 ——
写它们的目的正是让「双簿记不一致」这种最难查的 bug 变成一条红色断言。
**聚合判定的键名必须与子项写入的键名逐字一致**（M2 为此栽过一次）。

### `DevSequenceSim` 增加区域顺序断言

按用户真实操作顺序，**轮询直到条件成立，绝不写死帧数**：

| 步骤 | 断言 |
|---|---|
| 1. 牌库洗牌两次 | 成员集合相同、**次序至少变过一次**；`LastShuffleSeed` 被记录 |
| 2. 记录洗牌后的顶牌 | 双击牌库 → 手牌 +1、牌库 -1、**抽到的正是那张顶牌** |
| 3. 连抽 5 张 | 手牌 = 5、牌库 = 19；抽出的牌 `IsFaceDown = false`（手牌区强制翻开） |
| 4. 手牌清点 | 5 张在 `Row` 排版下位置互不重叠，且都落在手牌区矩形内 |
| 5. 把 1 张拖到出牌区 | 出牌区 +1、手牌 -1；物件 `ZoneId` 已切换 |
| 6. 把它拖到弃牌堆 | 弃牌堆 +1、出牌区 -1；该牌 `IsFaceDown = false` |
| 7. 拖回牌库 | 牌库 +1；该牌 `IsFaceDown = true`（牌库强制盖放） |
| 8. 从牌库拖一张到桌面空白 | 牌库 -1、`ZoneId = 0`、**`Visible = true`**（这条最容易漏） |
| 9. 把手牌填到超过 `MaxCards` | 超出的那次**整体被拒**，物件数不变，位置不变 |
| 10. 走完上面十步后重跑六条 `invariants` | 全绿 |

第 8 步和第 9 步是这次特意加的：它们各自对应一个**只在边界出现**的 bug
（拖出后卡片"消失"、超限时物件被吃进区域后卡死）。

### 画面证据

`.dev/shots/m3_zones.png`（默认整桌视角，看区域布局）+
`.dev/shots/m3_loop.png`（100% 近景，看牌库盖放与手牌排版）。

---

## 10. 实施顺序

每一步都编译通过、可跑自检，不留半成品：

1. `ZoneData.cs`（纯数据，无依赖）→ 编译
2. `Zone.cs` + `IZoneInteraction.cs`（能画出来，还没有交互）
3. `TabletopObject.ZoneId` + `ObjectState.ZoneId`
4. `ZoneManager.cs`（命中 + 排版 + 抽牌 + 洗牌）
5. `ObjectManager` 三处接缝 + `ViewportController.PrimaryDoubleClicked`
6. `Main` 装配 + `GameConfig` 常量 + InputMap
7. `DemoContent` 布区域 + HUD 提示条
8. `DevReport.zones` 六条不变量
9. `DevSequenceSim` 十步顺序断言
10. 跑通全绿 → 交验收 → 更新 README/STATUS/HANDOFF

---

## 11. 已确认的 5 个决定（用户已拍板）

| # | 决定 | 结论 |
|---|---|---|
| 1 | 手牌区默认排版 | **`Row` 和 `Fan` 都做**，默认 `Row`，右键菜单可切换 |
| 2 | 双击牌库抽到哪 | **抽到 `DrawTargetId` 指定的区域**（默认手牌区），找不到就退回桌面并吐司 |
| 3 | 牌库空自动把弃牌堆洗回 | **M3 不做** —— 那是玩法规则不是区域基础设施，留 M6 或另行指定 |
| 4 | `FaceOnEnter` 三态替代 `AutoFaceDown` 布尔 | **照做**（理由见 §1） |
| 5 | 区域「锁定」开关 | **加 `Enabled`**，锁住后不接受拖入、不响应双击 |

**另外两处按"照做"处理：**

- Stack 区域**不复用** `Pile` 对象（§3 有四条具体冲突），
  但复用徽章绘制，`PileId` 与 `ZoneId` 互斥
- 「左键点区域内空白」仍然算点空白（起框选 / 取消选中），区域不吞普通左键

> 决定 1 追加：手牌排版模式记在 `ZoneDefinition.SortMode` 上，
> 右键菜单「排版：横排 / 扇形」直接改它并重排 —— 所以运行时的切换
> 也走同一条 `ApplyLayout` 路径，不存在"两套排版逻辑"。
