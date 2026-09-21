# M4 设计：撤销 / 操作日志 / 存档

> **状态：✅ 十步全部完成，自检 16 节全绿，等用户验收。**
>
> M4 的目标是一句话：**乱操作一通之后，能回退；关掉再打开，还是那一桌。**
> 这是把工具从"演示"变成"能反复试玩"的那一步 —— 验玩法时最费时间的从来
> 不是摆牌，而是"刚刚那步改了什么、怎么改回去、昨天那版什么样"。
>
> 本文件同时是**实现记录**：每一节后面跟着"设计 → 实际 → 原因"，
> 见文末「实现后的偏差」。设计过程中被产品推翻的地方都留在正文里没有删 ——
> 那些句子记录了"当时为什么那么想"，比结论本身更值钱。

### 实际交付（与十步计划的对照）

| 步 | 计划 | 实际 |
|---|---|---|
| 1 | `SaveJson` + 三只 Converter | ✅ `json_simulation` 22 条 |
| 2 | `SceneSnapshot` + 公共不变量 | ✅ `undo_simulation` + `invariant_probe` |
| 3 | `CommandHistory` + 撤销/重做 | ✅ 改名 `UndoSystem`，`history_simulation` |
| 4 | 其余动作逐个接进历史 | ✅ 含骰子 / 堆 / 区域菜单；**没有实现 `ICommand`（见偏差）** |
| 5 | `DevUndoSim` 全量断言 | ✅ |
| 6 | 日志面板 + 时间旅行 + `history.jsonl` | ✅ 新增 `log_panel` 一节 |
| 7 | `--save-root` + `SaveSystem` | ✅ 提前到第 6 步做掉 |
| 8 | 存档面板 + `Ctrl+S` + 缩略图 | ✅ 缩略图接口留着（自检还没拍） |
| 9 | `DevSaveSim` 断言 | ✅ `save_simulation` 32 条 |
| 10 | 文档 | ✅ 本节 |

**判定节从八项变十六项**：原八项 + `json_simulation` / `undo_simulation` /
`invariant_probe` / `history_simulation` / `undo_flow_simulation` /
`log_panel` / `save_simulation`。

---

## 0. 前置调查（已实测，不是推测）

动手前先把两件事问清楚了，它们的结论直接决定了设计：

| 问题 | 实测结论 | 影响 |
|---|---|---|
| Godot 4.7.2 有没有 `--user-data-dir` 之类的命令行开关？ | **没有**（`--help` 里没有这个参数；`ProjectSettings` 的 `use_custom_user_dir` / `custom_user_dir_name` 运行时**能改**，但 Godot 把它拼成 `app_userdata/<名字>`，给绝对路径也照拼成 `app_userdata/D-/GodotProjects/...`，写不进去） | 不能借用引擎机制，**必须由应用自己接管存档根** |
| 自检进程（`shot.ps1` 拉起的游戏）能不能写**工作区里**的文件？ | **能**。实测写入 `.dev/userdata/probe_selfroot.txt` 成功 | 所以 `--save-root` 这条路由成立，**存档功能可以在沙箱里被自动断言**，而不是"等你自己点一下试试" |

第二条是这次调查里最值钱的一条：它意味着 M4 的存档不是"手测过就算"，
而是和 M1–M3 一样，每次自检都能重跑一遍存→改→读→比对的完整循环。

---

## 1. 公共底座：撤销与存档共用同一份"状态真相"

这是 M4 唯一的新抽象，也是整个设计的地基。

`ObjectState.cs` 开头那段注释在 M2 就写下了这句话：

> 这是 **M4 存档**与**撤销系统**的公共载体：
> 撤销不是「反向操作」，而是「把快照写回去」；存档就是把一堆快照存成 JSON。

M4 就是来兑现它的。所以：

**命令不实现"逆操作"，只负责把快照存下来、写回去。**

对比一下两条路：

| 做法 | 代价 |
|---|---|
| 每个命令写 `Undo()` 逆操作 | 每条都要正确可逆：并堆的逆是拆堆 + 恢复次序 + 恢复锚点；抽牌的逆是放回牌库**顶**并恢复盖放；区域移出的逆要恢复 `PileIndex`/`PileCount`/`Visible` 三个字段。**漏一个字段就是"数据全对、只是画错"那类 bug**（M3 已经栽过一次） |
| **快照写回** | 一条恢复路径覆盖全部命令。正确性由"快照是否完整"决定，而完整性是**可断言**的：恢复后逐字段比对 |

选快照。理由不是省事，是 **M3 那次徽章 bug 已经证明了「清字段」是最容易漏的地方**：
只要还存在"手写逆操作"，就存在"手写漏字段"。

### 1.1 快照里装什么

```csharp
/// 一桌的完整状态。撤销、存档、日志跳转都用它。
public sealed class SceneSnapshot
{
    /// 全部物件，顺序 = 绘制次序（谁压谁）。
    public List<ObjectState> Objects;

    /// 每个区域的成员次序（区域 id → uid 列表）。空表示该区域已空。
    public Dictionary<string, List<string>> ZoneMembers;

    /// uid 计数器。不复位的话，撤销"删除"之后新物件会撞 uid。
    public int NextUidSeq;
}
```

**为什么物件顺序必须在快照里**：画面上谁压谁完全由 `_drawOrder` 决定。
M3 的整摞翻转就踩过这个 —— 次序变了但 `_drawOrder` 没同步，看起来像"翻转没生效"。
撤销要恢复的是**画面**，所以次序是状态的一部分，不是实现细节。

**为什么不需要额外存堆的信息**：自由堆可以从物件自身推出来 ——
`PileId` 分组、`PileIndex` 定序、`Pile.Anchor` = 组内 `PileIndex` 最小的那张的 `Position`。
区域叠放的锚点同理（区域自己的 `StackAnchor`）。

> 也就是说：**`ObjectState` 的现有字段已经够描述一桌了。**
> 这是 M2 留下的设计红利，M4 不需要给 `ObjectState` 加任何新字段。

### 1.2 撤销的执行路径

```
RestoreSnapshot(snap):
  1. 物件对齐   快照里有、场景里没有 → InstantiateFromState 补上
                场景里有、快照里没有 → 从容器摘掉（RemoveChild + QueueFree）
                两边都有            → ApplyState
  2. 区域重建   ZoneManager.RestoreMembers(snap.ZoneMembers)
  3. 堆重建     按 PileId 分组、PileIndex 定序，重建锚点
  4. 绘制次序   按 snap.Objects 的顺序刷 _drawOrder（绝不能省）
  5. uid 计数器 复位到 snap.NextUidSeq
  6. 不变量复查 跑一遍八条区域一致性不变量，不一致就打红日志
```

第 6 步是关键：**撤销自己也要被断言**。少一个字段的 bug，
症状是"撤销之后牌库显示 20 张、实际只有 19 张"，靠肉眼根本看不出来。

为此把现在写在 `DevReport` 里的八条不变量**抽成公共函数** `ZoneInvariants.Check()`，
自检与撤销共用一份实现 —— 两份实现迟早会分叉，那时候自检就是绿的假象。

---

## 2. 命令规格

```csharp
public interface ICommand
{
    /// 中文可读描述，直接显示在日志里（"移动 3 张牌到出牌区"）。
    string Describe();

    void Do();
    void Undo();
}
```

| 命令 | 触发 | 备注 |
|---|---|---|
| `MoveCommand` | 拖拽落点后 | **一次手势 = 一条命令**，必须合并 |
| `MergePileCommand` | 拖到别的牌/堆上 | 并入目标堆（连它所在的堆） |
| `SplitPileCommand` | `Shift`+拖抽单张、右键「从堆中取出」 | |
| `DissolvePileCommand` | 右键「拆散这堆」 | |
| `GroupPileCommand` | 多选拖到空地成摞 | |
| `FlipCommand` | `F` / 右键「翻面」 | 目标是整摞时描述写"整摞翻转" |
| `RotateCommand` | `[` `]` / `Q` `E` / `Alt`+滚轮 / 右键旋转 | 连续转动合并 |
| `ResetRotationCommand` | 右键「重置旋转」 | |
| `DuplicateCommand` | `Ctrl+D` / 右键「复制」 | |
| `DeleteCommand` | `Del` / 右键「删除」 | |
| `ZoneAssignCommand` | 拖入区域 | 含被拒时的"退回原处"（被拒 = 无变化 = 不记历史） |
| `ZoneLeaveCommand` | 拖出区域 | 与 `MoveCommand` 合并在同一条拖拽命令里 |
| `ZoneDrawCommand` | 双击牌库 / `D` | 含"抽到哪、翻开、加入手牌"整条链 |
| `ZoneShuffleCommand` | `R` / 区域菜单洗牌 | 种子进描述："洗牌（种子 1234567）" |
| `ZoneLayoutCommand` | 右键「排版：横排 / 扇形」 | |
| `ZoneLockCommand` | 右键「锁定此区域」 | |
| `ZoneMoveAllCommand` | 区域菜单「把此区域牌移到 X」 | |
| `RollDiceCommand` | 轻点骰子 | 见 §9 待确认 2 |

### 2.1 什么**不进**历史

- 相机（平移 / 缩放 / `Home` 适配）—— 视角不是数据
- 选中集变化 —— 但它**随快照恢复**：撤销一次移动，那几张牌重新亮起来。
  这比"撤销后什么都不选中"顺手，因为你的下一个动作通常就是"再挪一次"
- 网格吸附开关、面板显隐、区域张数徽章
- 骰子的滚动动画帧（只记最终点数）

### 2.2 合并规则："一次手势 = 一条历史"

这是 M4 最容易翻车的地方。如果每次 `PrimaryDragged` 都记一条，
拖一张牌 100 帧就产生 100 条历史，`Ctrl+Z` 要按 100 次 —— 撤销功能等于废了。

```csharp
public sealed class CommandHistory
{
    private const int Capacity = 300;
    private const ulong MergeWindowMs = 800;

    /// 拖拽专用：手势开始取 before，结束时取 after，整段只产出 1 条。
    public void BeginGesture();
    public void EndGesture(ICommand command, string description);

    /// 连击专用：同类命令在 800ms 内合并（连转 4 次 15° = "旋转 60°"）。
    public bool TryMergeWithTop(ICommand next, ulong nowMs);
}
```

两条路径分开是有意的：
**拖拽靠"手势"划分，连击靠"时间窗"划分** —— 前者是明确的开始与结束，
后者只能靠时间猜。混成一条规则会把"连续按 F 翻 5 张不同的牌"也合并掉。

---

## 3. 操作日志面板

| 项 | 做法 |
|---|---|
| 打开 | `Tab`（可重绑） |
| 位置 | 右侧固定宽 320px 的面板，从顶栏下沿到底部提示条 |
| 内容 | 每行一条：序号 + `Describe()` 中文描述 + 相对时间（`3 秒前`） |
| 当前步 | 高亮；撤销过的行变暗 |
| 点击一行 | 见 §9 待确认 3（默认：**撤回到那一步**，即时间旅行） |
| 复制 | 面板底部「复制全部」→ 剪贴板纯文本，方便贴进 issue |
| 落盘 | 同时追加到 `<saveRoot>/logs/history.jsonl`（时间戳 + 描述 + 命令类名） |

**落盘为什么值得做**：会话里的面板一关就没了，而"我昨天是怎么把那摞牌搞乱的"
正是这个工具要回答的问题。JSONL 每行一条，可以直接 diff、也可以二次处理。

---

## 4. 存档：文件格式

沿用 `AppPaths` 里已经定好的目录约定，一个存档 = 一个自包含目录：

```
<saveRoot>/saves/
  index.json                 存档列表（名称 / 最后修改 / 缩略图文件名）
  存档1/
    project.json             卡牌定义 / Token 定义 / 区域定义 / 桌面主题
    state.json               当前对局：全部物件状态 + uid 计数器
    images/                  这个存档导入的图片
    thumbs/                  存档缩略图（从视口截图，延迟一帧取）
```

### 4.1 `project.json`

```jsonc
{
  "formatVersion": 1,
  "board": { "background": "#3b4252", "size": [3200, 2000], "grid": 100 },
  "cards":  [ /* CardDefinition[] —— 与内存里同构，直接序列化 */ ],
  "tokens": [ /* TokenDefinition[] */ ],
  "zones":  [ /* ZoneDefinition[] —— 含 Rect / SortMode / FaceOnEnter / MaxCards */ ]
}
```

#### 4.1.1 先落一块地基：`SaveJson` 与三只自定义 Converter

**项目目前还没有任何 POCO 的 JSON 读写** —— 只有 `DevCapture` 用 Godot 的
`Json.Stringify` 写自检报告（那棵树全是 `Dictionary`/`string`，不是 POCO）。
而 `BoardTheme.cs` 的注释在 M1 就写下了"整套存档用 `System.Text.Json` 序列化"。
所以 M4 要先补上这一层，不能等到读档时才现补：

```csharp
internal static class SaveJson
{
    // 一眼能看懂的配置，不是压缩包：缩进、字段顺序稳定、方便手改与 diff
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,  // 中文不转成 \uXXXX
    };

    internal static string Serialize<T>(T value);
    internal static T? Deserialize<T>(string text);   // 失败返回 null 并打警告，绝不抛穿
}
```

三只 Converter（`System.Text.Json` 不认 Godot 的值类型，不给就会在运行时抛）：

| 类型 | 写法 | 为什么 |
|---|---|---|
| `Color` | `"#3b4252"` / `"#3b4252ff"` | 与 `GameConfig` / 主题里的写法一致，人能直接改 |
| `Vector2` | `[3200, 2000]` | 比 `{"x":3200,"y":2000}` 短，且一屏能看完 |
| `Rect2` | `[80, 60, 300, 460]` | 同上；`ZoneDefinition.Rect` 全靠它 |

**`UnsafeRelaxedJsonEscaping` 是必须的**：默认编码器会把「牌库」写成 `\u724c\u5e93`，
于是"存档要能手改、能 diff"这件事当场作废。名字里带 "Unsafe" 是因为它不转义
HTML 敏感字符 —— 我们是本地文件，不是网页输出。

> 注意 `CardField.Color` 是 `Color?`，nullable 的 Converter 要单独处理
> （`JsonConverter<Color?>` 或让写出端在 null 时写 `null`）。

**序列化的目标类型就是内存里的定义类**（`CardDefinition` / `TokenDefinition` /
`ZoneDefinition`），不另建一套 DTO。它们是纯 POCO、没有 Godot 依赖，
再加一只 Converter 就能直接读写 —— 另建 DTO 等于多一份要同步维护的定义，
而两边一旦分叉，症状是"存进去的东西读出来少了一半"。

**`project.json` 只写非默认值吗？** 不写全量。反序列化之后跑一遍
`ZoneDefaults.For(kind)` 填缺省字段 —— 于是以后加字段不会让旧存档炸，
而 `project.json` 里能一眼看出"这个存档改过什么"。

> 落地时先写一个 `save_json_roundtrip` 的自检断言：把三种定义各造一份、
> 序列化、反序列化、逐字段比。**先证明这块地基是对的，再往它上面盖东西。**

### 4.2 `state.json`

```jsonc
{
  "formatVersion": 1,
  "nextUidSeq": 37,
  "objects": [ /* ObjectState[]，顺序即绘制次序 */ ]
}
```

**`nextUidSeq` 必须存。** 不存的话：存档 A 有 `card-0036`、删掉它、存档、
再开一个存档并从 A 复制过来 → 复制的物件会拿到 `card-0036`，而 A 里已经有一个。
**uid 撞车是最难查的一类 bug**，因为两个物件在代码里长得完全一样。

### 4.3 图片路径

`TextureStore` 现在把图片按 **`user://` 绝对路径**缓存，而 `AspectRatioContainer`
之外的所有地方都只认完整路径。改成：

- `project.json` 里只存**文件名**（`"bg.png"`），不存路径 → 存档目录可整体搬走
- 运行时用 `AppPaths.ImageFile(saveName, fileName)` 拼出 `res://` 之外的完整路径
- **切换存档时必须清 `TextureStore` 缓存**（按存档分桶，或整体清空）

### 4.4 读档时重建成员关系

这是 §1 那句"ZoneId 是声明不是加入"的落地：

```
读 state.json
  → 逐个 InstantiateFromState（此时每个物件的 ZoneId 已就位，但区域还不认识它们）
  → ZoneManager.RestoreMembers(按 ZoneId 分组的 uid 列表)
  → 按存档里的次序刷 _drawOrder
  → 跑一遍八条不变量，绿了才算读档成功
```

顺序不能反：先造物件再让区域认领。反过来会在 `Zone.Members` 里留下
指向尚未创建的物件的悬空引用。

---

## 5. 存档管理界面

顶栏加一个 `存档 ▾` 按钮（在 `100%` 左边），弹出面板：

```
┌─ 存档 ─────────────────────────────┐
│ ● 存档1          9/21 10:32   [切换] │
│   测试局         9/20 22:10   [切换] │
│   spell-v2       9/19 18:04   [切换] │
├────────────────────────────────────┤
│ [新建] [另存为] [重命名] [删除]      │
│ 当前：存档1     [立即保存]           │
└────────────────────────────────────┘
```

`[删除]` 二次确认（`ConfirmationDialog`）—— 删档是不可撤销的，
而"撤销"这件事本身正是 M4 在做的，不做二次确认会很难看。

---

## 6. 快捷键与输入接缝

沿 M1 定的规矩：**输入只有一处入口，命令由 `ViewportController` 分发**。
撤销系统不自己抢 `_UnhandledInput`（两个节点各抢一次，谁先拿到取决于节点顺序 —— M3 已经知道那种 bug 极难查）。

| 键 | 动作 |
|---|---|
| `Ctrl`+`Z` | 撤销 |
| `Ctrl`+`Shift`+`Z` / `Ctrl`+`Y` | 重做 |
| `Ctrl`+`S` | 保存当前存档（不弹框，吐司提示） |
| `Ctrl`+`Shift`+`S` | 另存为 |
| `Tab` | 开关操作日志面板 |
| `F1` | （M5 的运行时编辑器，M4 不用） |

`ViewportController` 已有 261 行、负责点/拖/双击的判定，
再加撤销与存盘会让它变成"什么都管"。所以新增**一个薄分发层**：

```
ViewportController ──信号──> CommandRouter ──> UndoSystem / SaveSystem / LogPanel
```

`CommandRouter` 只做三件事：把按键映射成动作、把拖拽起止翻成
`History.BeginGesture/EndGesture`、把 HTML 输入控件有焦点时的按键放行
（在 `LineEdit` 里按 `Ctrl+Z` 应该是撤销**输入**，不是撤销摆牌 —— 这条不做就会很难用）。

---

## 7. 怎么验证

**全部走自检**，`--save-root <工作区临时目录>` 让存档落到可写位置。
下面每条都是自检报告里的一节断言，跑一次约 40 秒。

### 7.1 撤销（`DevUndoSim`）

| 断言 | 内容 | 为什么必须有 |
|---|---|---|
| `undo_restores_exactly` | 初始快照 → 干 12 件事（移动/成摞/抽牌/打出/弃牌/洗牌/翻整摞/复制/删除/排版/锁定/掷骰）→ 全撤销 → 与初始快照**逐字段**比对 | 核心断言。比的是 uid/位置/旋转/正反/PileId/PileIndex/ZoneId/Visible + 区域成员 + `_drawOrder` |
| `redo_reapplies_exactly` | 全撤销后再全重做 → 与"操作后快照"逐字段一致 | 只测撤销会漏掉"重做把状态搞坏" |
| `drag_is_one_history_entry` | 拖一张牌 → `History.Count` 只 +1，描述是「移动 1 张牌」 | 少了它，拖一次产生上百条历史，`Ctrl+Z` 形同虚设 |
| `rapid_flip_merges` / `slow_flip_does_not` | 800ms 内连按两次 `F` → 合 1 条；隔 2 秒再按 → 2 条 | 合并规则最容易写反 |
| `undo_invariants_hold` | 撤销之后八条区域不变量仍全绿 | 撤销漏清字段的 bug（幽灵声明 / 残留徽章）只有这条抓得住 |
| `history_capacity_capped` | 400 次操作后 `Count == 300`，最早的被丢 | |
| `log_descriptions_are_chinese` | 每条描述非空、含中文、不含 `null`/类名 | 描述是给人看的，退化立刻要发现 |
| `log_time_travel` | 点日志第 3 条 → 场景状态 == 第 3 条之后的快照 | |
| `every_action_records` | 逐个触发右键菜单项 + 每个快捷键，每个都产出 ≥1 条历史 | 防"某个入口忘了接历史"——那是最容易漏的缝 |

### 7.2 存档（`DevSaveSim`）

| 断言 | 内容 |
|---|---|
| `save_load_roundtrip_exact` | 复杂局面 → 保存 → 打乱 → 读档 → 与保存时逐字段一致（含区域定义、桌面主题） |
| `reload_twice_no_uid_collision` | 读档 → 复制物件 → 再读档 → 再复制：uid 集合无重复 |
| `unknown_definition_is_graceful` | 手改 `project.json` 指向不存在的卡定义 → 跳过该物件 + 打警告，**不崩** |
| `save_files_are_valid_json` | 两个 json 能被 `Json.ParseString` 解析，且有 `formatVersion` |
| `image_path_is_save_relative` | `project.json` 里是文件名不是路径；换存档目录后仍能加载 |
| `switch_save_reloads_images` | 存档 A/B 各有一张不同图 → 来回切换，`TextureStore` 拿到的是各自那张 |
| `selfcheck_never_touches_real_saves` | 自检全程只写 `--save-root` 指定的目录；真实 `user://saves` 无新文件 |

最后一条是**元断言**：它保证"自检不会污染你自己的存档"。
不写这条的话，某天你发现自己的存档被自检改乱了，而那时已经查不出是哪次跑的。

### 7.3 报告结构

`DevReport` 加两节：`undo_simulation`、`save_simulation`。
沿用既定约定 —— **某节没有 `pass` 键 = 没跑（skipped），不是失败**。
判定节从八项变十项；`.dev/counts.ps1` 直接就能用。

---

## 8. 实施顺序

1. `SaveJson` + 三只 Converter（`Color` / `Vector2` / `Rect2`）→ 编译 + `save_json_roundtrip` 自检绿
2. `SceneSnapshot` + `ZoneInvariants.Check()`（抽公共实现，`DevReport` 改用它）
   → 编译 + 自检**必须仍然全绿**
3. `CommandHistory` + `CommandRouter` + 撤销/重做两个快捷键（先只接移动与翻转）
4. 把其余动作逐个接进历史 → 每接一类就补一条 `every_action_records`
5. `DevUndoSim` 全量断言 → 全绿
6. 日志面板 UI + 时间旅行 + `history.jsonl`
7. `--save-root` + `SaveSystem`（project / state 两个 json）
8. 存档管理面板 + `Ctrl+S` + 缩略图
9. `DevSaveSim` 断言 → 全绿
10. 更新 README / STATUS / `docs/m4-undo-saves.md`（补"实现后的偏差"）→ 交验收

第 1、2 步刻意排在"接命令"之前：
**先把地基（JSON 读写）与裁判（共用的不变量）做对**，
否则接命令的过程中一旦出问题，你分不清是撤销写错了、序列化丢了字段、还是自检自己算错了。

---

## 9. 已确认的 6 个决定（用户已拍板）

| # | 决定 | 结论 |
|---|---|---|
| 1 | 启动行为 | **自动载入上次的存档**（首次运行才用示例内容） |
| 2 | 骰子结果能否撤销 | **能** —— 骰子与洗牌都进历史 |
| 3 | 日志点击行为 | **点击某行 = 撤回到那一步**（时间旅行） |
| 4 | 存档文件拆分 | **拆**：`project.json`（定义）+ `state.json`（对局） |
| 5 | 快捷键 | **照 §6 默认**：`Ctrl+Z` / `Ctrl+Shift+Z`+`Ctrl+Y` / `Ctrl+S`+`Ctrl+Shift+S` / `Tab` |
| 6 | 撤销粒度 | **快照写回**（不是每个命令写逆操作） |

### 决定 1 的落地：`last_save`

`<saveRoot>/last_save.txt` 一行文本，存上次打开的存档名。启动读它，
文件不存在或指向已被删掉的存档 → 退回「新建存档1 + 示例内容」。

> 也可放 `ProjectSettings`，但那是引擎配置、会被 `.godot/` 的重建带走。
> 一个纯文本文件更直白，坏了好手改，也方便自检断言。

### 决定 2 的代价（说在前面）

骰子进历史 = **可以 `Ctrl+Z` 重掷**。验玩法时这是个双刃剑：
连掷三次取最大值会让数值手感的反馈失真。

之所以仍然选它，是因为"每个动作都该能回退"这条一致性更值钱：
一旦留了"某些操作撤不了"的例外，下次出问题时你就得先想"这一步到底能不能撤"。
真正的做法是**让报告把"重掷"这件事显形** —— 自检里骰子断言的写法是
「点一次 → 记录点数 → 撤销 → 重新点 → 点数应当**不同**（随机性还在）」，
而不是"撤销后点数恢复" —— 后者会把"撤销把 RNG 也冻住了"当成正确行为。

### 决定 3 的落地

时间旅行 = 从当前步循环 `Undo()` 到目标步（或 `Redo()` 到后面的步）。
日志面板里比当前步新的行显示为「可重做」并变暗，而不是不可点。

---

## 实现后的偏差

> 每完成一步就往这里追加。写法沿用 M2/M3：**设计 → 实际 → 原因**。

### 第 1 步：`SaveJson` 地基 ✅（`json_simulation` 22 条全绿）

`scripts/Core/SaveJson.cs` + `scripts/Dev/DevJsonSim.cs`。
自检里第一次跑这一节就抓出**三个真 bug**，全都在这块新地基里 —— 这就是
"先单独验这一层"的价值，它们如果留到读档时才暴露，症状会是"存档少了个字段""颜色不对"，
极难定位。

| 项 | 设计 | 实际 | 原因 |
|---|---|---|---|
| 枚举写法 | 写名字（`"stack"`） | 写名字，但**首字母大写**（`"Stack"`）—— 与 `JsonNamingPolicy.CamelCase` 无关 | `JsonStringEnumConverter` 不套用命名策略。可读性已达成，不为大小写再包一层 |
| `[JsonIgnore]` | 没想过 | **必须显式加**在 `ZoneDefinition.Center` 与 `BoardTheme.BoardRect` 上 | `System.Text.Json` 默认会把**只读的计算属性写进 JSON**，读的时候又静默丢弃 —— 存档里白多一行，还会与真相字段漂移（改了那行毫无效果） |

**三个坑的详细记录**（都值得记，因为都不会报错）：

1. **`JsonStringEnumConverter` 有两个同名类型。** Godot 命名空间里有一个
   （给引擎的 `Json` 用的），BCL 里有一个。`using Godot;` 会把它带进来**遮蔽** BCL 那个，
   于是 `new JsonStringEnumConverter()` 拿到的是**错的**那个 ——
   它默默不生效，枚举被写成数字 `1`。**必须写全限定名**
   `System.Text.Json.Serialization.JsonStringEnumConverter`。
   抓它的是断言 `json_enum_is_named`。

2. **8 位颜色量化必然损失精度，"往返一致"不能按 float 比。**
   `new Color(1,1,1,0.11f)` 的 A 是 `0.1099999994`；`0.11 × 255 = 28.05` 四舍五入成 28，
   写出去再读回来是 `28/255 ≈ 0.10980392`。**产品没错，是断言在拿 8 位存储要求 float 级相等。**
   新增 `ColorJsonConverter.Quantize()` / `Matches()` 作为"存档精度下的相等"，
   凡是要判断两个颜色在存档里是否相同，都必须过它。

3. **字段条数不能靠眼睛数。** 我把 `ZoneDefinition` 数成 13，实际 **14**。
   改对的办法不是重数一遍，而是把 `json_field_names` 打进报告照着核对 ——
   所以报告里现在常驻这份清单：以后谁加了字段、或加了计算属性忘了 `[JsonIgnore]`，
   报告里直接能看出多了哪一行。

另外一条设计调整：**断言总数不再写死**（`Checks.AllPass()` 取代 `Passed() == 16`）。
写死条数的后果是"每加一条断言都要回来改数字，忘了改就是新断言全绿、聚合报红" ——
那正是「聚合键名漂移」那类错误的另一个化身。

### 第 2 步：`SceneSnapshot` + 公共不变量 ✅（`undo_simulation` 10 条全绿）

`scripts/Core/SceneSnapshot.cs`、`scripts/Core/ZoneInvariants.cs`、
`scripts/Dev/DevUndoSim.cs`；`DevCapture` 的探针节从 8 个变成 12 个
（新增 `json_simulation` / `undo_simulation` / `invariant_probe`）。

**主体断言是一条往返**：开局抓快照 → 让后面一百多条断言把桌子折腾乱
（抽牌、打出、弃牌、洗牌、整摞翻转、成摞、拆堆、删物件、复制）→ 把快照写回去
→ **与开局逐字段比对**。实测 `field_diff = 0`、`zone_diff = 0`、`orphan = 0`，
恢复后八条不变量全绿。

#### 抓到的真 bug：`DuplicateObjects` 复制了原件的 uid

`CaptureState()` 带来的 uid 非空，而 `InstantiateFromState` 的规则是
`IsNullOrEmpty(state.Uid) ? NextUid() : state.Uid` —— **刚生成的 uid 被原件那份覆盖**，
副本与原件完全同名。一局下来 60 个物件只映射出 36 个不同 uid。

这正是 `ObjectState` 注释里写的"最难查的一类 bug"：两个物件在代码里长得完全一样，
而按 uid 找物件的地方（堆成员、区域成员、存档、快照写回）会随机命中一个。
`undo_simulation.uid_unique_on_board` 现在常驻。

#### 设计与实际的三处偏差

| 项 | 设计 | 实际 | 原因 |
|---|---|---|---|
| 快照内容 | 物件 + 区域成员 + uid 计数器 | **还要存区域定义 + 建区次序** | `zone_teardown` 会把区域删掉。只存成员的话，写回之后牌还指着 `demo.zone.deck`、区域却不存在了 —— 正是"孤儿声明"报的东西。**撤销必须能把区域本身也恢复出来** |
| 写回顺序 | "物件 → 堆 → 区域" | **区域对齐 → 丢弃陈旧堆（`DropAllPiles`，不碰物件字段）→ 物件 → 重建堆 → 区域成员 + `ApplyLayout` → 绘制次序 → 归一化残留 → uid 复位** | 两条权威各管各的对象集合（自由堆成员恒 `ZoneId == ""`，区域成员恒 `PileId == 0`）。**"解散旧堆"如果排在半途做，会把刚由区域排版写好的 `PileIndex` 清掉** —— 陈旧堆的成员关系与快照并不一致，那个"解散"清掉的正是快照里正确的值。这一条查了很久 |
| `PileCount` | 随 `ObjectState` 一起恢复 | **它是派生值，没进 `ObjectState`** —— 恢复后必须由写回这一步归一化 | 否则会留下"不在任何叠放组里却挂着张数"的残留（实测两张散牌挂着上一轮某堆解散后的 `count=2`）。与 M3 那个「从牌库拖出来还挂着 30」是同一类 |

#### 裁判自己也要被验证

`invariant_probe` 那一节注入 **5 种真实 bug 的样子**，要求每条恰好触发对应的一条不变量：

| 注入 | 应当触发 |
|---|---|
| 成员 `ZoneId` 被清掉 | `zone_id_matches_membership` |
| 半截账本（区域忘了成员、成员还认着区域） | `member_counts_match` |
| 物件指着一个**不存在**的区域 | `no_orphan_zone_claims` |
| `PileId` 与 `ZoneId` 同时非零 | `pile_zone_exclusive` |
| 散件挂着 `PileCount` | `badge_only_when_stacked` |

第一版我把"半截账本"断言成 `no_orphan_zone_claims`，结果红了 —— **是自检把这个错误纠正过来的**：
`no_orphan_zone_claims` 问的是"这个区域还在不在"，区域明明还在、只是账本少一行。

> 为什么必须有这一节：M4 最依赖的保证是"撤销/读档之后一致性仍然全绿"。
> 若裁判是空转的（永远返回绿），那句话**毫无价值**。

#### 一处刻意不改产品的地方

`badge_only_when_stacked` 原本读 `PileCount` 当真相。改成按上下文推之后，
开局桌面上曾有 21 条报红 —— 因为牌库里 20 张**未显示徽章的成员**都带着 `PileCount = 20`，
而渲染本来就只在顶张画徽章。这是**既有口径**，把判红就会变成"改产品去迁就断言"。
最终判据落到一句与渲染无关的话：**`PileCount` 必须等于它所属叠放组的真实张数，
不在任何叠放组里就必须是 0**。开局与恢复后都成立。

#### 已知的偶发抖动

`sequence_simulation` 的悬停目标那几条看到过**一次**变红，同一份代码重跑即通过，
之后连跑 3 次全绿。判定是既有偶发问题（那几条只依赖鼠标悬停命中，与 M4 无关），
但当时没保留复现窗口。已记进 `docs/STATUS.md`。

### 第 3 步：`UndoSystem` + 历史断言 ✅（`history_simulation` 22 条全绿）

`scripts/Core/UndoSystem.cs`、`scripts/Dev/DevHistorySim.cs`；
`Main.tscn` 加 `Undo` 节点、`ObjectManager.Undo` 接上、各动作收口写入历史。

撤销栈上限 300，`Ctrl+Z` / `Ctrl+Shift+Z` / `Ctrl+Y` —— 这些 InputMap 动作
（`tt_undo` / `tt_redo`）**M1 就预留好了**，M4 直接接上。

历史走"起止两条边"而不是"每一步"：`PrimaryDragStarted → BeginGesture`、
`PrimaryReleased → EndGesture`。一次手势产出**恰好 1 条**（断言
`drag_records_exactly_one_entry`）—— 若每帧记一条，拖一张牌就是上百条，
`Ctrl+Z` 会形同虚设。

#### 抓到的真 bug：拖拽手势中间按 `Ctrl+Z` 会把历史弄坏

这是自检逼出来的，**而且用户也能踩到**（按住左键拖着牌、同时按 `Ctrl+Z`）。
原来的行为：手势的 `before` 取自撤销之前、`after` 取自撤销之后 ——
于是**把一次撤销的结果记成了拖拽的结果**。更糟的是：这次记录算"在时间线中间做了新操作"，
于是会把被撤销的那一条从时间线上删掉并替换。历史从此与真实状态错位，
症状是"撤销之后牌没回到该在的地方"。

处置：**手势进行中拒绝撤销 / 重做**（`Undo` 返回 false，
`TravelTo` 返回 `-1`），并吐司提示「正在拖拽，先松开鼠标再撤销」。
交互工具里这是通行做法 —— 一个正在进行的拖拽本来就不该被另一种操作从中间打断。

#### 第二处：净效果为零的连击不能合并

连按 `[` `]` 各一次 → `15° - 15° = 0`，桌面回到原样。合并会把标签算成
「旋转 0°」并**不新增条目**，于是历史里留下一条什么都没干的记录，
后面所有条目的语义都被它带偏。现在：**净效果为零时不合并，记成独立的一步**
（"转回原位"本身就是一个合理且用户做过的操作）。

#### 一处是断言自己错了，不是产品错

第一版把"撤销一步 / 重做一步"紧跟在拖拽断言之后，而合成输入下
**松手事件晚一帧才被输入路由处理**。于是 `Undo()` 正好落在手势中间 ——
查了半天才意识到是断言自己的时序问题。

处置：断言在拖拽之后多等一帧；撤销/重做那段**整体挪到所有手势之后**。
这段顺序在代码里有注释标明「不是随便排的」。

#### 两处如实记下的取舍

| 项 | 处理 |
|---|---|
| 菜单项走**程序化触发**（`EmitSignal(IdPressed)`）而不是合成点击 | `PopupMenu` 是 `Window`，弹出来会抢走后续合成鼠标事件（M2 为此栽过）。这里验的是"菜单那条分支有没有接历史"，不是"点击能不能命中菜单项"——在代码与文档里都写明了 |
| 时间旅行暂时**直接调** `TravelTo` | 日志面板第 6 步才做。面板做好之后这一段改成"点面板上的第 N 行" |
| 超限丢最旧条目**没有断言** | 要触发得填满 300 条（约 300 次操作、每次一份全表快照），代价远大于它保护的东西；那段逻辑短到能直接看明白。**不写假断言，写清楚为什么没写** |

#### 合并窗口用帧数，不用毫秒

`MergeWindowFrames = 36`（约 0.6 秒 @60fps）。用帧数是有意的：
合成输入的自检里帧是唯一可靠的"时间"，依赖真实毫秒会让断言变成一条
随机器速度变化的抖动源 —— M2 就因为写死帧数栽过一次（那次是反过来的教训）。

### 第 4 步：把剩下每个入口接进历史 ✅

`ObjectManager`（骰子轻点 / 骰子菜单 / 从堆中取出 / 拆散这堆）、
`ZoneManager`（区域菜单那几项在第 4 步之前就已接好）。
`history_simulation` 的 `every_entry_records_history` 之外，新增一段
**"每个入口恰好一条"**：只数"有没有记"是不够的，
一个入口记两条（用户要按两次 `Ctrl+Z`）和记零条一样能蒙过去。

#### 抓到的两个真 bug

1. **骰子点数不在状态里。** `ObjectState.SameAs` 不比 `DiceValues` / `DiceSeed`，
   于是"掷骰"根本不是一次状态变化：`Record` 认为什么都没变、连历史都不记，
   而快照写回又会把骰子还原成掷之前的点数。
   用户看到的是「点了骰子、数字跳了、**但 Ctrl+Z 撤不掉它**」。
   顺带补上 `Clone` 漏拷的 `DiceSeed` —— 漏拷时快照里那一栏恒为 0，
   而 0 恰好是"未掷过"的哨兵值。**这种"错得很像对的"漏字段只有逐字段比对抓得住。**

2. **`SceneSnapshot.RestoreZones` 对已存在的区域只对 id、不更新定义。**
   于是"锁定此区域"这类只改区域定义的动作撤销不掉：牌序、成员、位置全都正确恢复，
   唯独那块区域的锁还挂着。`ZoneDefinition` 是引用类型而快照存 `Clone()`，
   `ReferenceEquals` 永远为假 —— 靠"引用相同就跳过"这类优化发现不了。
   抓它的是 `undo_restores_exactly`，差异精确到一行 `区域定义 demo.zone.deck`。
   **已回退验证：退回修复它必红。**

#### 一处与蓝图的偏差：没有 `ICommand`

§2 里的命令接口（`Describe/Do/Undo/TryMergeWith`）**没有实现**，
因为决定 6 选了"快照写回"这条路线之后，它就没有立足点了：
"接历史"= 在动作之后调一次 `UndoSystem.Record(中文描述)`，
而"合并"由 `mergeKey` + 帧窗口处理。
写一个只有 `Describe()` 有意义的接口，等于给自己加一层要维护的空壳。
蓝图里那张命令表的**语义**全部保留（每个动作一条历史、拖拽合并、连击合并），
只是落成了"收口调用"而不是"命令对象"。

#### 三处实现细节

| 项 | 做法 | 为什么 |
|---|---|---|
| 骰子的两个入口 | 轻点那条**自己记一条**并让 `EndGesture` 跳过收尾；菜单那条直接 `Record` | 轻点没有拖动，本来就不属于"一次拖拽 = 一条历史"那套。不让 `EndGesture` 跳过就是一次点击两条 |
| "已经记过"的判据 | `ObjectManager.DropAlreadyRecorded` 标志，**不是**读拖动距离 | 距离是内部量、什么时候复位是另一处实现细节；用"我记过了"这件事本身当判据就没有时序可言 |
| 兜底描述 | `DescribeLastDrop()` 保证非空（"移动 N 个物件"） | 它逼出了一条**空描述的历史**，见第 6 步 |

### 第 6 步：操作日志面板 + 时间旅行 + `history.jsonl` ✅

`LogPanel.cs` / `HistoryLog.cs`。面板不自己存历史：一切从
`UndoSystem.LogEntries` 读，挂在新增的 `HistoryChanged` 信号上重建；
落盘也挂同一个信号，只追加"比上次新"的那几条。
两处各自维护一份列表迟早会分叉，症状是"面板显示 12 条、实际 13 条"。

时间旅行的断言从"直接调 `TravelTo`"改成了**真的点第 3 行**，
并且游标必须恰好等于 3（选中间而不是 0 或末行：那两个位置用"是不是 0/末端"就能蒙对）。

#### 抓到的三个真 bug

1. **一条空描述的历史。** 来源是 `ObjectManager` 的框选分支提前 `return`，
   没给 `_lastDropLabel` 赋值，而手势确实改了东西、`EndGesture` 照样记了一条。
   处置：框选显式标记"这次手势不值得记"，其余情况给兜底描述。
   新增四条常驻断言（非空 / 含中文 / 不是类名 / 下标连续）。

2. **`FileAccess.Open(ReadWrite)` 打不开不存在的文件**（`FileNotFound`）。
   于是"目录建好了、一条也写不进去、还什么都不报"。
   处置：先按 `Write` 模式创建一次，再 `ReadWrite + SeekEnd` 追加。

3. **面板高度恒为 0 —— 它在屏幕上根本不显示任何东西。**
   根因：在 `AddChild` **之前**设 anchors/offsets，而锚点要在进树之后的布局计算里
   才解析；`SetAnchorsPreset` 又会先把 offsets 归零，之后改 anchor 也不重算。
   处置：UI 构建挪进 `_Ready`。

   > 这个 bug 值得单独记：宽度对（320）、位置对（右边缘）、`Visible` 为真 ——
   > **原有三条判据全都通过**。所以 `log_panel` 这一节现在量的是矩形本身
   > （含"高度是否等于带宽"），外加"面板矩形里颜色数 ≥ 20"
   > （纯色板是 1~2 种，实测 257）：矩形量对、`Visible` 为真，
   > 都还可能是"一块和桌面同色的空板"。

#### 最有价值的一条教训：探针不许改变被测对象的可观测状态

查了很久的一件事：报告里**每条入口的标签都对、条数却全是 0**。
两份数字自相矛盾，第一版据此误判成"产品漏记了历史"。

真相是**探针自己没把手擦干净**：它"测完就撤销"，于是时间线上留下一条"可重做"；
下一个探针写入历史时走的是"在时间线中间做新操作 → 截断掉后面的"那条路，
**条数原地不动**。

处置：`UndoSystem.DiscardRedo()` + 探针的 `Settle()`（撤销 + 丢弃尾巴 + 等一帧）。
判据写进代码注释：**诊断与探针不许改变被测对象的可观测状态 —— 时间线游标也是。**

> 挖出它的工具也留下来了：`RecordAttempts` / `RecordSuppressedApplying` /
> `RecordSuppressedUnchanged` / `AppendCount` / `MergeCount` / `TruncatedRedoCount` /
> `EntryLabelsForTest` / `PushTrace + TraceMark`。
> 它们把"哪一步、第几条、什么原因"变成报告里可以直接读的数字。
> **"把猜换成读"是这一轮最省钱的一件事。**

### 第 7–9 步：存档 ✅（`save_simulation` 32 条）

拆成三个文件：`SaveData.cs`（格式）、`SaveSystem.cs`（读写 + 换存档）、
`SavePanel.cs`（菜单界面）。读档**复用撤销那套写回**：把 `state.json`
装进一份 `SceneSnapshot` 再 `Restore` —— 那个顺序是撤销的自检反复折腾过的，
读档自己再写一遍等于把坑重新挖开。

顺带把第 7 步的前置 `--save-root` 提前做掉了（第 6 步的落盘就需要它）。
新增 `--fresh`：报告里所有断言都建立在示例内容之上，
上一次自检留下的存档被自动载入会让十几条互不相关的断言同时变红。

#### 抓到的两个真 bug

1. **`state.json` 少了区域成员次序。** 第一版只存物件的 `ZoneId`
   （那是"我属于哪个区域"的**声明**），而"谁是顶牌"完全由 `Zone.Members`
   的次序决定、物件身上查不到。
   症状：读档后牌库成员表是空的、16 张牌却自称在牌库里，
   报 `member_counts_match`，而"坏定义优雅降级"那条**无关的**断言也跟着红。
   已回退验证：退回修复它必红。

2. **读档时快照没带区域定义**，于是 `RestoreZones` 把刚建好的区域当成"多出来的"
   删掉，而成员的 `ZoneId` 还指着它们 —— 正是"孤儿声明"。
   **而逐字段比对全是 0 差异：数据是对的，坏的是那层关系。**
   这是"裁判自己也要被验证"那条教训的又一次应验。

#### 一连串"静默失败"

这一节踩到的坑有个共同形状：**失败不报错，只是结果不对**。

| 现象 | 根因 |
|---|---|
| 面板高度 0 | 锚点在 `AddChild` 之前设 → 布局计算里没人解析它 |
| 日志文件一直是空的 | `Open(ReadWrite)` 不创建不存在的文件，返回 null |
| 自检跑完磁盘上一个存档都不剩 | 收尾那个 `Load` 排在"改名 / 删除"之后，静默失败 |
| 读档"数据全对"却判红 | 快照没带区域定义，关系被删了 |

处置的共同点是**给每个失败点留一条能被读到的痕迹**：
`LastError` 字段、写回现场、`PushTrace`、日志的 `appends/lines/last_error`。

#### 重启实测

先带 `--fresh` 起一次（写出一份存档），再**不带** `--fresh` 起一次：
第二局 35 件、牌库 15 张、手牌 4 张 —— 正是上一局结束时的牌面，
八条区域不变量全绿。存档那一节自己也再走了一遍完整往返，0 字段差异。

---

## 10. 与后面里程碑的接缝

- **M5 运行时编辑器**是"造内容"的地方，它改的是 `project.json` 那一半 ——
  所以 M4 把定义与对局拆开（决定 4），M5 就能只在 `project.json` 上工作，
  不用碰对局状态
- M5 的每个编辑动作也该走 `ICommand`，于是"改数值"也能撤销 —— M4 定好的接口直接复用
- `--save-root` 之后还可以加 `--load-save <名字>`，用于做可复现的演示与回归
