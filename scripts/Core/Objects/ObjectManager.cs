using System.Collections.Generic;
using Godot;
using TabletopSimulator.Data;

namespace TabletopSimulator.Core.Objects;

/// <summary>
/// 物件管理器：所有桌面物件的所有者，也是<b>唯一</b>的物件交互入口。
///
/// 它同时实现两个 M1 预留的接口：
/// <list type="bullet">
/// <item><see cref="IWorldPicker"/> —— 决定一次左键按下是拖物件还是点空白</item>
/// <item><see cref="IWheelHandler"/> —— 让 Alt+滚轮优先用于旋转，而不是相机缩放</item>
/// </list>
///
/// 绘制次序只有一处真相：<see cref="_drawOrder"/>（底 → 顶）。节点顺序由
/// <see cref="ApplyDrawOrder"/> 同步过去。这样「拖拽置顶」「堆内连续」都只是改列表，
/// 不涉及节点增删。
/// </summary>
public partial class ObjectManager : Node2D, IWorldPicker, IWheelHandler
{
	/// <summary>右键菜单的条目 id。用固定区间区分"命令"与"参数选择"。</summary>
	private enum MenuId
	{
		Flip = 1,
		RotateCw,
		RotateCcw,
		ResetRotation,
		Duplicate,
		Delete,
		PullFromPile,
		DissolvePile,
		RollDice,
		/// <summary>
		/// 「编辑这张」—— 打开 F1 编辑器并停在这一张牌上（M5 遗留口子的入口）。
		///
		/// 它<b>不是</b>对桌面的操作，而是"把这个物件交给编辑器看"：
		/// 用户已经在用右键菜单了，那是他最可能去找的地方（M5.5 第 4c 条同一条教训：
		/// 功能早就有，只是入口不可发现）。
		/// </summary>
		EditInstance,
		// 面数与数量用区间
		SidesBase = 100,
		CountBase = 200,

		// ---- 计数器（M5.5 P4）----
		StatPlus,
		StatMinus,
		StatPlusBig,
		StatMinusBig,
		/// <summary>改上限用区间：<c>StatMaxBase + i</c> 对应 <see cref="StatMaxChoices"/>[i]。</summary>
		StatMaxBase = 300,
	}

	private static readonly int[] DiceSideChoices = { 4, 6, 8, 10, 12, 20, 100 };
	private static readonly int[] DiceCountChoices = { 1, 2, 3, 5 };

	/// <summary>
	/// 计数器右键菜单里可选的"上限"。
	///
	/// 有一组预设是因为"改上限"本身是低频操作，而让它去弹一个输入框
	/// 会为了少数几次改动给右键菜单加一条异步路径；
	/// 真要任意数值（用户说过 <c>xxx/xxx</c>），走「桌面」页之外的
	/// 计数器数字输入框那条路 —— 见 <c>StatEditorPage</c>。
	/// </summary>
	private static readonly int[] StatMaxChoices = { 10, 20, 40, 60, 100, 200 };

	// ---- 集合 ----
	private readonly List<TabletopObject> _drawOrder = new();
	private readonly List<TabletopObject> _selection = new();
	private readonly Dictionary<int, Pile> _piles = new();
	private readonly List<TabletopObject> _dragging = new();
	private readonly HashSet<Pile> _draggingPiles = new();

	private int _nextPileId = 1;
	private int _nextUid;

	// ---- 依赖 ----
	private Board _board = null!;
	private BoardCamera _camera = null!;
	private ViewportController _viewport = null!;
	private SelectionBox _box = null!;
	private Hud? _hud;
	private PopupMenu? _menu;
	// ---- 交互状态 ----
	private TabletopObject? _hovered;
	private bool _boxSelecting;
	private bool _dragMoved;
	private Vector2 _boxCursor;

	/// <summary>
	/// 本次拖拽的物件是否<b>全部刚从某个区域里取出来</b>。必须在
	/// <see cref="OnPrimaryDragStarted"/> 里、<c>ZoneManager.BeginDrag</c> 摘除归属<b>之前</b>记录 ——
	/// 摘完之后所有物件的 <c>ZoneId</c> 都空了，就再也问不出来。
	///
	/// 它的用途是区分两种"拖一把牌"：
	/// <list type="bullet">
	/// <item><b>从区域里取出</b>（手牌 / 牌库拿一把到桌上）→ 放下就是一把，应当叠成一摞；</item>
	/// <item><b>在桌面上挪</b>（框选几张已排好位置的散牌换个地方）→ 只是搬家，<b>不该改变它们的排列</b>。</item>
	/// </list>
	/// 这两种意图在动作上一模一样，只能靠"从哪来"区分。
	/// </summary>
	private bool _draggingFromZones;

	/// <summary>
	/// 刚结束的这次手势干了什么（中文可读），供撤销历史当那一行的描述。
	///
	/// 为什么由物件管理器来写而不是让撤销系统猜：<b>只有它知道落点判定的结果</b> ——
	/// 是被区域收下了、被拒了、并进了一堆、还是只是挪了个位置。
	/// 撤销系统看到的两份快照差异，这几种情况长得一模一样。
	/// </summary>
	private string _lastDropLabel = "";

	/// <summary>
	/// 刚结束的这次手势涉及几个物件（<see cref="DescribeLastDrop"/> 的兜底描述用它）。
	///
	/// 有兜底是因为真出过一次<b>空描述的历史条目</b>：某些手势路径没有走到
	/// <see cref="OnPrimaryReleased"/> 的赋值语句，于是 <c>_lastDropLabel</c> 是空串，
	/// 而手势确实改了东西、<c>EndGesture</c> 照样记了一条 ——
	/// 日志里于是出现一行什么都没有的记录。它不报错、不影响一致性，
	/// 只在"我刚刚那步改了什么"这个功能最该有用的时候失效。
	/// </summary>
	private int _draggedCount;

	/// <summary>这次手势是否<b>不值得记</b>（框选：只改选中集，而选中集不在快照里）。</summary>
	private bool _gestureHasNoDescription;

	/// <summary>
	/// 这次手势是否<b>已经自己记过历史</b>了（目前的唯一来源是"轻点骰子掷骰"）。
	///
	/// <see cref="UndoSystem.EndGesture"/> 会读它，是则跳过收尾那一条 ——
	/// 否则一次点击产出两条历史，<c>Ctrl+Z</c> 要按两次才回到点之前。
	///
	/// 生命周期：<see cref="OnPrimaryReleased"/> 开头复位、记录那一刻置位，
	/// 而 <c>EndGesture</c> 在<b>同一次信号</b>里紧跟着读它（见 Main 的接线顺序）。
	/// </summary>
	internal bool DropAlreadyRecorded => _dropAlreadyRecorded;

	private bool _dropAlreadyRecorded;

	// ------------------------------------------------------------------ 属性

	/// <summary>
	/// 撤销系统。为 <c>null</c> 时所有"记历史"的动作静默跳过 ——
	/// 物件系统不依赖它也能跑（自检的某些探针、以及 M4 之前的行为）。
	/// </summary>
	public UndoSystem? Undo { get; set; }

	/// <summary>卡牌定义池（M5 的编辑器会往里加）。</summary>
	public Dictionary<string, CardDefinition> CardDefinitions { get; } = new();

	/// <summary>Token 定义池。</summary>
	public Dictionary<string, TokenDefinition> TokenDefinitions { get; } = new();

	/// <summary>
	/// 卡组池（M5）：一副牌的配方，"哪种卡几张"。
	///
	/// 放在物件管理器上而不是各自持有：发牌要同时看到"定义池"与"卡组"两样东西
	/// （卡组只存 id，得查定义才造得出卡），两份分开持有迟早会出现
	/// "有人拿到了卡组、却没有对应定义"的中间状态。
	/// </summary>
	public Dictionary<string, CardDeck> Decks { get; } = new();

	/// <summary>松手时是否吸附到桌面网格（`G` 键切换）。</summary>
	public bool GridSnapEnabled { get; set; }

	public int ObjectCount => _drawOrder.Count;

	public IReadOnlyList<TabletopObject> Selection => _selection;

	/// <summary>按绘制次序（底 → 顶）返回全部物件。</summary>
	public IReadOnlyList<TabletopObject> AllObjects => _drawOrder;

	public IReadOnlyDictionary<int, Pile> Piles => _piles;

	/// <summary>右键上下文菜单（挂在 HUD 那层）。自检用它验证菜单真的弹出来了，而不只是发了信号。</summary>
	public PopupMenu? ContextMenu => _menu;

	/// <summary>当前鼠标悬停的物件。它是键盘操作的优先目标。</summary>
	public TabletopObject? Hovered => _hovered;

	/// <summary>
	/// 区域系统（M3）。<b>可空</b>：没接区域时物件系统照常工作，只是区域相关交互整体关闭。
	///
	/// 这里只依赖接口不依赖 <c>ZoneManager</c> 具体类型 —— 和 M1 预留
	/// <see cref="IWorldPicker"/> / <see cref="IWheelHandler"/> 同一个套路：
	/// 被"问"，而不是自己去抢事件或反向持有对方。
	/// </summary>
	public IZoneInteraction? Zones
	{
		get => _zones;
		set => _zones = value;
	}

	private IZoneInteraction? _zones;

	[Signal] public delegate void SelectionChangedEventHandler(int count);
	[Signal] public delegate void ObjectCountChangedEventHandler(int count);
	[Signal] public delegate void GridSnapChangedEventHandler(bool enabled);

	/// <summary>
	/// 定义池变了（M5 的编辑器改了一张卡 / 一个 Token 的样式）。
	/// 参数是受影响的定义 id（<b>空串 = 整个池子都变了，比如读档</b>）。
	///
	/// 为什么要发信号而不是让编辑器自己去遍历重画：桌面上的物件<b>怎么跟着变</b>
	/// 是物件系统的知识（哪些卡在用这个定义、实例级覆盖要不要保留），
	/// 编辑器只该说"这个定义改了"。两处各自遍历的话，
	/// "编辑器认为改完了、桌上有张卡没跟上"这类不一致迟早出现。
	/// </summary>
	[Signal] public delegate void CardDefinitionChangedEventHandler(string definitionId);

	[Signal] public delegate void TokenDefinitionChangedEventHandler(string definitionId);

	/// <summary>
	/// 用户在右键菜单里点了「编辑这张」。
	///
	/// <b>为什么发信号而不是直接去开编辑器：</b>与上面两个定义信号同一条理由 ——
	/// 物件系统不该知道编辑器长什么样（它连 <c>EditorPanel</c> 这个类型都不认识）。
	/// 它只负责说"用户想在编辑器里看这一个物件"，
	/// 由 <c>Main</c> 把它接到面板上（与 <c>Hud.RequestToggleEditor</c> 同一个套路）。
	///
	/// <paramref name="kind"/> 是 <c>"card"</c> / <c>"token"</c>（决定切到哪一页），
	/// <paramref name="uid"/> 是那个物件（编辑器据此找到实例级覆盖）。
	/// </summary>
	[Signal] public delegate void EditInstanceRequestedEventHandler(string kind, string uid);

	// ------------------------------------------------------------------ 装配

	public void Bind(Board board, BoardCamera camera, ViewportController viewport, SelectionBox box, CanvasLayer hudLayer)
	{
		_board = board;
		_camera = camera;
		_viewport = viewport;
		_box = box;
		_hud = hudLayer as Hud;

		viewport.Picker = this;
		viewport.WheelHandler = this;

		viewport.PrimaryPressed += OnPrimaryPressed;
		viewport.PrimaryDragStarted += OnPrimaryDragStarted;
		viewport.PrimaryDragged += OnPrimaryDragged;
		viewport.PrimaryReleased += OnPrimaryReleased;
		viewport.EmptyAreaClicked += OnEmptyAreaClicked;
		viewport.ContextMenuRequested += OnContextMenuRequested;
		viewport.PrimaryDoubleClicked += OnPrimaryDoubleClicked;
		viewport.PointerMoved += OnPointerMoved;

		// 「轻点计数器即改数值」（M5.5 P4）—— 与"轻点骰子即掷"是同一类：
		// 高频操作不该逼人去右键菜单里翻。走 PrimaryReleased 而不是 PrimaryPressed：
		// 那一条在"松开且没拖动"时才发，正是"轻点"的语义。
		viewport.PrimaryReleased += OnStatClicked;

		// 【项目约定】右键菜单在 Main.tscn 里搭好（挂在 HudRoot 下），这里只取来接线。
		_menu = hudLayer.GetNode<PopupMenu>("HudRoot/ObjectMenu");
		_menu.IdPressed += OnMenuItemPressed;
	}

	// ------------------------------------------------------------------ 创建

	/// <summary>
	/// 在桌面上造一个计数器（M5.5 P4）。
	///
	/// <b>数值完全由调用方给</b>：默认 60/60 只是"新建时的样子"，
	/// 用户实测反馈明确说过"血量不一定是 60/60，也有可能是 xxx/xxx"。
	/// </summary>
	public StatObject SpawnStat(StatData data, Vector2 position)
	{
		StatObject stat = new() { Position = position };
		stat.AssignUid(NextUid("stat"));
		stat.SetData(data);
		Register(stat);
		return stat;
	}

	public CardObject SpawnCard(CardDefinition definition, Vector2 position, float rotationDeg = 0f, bool faceDown = false)
	{
		CardObject card = new()
		{
			Position = position,
			IsFaceDown = faceDown,
		};
		card.AssignUid(NextUid("card"));
		card.SetDefinition(definition);
		card.RotationDeg = rotationDeg;
		Register(card);
		return card;
	}

	public TokenObject SpawnToken(TokenDefinition definition, Vector2 position, string textOverride = "")
	{
		TokenObject token = new() { Position = position, TextOverride = textOverride };
		token.AssignUid(NextUid("token"));
		token.SetDefinition(definition);
		Register(token);
		return token;
	}

	public DiceObject SpawnDice(int sides, int count, Vector2 position)
	{
		DiceObject dice = new() { Position = position };
		dice.AssignUid(NextUid("dice"));
		dice.Configure(sides, count);
		Register(dice);
		return dice;
	}

	private string NextUid(string prefix) => $"{prefix}-{++_nextUid:D4}";

	/// <summary>
	/// 自检入口：合成一次"轻点物件后松手"（<c>PrimaryReleased</c>）。
	///
	/// 为什么不直接调 <see cref="OnStatClicked"/>：那验的是"这个私有方法对不对"，
	/// 而真正会出错的是"它有没有被接到信号上""参数是不是屏幕/世界坐标搞混了"。
	/// 走信号就等于顺带验了接线 —— 与遮罩层那个 <c>SimulateDragForTest</c> 同一个套路
	/// （那边也是"不另写一条给测试用的捷径，捷径验的是捷径"）。
	/// </summary>
	internal void SimulatePrimaryReleaseForTest(Vector2 worldPos) => OnStatClicked(worldPos);

	// ------------------------------------------------------------------ 定义改动（M5）

	/// <summary>
	/// 数一数桌上有几张牌在用 <paramref name="definitionId"/> 这份定义。
	///
	/// 用途是"删定义之前拦住你"：删掉一个还有 12 张牌在用的定义，
	/// 那 12 张在存档里就变成"引用了不存在的定义"——而<b>读档时这类物件会被静默跳过</b>，
	/// 代价是"存档里凭空少牌"。所以宁可不删，先告诉你还有几张在用。
	/// </summary>
	public int CountCardInstances(string definitionId)
	{
		int n = 0;
		foreach (TabletopObject obj in _drawOrder)
		{
			if (obj is CardObject card && card.Definition.Id == definitionId)
				n++;
		}

		return n;
	}

	/// <summary>同上，Token 版。</summary>
	public int CountTokenInstances(string definitionId)
	{
		int n = 0;
		foreach (TabletopObject obj in _drawOrder)
		{
			if (obj is TokenObject token && token.Definition.Id == definitionId)
				n++;
		}

		return n;
	}

	/// <summary>
	/// 桌上所有用这份定义的卡<b>立刻重画</b>（M5 的"边改边看"）。
	///
	/// 两件事它必须都做，而且次序不能反：
	/// <list type="number">
	/// <item><b>把新定义实例交给每一张卡</b>（<c>SetDefinition</c>）。
	///   只 <c>QueueRedraw</c> 是不够的 —— 卡画的是它<b>持有</b>的那份定义，
	///   定义池里换了新对象、卡还指着旧的，重画出来还是老样子。</item>
	/// <item>逐个 <c>QueueRedraw</c>。</item>
	/// </list>
	///
	/// <b>实例级覆盖（<c>FieldOverrides</c>）刻意不动。</b>它是"这一张和别的不同"，
	/// 改的是定义（模板），不该把某个实例的手改数值冲掉。
	/// </summary>
	/// <returns>受影响的卡张数（自检要按它核对，别靠数屏幕）。</returns>
	public int ApplyCardDefinition(CardDefinition definition)
	{
		CardDefinitions[definition.Id] = definition;

		int touched = 0;
		foreach (TabletopObject obj in _drawOrder)
		{
			if (obj is not CardObject card || card.Definition.Id != definition.Id)
				continue;

			card.SetDefinition(definition);
			touched++;
		}

		EmitSignal(SignalName.CardDefinitionChanged, definition.Id);
		return touched;
	}

	/// <summary>Token 版：见 <see cref="ApplyCardDefinition"/> 的三条说明。</summary>
	public int ApplyTokenDefinition(TokenDefinition definition)
	{
		TokenDefinitions[definition.Id] = definition;

		int touched = 0;
		foreach (TabletopObject obj in _drawOrder)
		{
			if (obj is not TokenObject token || token.Definition.Id != definition.Id)
				continue;

			token.SetDefinition(definition);
			touched++;
		}

		EmitSignal(SignalName.TokenDefinitionChanged, definition.Id);
		return touched;
	}

	/// <summary>
	/// uid 计数器。<b>撤销与读档都要把它一起还原</b>。
	///
	/// 不还原的后果很具体：删掉一张牌 → 撤销 → 再复制一张，
	/// 新建的那张会拿到刚被还原的那张的序号，<b>桌面上于是有两个同 uid 的物件</b>。
	/// 这类 bug 极难查，因为两个物件在代码里长得完全一样，
	/// 而按 uid 找物件的地方（堆成员、区域成员、存档）会随机命中一个。
	///
	/// 注意它不区分前缀：<c>card-0007</c> 与 <c>token-0007</c> 是同一个序号取出来的，
	/// 所以还原时只能整体还，不能逐类还。
	/// </summary>
	public int UidSequence
	{
		get => _nextUid;
		set => _nextUid = value;
	}

	/// <summary>把绘制次序整体设成给定的顺序（新物件在末尾）。
	///
	/// 与 <see cref="SyncDrawOrderToStack"/> 的区别：那个是"段内重排"
	/// （只换给定这批人的相对次序，不动别人），这个是<b>整体重置</b>。
	/// 读档与撤销要的是后者 —— 快照里存的就是完整次序（谁压谁）。
	/// </summary>
	/// <returns>实际排进去的物件数（若列表里有不认识的对象，会少于传入数）。</returns>
	public int RestoreDrawOrder(IReadOnlyList<TabletopObject> bottomToTop)
	{
		var wanted = new List<TabletopObject>(bottomToTop.Count);
		foreach (TabletopObject obj in bottomToTop)
		{
			if (IsInstanceValid(obj) && _drawOrder.Contains(obj))
				wanted.Add(obj);
		}

		// 快照里没有的（理论上不该有）留在末尾，而不是丢掉 ——
		// 宁可次序不完美，也不能让物件从桌面上凭空消失。
		foreach (TabletopObject obj in _drawOrder)
		{
			if (!wanted.Contains(obj))
				wanted.Add(obj);
		}

		_drawOrder.Clear();
		_drawOrder.AddRange(wanted);
		ApplyDrawOrder();
		EmitSignal(SignalName.ObjectCountChanged, _drawOrder.Count);
		return _drawOrder.Count;
	}

	private void Register(TabletopObject obj)
	{
		AddChild(obj);
		_drawOrder.Add(obj);
		ApplyDrawOrder();
		EmitSignal(SignalName.ObjectCountChanged, _drawOrder.Count);
	}

	/// <summary>清空桌面上的所有物件。</summary>
	public void ClearAll()
	{
		// 先让区域松手，否则 Zone.Members 会留着即将释放的节点引用
		_zones?.ForgetObjects(new List<TabletopObject>(_drawOrder));

		foreach (TabletopObject obj in _drawOrder)
			obj.QueueFree();

		_drawOrder.Clear();
		_selection.Clear();
		_piles.Clear();
		_dragging.Clear();
		_draggingPiles.Clear();
		_hovered = null;
		_nextPileId = 1;

		EmitSignal(SignalName.SelectionChanged, 0);
		EmitSignal(SignalName.ObjectCountChanged, 0);
	}

	/// <summary>
	/// 往定义池里塞一份<b>示例定义</b>（卡牌 + Token + 一副示例卡组），桌面上不放任何物件。
	///
	/// 用途是"新建存档"：一个连卡牌定义都没有的空存档让人无从下手，
	/// 而 M5 的运行时编辑器正是从"有卡可改"开始工作的。
	///
	/// 卡组也一起塞：M5 的「组卡组」那一页同样从"有牌可组"开始，
	/// 而空卡组列表会让人以为功能没做。
	/// </summary>
	public void SeedDemoDefinitions()
	{
		List<CardDefinition> cards = DemoContent.CreateDemoCardDefinitions();
		foreach (CardDefinition card in cards)
			CardDefinitions[card.Id] = card;

		foreach (TokenDefinition token in DemoContent.CreateDemoTokenDefinitions())
			TokenDefinitions[token.Id] = token;

		Decks.Clear();
		CardDeck starter = DemoContent.CreateStarterDeck(cards);
		Decks[starter.Id] = starter;
	}

	// ------------------------------------------------------------------ 拾取

	public Node2D? PickTopmost(Vector2 worldPos) => PickTopmostExcluding(worldPos, null);

	private TabletopObject? PickTopmostExcluding(Vector2 worldPos, ICollection<TabletopObject>? exclude)
	{
		for (int i = _drawOrder.Count - 1; i >= 0; i--)
		{
			TabletopObject obj = _drawOrder[i];

			if (!obj.Visible || !IsInstanceValid(obj))
				continue;

			if (exclude is not null && exclude.Contains(obj))
				continue;

			if (obj.ContainsWorldPoint(worldPos))
				return obj;
		}

		return null;
	}

	// ------------------------------------------------------------------ 输入：鼠标

	private void OnPointerMoved(Vector2 worldPos)
	{
		// 区域先更新"悬停的是哪个区域"（S 洗牌 / D 抽牌的落点靠它）。
		// 无返回值、不影响下面的物件悬停逻辑 —— 两者各自维护自己的悬停对象。
		_zones?.NotifyPointerMoved(worldPos);

		if (_boxSelecting)
			return;

		TabletopObject? hit = PickTopmostExcluding(worldPos, _dragging.Count > 0 ? _dragging : null);
		bool hoverChanged = !ReferenceEquals(hit, _hovered);

		if (hoverChanged)
		{
			if (_hovered is not null && IsInstanceValid(_hovered))
			{
				_hovered.IsHovered = false;
				_hovered.QueueRedraw();
			}

			_hovered = hit;

			if (_hovered is not null)
			{
				_hovered.IsHovered = true;
				_hovered.QueueRedraw();
			}
		}

		// 鼠标离开物件 → 取消选中。
		//
		// 否则会出现这种情况：点过一张卡之后鼠标移开，它仍是"选中"状态、
		// 描边仍亮着，按 F 还会翻它 —— 明明鼠标已经不在它上面了。
		//
		// 两种情况刻意不清：
		//   1. 正在拖拽 —— 拖拽途中鼠标必然会"离开"被拖的物件
		//      （它被排除在拾取之外），不清的话一拖就丢选中。
		//   2. 多选（框选 / Ctrl+点选）—— 那是用户刻意建立的集合，
		//      不该被一次鼠标移动就冲掉，否则框选完立刻失效。
		//      多选要取消用 Esc 或点空白。
		//
		// 注意这一步<b>不能</b>塞进上面的 `if (hoverChanged)` 里。
		// 早先这里是个"悬停没变就整体 return"的优化，于是当鼠标本来就没悬停任何东西
		// （合成输入、或程序化设置选中集）时，"移到空白处取消选中"整条逻辑被跳过 ——
		// 单选会一直留着。悬停变化的判断只该管描边，不该管选中。
		if (hit is null && _dragging.Count == 0 && _selection.Count == 1)
		{
			ClearSelectionInternal();
			return;
		}

		// 悬停就是"指着的那张"，操作目标随之变化，描边要跟着变
		if (hoverChanged)
			RefreshActionTargets();
	}

	private void OnPrimaryPressed(Vector2 worldPos)
	{
		TabletopObject? picked = PickTopmostExcluding(worldPos, null);
		if (picked is null)
			return;

		bool additive = Input.IsKeyPressed(Key.Ctrl);
		bool takeSingle = Input.IsKeyPressed(Key.Shift);

		if (!_selection.Contains(picked))
		{
			if (!additive)
				ClearSelectionInternal();

			AddToSelectionInternal(picked);
		}

		// 无条件广播一次：AddToSelectionInternal 自己不广播，
		// 而操作目标的描边靠 EmitSelectionChanged 里的重算来更新。
		EmitSelectionChanged();

		BuildDragSet(picked, takeSingle);
		_dragMoved = false;
	}

	/// <summary>
	/// 决定这一次拖什么：
	/// Shift 只拖被按到的那一张（从堆中抽出）；否则若它属于某堆就拖整堆；
	/// 再否则拖整个选中集（选中集里的堆会被展开成全部成员）。
	/// </summary>
	private void BuildDragSet(TabletopObject picked, bool takeSingle)
	{
		_dragging.Clear();
		_draggingPiles.Clear();

		if (takeSingle)
		{
			DetachFromPile(picked, keepPosition: true);
			_dragging.Add(picked);
			return;
		}

		var set = new HashSet<TabletopObject>();

		foreach (TabletopObject sel in _selection)
		{
			if (sel.PileId != 0 && _piles.TryGetValue(sel.PileId, out Pile? pile))
			{
				foreach (TabletopObject m in pile.Members)
					set.Add(m);

				_draggingPiles.Add(pile);
			}
			else
			{
				set.Add(sel);
			}
		}

		if (set.Count == 0)
			set.Add(picked);

		_dragging.AddRange(set);
	}

	private void OnPrimaryDragStarted(Vector2 startWorldPos)
	{
		// 按下时没捞到物件 → 这次拖动是框选
		if (_dragging.Count == 0)
		{
			_boxSelecting = true;
			_boxCursor = startWorldPos;
			_box.Begin(startWorldPos);
			return;
		}

		// 有物件 → 拖拽置顶，并让被拖的堆整体离开原层级
		// 区域成员也要在起点就脱离区域：落点可能不在任何区域里，
		// 那时若还留着 ZoneId 就成了"还在牌库里、位置却在桌面"的半截状态。
		//
		// 顺序有讲究：必须<b>先</b>问"是不是全从区域里来的"，再让区域摘除归属 ——
		// 摘完就没法判断了。
		_draggingFromZones = AllCameFromZone(_dragging);

		_zones?.BeginDrag(_dragging);
		BringToFront(_dragging);
	}

	/// <summary>一组物件是否全部来自区域（每一张拖拽前都有 ZoneId）。少于 2 张时返回 false。</summary>
	private static bool AllCameFromZone(IReadOnlyList<TabletopObject> objects)
	{
		if (objects.Count < 2)
			return false;

		foreach (TabletopObject obj in objects)
		{
			if (string.IsNullOrEmpty(obj.ZoneId))
				return false;
		}

		return true;
	}

	/// <summary>这组物件是否恰好就是桌面上已有的某一个自由堆的全部成员（"拖整堆"）。</summary>
	private bool IsExactlyOneExistingPile(IReadOnlyList<TabletopObject> objects)
	{
		if (objects.Count < 2)
			return false;

		int pileId = objects[0].PileId;
		if (pileId == 0 || !_piles.TryGetValue(pileId, out Pile? pile))
			return false;

		if (pile.Count != objects.Count)
			return false;

		foreach (TabletopObject obj in objects)
		{
			if (obj.PileId != pileId)
				return false;
		}

		return true;
	}

	private void OnPrimaryDragged(Vector2 worldDelta)
	{
		if (_boxSelecting)
		{
			_boxCursor += worldDelta;
			_box.Update(_boxCursor);
			return;
		}

		if (_dragging.Count == 0)
			return;

		_dragMoved = true;

		foreach (TabletopObject obj in _dragging)
		{
			if (IsInstanceValid(obj))
				obj.Position += worldDelta;
		}

		foreach (Pile pile in _draggingPiles)
			pile.Anchor += worldDelta;

		// "拖出桌面就要删"必须是<b>可预期</b>的：松手之前就让用户看见。
		// 用户拍板的原话是"拖到画布外的东西就被删除"，而误删的代价是
		// "一次误拖花十分钟收拾"，所以这里在拖动过程中就把结果摆在眼前。
		UpdateDragOutTint();
	}

	/// <summary>
	/// 这一次拖拽的"锚点"位置 —— 拖的那组里<b>最上面</b>的那张（与
	/// <see cref="TryMergeAfterDrop"/> 的锚点取法一致）。
	///
	/// 和落点判定共用它，是为了让"松手会不会删"与"松手删哪一组"说的是同一件事。
	/// </summary>
	private Vector2 DraggedAnchorPosition()
	{
		TabletopObject? anchor = null;

		foreach (TabletopObject obj in _dragging)
		{
			if (!IsInstanceValid(obj))
				continue;

			if (anchor is null || _drawOrder.IndexOf(obj) > _drawOrder.IndexOf(anchor))
				anchor = obj;
		}

		return anchor?.Position ?? Vector2.Zero;
	}

	/// <summary>是否正处于"松手就会被删除"的状态（自检读它验"提前变色"真的发生过）。</summary>
	internal bool DragOutWarned { get; private set; }

	/// <summary>
	/// 刷新"即将因拖出桌面而被删"的提示色。
	///
	/// 用 <c>SelfModulate</c> 而不是 <c>Modulate</c>：它是"把自己的绘制结果再乘一层"，
	/// 不会往下传给子节点，也就不会碰 <c>_selection</c> / 描边那套逻辑。
	/// 关掉时恢复白色（乘法单位元）。
	///
	/// 只在状态<b>变化</b>时才写，而不是每帧写 —— 与 M5 那条"值没变就别赋值"
	/// 同一个道理（那边是 <c>LineEdit.Text</c>，这边是避免无意义的重绘风暴）。
	/// </summary>
	private void UpdateDragOutTint()
	{
		if (_dragging.Count == 0)
			return;

		bool outside = !_board.BoardRect.HasPoint(DraggedAnchorPosition());

		if (outside == DragOutWarned)
			return;

		DragOutWarned = outside;
		ApplyDragOutTint(outside);

		if (outside)
			_hud?.Toast("松手即删除（拖回桌面内可取消）");
	}

	private void ApplyDragOutTint(bool outside)
	{
		Color tint = outside ? GameConfig.DragOutTint : Colors.White;

		foreach (TabletopObject obj in _dragging)
		{
			if (IsInstanceValid(obj))
				obj.SelfModulate = tint;
		}
	}

	/// <summary>收干净提示色。任何一次拖拽结束都要调 —— 否则那张牌会一直红着。</summary>
	private void ClearDragOutTint()
	{
		if (!DragOutWarned)
			return;

		DragOutWarned = false;
		ApplyDragOutTint(false);
	}

	private void OnPrimaryReleased(Vector2 worldPos)
	{
		// 一次手势的落点描述（撤销历史里的那一行文字）。
		// 在这里统一重置：它描述的是"刚结束的这一次手势"，不能沿用上一次的。
		// "这次手势自己记过历史了吗"一并复位 —— 否则上一点击的标记会漏到下一次。
		_lastDropLabel = "";
		_dropAlreadyRecorded = false;
		_gestureHasNoDescription = false;

		if (_boxSelecting)
		{
			_boxSelecting = false;
			Rect2 rect = _box.Finish();

			if (!Input.IsKeyPressed(Key.Ctrl))
				ClearSelectionInternal();

			foreach (TabletopObject obj in _drawOrder)
			{
				if (obj.Visible && rect.Intersects(obj.GetWorldAabb()))
					AddToSelectionInternal(obj);
			}

			EmitSelectionChanged();
			ClearDragOutTint();
			_dragging.Clear();
			_draggingPiles.Clear();

			// 框选<b>不改任何状态</b>（选中集不在快照里），所以它不该进历史 ——
			// 而这里正是"提前 return 导致描述没被赋值"的那条路。
			// 标记一下，让 EndGesture 跳过记录，而不是记一条空描述。
			_gestureHasNoDescription = true;
			return;
		}

		if (_dragging.Count == 0)
			return;

		_draggedCount = _dragging.Count;

		if (_dragMoved)
		{
			// 落点在区域里 → 由区域定夺（接受则归位并改朝向，被拒则原地不动）。
			// 必须放在自由堆逻辑之前：否则一张牌会被"区域收下"和"和桌上的牌粘成一堆"同时处理。
			int dragged = _dragging.Count;

			if (_zones is not null && _zones.TryHandleDrop(_dragging, worldPos))
			{
				_lastDropLabel = $"移入区域 {dragged} 个物件";
			}
			else
			{
				if (GridSnapEnabled)
					SnapToGrid(_dragging);

				TryMergeAfterDrop(worldPos);

				// 被拒（满了 / 锁定）时区域会给理由，那比"移动 1 个物件"更说明问题
				string reject = _zones?.LastRejectReason ?? "";
				if (_lastDropLabel.Length == 0)
				{
					_lastDropLabel = reject.Length > 0
						? $"移动被拒（{reject}）"
						: $"移动 {dragged} 个物件";
				}
			}
			// ---- 桌面边界（M5.5 P3，用户拍板：拖出桌面 = 删除）----
			//
			// 放在"区域定夺"之后：区域本身也在桌面内，落点若被某个区域收下，
			// 那就不是"拖到桌外"。而这一步之后、并堆之前 —— 拖出去的东西不该
			// 先和桌上的牌粘成一堆再被删掉（那会多出一条莫名其妙的并堆历史）。
			//
			// <b>判据用"锚点"（拖的那组里最上面那张）而不是每一张都在桌内</b>：
			// 一次手势拿起 10 张牌，边缘那几张压线是常事，若按"每一张都在桌内"
			// 判定，用户会被迫反复微调落点。用锚点 = "你把手上的东西放在哪儿"。
			if (_dragging.Count > 0 && !_board.BoardRect.HasPoint(DraggedAnchorPosition()))
			{
				_lastDropLabel = _dragging.Count == 1
					? "拖出桌面，删除 1 个物件"
					: $"拖出桌面，删除 {_dragging.Count} 个物件";

				_hud?.Toast($"{_lastDropLabel}（Ctrl+Z 可撤回）");
				DeleteObjectsCore(new List<TabletopObject>(_dragging), record: true);

				// DeleteObjectsCore 已经把它们从 _dragging 指向的集合里摘掉了，
				// 但这里仍然显式收尾 —— 与下面那条正常路径保持同一套收尾动作，
				// 免得将来给收尾加东西时漏掉这一支。
				_dropAlreadyRecorded = true;   // 上面那条 DeleteObjectsCore 已经记过历史
				ClearDragOutTint();
				_dragging.Clear();
				_draggingPiles.Clear();
				ApplyDrawOrder();
				return;
			}
		}
		else if (_selection.Count == 1 && _selection[0] is DiceObject single)
		{
			// 轻点骰子即掷 —— 掷骰是高频操作，不该逼人去右键菜单里翻
			single.Roll();
			_lastDropLabel = $"掷骰 {string.Join("/", single.Values)}";

			// <b>轻点掷骰自己就是一条历史</b>（M4 第 4 步）。
			//
			// 这次手势没有"拖动"，所以它不属于"一次拖拽 = 一条历史"那套：
			// 用户在骰子上点了一下，意图就是"掷"，那一下本身就该能撤。
			//
			// 记完之后<b>不让 EndGesture 再记</b>：否则同一次点击会产出两条
			// （"掷骰 3" + "移动 1 个物件"），用户按一次 Ctrl+Z 只退掉一半。
			RecordHistory(_lastDropLabel);
			_dropAlreadyRecorded = true;
		}

		ClearDragOutTint();
		_dragging.Clear();
		_draggingPiles.Clear();
		ApplyDrawOrder();
	}

	/// <summary>
	/// 「轻点计数器即改数值」（M5.5 P4）。
	///
	/// <list type="bullet">
	/// <item><b>点一下</b>：+<see cref="GameConfig.StatStep"/>（默认 1）。</item>
	/// <item><b>Shift + 点</b>：−<see cref="GameConfig.StatStep"/>。</item>
	/// <item><b>Ctrl + 点</b>：+<see cref="GameConfig.StatBigStep"/>（快速调大，不用点几十次）。</item>
	/// <item><b>Shift + Ctrl + 点</b>：−<see cref="GameConfig.StatBigStep"/>。</item>
	/// </list>
	///
	/// <b>为什么用修饰键而不是"左右键"或"两个小按钮"</b>：计数器上画两个小按钮意味着
	/// 每个按钮都要有自己的命中区域，而它们比把手还小、在缩放下点不中；
	/// 右键在本项目里已经固定是"弹菜单"，占用它会破坏一致性。
	///
	/// <b>记历史是这里的关键</b>：改完必须 <see cref="RecordHistory"/>，
	/// 否则"改了血量却撤不掉" —— 那正是骰子点数当年栽过的坑（M4 的真 bug）。
	/// 而 <c>ObjectState.SameAs</c> 比了这三个字段，所以"记录"这一步才认得出变化。
	/// </summary>
	private void OnStatClicked(Vector2 worldPos)
	{
		if (_selection.Count != 1 || _selection[0] is not StatObject stat)
			return;

		if (!IsInstanceValid(stat))
			return;

		bool big = Input.IsKeyPressed(Key.Ctrl);
		bool minus = Input.IsKeyPressed(Key.Shift);
		int step = big ? GameConfig.StatBigStep : GameConfig.StatStep;
		int delta = minus ? -step : step;

		if (!stat.AddCurrent(delta))
			return;

		RecordHistory($"{stat.Data.Label} {(delta > 0 ? "+" : "")}{delta}（现在 {stat.Data.Current}/{stat.Data.Max}）");

		// 这次手势已经在上面记过一条，别让收尾那行再记一次 ——
		// 否则一次点击产出两条历史，用户按一次 Ctrl+Z 只退掉一半。
		_dropAlreadyRecorded = true;
		_lastDropLabel = "";
	}

	private void OnEmptyAreaClicked(Vector2 worldPos)	{
		// 空点一下：没有选中集就什么都不做，有就取消
		if (_selection.Count > 0)
			ClearSelectionInternal();

		ClearDragOutTint();
		_dragging.Clear();
		_draggingPiles.Clear();
	}

	/// <summary>
	/// 左键双击。<b>这里刻意让区域优先于物件</b>，和右键的规则相反，原因很实际：
	/// 牌库最上面那几张牌正好压在自己的矩形中心，双击"牌库"时命中的几乎总是那张顶牌，
	/// 而不是区域本身。若按"物件优先"，抽牌这个 M3 最核心的操作就永远触发不了。
	/// 只有 <see cref="Data.ZoneDefinition.DrawOnDoubleClick"/> 为真的区域会接管，所以不误伤。
	/// </summary>
	private void OnPrimaryDoubleClicked(Vector2 worldPos)
	{
		_zones?.TryHandleDoubleClick(worldPos);
	}

	/// <summary>
	/// 松手后决定这组物件成不成堆。
	///
	/// <summary>
	/// 松手后决定这组物件要不要成堆。<b>先看"拖的是什么"，再看"落在哪"。</b>
	///
	/// <list type="number">
	/// <item><b>只有一张</b>（M2 语义，不变）：压到别的物件上 → 并进它；
	///   落在空地 → 脱离原堆，变成散件。</item>
	///
	/// <item><b>整组恰好是桌面上已有的一个堆</b>（拖整堆）：
	///   压到别的物件上 → 并堆（把两堆合成一堆，这是刻意的）；
	///   落在空地 → 只是换了个位置，堆保持不变。</item>
	///
	/// <item><b>其余的一把多张</b>（框选几张散牌）：
	///   <list type="bullet">
	///   <item>整组<b>全部刚从区域里取出来</b> + 落在空地 → <b>叠成一摞</b>。
	///     一次手势从手牌里抓起 N 张、放下就是 N 张一摞，对得上手上的动作。</item>
	///   <item>否则 → <b>仅移动</b>：保持相对排列，<b>一概不并堆</b>（压在别的牌上也不并）。</item>
	///   </list></item>
	/// </list>
	///
	/// <b>第 3 条里"从哪来"必须区分开</b>，这是用户实测逼出来的：
	/// 桌面上本来排好位置的几张牌，框选后拖到别处只是想换个地方摆，
	/// 若一律自动成摞，等于把用户的排列毁掉 —— 而他从没表达过"要堆起来"。
	/// 反过来，从手牌 / 牌库里抓一把出来，那就是"一把牌"，应当成摞。
	/// 两个动作在手上完全一样，唯一的区别就是<b>从哪来</b>。
	/// </summary>
	private void TryMergeAfterDrop(Vector2 worldPos)
	{
		TabletopObject? anchor = null;
		foreach (TabletopObject obj in _dragging)
		{
			if (anchor is null || _drawOrder.IndexOf(obj) > _drawOrder.IndexOf(anchor))
				anchor = obj;
		}

		if (anchor is null)
			return;

		TabletopObject? target = PickTopmostExcluding(anchor.Position, _dragging);

		// ---- 1. 单张 ----
		if (_dragging.Count == 1)
		{
			if (target is not null)
			{
				MergeInto(target, _dragging);
				_lastDropLabel = "并成一摞";
				return;
			}

			DetachFromPile(_dragging[0], keepPosition: true);
			return;   // 描述交给上层统一写"移动 1 个物件"
		}

		// ---- 2. 拖整堆 ----
		if (IsExactlyOneExistingPile(_dragging))
		{
			if (target is not null)
			{
				MergeInto(target, _dragging);
				_lastDropLabel = $"把两摞合成一摞（{_dragging.Count} 张）";
			}

			return;   // 落空地：堆不变，位置已经在拖动时跟着走了
		}

		// ---- 3. 一把散牌 ----
		if (target is not null)
			return;   // 压在别的牌上也只移动，绝不并堆

		if (!_draggingFromZones)
			return;   // 桌面上挪位置 → 仅移动，保持排列

		// 从区域里取出的一把 + 落在空地 → 叠成一摞。
		// 锚点取"离松手位置最近的那张"，也就是光标底下那张 ——
		// 一摞牌应该成形在你放手的地方，而不是整组跳到最左边那张上去。
		GroupIntoPile(_dragging, NearestTo(_dragging, worldPos));
		_lastDropLabel = $"叠成一摞（{_dragging.Count} 张）";
	}

	// ------------------------------------------------------------------ 撤销系统的接缝

	/// <summary>
	/// 刚结束的这次手势的描述（撤销历史里那一行）。<b>保证非空 —— 只要这次手势值得记。</b>
	///
	/// 空串只有一个含义：这次手势没有产生任何"可描述的变化"（框选），
	/// <see cref="UndoSystem.EndGesture"/> 会据此跳过记录。
	/// 其余情况一律给出兜底描述，宁可写"移动 3 个物件"这种笼统说法，
	/// 也不许日志里出现一行空白 —— 那会让"我刚刚那步改了什么"这个问题无从回答。
	/// </summary>
	public string DescribeLastDrop()
	{
		if (_lastDropLabel.Length > 0)
			return _lastDropLabel;

		if (_gestureHasNoDescription || _draggedCount == 0)
			return "";

		return $"移动 {_draggedCount} 个物件";
	}

	/// <summary>
	/// 撤销/读档把状态写回场景之后，物件系统要做的收尾。
	///
	/// 快照恢复的是<b>数据</b>，但有几样东西不在数据里：绘制次序要重排、
	/// 选中集要清干净、物件数变化要广播给 HUD。
	/// 漏掉任何一样，症状都是"数据对了、画面还停在撤销前"。
	/// </summary>
	public void NotifyRestored()
	{
		// 写回之后哪些物件还在、次序是什么，都以当前的 _drawOrder 为准
		ApplyDrawOrder();
		ClearSelectionInternal();

		_hovered = null;
		_dragging.Clear();
		_draggingPiles.Clear();

		EmitSelectionChanged();
		EmitSignal(SignalName.ObjectCountChanged, _drawOrder.Count);

		foreach (TabletopObject obj in _drawOrder)
			obj.QueueRedraw();
	}

	/// <summary>
	/// <b>定义池整体被换掉了</b>（编辑器撤销写回定义、或读档）之后的收尾。
	///
	/// 与 <see cref="ApplyCardDefinition"/> 的区别：那个是"某一个定义改了"，
	/// 而这里是"池子里那一批对象<b>整批换成了新对象</b>"—— 撤销快照写回时
	/// <c>CardDefinitions</c> 里装的是刚从 JSON 造出来的新对象，
	/// 场上那些卡还<b>持有旧的</b>，于是"撤销了但桌子没变"。
	///
	/// 逐个把新定义交给场上的实例，再整体重画；实例级覆盖刻意不动
	/// （那条规矩见 <see cref="ApplyCardDefinition"/>）。
	/// </summary>
	public void NotifyDefinitionsReloaded()
	{
		foreach (TabletopObject obj in _drawOrder)
		{
			switch (obj)
			{
				case CardObject card when CardDefinitions.TryGetValue(card.Definition.Id, out CardDefinition? def):
					card.SetDefinition(def);
					break;

				case TokenObject token when TokenDefinitions.TryGetValue(token.Definition.Id, out TokenDefinition? tdef):
					token.SetDefinition(tdef);
					break;
			}

			obj.QueueRedraw();
		}

		// 空串 = "整个池子都变了"（与读档那条路同一个约定）。
		EmitSignal(SignalName.CardDefinitionChanged, "");
		EmitSignal(SignalName.TokenDefinitionChanged, "");
	}

	/// <summary>给定一组物件里离某个世界坐标最近的那个。</summary>
	private static TabletopObject? NearestTo(IReadOnlyList<TabletopObject> objects, Vector2 worldPos)	{
		TabletopObject? best = null;
		float bestDistance = float.MaxValue;

		foreach (TabletopObject obj in objects)
		{
			float distance = obj.Position.DistanceSquaredTo(worldPos);
			if (distance < bestDistance)
			{
				bestDistance = distance;
				best = obj;
			}
		}

		return best;
	}

	// ------------------------------------------------------------------ 滚轮：Alt 旋转

	public bool HandleWheel(float steps, Vector2 screenPos)
	{
		if (!Input.IsKeyPressed(Key.Alt))
			return false;

		// 和键盘操作同一套目标规则：悬停优先，其次选中集
		List<TabletopObject> targets = ResolveActionTargets();
		if (targets.Count == 0)
			return false;

		RotateObjects(targets, steps * GameConfig.WheelRotateStepDegrees);
		return true;
	}

	// ------------------------------------------------------------------ 选择

	private void ClearSelectionInternal()
	{
		foreach (TabletopObject obj in _selection)
		{
			obj.IsSelected = false;
			obj.QueueRedraw();
		}

		_selection.Clear();
		EmitSelectionChanged();
	}

	private void AddToSelectionInternal(TabletopObject obj)
	{
		if (_selection.Contains(obj))
			return;

		_selection.Add(obj);
		obj.IsSelected = true;
		obj.QueueRedraw();
	}

	/// <summary>
	/// 选中集变化后统一在这里广播，顺便重算操作目标描边。
	/// 放在这里而不是各调用点，是因为所有选中变化最终都会走到这一句 ——
	/// 一处覆盖全部，不会漏。
	/// </summary>
	private void EmitSelectionChanged()
	{
		RefreshActionTargets();
		EmitSignal(SignalName.SelectionChanged, _selection.Count);
	}

	public void SelectAll()
	{
		ClearSelectionInternal();

		foreach (TabletopObject obj in _drawOrder)
		{
			if (obj.Visible)
				AddToSelectionInternal(obj);
		}

		EmitSelectionChanged();
	}

	/// <summary>只选中给定物件（清掉其它）。</summary>
	public void SelectOnly(TabletopObject obj)
	{
		ClearSelectionInternal();
		AddToSelectionInternal(obj);
		EmitSelectionChanged();
	}

	/// <summary>只选中给定的一批物件（区域菜单的"选中此区域全部"用）。</summary>
	public void SelectOnly(IReadOnlyList<TabletopObject> objects)
	{
		ClearSelectionInternal();

		foreach (TabletopObject obj in objects)
		{
			if (IsInstanceValid(obj))
				AddToSelectionInternal(obj);
		}

		EmitSelectionChanged();
	}

	/// <summary>
	/// 把物件从它所在的自由堆里摘出来。
	///
	/// 区域接管落点时必须先调它 —— <c>PileId</c> 与 <c>ZoneId</c> 是互斥的：
	/// 一个物件不能既属于自由堆又属于区域叠。不摘的话，
	/// <c>DetachFromPile</c> 的"成员剩 1 个就散堆"与区域的排版会互相打架。
	/// </summary>
	public void ReleaseFromPile(TabletopObject obj) => DetachFromPile(obj, keepPosition: true);

	/// <summary>两个菜单一起收起（探针收尾用 —— <c>PopupMenu</c> 是 <c>Window</c>，会抢合成事件）。</summary>
	public void HideAllMenus()
	{
		_menu?.Hide();
		_zones?.HideMenu();
	}

	public void ClearSelection() => ClearSelectionInternal();

	// ------------------------------------------------------------------ 物件操作

	/// <summary>
	/// 键盘/滚轮操作的<b>目标集</b>。用户要求「悬停即为选中」，即指着哪张就操作哪张，
	/// 不必先点一下。规则：
	/// <list type="number">
	/// <item>鼠标悬停在某个物件上 → 操作它（或它所在的那一摞，见下）。</item>
	/// <item>悬停的物件<b>已在选中集里，且选中集不止一个</b> → 操作整个选中集。
	///   这条是为多选服务的：框选完之后鼠标通常还停在其中一个上，
	///   若只操作那一张，用户会觉得"框选白做了"。</item>
	/// <item>什么都没悬停 → 操作选中集。</item>
	/// </list>
	///
	/// <b>叠放语境下"指着哪张"要升格成"指着哪一摞"。</b>
	/// 牌堆是一个整体，指着它按 F 得到的应该是"整个牌堆反过来"，
	/// 而不是"恰好被指到的那第 7 张牌翻个面"。这与鼠标拖拽已有的语义一致
	/// （拖任一成员 = 拖整堆，`Shift`+拖 = 抽单张）。
	///
	/// <b>但"只选中一张"时不走规则 2</b> —— 这是个很实际的坑：
	/// 把一张牌拖到另一张上之后，那张牌<b>仍然是选中状态</b>（M2 刻意保留，
	/// 方便拖完接着旋转）。于是"悬停对象在选中集里 → 目标是选中集"会把目标
	/// 缩小成一张，整摞翻转退化成逐张翻面 —— 一张背靠背的牌（底盖顶开、
	/// 上下看都是牌面）只翻过顶上那张之后，立刻显示卡背。
	///
	/// 系统无从区分"刻意选中一张"与"拖拽的残留"，所以这里按<b>意图强度</b>取舍：
	/// 选中一张 ≈ 没选（多半是残留），选中多张才是刻意建立的一组。
	/// 想只动一摞里的某张：`Shift`+拖把它抽出来，或用右键菜单 ——
	/// 那是显式操作，作用于当时被点的那一张。
	/// </summary>
	private List<TabletopObject> ResolveActionTargets()
	{
		if (_hovered is not null && IsInstanceValid(_hovered))
		{
			bool hoveredIsSelected = _selection.Contains(_hovered);

			// 多选优先：那是刻意建立的一组，不该被"指着一摞"抢走
			if (hoveredIsSelected && _selection.Count > 1)
				return new List<TabletopObject>(_selection);

			// 一摞牌优先于"只有一张的选中集"
			List<TabletopObject>? stack = StackAround(_hovered);
			if (stack is not null)
				return stack;

			if (hoveredIsSelected)
				return new List<TabletopObject>(_selection);

			return new List<TabletopObject> { _hovered };
		}

		return new List<TabletopObject>(_selection);
	}

	/// <summary>
	/// 该物件所在的"一摞"的全部成员（底 → 顶）；不在任何 ≥2 的组里时返回 <c>null</c>。
	///
	/// 两种叠放组都要认：桌面上的<b>自由堆</b>（靠 <c>PileId</c> 认），
	/// 与<b>叠放区域</b>（牌库 / 弃牌堆，靠 <c>ZoneId</c> 认 —— 后者的成员
	/// <c>PileId</c> 恒为 0，只查 PileId 会漏掉整个牌库）。
	///
	/// <b>2026-09-23 修订：非叠放区域的成员也算"一组"。</b>
	/// 用户实测报的："在区域内对卡组进行翻转有问题，和在桌面上操作的结果不一致。"
	/// 并排量出来的读数（见 <c>DevZoneSim.FlipConsistencyAcrossContexts</c>）：
	/// 同样"指着一组成的牌按 F"，<b>叠放区域（牌库）翻整组、横排区域（手牌）只翻一张</b>
	/// —— 因为这里当时只认 <c>SortMode.Stack</c> 的区域，横排/扇形/自由排版一律返回 <c>null</c>，
	/// 于是目标被缩成"悬停的那一张"。
	/// </summary>
	private List<TabletopObject>? StackAround(TabletopObject obj)
	{
		if (obj.PileId != 0 && _piles.TryGetValue(obj.PileId, out Pile? pile) && pile.Count >= 2)
			return new List<TabletopObject>(pile.Members);

		// 任何区域的"全部成员"都算一组 —— 不再按 <c>SortMode</c> 分。
		//
		// 理由不是"统一好看"，而是**用户指着的那一组成的东西就是一个整体**：
		// 一手横排的牌、摊在公共区的一堆牌，与一摞叠起来的牌在这一点上没有区别。
		// 具体翻法（整组反过来 / 只翻顶上一张）再由 <c>F</c> / <c>Shift+F</c> 决定。
		if (_zones is not null)
		{
			Zone? zone = _zones.ZoneOf(obj);
			if (zone is not null && zone.Count >= 2)
				return new List<TabletopObject>(zone.Members);
		}

		return null;
	}

	/// <summary>
	/// 重算每个物件的 <see cref="TabletopObject.IsActionTarget"/>。
	/// 悬停或选中一变就必须调 —— 描边完全靠这个标志，漏调就会出现
	/// "描边说会改这张、实际改的是另一张"。
	/// </summary>
	private void RefreshActionTargets()
	{
		var targets = new HashSet<TabletopObject>(ResolveActionTargets());

		foreach (TabletopObject obj in _drawOrder)
		{
			bool shouldBeTarget = targets.Contains(obj);
			if (obj.IsActionTarget == shouldBeTarget)
				continue;

			obj.IsActionTarget = shouldBeTarget;
			obj.QueueRedraw();
		}
	}

	/// <summary>
	/// 翻转。分两种情况，区别很重要：
	/// <list type="bullet">
	/// <item>目标<b>正好是一整摞</b>（自由堆或叠放区域的全部成员）→ <b>整摞反过来</b>：
	///   成员次序反转（底牌变顶牌）+ 每张牌正反面翻转。这就是物理上把一摞牌拿起来翻个面。</item>
	/// <item>其它情况 → 逐张翻面（M2 的行为，不变）。</item>
	/// </list>
	///
	/// 为什么不统一成逐张翻面：在一摞盖着的牌上逐张翻面，结果是"顶牌还是原来那张、
	/// 只是翻了面"，用户看到的是"牌堆没动"。而他要的是<b>整个牌堆反过来了</b> ——
	/// 底下的牌跑到顶上。只反转次序也不够：盖着的一摞反转之后画面毫无变化，像没反应。
	/// 两件事必须一起做，因为整摞转 180° 本来就会同时造成这两个结果。
	/// </summary>
	public void FlipObjects(IReadOnlyList<TabletopObject> targets)
	{
		if (TryFlipWholeStack(targets))
		{
			RecordHistory($"整摞翻转（{targets.Count} 张）", FlipMergeKey(targets));
			return;
		}

		foreach (TabletopObject obj in targets)
		{
			obj.IsFaceDown = !obj.IsFaceDown;
			obj.QueueRedraw();
		}

		RecordHistory($"翻面 {targets.Count} 个物件", FlipMergeKey(targets));
	}

	/// <summary>
	/// 连击合并键：同一组目标才允许合并。
	///
	/// 少了"同一组"这个条件，"快速连按 F 翻 5 张不同的牌"会被并成一条
	/// —— 而那 5 次是 5 个独立意图。用排序后的 uid 拼键就够了：
	/// 同一组目标 → 同一个键 → 800ms 内合并；换了目标 → 键不同 → 各记一条。
	/// </summary>
	private static string FlipMergeKey(IReadOnlyList<TabletopObject> targets)
		=> $"flip:{TargetKey(targets)}";

	private static string TargetKey(IReadOnlyList<TabletopObject> targets)
	{
		var uids = new List<string>(targets.Count);
		foreach (TabletopObject obj in targets)
			uids.Add(obj.Uid);

		uids.Sort(System.StringComparer.Ordinal);
		return string.Join(",", uids);
	}

	/// <summary>
	/// 把一次操作记进撤销历史。
	///
	/// 收成一个方法是有意的：接历史的地方一旦散落在十几个调用点，
	/// 迟早会漏一处 —— 而"某个入口没接历史"的症状是"这个操作撤不掉"，
	/// 用户以为撤销坏了。自检里 <c>every_action_records</c> 专门盯这件事。
	/// </summary>
	private void RecordHistory(string label, string mergeKey = "") => Undo?.Record(label, mergeKey);

	/// <summary>
	/// <b>只翻最上面那一张</b>（用户拍板：<c>Shift+F</c>）。
	///
	/// 与 <see cref="FlipObjects"/> 的分工：
	/// <list type="bullet">
	/// <item><c>F</c> → 整组反过来（见 <see cref="FlipObjects"/>）；</item>
	/// <item><c>Shift+F</c> → 只翻最上面那一张，次序不动。</item>
	/// </list>
	///
	/// "最上面那一张"按语境取：
	/// <list type="bullet">
	/// <item>叠放（自由堆 / 叠放区域）→ <b>成员次序里的最后一个</b>（那就是顶牌）；</item>
	/// <item>非叠放（横排 / 扇形 / 自由排版）→ 成员次序对位置没有意义，
	///   所以用<b>绘制次序最高的那个</b>（画面上压在最上面的那张），
	///   那正是用户指着的那一张。</item>
	/// </list>
	/// </summary>
	public void FlipTopOnly(IReadOnlyList<TabletopObject> targets)
	{
		if (targets.Count == 0)
			return;

		TabletopObject? top = TopOfGroup(targets);
		if (top is null)
			return;

		top.IsFaceDown = !top.IsFaceDown;
		top.QueueRedraw();

		RecordHistory($"翻最上面一张（{top.Uid}）");
	}

	/// <summary>一组里"最上面"的那一个（叠放按成员次序，其余按绘制次序）。</summary>
	private TabletopObject? TopOfGroup(IReadOnlyList<TabletopObject> targets)
	{
		if (targets.Count == 0)
			return null;

		// 叠放：成员次序的末尾就是顶牌
		int pileId = targets[0].PileId;
		if (pileId != 0 && _piles.TryGetValue(pileId, out Pile? pile) && pile.Count == targets.Count)
			return pile.Top;

		Zone? zone = _zones?.StackZoneOf(targets[0]);
		if (zone is not null && zone.Count == targets.Count && zone.Members.Count > 0)
			return zone.Top;

		// 其余（横排 / 扇形 / 自由排版 / 桌面一把散牌）：取绘制次序最高的那个 ——
		// 那就是画面上压在最上面的，也是用户指着的那一张。
		TabletopObject? best = null;
		int bestIndex = -1;

		foreach (TabletopObject obj in targets)
		{
			int index = _drawOrder.IndexOf(obj);
			if (index > bestIndex)
			{
				bestIndex = index;
				best = obj;
			}
		}

		return best;
	}

	/// <summary>目标是否构成一整摞；是则整摞翻过来并返回 true。</summary>
	private bool TryFlipWholeStack(IReadOnlyList<TabletopObject> targets)
	{
		if (targets.Count < 2)
			return false;

		// (a) 桌面上的自由堆
		int pileId = targets[0].PileId;
		if (pileId != 0 && _piles.TryGetValue(pileId, out Pile? pile) && pile.Count == targets.Count)
		{
			pile.Members.Reverse();

			foreach (TabletopObject m in pile.Members)
				m.IsFaceDown = !m.IsFaceDown;

			LayoutPile(pile);
			SyncDrawOrderToStack(pile.Members);
			_hud?.Toast($"这摞已整个翻过来（{pile.Count} 张）");
			return true;
		}

		// (b) 区域里的整组（牌库 / 弃牌堆，以及横排 / 扇形 / 自由排版的区域）
		//
		// <b>2026-09-23 修订：不再限定 <c>SortMode.Stack</c>。</b>
		// 用户的诉求是"区域里的整组也整个反过来，与桌面一致"，
		// 而叠放与非叠放的区别只在于<b>看不看得出来</b>（横排的位置由排版算、
		// 与成员次序无关，所以次序反转在画面上看不出来）—— 不该让"翻不翻"取决于排版。
		Zone? zone = _zones?.ZoneOf(targets[0]);
		if (zone is not null && zone.Count == targets.Count)
		{
			zone.FlipOver();                       // 反转成员 + 翻正反面 + 重排
			SyncDrawOrderToStack(zone.Members);    // 绘制次序得跟上，否则新顶牌被画在底下
			_hud?.Toast($"{zone.DisplayName} 已整个翻过来（{zone.Count} 张）");
			return true;
		}

		return false;
	}

	/// <summary>
	/// 该物件在绘制次序里的位置（<b>越大越靠上</b>；不属于本管理器时返回 -1）。
	/// 自检用它比较"一摞牌的绘制次序"与"成员次序"是否一致 —— 见
	/// <c>DevZoneSim</c> 里对 <c>stack_draw_order_matches_members</c> 的说明。
	/// </summary>
	public int DrawIndexOf(TabletopObject obj) => _drawOrder.IndexOf(obj);

	/// <summary>
	/// 让绘制次序跟上给定的"底 → 顶"顺序。
	///
	/// 这些物件<b>原本占用的位置不变</b>，只是按新顺序重填 ——
	/// 于是它们仍然是连续的一段（堆内连续是 M2 的约定），只是段内次序换了。
	/// 整摞翻转 / 洗牌之后<b>必须</b>调：画面上谁压谁完全由 <c>_drawOrder</c> 决定，
	/// 不同步的话新的顶牌会被画在其它牌底下，看起来像"翻转没生效"。
	/// </summary>
	public void SyncDrawOrderToStack(IReadOnlyList<TabletopObject> bottomToTop)
	{
		var wanted = new HashSet<TabletopObject>();
		foreach (TabletopObject obj in bottomToTop)
			wanted.Add(obj);

		var slots = new List<int>();
		for (int i = 0; i < _drawOrder.Count; i++)
		{
			if (wanted.Contains(_drawOrder[i]))
				slots.Add(i);
		}

		// 数量对不上说明状态不一致（比如有成员已被删掉），宁可不改，也别把次序搅乱
		if (slots.Count != bottomToTop.Count)
			return;

		for (int i = 0; i < slots.Count; i++)
			_drawOrder[slots[i]] = bottomToTop[i];

		ApplyDrawOrder();
	}

	public void RotateObjects(IReadOnlyList<TabletopObject> targets, float degrees)
	{
		foreach (TabletopObject obj in targets)
		{
			obj.RotationDeg += degrees;
			obj.QueueRedraw();
		}

		// 连按 [ / ] 会合并成一条"旋转 60°" —— 撤销时一次退回去，
		// 而不是按四次才转回原位。
		RecordHistory($"旋转 {degrees:0.#}°", $"rotate:{TargetKey(targets)}");
	}

	/// <summary>把角度归零 —— 「转正」用。</summary>
	public void ResetObjectsRotation(IReadOnlyList<TabletopObject> targets)
	{
		foreach (TabletopObject obj in targets)
		{
			obj.RotationDeg = 0f;
			obj.QueueRedraw();
		}

		RecordHistory($"转正 {targets.Count} 个物件");
	}

	/// <summary>
	/// 删除物件。<b>这是用户动作，会进历史。</b>
	/// 快照写回那条路必须用 <see cref="DeleteObjectsWithoutHistory"/>，理由见那边。
	/// </summary>
	public void DeleteObjects(IReadOnlyList<TabletopObject> targets)
		=> DeleteObjectsCore(targets, record: true);

	/// <summary>
	/// 删除物件但<b>不进历史</b>。快照写回（撤销 / 重做 / 读档）专用。
	///
	/// <b>为什么必须分成两条路：</b>写回过程中调 <see cref="DeleteObjects"/> 会顺手
	/// 记一条"删除 N 个物件"，而那一刻时间线正停在<b>被撤销的那一条之前</b> ——
	/// 于是 <c>UndoSystem.PushInternal</c> 里"在时间线中间做了新操作就丢掉后面的重做"
	/// 这条规则会把整个 redo 截断，同时把 <c>_current</c> 改写成一份
	/// "写回才写了一半"的快照。症状正是用户报的那句<b>「重做不回来」</b>。
	///
	/// 这个 bug 是 M4 自检 <c>undo_flow_simulation</c> 抓出来的：
	/// 7 条历史撤销要按 9 次，而且每退一步都多出一条"删除 36 个物件"。
	/// </summary>
	internal void DeleteObjectsWithoutHistory(IReadOnlyList<TabletopObject> targets)
		=> DeleteObjectsCore(targets, record: false);

	private void DeleteObjectsCore(IReadOnlyList<TabletopObject> targets, bool record)
	{
		// 先让区域松手。漏掉这一步 Zone.Members 会留着已释放节点的引用，
		// 下一次区域排版就会碰到野指针 —— 那是崩溃，不是数据不准。
		_zones?.ForgetObjects(targets);

		foreach (TabletopObject obj in targets)
		{
			DetachFromPile(obj, keepPosition: true);
			_drawOrder.Remove(obj);
			_selection.Remove(obj);

			// 被删掉的物件可能正是当前悬停对象，留着会变成悬空引用
			if (ReferenceEquals(_hovered, obj))
				_hovered = null;

			// <b>先摘出场景树，再 QueueFree。</b>
			//
			// QueueFree 是<b>帧末</b>释放，而 AllObjects 是 _drawOrder，
			// 上面已经把它移出去了 —— 所以对 M2/M3 的代码路径没有区别。
			// 但 M4 的撤销要在<b>同一帧内</b>把快照写回去（撤销不能等下一帧，
			// 否则"撤销后立刻截图/断言"看到的是中间态）。
			// 摘出树之后 IsInstanceValid 立刻转为 false，
			// 于是"快照里没有的物件还在吗"这类判断当场就有答案。
			if (obj.GetParent() is not null)
				RemoveChild(obj);

			obj.QueueFree();
		}

		EmitSelectionChanged();
		EmitSignal(SignalName.ObjectCountChanged, _drawOrder.Count);
		ApplyDrawOrder();

		// 写回路径不许走到这里 —— 那是"把一个已知状态抄回去"，不是用户的动作
		if (record)
			RecordHistory($"删除 {targets.Count} 个物件");
	}

	/// <summary>复制物件，副本略微偏移（否则会跟原件完全重叠，看起来像没反应）。</summary>
	public void DuplicateObjects(IReadOnlyList<TabletopObject> targets)
	{
		var copies = new List<TabletopObject>();
		Vector2 offset = new(28f, 28f);

		foreach (TabletopObject obj in targets)
		{
			ObjectState state = obj.CaptureState();
			state.Position += offset;

			// 副本不继承任何归属：它既不在原堆里，也不在区域里。
			// 只清 PileId 不清 ZoneId 的话，副本会自称属于牌库，
			// 而牌库的 Members 里根本没有它 —— 这种"双簿记不一致"是最难查的一类 bug，
			// 自检里的 member_counts_match 不变量就是冲着它去的。
			state.PileId = 0;
			state.PileIndex = 0;
			state.ZoneId = "";

			// <b>副本必须换一个 uid。</b>
			//
			// 这里原本漏了，后果是"桌面上出现两个同 uid 的物件"：
			// CaptureState 带来的 uid 非空，而 InstantiateFromState 的规则是
			// <c>string.IsNullOrEmpty(state.Uid) ? NextUid() : state.Uid</c> ——
			// 于是刚生成的 uid 被原件那份覆盖，副本与原件完全同名。
			//
			// 为什么危险：按 uid 找物件的地方很多（堆成员、区域成员、存档、
			// 撤销快照写回），同 uid 时它们会随机命中一个。症状是
			// "撤销之后消失/多出来的不是那一张""读档后有一张牌位置不对"，
			// 而两个物件在代码里长得完全一样，极难定位。
			//
			// 这个 bug 是 M4 做快照写回时被自检抓出来的：
			// 60 个物件只映射出 36 个不同 uid。断言 undo.uid_unique_on_board 现在常驻。
			state.Uid = "";

			TabletopObject? copy = InstantiateFromState(state);
			if (copy is not null)
				copies.Add(copy);
		}

		ClearSelectionInternal();
		foreach (TabletopObject copy in copies)
			AddToSelectionInternal(copy);

		EmitSelectionChanged();

		if (copies.Count > 0)
			RecordHistory($"复制 {copies.Count} 个物件");
	}

	// ---- 以下四个是"作用于选中集"的版本，右键菜单用（菜单已把选中设成被点的那个）----

	public void FlipSelection() => FlipObjects(_selection);

	public void RotateSelection(float degrees) => RotateObjects(_selection, degrees);

	public void ResetSelectionRotation() => ResetObjectsRotation(_selection);

	public void DeleteSelection() => DeleteObjects(_selection.ToArray());

	public void DuplicateSelection() => DuplicateObjects(_selection.ToArray());

	/// <summary>按快照造一个物件（复制、读档、撤销都走这里）。</summary>
	public TabletopObject? InstantiateFromState(ObjectState state)
	{
		TabletopObject? obj = state.Kind switch
		{
			ObjectKind.Card => CreateCard(state),
			ObjectKind.Token => CreateToken(state),
			ObjectKind.Dice => CreateDice(state),
			ObjectKind.Stat => CreateStat(state),
			_ => null,
		};

		if (obj is null)
			return null;

		obj.AssignUid(string.IsNullOrEmpty(state.Uid) ? NextUid("obj") : state.Uid);
		obj.ApplyState(state);
		Register(obj);
		return obj;
	}

	private CardObject? CreateCard(ObjectState state)
	{
		if (!CardDefinitions.TryGetValue(state.DefinitionId, out CardDefinition? def))
		{
			GD.PushWarning($"[ObjectManager] 找不到卡牌定义 {state.DefinitionId}，跳过。");
			return null;
		}

		CardObject card = new();
		card.SetDefinition(def);
		return card;
	}

	private TokenObject? CreateToken(ObjectState state)
	{
		if (!TokenDefinitions.TryGetValue(state.DefinitionId, out TokenDefinition? def))
		{
			GD.PushWarning($"[ObjectManager] 找不到 Token 定义 {state.DefinitionId}，跳过。");
			return null;
		}

		TokenObject token = new();
		token.SetDefinition(def);
		return token;
	}

	private static DiceObject CreateDice(ObjectState state)
	{
		DiceObject dice = new();
		dice.Configure(state.DiceSides, state.DiceCount, state.DiceValues, state.DiceSeed);
		return dice;
	}

	/// <summary>
	/// 按快照造一个计数器（读档 / 撤销 / 复制都走这里）。
	///
	/// <b>与卡牌 / 指示物不同的地方：它不需要任何"定义"</b> ——
	/// 计数器的全部内容（标签、当前值、上限）都在实例状态里。
	/// 所以这里不会像 <see cref="CreateCard"/> 那样因为"定义找不到"而返回 null，
	/// 也就不存在"读档之后某个计数器消失了"这种事。
	///
	/// 数值本身由随后的 <c>ApplyState</c> → <c>ApplyExtra</c> 写进去；
	/// 这里先给一份默认的，免得那一步之前有人读到半成品状态。
	/// </summary>
	private static StatObject CreateStat(ObjectState state)
	{
		StatObject stat = new();
		stat.SetData(new StatData
		{
			Label = state.StatLabel,
			Current = state.StatCurrent,
			Max = state.StatMax,
		});
		return stat;
	}

	/// <summary>抓取全部物件的状态快照（M4 存档 / 撤销用）。</summary>
	public List<ObjectState> CaptureAllStates()
	{
		var list = new List<ObjectState>(_drawOrder.Count);
		foreach (TabletopObject obj in _drawOrder)
			list.Add(obj.CaptureState());
		return list;
	}

	/// <summary>
	/// 丢弃全部自由堆，<b>不改动任何物件的字段</b>。
	///
	/// 与 <see cref="RebuildPiles"/> 的区别很重要：后者的职责是"把堆重建为给定状态"，
	/// 所以它开头要先解散旧堆（顺带 <c>ClearStackVisual</c>）。
	/// 但撤销/读档时，旧堆是<b>上一轮的陈旧结构</b> —— 在快照写回的半途把它解散掉，
	/// 会把刚由区域排版写好的 <c>PileIndex</c> 一起清掉（陈旧堆的成员关系
	/// 与快照的成员关系并不一致，那个"解散"清的就是快照里正确的那些值）。
	/// 所以这条路径只丢结构，字段留给后面的重建去写。
	/// </summary>
	public void DropAllPiles()
	{
		_piles.Clear();
		_nextPileId = 1;
	}

	/// <summary>
	/// 把自由堆整体重建为给定状态（撤销 / 读档用）。
	///
	/// <b>不做任何"合并语义"判断</b>：现有堆全部丢弃，然后严格按
	/// <c>PileId</c> 分组、<c>PileIndex</c> 定序重建。这是它与
	/// <see cref="GroupIntoPile"/> / <see cref="MergeInto"/> 的根本区别 ——
	/// 后两者是"用户动作"，带朝向、位置、可见性的连带处理；
	/// 而这里是"把已知状态写回去"，一个有副作用就会把快照的语义破坏掉。
	///
	/// 成员的<b>位置不动</b>：位置已经在 <c>ApplyState</c> 里逐个写好了，
	/// 这里的 <c>Position</c> 只是用来给新堆定锚点（取 <c>PileIndex</c> 最小那张）。
	/// 反过来"按锚点 + 阶梯偏移重算位置"会引入亚像素误差，
	/// 让"撤销之后位置逐字段一致"这条断言假红。
	/// </summary>
	/// <param name="states">完整快照。不在其中的物件会被当作不属于任何堆。</param>
	public void RebuildPiles(List<ObjectState> states)
	{
		// 先丢弃旧堆结构。注意这里<b>不</b>逐个 ClearStackVisual ——
		// 见 DropAllPiles 的说明：旧堆是上一轮的陈旧结构，解散它会清掉
		// 快照写回过程中刚写好的正确值。
		DropAllPiles();

		var byUid = new Dictionary<string, TabletopObject>();
		foreach (TabletopObject obj in _drawOrder)
			byUid[obj.Uid] = obj;

		// PileId → (PileIndex → 物件)。用字典而不是"逐张查找插入"，
		// 因为存档里的 PileIndex 必须被当作权威次序读入，不能靠遍历顺序碰运气。
		var grouped = new Dictionary<int, SortedDictionary<int, TabletopObject>>();

		foreach (ObjectState s in states)
		{
			if (s.PileId == 0 || !byUid.TryGetValue(s.Uid, out TabletopObject? obj))
				continue;

			// 已被删掉的物件不再进堆。撤销/读档的当下，被删的对象可能还挂在
			// _drawOrder 之外尚未真正释放 —— IsInstanceValid 是唯一可靠的判据。
			if (!GodotObject.IsInstanceValid(obj))
				continue;

			if (!grouped.TryGetValue(s.PileId, out SortedDictionary<int, TabletopObject>? members))
			{
				members = new SortedDictionary<int, TabletopObject>();
				grouped[s.PileId] = members;
			}

			// 同一 PileIndex 出现两次（坏存档）时后写覆盖先写，不抛异常 ——
			// 读档要"能开就开、开不了说清楚"，不该在主循环里炸。
			members[s.PileIndex] = obj;
		}

		foreach (KeyValuePair<int, SortedDictionary<int, TabletopObject>> kv in grouped)
		{
			Pile pile = new(kv.Key);
			int count = kv.Value.Count;
			int index = 0;

			foreach (TabletopObject obj in kv.Value.Values)
			{
				pile.Members.Add(obj);
				obj.PileId = pile.Id;
				obj.PileIndex = index;
				obj.PileCount = count;   // 派生字段也一并归一化，别留上一轮的旧值

				if (index == 0)
					pile.Anchor = obj.Position;

				index++;
			}

			_piles[pile.Id] = pile;
		}

		// <b>堆 id 计数器必须推过重建出来的最大 id。</b>
		//
		// 这条是自检 undo_flow_simulation 逼出来的（它跑在其它探针之前，
		// 而写回会经过这里，于是"写回之后新造一个堆"这条路第一次被走到）：
		// DropAllPiles 把 _nextPileId 置回 1，而重建出来的堆<b>用的就是快照里的 id</b>
		// —— 桌面上那张三张牌的示例堆恒为 1 号。于是下一次成堆会造出<b>又一个 1 号堆</b>，
		// 把 _piles[1] 覆盖掉：老堆那三张牌嘴上还说"我在 1 号堆、共 3 张"，
		// 而程序按 PileId 查到的却是另一个堆（10 张）—— 张数徽章显示 3、
		// 拖其中一张会拖动<b>另一个堆</b>的成员。
		//
		// 与 uid 计数器是同一类错误（见 UidSequence 的说明），
		// 只是 uid 撞车的症状是"撤销之后物件身份重叠"，堆 id 撞车的症状是"拖错一批牌"。
		// 实测报错文本：card-0030 pile=1 index=0 count=3（应为 10）。
		foreach (int id in _piles.Keys)
		{
			if (id >= _nextPileId)
				_nextPileId = id + 1;
		}
	}

	// ------------------------------------------------------------------ 堆叠

	/// <summary>
	/// 把一组物件直接并成一堆（不经过拖放）。
	/// 演示内容、区域抽牌、以及"多选拖到空地自动成摞"都会用到。
	/// </summary>
	/// <param name="members">要成堆的物件，至少 2 个。</param>
	/// <param name="anchor">指定哪一张当<b>最底下</b>那张（堆的成形位置就是它的位置）。
	/// 为 <c>null</c> 或不在 <paramref name="members"/> 里时取第一个。</param>
	public void GroupIntoPile(IReadOnlyList<TabletopObject> members, TabletopObject? anchor = null)
	{
		if (members.Count < 2)
			return;

		// 锚点必须真的在 members 里（用引用比较，不用 Equals —— Godot 节点比较语义容易踩坑）。
		// IReadOnlyList 没有 Contains，所以自己走一遍。
		TabletopObject target = members[0];
		if (anchor is not null)
		{
			foreach (TabletopObject m in members)
			{
				if (ReferenceEquals(m, anchor))
				{
					target = m;
					break;
				}
			}
		}

		var rest = new List<TabletopObject>(members.Count - 1);
		foreach (TabletopObject m in members)
		{
			if (!ReferenceEquals(m, target))
				rest.Add(m);
		}

		MergeInto(target, rest);
	}

	private void MergeInto(TabletopObject target, List<TabletopObject> dragged)
	{
		if (dragged.Contains(target))
			return;

		Pile pile;

		if (target.PileId != 0 && _piles.TryGetValue(target.PileId, out Pile? existing))
		{
			// 已经拖进同一个堆了，别重复加
			foreach (TabletopObject obj in dragged)
			{
				if (existing.Contains(obj))
					return;
			}

			pile = existing;
		}
		else
		{
			DetachFromPile(target, keepPosition: true);

			// id 由这里统一发，且<b>跳过已被占用的号</b>。
			// 计数器与 _piles 的对应关系只要有过一次错位（写回、读档、将来 M5 的编辑器），
			// 这里就会造出一个覆盖现有堆的同号堆 —— 而那种损坏不会报错，
			// 只会让"拖一张牌带动了另一批牌"。几行查询换掉一整类脏数据，值得。
			int id = NextFreePileId();
			pile = new Pile(id)
			{
				Anchor = target.Position,
			};

			_piles[id] = pile;
			pile.Members.Add(target);
		}

		foreach (TabletopObject obj in dragged)
			DetachFromPile(obj, keepPosition: true);

		foreach (TabletopObject obj in dragged)
		{
			if (!pile.Contains(obj))
				pile.Members.Add(obj);
		}

		LayoutPile(pile);
		ApplyDrawOrder();
	}

	/// <summary>
	/// 取一个还没被占用的堆 id。
	///
	/// <b>唯一性不能只靠计数器。</b>计数器会在"丢堆 / 重建堆"的路上被重置
	/// （<see cref="DropAllPiles"/> 置回 1，而 <see cref="RebuildPiles"/> 用的是快照里的 id），
	/// 而堆 id 一旦撞车就是静默的数据损坏：两个堆共用一个 id，
	/// 按 PileId 查成员的地方会随机命中一个。详见 <see cref="RebuildPiles"/> 末尾的说明。
	/// </summary>
	private int NextFreePileId()
	{
		while (_piles.ContainsKey(_nextPileId))
			_nextPileId++;

		return _nextPileId++;
	}

	/// <summary>把物件从它所在的堆里摘出来。堆剩不到两张就散堆。</summary>
	private void DetachFromPile(TabletopObject obj, bool keepPosition)
	{
		if (obj.PileId == 0)
			return;

		if (!_piles.TryGetValue(obj.PileId, out Pile? pile))
		{
			ResetPileFields(obj);
			return;
		}

		pile.Members.Remove(obj);
		ResetPileFields(obj);

		if (!keepPosition)
			obj.Position = pile.Anchor;

		if (pile.Count <= 1)
		{
			// 只剩 0 或 1 张 —— 散堆，别留一个只有一个成员的"堆"
			foreach (TabletopObject m in pile.Members)
				ResetPileFields(m);

			pile.Members.Clear();
			_piles.Remove(pile.Id);
			return;
		}

		LayoutPile(pile);
	}

	/// <summary>
	/// 把物件从自由堆里摘干净。
	/// 直接委托给 <see cref="TabletopObject.ClearStackVisual"/> —— 自由堆与区域两条路
	/// 都必须用同一份"回到普通散件"的逻辑，否则总有一条会漏清张数徽章。
	/// </summary>
	private static void ResetPileFields(TabletopObject obj) => obj.ClearStackVisual();

	/// <summary>拆散选中物件所在的堆。</summary>
	public void DissolvePile(TabletopObject obj)
	{
		if (obj.PileId == 0 || !_piles.TryGetValue(obj.PileId, out Pile? pile))
			return;

		// 摊开：每张往右下错开，方便看清有些什么
		Vector2 start = pile.Anchor;
		Vector2 step = new(36f, 36f);

		for (int i = 0; i < pile.Members.Count; i++)
		{
			TabletopObject m = pile.Members[i];
			ResetPileFields(m);
			m.Position = start + (step * i);
			m.QueueRedraw();
		}

		pile.Members.Clear();
		_piles.Remove(pile.Id);
		ApplyDrawOrder();
	}

	/// <summary>重排一个堆：位置阶梯对齐、层级连号、只显示最上面几张。</summary>
	private void LayoutPile(Pile pile)
	{
		int count = pile.Members.Count;

		for (int i = 0; i < count; i++)
		{
			TabletopObject m = pile.Members[i];
			m.PileId = pile.Id;
			m.PileIndex = i;
			m.PileCount = count;
			m.Position = pile.Anchor + Pile.OffsetFor(i);

			// 只画最上面 PileVisibleDepth 张 —— 一叠 60 张牌没必要全画
			m.Visible = i >= count - GameConfig.PileVisibleDepth;
			m.QueueRedraw();
		}
	}

	/// <summary>把一堆的锚点对齐到当前实际位置（拖完之后调用）。</summary>
	private void SyncPileAnchors()
	{
		foreach (Pile pile in _piles.Values)
		{
			if (pile.Count == 0)
				continue;

			pile.Anchor = pile.Bottom!.Position - Pile.OffsetFor(0);
		}
	}

	private void SnapToGrid(IEnumerable<TabletopObject> objects)
	{
		foreach (TabletopObject obj in objects)
		{
			obj.Position = _board.SnapToGrid(obj.Position);
			obj.QueueRedraw();
		}

		SyncPileAnchors();
	}

	// ------------------------------------------------------------------ 绘制次序

	private void BringToFront(IReadOnlyList<TabletopObject> objects)
	{
		var moved = new List<TabletopObject>(objects);

		// 保持组内相对次序：按当前绘制次序排一遍
		moved.Sort((a, b) => _drawOrder.IndexOf(a).CompareTo(_drawOrder.IndexOf(b)));

		foreach (TabletopObject obj in moved)
			_drawOrder.Remove(obj);

		_drawOrder.AddRange(moved);
		ApplyDrawOrder();
	}

	/// <summary>把 <see cref="_drawOrder"/> 的次序同步到真实子节点次序。</summary>
	private void ApplyDrawOrder()
	{
		for (int i = 0; i < _drawOrder.Count; i++)
		{
			TabletopObject obj = _drawOrder[i];
			if (IsInstanceValid(obj) && obj.GetIndex() != i)
				MoveChild(obj, i);
		}
	}

	// ------------------------------------------------------------------ 右键菜单

	/// <summary>
	/// 自检用：在某个屏幕位置弹一次<b>真实的</b>右键菜单。
	///
	/// 只是把 <c>ViewportController.ContextMenuRequested</c> 那条信号换成直接调用 ——
	/// 走的是与"用户右键"完全相同的处理（命中 → 选中 → 建菜单 → 定位），
	/// 而不是另写一份"给测试用的建菜单"。
	/// </summary>
	internal void RequestContextMenuAtForTest(Vector2 worldPos, Vector2 screenPos) =>
		OnContextMenuRequested(worldPos, screenPos);

	private void OnContextMenuRequested(Vector2 worldPos, Vector2 screenPos)
	{		if (_menu is null)
			return;

		TabletopObject? target = PickTopmostExcluding(worldPos, null);

		// 区域可能先接管。Stack 区域（牌库 / 弃牌堆）的正确操作单位是"这一摞"
		// （洗牌 / 抽牌 / 全部翻开），而不是"恰好被点到的那第 7 张牌"。
		// 别的排版类型仍然是物件优先 —— 那里每张牌都要能单独操作。
		if (_zones is not null && _zones.TryHandleContextMenu(worldPos, screenPos, target))
		{
			// 顺手收掉上一次的物件菜单：PopupMenu 是 Window，留着会抢后续的合成鼠标事件
			_menu.Hide();
			return;
		}

		if (target is null)
		{
			// 空白处右键：只给"全选"这类全局项
			_menu.Clear();
			_menu.AddItem("全选", (int)MenuId.SidesBase + 900);
			ShowMenuAt(screenPos);
			return;
		}

		if (!_selection.Contains(target))
		{
			ClearSelectionInternal();
			AddToSelectionInternal(target);
			EmitSelectionChanged();
		}

		BuildMenu(target);
		ShowMenuAt(screenPos);
	}

	/// <summary>
	/// 把菜单弹在指定的<b>视口坐标</b>处，并收拢到视口内。
	///
	/// 两个要点：
	/// <list type="number">
	/// <item>必须用输入事件带来的视口坐标，不能用 <c>DisplayServer.MouseGetPosition()</c> ——
	/// 后者是桌面坐标，和嵌入式子窗口的 <c>Position</c> 不是一个坐标系。</item>
	/// <item>必须自己做边界收拢。鼠标靠近右/下边缘时，菜单会伸出视口外。</item>
	/// </list>
	/// </summary>
	private void ShowMenuAt(Vector2 screenPos)
	{
		if (_menu is null)
			return;

		// 先用内容最小尺寸估一次（此时还没 Popup，拿不到实际尺寸）
		Vector2 estimate = _menu.GetContentsMinimumSize();
		if (estimate.X <= 0f || estimate.Y <= 0f)
			estimate = new Vector2(180f, 140f);

		_menu.Position = (Vector2I)ClampToViewport(screenPos, estimate);
		_menu.Popup();

		// Popup 之后才有真实尺寸，用真实值再收一次，避免估小了导致仍然溢出
		Vector2 actual = _menu.Size;
		if (actual.X > 0f && actual.Y > 0f)
		{
			Vector2 corrected = ClampToViewport(screenPos, actual);
			if (!corrected.IsEqualApprox((Vector2)_menu.Position))
				_menu.Position = (Vector2I)corrected;
		}
	}

	private Vector2 ClampToViewport(Vector2 pos, Vector2 menuSize)
	{
		Vector2 viewport = GetViewportRect().Size;
		return new Vector2(
			Mathf.Clamp(pos.X, 0f, Mathf.Max(viewport.X - menuSize.X, 0f)),
			Mathf.Clamp(pos.Y, 0f, Mathf.Max(viewport.Y - menuSize.Y, 0f)));
	}

	private void BuildMenu(TabletopObject target)
	{
		if (_menu is null)
			return;

		// 「编辑这张」要知道"用户右键的是哪一个"。
		//
		// <b>为什么不能等到菜单项被点的时候再解析：</b>右键菜单弹出之后，
		// 用户可能把鼠标移到别处、选中集也可能已经变了，而
		// <c>ResolveActionTargets</c> 那套"悬停优先"的规则是按<b>当下</b>算的。
		// 菜单里的文案（"复制 3 个"）也是按当下算的 —— 于是这个目标必须
		// 在<b>建菜单那一刻</b>定下来，与文案同源。
		_editTarget = target;

		_menu.Clear();
		_menu.AddItem("翻面 (F)", (int)MenuId.Flip);
		_menu.AddItem("顺时针 90°", (int)MenuId.RotateCw);
		_menu.AddItem("逆时针 90°", (int)MenuId.RotateCcw);
		_menu.AddItem("重置旋转", (int)MenuId.ResetRotation);

		// ---- 「编辑这张 / 编辑这个」（M5 遗留口子的入口）----
		//
		// <b>为什么放在最上面一档、而不是塞进某个子菜单：</b>
		// 这条口子的全部内容就是"入口不可发现" —— 编辑器的实例级覆盖数据层早就有了
		// （<c>CardObject.SetFieldOverride</c>），缺的只是"从桌上这张牌走过去"这一步。
		// 藏进子菜单等于把它又藏起来一次。
		//
		// 只对**有定义**的物件出现：骰子/计数器没有"定义"这回事
		// （计数器的内容全在实例状态里），给它们显示一条点下去什么都不做的菜单项是坏的。
		if (target is CardObject or TokenObject)
		{
			// 文案按物件类型分开写："编辑这张卡" / "编辑这个指示物" ——
			// 拼成 $"编辑这{what}" 会得到"编辑这卡"，读着别扭（第一版就是那么写的）。
			string label = target is CardObject ? "编辑这张卡（F1）" : "编辑这个指示物（F1）";
			_menu.AddItem(label, (int)MenuId.EditInstance);
		}

		_menu.AddSeparator();
		_menu.AddItem($"复制 {_selection.Count} 个", (int)MenuId.Duplicate);
		_menu.AddItem($"删除 {_selection.Count} 个", (int)MenuId.Delete);

		if (target.PileId != 0 && _piles.TryGetValue(target.PileId, out Pile? pile))
		{
			_menu.AddSeparator();
			_menu.AddItem("从堆中取出", (int)MenuId.PullFromPile);
			_menu.AddItem($"拆散这堆（{pile.Count} 张）", (int)MenuId.DissolvePile);
		}

		if (target is DiceObject dice)
		{
			_menu.AddSeparator();
			_menu.AddItem("掷！", (int)MenuId.RollDice);

			PopupMenu sides = new() { Name = "Sides" };
			for (int i = 0; i < DiceSideChoices.Length; i++)
				sides.AddItem($"D{DiceSideChoices[i]}", (int)MenuId.SidesBase + i);
			sides.IdPressed += OnMenuItemPressed;
			_menu.AddChild(sides);
			_menu.AddSubmenuNodeItem("面数", sides);

			PopupMenu counts = new() { Name = "Counts" };
			for (int i = 0; i < DiceCountChoices.Length; i++)
				counts.AddItem($"{DiceCountChoices[i]} 个", (int)MenuId.CountBase + i);
			counts.IdPressed += OnMenuItemPressed;
			_menu.AddChild(counts);
			_menu.AddSubmenuNodeItem("数量", counts);

			_ = dice;
		}

		// ---- 计数器专属（M5.5 P4）----
		//
		// 轻点就能加减，菜单里再给一套显式的 —— 两件事都有理由：
		// 轻点是高频操作（每次受伤害都要点一下），而菜单是<b>可发现性</b>
		// （新用户不会知道"Shift + 点"是减）。两套入口共用同一条改数值的路径。
		if (target is StatObject stat)
		{
			_menu.AddSeparator();
			_menu.AddItem($"加 {GameConfig.StatStep}（现在 {stat.Data.Current}/{stat.Data.Max}）", (int)MenuId.StatPlus);
			_menu.AddItem($"减 {GameConfig.StatStep}", (int)MenuId.StatMinus);
			_menu.AddItem($"加 {GameConfig.StatBigStep}", (int)MenuId.StatPlusBig);
			_menu.AddItem($"减 {GameConfig.StatBigStep}", (int)MenuId.StatMinusBig);

			PopupMenu maxes = new() { Name = "StatMax" };
			for (int i = 0; i < StatMaxChoices.Length; i++)
				maxes.AddItem($"上限 {StatMaxChoices[i]}", (int)MenuId.StatMaxBase + i);
			maxes.IdPressed += OnMenuItemPressed;
			_menu.AddChild(maxes);
			_menu.AddSubmenuNodeItem("上限", maxes);
		}
	}

	/// <summary>
	/// 右键菜单建出来时"用户指着的那个物件"（M5 遗留口子：「编辑这张」的依据）。
	///
	/// 见 <see cref="BuildMenu"/> 里的说明：菜单里的文案与动作目标都必须在
	/// <b>建菜单那一刻</b>定下来，不能等菜单项被点时再按"悬停优先"重算。
	/// </summary>
	private TabletopObject? _editTarget;

	/// <summary>
	/// 把"编辑这一个物件"这件事交给外面（<c>Main</c> 接到编辑器上）。
	///
	/// 只认有定义的物件：卡牌（<c>CardObject</c>）与指示物（<c>TokenObject</c>）。
	/// 骰子与计数器的"内容"全在实例状态里，没有可编辑的定义，
	/// 而它们本来就能在桌面上直接改（`[ ]` 旋转、右键加减血量）。
	/// </summary>
	private void RequestEditInstance()
	{
		if (_editTarget is null || !IsInstanceValid(_editTarget))
			return;

		string kind = _editTarget switch
		{
			CardObject => "card",
			TokenObject => "token",
			_ => "",
		};

		if (kind.Length == 0)
			return;

		EmitSignal(SignalName.EditInstanceRequested, kind, _editTarget.Uid);
	}

	private void OnMenuItemPressed(long id)
	{
		int menuId = (int)id;
		// 计数器的"上限"子菜单（M5.5 P4）。
		//
		// <b>这一段必须排在下面那几条区间判断之前</b>：那些判断写的是
		// <c>menuId &gt;= CountBase</c>（无上界），所以任何比 200 大的 id 都会被它们先接走 ——
		// StatMaxBase = 300 会落进 CountBase 那一支，然后被
		// "index 超范围就什么都不做"静默吞掉。症状是"点了上限没反应"，极难往回查。
		if (menuId >= (int)MenuId.StatMaxBase && menuId < (int)MenuId.StatMaxBase + StatMaxChoices.Length)
		{
			ApplyStatMax(StatMaxChoices[menuId - (int)MenuId.StatMaxBase]);
			return;
		}

		if (menuId >= (int)MenuId.SidesBase + 900)
		{
			SelectAll();
			return;
		}

		if (menuId >= (int)MenuId.CountBase)
		{
			int index = menuId - (int)MenuId.CountBase;
			if (index >= 0 && index < DiceCountChoices.Length)
				ApplyDiceCount(DiceCountChoices[index]);
			return;
		}

		if (menuId >= (int)MenuId.SidesBase)
		{
			int index = menuId - (int)MenuId.SidesBase;
			if (index >= 0 && index < DiceSideChoices.Length)
				ApplyDiceSides(DiceSideChoices[index]);
			return;
		}

		switch ((MenuId)menuId)
		{
			case MenuId.EditInstance:
				RequestEditInstance();
				break;

			case MenuId.Flip:
				FlipSelection();
				break;
			case MenuId.RotateCw:
				RotateSelection(90f);
				break;
			case MenuId.RotateCcw:
				RotateSelection(-90f);
				break;
			case MenuId.ResetRotation:
				ResetSelectionRotation();
				break;
			case MenuId.Duplicate:
				DuplicateSelection();
				break;
			case MenuId.Delete:
				DeleteSelection();
				break;
			case MenuId.PullFromPile:
				{
					int pulled = 0;
					foreach (TabletopObject obj in _selection.ToArray())
					{
						DetachFromPile(obj, keepPosition: false);
						pulled++;
					}

					ApplyDrawOrder();

					// 拆堆也要进历史（M4 第 4 步）：否则"我把它从堆里抽出来"这一步撤不掉，
					// 而那一步往往正是发现"抽错了"的那一刻。
					if (pulled > 0)
						RecordHistory($"从堆中取出 {pulled} 个物件");
					break;
				}

			case MenuId.DissolvePile:
				{
					int dissolved = 0;
					foreach (TabletopObject obj in _selection.ToArray())
					{
						DissolvePile(obj);
						dissolved++;
					}

					if (dissolved > 0)
						RecordHistory($"拆散 {dissolved} 个物件所在的堆");
					break;
				}

			case MenuId.RollDice:
				{
					var rolled = new List<string>();
					foreach (TabletopObject obj in _selection)
					{
						if (obj is DiceObject d)
						{
							d.Roll();
							rolled.Add(string.Join("/", d.Values));
						}
					}

					if (rolled.Count > 0)
						RecordHistory($"掷骰 {string.Join("、", rolled)}");
					break;
				}

			// ---- 计数器（M5.5 P4）----
			//
			// 四个加减项与「上限」子菜单都走 <see cref="ChangeSelectedStats"/> ——
			// 与轻点那条路共用同一条改数值 + 记历史的实现，
			// 免得"轻点能撤、菜单不能撤"这种两套行为分叉。
			case MenuId.StatPlus:
				ChangeSelectedStats(GameConfig.StatStep);
				break;
			case MenuId.StatMinus:
				ChangeSelectedStats(-GameConfig.StatStep);
				break;
			case MenuId.StatPlusBig:
				ChangeSelectedStats(GameConfig.StatBigStep);
				break;
			case MenuId.StatMinusBig:
				ChangeSelectedStats(-GameConfig.StatBigStep);
				break;
		}
	}

	/// <summary>
	/// 给选中的计数器加减数值，并记一条历史。
	///
	/// <b>它是"改数值"的唯一实现</b>：轻点（<see cref="OnStatClicked"/>）与右键菜单
	/// 都调它。分成两份的下场是"轻点撤得掉、菜单撤不掉"这类不对称 bug，
	/// 而那种 bug 只有用户按 Ctrl+Z 时才会发现。
	/// </summary>
	private void ChangeSelectedStats(int delta)
	{
		var changed = new List<StatObject>();

		foreach (TabletopObject obj in _selection)
		{
			if (obj is StatObject stat && stat.AddCurrent(delta))
				changed.Add(stat);
		}

		if (changed.Count == 0)
			return;   // 没有净变化就不记历史（与"空操作不进历史"同一条规矩）

		StatObject first = changed[0];
		string label = changed.Count == 1
			? $"{first.Data.Label} {(delta > 0 ? "+" : "")}{delta}（现在 {first.Data.Current}/{first.Data.Max}）"
			: $"{changed.Count} 个计数器 {(delta > 0 ? "+" : "")}{delta}";

		RecordHistory(label);
	}

	/// <summary>把选中计数器的上限改成 <paramref name="max"/>（菜单里的预设值）。</summary>
	private void ApplyStatMax(int max)
	{
		var changed = new List<StatObject>();

		foreach (TabletopObject obj in _selection)
		{
			if (obj is StatObject stat && stat.SetMax(max))
				changed.Add(stat);
		}

		if (changed.Count == 0)
			return;

		RecordHistory(changed.Count == 1
			? $"{changed[0].Data.Label} 上限改为 {max}"
			: $"{changed.Count} 个计数器的上限改为 {max}");
	}

	private void ApplyDiceSides(int sides)
	{
		foreach (TabletopObject obj in _selection)
		{
			if (obj is DiceObject dice)
				dice.Configure(sides, dice.Count);
		}
	}

	private void ApplyDiceCount(int count)
	{
		foreach (TabletopObject obj in _selection)
		{
			if (obj is DiceObject dice)
				dice.Configure(dice.Sides, count);
		}
	}

	// ------------------------------------------------------------------ 键盘

	/// <summary>
	/// 洗桌面上的<b>自由堆</b>（R）：悬停在一摞散牌上就洗它。
	///
	/// 叠放区域（牌库 / 弃牌堆）不在这里处理 —— 那条路归 <c>ZoneManager</c>，
	/// 它有自己的成员表与排版，混在一起会变成两处各自维护同一件事。
	/// 这里的判断条件刻意写窄（必须是自由堆成员），保证区域那条路不被拦住。
	/// </summary>
	private bool TryShuffleHoveredPile()
	{
		if (_hovered is null || !IsInstanceValid(_hovered))
			return false;

		if (_hovered.PileId == 0 || !_piles.TryGetValue(_hovered.PileId, out Pile? pile))
			return false;

		if (pile.Count < 2)
			return false;

		int seed = pile.Shuffle();
		LayoutPile(pile);
		SyncDrawOrderToStack(pile.Members);
		_hud?.Toast($"这摞已洗牌（{pile.Count} 张，种子 {seed}）");

		// 洗牌<b>进历史</b>：它是对已有牌序的操作，撤销就是恢复原来的次序
		// （骰子不同，那个见 UndoSystem 的说明）。种子写进描述里，
		// 于是"这把怎么这么离谱"可复现。
		RecordHistory($"洗牌（{pile.Count} 张，种子 {seed}）");
		return true;
	}

	/// <summary>本节点看到过的按键数（只读诊断；见 <c>_UnhandledKeyInput</c> 里的说明）。</summary>
	internal int KeyEventsSeen { get; private set; }

	/// <summary>
	/// 当前悬停的物件 uid（只读诊断；没有则空串）。
	///
	/// 为什么值得暴露：按键作用的<b>目标</b>按"悬停优先"解析（见
	/// <see cref="ResolveActionTargets"/>），所以"按 E 没反应"完全可能是
	/// "悬停还停在另一张牌上、而 E 转的是那一张"——那时被观察的那张自然纹丝不动，
	/// 而报告里只有一个 0。把悬停对象的名字写出来，这种情况一眼可辨。
	/// </summary>
	internal string HoveredUidForTest => _hovered is not null && IsInstanceValid(_hovered) ? _hovered.Uid : "";

	public override void _UnhandledKeyInput(InputEvent @event)
	{
		if (@event is not InputEventKey key || !key.Pressed || key.Echo)
			return;

		// 只读计数器（诊断用）：本节点到底看到过几个按键。
		//
		// 存在的理由与 ViewportController.KeyEvents 同一套"把猜换成读"：
		// 自检报出"按 E 没反应"时，报告里只有 rotation_delta = 0 ——
		// 那个 0 分不出三种情况：键没送到引擎、被别的节点吃了、还是送到了但目标集是空的。
		// 把这个数写进报告，三种情况立刻分得开（本轮就靠它把范围缩到了"键没到这一层"）。
		KeyEventsSeen++;

		// 撤销 / 重做。放在<b>最前面</b>：它的目标是"整张桌子"，
		// 与悬停 / 选中无关，不该被后面的分支抢先。
		//
		// 能走到这里说明 UI 控件没吃掉这个键（_UnhandledKeyInput 的定义如此），
		// 于是"在输入框里按 Ctrl+Z"仍然是撤销输入而不是撤销摆牌 —— 这条自动成立。
		if (Undo is not null && (key.IsActionPressed("tt_undo") || key.IsActionPressed("tt_redo")))
		{
			bool redo = key.IsActionPressed("tt_redo");

			// <b>两套历史，按"编辑器开着没有"分流</b>（用户拍板）：
			// 面板开着 → Ctrl+Z 退编辑器改的定义 / 实例覆盖；
			// 关着 → 退桌面动作。
			//
			// 界面上必须说得清这件事，所以底部提示栏里那一条写了两种键。
			// 另一条路是显式的：<c>Ctrl+Alt+Z</c> 永远退桌面那条
			// （面板开着时想撤刚才拖的那一下，不必先去关面板）。
			EditorPanel? editor = _hud?.EditorPanelForShortcuts;
			EditorUndo? editorUndo = editor?.EditorUndoForTest;
			bool forceTable = key.IsActionPressed("tt_undo_table");
			bool useEditor = editor is not null && editor.IsOpen && editorUndo is not null && !forceTable;

			if (useEditor)
			{
				string editorLabel = redo ? editorUndo!.RedoLabel : editorUndo!.UndoLabel;
				bool didEditor = redo ? editorUndo!.Redo() : editorUndo!.Undo();

				if (didEditor)
				{
					// 撤销改了定义 → 界面要跟上（列表 / 表单 / 预览 / 桌面上的实例）。
					editor!.OnDefinitionUndone();
					_hud?.Toast($"编辑器：{editorLabel}");
				}
				else
				{
					_hud?.Toast(redo ? "编辑器没有可重做的改动" : "编辑器没有可撤销的改动");
				}

				GetViewport().SetInputAsHandled();
				return;
			}

			string label = redo ? Undo.RedoLabel : Undo.UndoLabel;
			bool did = redo ? Undo.Redo() : Undo.Undo();

			if (did)
			{
				_hud?.Toast(label);
			}
			else if (Undo.GestureInProgress)
			{
				// 见 UndoSystem.Undo 的说明：手势中间不能撤销
				_hud?.Toast("正在拖拽，先松开鼠标再撤销");
			}
			else
			{
				_hud?.Toast(redo ? "没有可重做的操作" : "没有可撤销的操作");
			}

			GetViewport().SetInputAsHandled();
			return;
		}

		// 洗牌（R）：悬停在一摞牌上就洗那一摞。
		// <b>物件优先于区域</b> —— 鼠标指着牌堆里的某张牌时，要洗的是那一摞，
		// 而不是"恰好压在这摞底下的那个区域"。
		if (key.IsActionPressed("tt_shuffle") && TryShuffleHoveredPile())
		{
			GetViewport().SetInputAsHandled();
			return;
		}

		// 剩下的按键（区域洗牌 / 双击抽牌的 D）交给区域系统。
		// 它没有悬停目标时返回 false，照常往下走 —— 于是这些键在区域之外毫无副作用。
		if (_zones is not null && _zones.TryHandleKey(key))
		{
			GetViewport().SetInputAsHandled();
			return;
		}

		// 「悬停即为选中」：操作目标是"指着的那张"，没有悬停才退回选中集。
		// 所以这里判断的是目标集是否为空，而不是选中集是否为空 ——
		// 否则光是悬停一张卡、没点过任何东西时，按键会毫无反应。
		bool actionable =
			key.IsActionPressed("tt_flip") ||
			key.IsActionPressed("tt_rotate_cw") ||
			key.IsActionPressed("tt_rotate_ccw") ||
			key.IsActionPressed("tt_duplicate") ||
			key.IsActionPressed("tt_delete");

		if (actionable)
		{
			List<TabletopObject> targets = ResolveActionTargets();
			if (targets.Count > 0)
			{
				// <b>F / Shift+F 的分工（用户拍板）：</b>
				//   F       → 整组整个反过来（次序反转 + 每张翻面）
				//   Shift+F → 只翻最上面那一张，次序不动
				//
				// 这两件事都要有：验牌时既会"把这一副整个翻过来看看",
				// 也会"只把顶上那张翻开看看"。
				if (key.IsActionPressed("tt_flip"))
				{
					if (key.ShiftPressed)
						FlipTopOnly(targets);
					else
						FlipObjects(targets);
				}
				else if (key.IsActionPressed("tt_rotate_cw"))
					RotateObjects(targets, GameConfig.KeyRotateStepDegrees);
				else if (key.IsActionPressed("tt_rotate_ccw"))
					RotateObjects(targets, -GameConfig.KeyRotateStepDegrees);
				else if (key.IsActionPressed("tt_duplicate"))
					DuplicateObjects(targets);
				else if (key.IsActionPressed("tt_delete"))
					DeleteObjects(targets);

				GetViewport().SetInputAsHandled();
				return;
			}
		}

		if (key.IsActionPressed("tt_select_all"))
		{
			SelectAll();
			GetViewport().SetInputAsHandled();
		}
		else if (key.IsActionPressed("tt_deselect"))
		{
			ClearSelectionInternal();
			_dragging.Clear();
			GetViewport().SetInputAsHandled();
		}
		else if (key.IsActionPressed("tt_grid_snap"))
		{
			GridSnapEnabled = !GridSnapEnabled;
			EmitSignal(SignalName.GridSnapChanged, GridSnapEnabled);
			GetViewport().SetInputAsHandled();
		}
	}
}
