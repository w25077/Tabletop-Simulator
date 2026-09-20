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
		// 面数与数量用区间
		SidesBase = 100,
		CountBase = 200,
	}

	private static readonly int[] DiceSideChoices = { 4, 6, 8, 10, 12, 20, 100 };
	private static readonly int[] DiceCountChoices = { 1, 2, 3, 5 };

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
	private PopupMenu? _menu;

	// ---- 交互状态 ----
	private TabletopObject? _hovered;
	private bool _boxSelecting;
	private bool _dragMoved;
	private Vector2 _boxCursor;

	// ------------------------------------------------------------------ 属性

	/// <summary>卡牌定义池（M5 的编辑器会往里加）。</summary>
	public Dictionary<string, CardDefinition> CardDefinitions { get; } = new();

	/// <summary>Token 定义池。</summary>
	public Dictionary<string, TokenDefinition> TokenDefinitions { get; } = new();

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

	[Signal] public delegate void SelectionChangedEventHandler(int count);
	[Signal] public delegate void ObjectCountChangedEventHandler(int count);
	[Signal] public delegate void GridSnapChangedEventHandler(bool enabled);

	// ------------------------------------------------------------------ 装配

	public void Bind(Board board, BoardCamera camera, ViewportController viewport, SelectionBox box, CanvasLayer hudLayer)
	{
		_board = board;
		_camera = camera;
		_viewport = viewport;
		_box = box;

		viewport.Picker = this;
		viewport.WheelHandler = this;

		viewport.PrimaryPressed += OnPrimaryPressed;
		viewport.PrimaryDragStarted += OnPrimaryDragStarted;
		viewport.PrimaryDragged += OnPrimaryDragged;
		viewport.PrimaryReleased += OnPrimaryReleased;
		viewport.EmptyAreaClicked += OnEmptyAreaClicked;
		viewport.ContextMenuRequested += OnContextMenuRequested;
		viewport.PointerMoved += OnPointerMoved;

		_menu = new PopupMenu { Name = "ObjectMenu" };
		_menu.IdPressed += OnMenuItemPressed;
		hudLayer.AddChild(_menu);
	}

	// ------------------------------------------------------------------ 创建

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
		if (_boxSelecting)
			return;

		TabletopObject? hit = PickTopmostExcluding(worldPos, _dragging.Count > 0 ? _dragging : null);
		if (ReferenceEquals(hit, _hovered))
			return;

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

		// 悬停就是"指着的那张"，操作目标随之变化，描边要跟着变
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
		BringToFront(_dragging);
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
	}

	private void OnPrimaryReleased(Vector2 worldPos)
	{
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
			_dragging.Clear();
			_draggingPiles.Clear();
			return;
		}

		if (_dragging.Count == 0)
			return;

		if (_dragMoved)
		{
			if (GridSnapEnabled)
				SnapToGrid(_dragging);

			TryMergeAfterDrop();
		}
		else if (_selection.Count == 1 && _selection[0] is DiceObject single)
		{
			// 轻点骰子即掷 —— 掷骰是高频操作，不该逼人去右键菜单里翻
			single.Roll();
		}

		_dragging.Clear();
		_draggingPiles.Clear();
		ApplyDrawOrder();
	}

	private void OnEmptyAreaClicked(Vector2 worldPos)
	{
		// 空点一下：没有选中集就什么都不做，有就取消
		if (_selection.Count > 0)
			ClearSelectionInternal();

		_dragging.Clear();
		_draggingPiles.Clear();
	}

	/// <summary>松手后：如果最上面那张压在了别的物件上，就并成一堆。</summary>
	private void TryMergeAfterDrop()
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
		if (target is not null)
		{
			MergeInto(target, _dragging);
			return;
		}

		// 没压到东西：单张拖出来 → 脱离原堆
		if (_dragging.Count == 1)
			DetachFromPile(_dragging[0], keepPosition: true);
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

	public void ClearSelection() => ClearSelectionInternal();

	// ------------------------------------------------------------------ 物件操作

	/// <summary>
	/// 键盘/滚轮操作的<b>目标集</b>。用户要求「悬停即为选中」，即指着哪张就操作哪张，
	/// 不必先点一下。规则：
	/// <list type="number">
	/// <item>鼠标悬停在某个物件上 → 操作它。</item>
	/// <item>悬停的物件<b>本身已在选中集里</b> → 操作整个选中集。
	///   这条很关键：框选完之后鼠标通常还停在其中一个上，
	///   若按规则 1 只操作那一张，用户会觉得"框选白做了"。</item>
	/// <item>什么都没悬停 → 操作选中集。</item>
	/// </list>
	/// </summary>
	private List<TabletopObject> ResolveActionTargets()
	{
		if (_hovered is not null && IsInstanceValid(_hovered))
		{
			if (_selection.Contains(_hovered))
				return new List<TabletopObject>(_selection);

			return new List<TabletopObject> { _hovered };
		}

		return new List<TabletopObject>(_selection);
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

	public void FlipObjects(IReadOnlyList<TabletopObject> targets)
	{
		foreach (TabletopObject obj in targets)
		{
			obj.IsFaceDown = !obj.IsFaceDown;
			obj.QueueRedraw();
		}
	}

	public void RotateObjects(IReadOnlyList<TabletopObject> targets, float degrees)
	{
		foreach (TabletopObject obj in targets)
		{
			obj.RotationDeg += degrees;
			obj.QueueRedraw();
		}
	}

	/// <summary>把角度归零 —— 「转正」用。</summary>
	public void ResetObjectsRotation(IReadOnlyList<TabletopObject> targets)
	{
		foreach (TabletopObject obj in targets)
		{
			obj.RotationDeg = 0f;
			obj.QueueRedraw();
		}
	}

	public void DeleteObjects(IReadOnlyList<TabletopObject> targets)
	{
		foreach (TabletopObject obj in targets)
		{
			DetachFromPile(obj, keepPosition: true);
			_drawOrder.Remove(obj);
			_selection.Remove(obj);

			// 被删掉的物件可能正是当前悬停对象，留着会变成悬空引用
			if (ReferenceEquals(_hovered, obj))
				_hovered = null;

			obj.QueueFree();
		}

		EmitSelectionChanged();
		EmitSignal(SignalName.ObjectCountChanged, _drawOrder.Count);
		ApplyDrawOrder();
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
			state.PileId = 0;
			state.PileIndex = 0;

			TabletopObject? copy = InstantiateFromState(state);
			if (copy is not null)
				copies.Add(copy);
		}

		ClearSelectionInternal();
		foreach (TabletopObject copy in copies)
			AddToSelectionInternal(copy);

		EmitSelectionChanged();
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

	/// <summary>抓取全部物件的状态快照（M4 存档 / 撤销用）。</summary>
	public List<ObjectState> CaptureAllStates()
	{
		var list = new List<ObjectState>(_drawOrder.Count);
		foreach (TabletopObject obj in _drawOrder)
			list.Add(obj.CaptureState());
		return list;
	}

	// ------------------------------------------------------------------ 堆叠

	/// <summary>
	/// 把一组物件直接并成一堆（不经过拖放）。
	/// 演示内容、以及将来"把牌库洗好摆成一叠"都会用到。
	/// </summary>
	public void GroupIntoPile(IReadOnlyList<TabletopObject> members)
	{
		if (members.Count < 2)
			return;

		TabletopObject target = members[0];
		var rest = new List<TabletopObject>(members.Count - 1);
		for (int i = 1; i < members.Count; i++)
			rest.Add(members[i]);

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
			pile = new Pile(_nextPileId++)
			{
				Anchor = target.Position,
			};
			_piles[pile.Id] = pile;
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

	private static void ResetPileFields(TabletopObject obj)
	{
		obj.PileId = 0;
		obj.PileIndex = 0;
		obj.PileCount = 0;
		obj.Visible = true;
		obj.QueueRedraw();
	}

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

	private void OnContextMenuRequested(Vector2 worldPos, Vector2 screenPos)
	{
		if (_menu is null)
			return;

		TabletopObject? target = PickTopmostExcluding(worldPos, null);

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

		_menu.Clear();
		_menu.AddItem("翻面 (F)", (int)MenuId.Flip);
		_menu.AddItem("顺时针 90°", (int)MenuId.RotateCw);
		_menu.AddItem("逆时针 90°", (int)MenuId.RotateCcw);
		_menu.AddItem("重置旋转", (int)MenuId.ResetRotation);
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
	}

	private void OnMenuItemPressed(long id)
	{
		int menuId = (int)id;

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
				foreach (TabletopObject obj in _selection.ToArray())
					DetachFromPile(obj, keepPosition: false);
				ApplyDrawOrder();
				break;
			case MenuId.DissolvePile:
				foreach (TabletopObject obj in _selection.ToArray())
					DissolvePile(obj);
				break;
			case MenuId.RollDice:
				foreach (TabletopObject obj in _selection)
				{
					if (obj is DiceObject d)
						d.Roll();
				}
				break;
		}
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

	public override void _UnhandledKeyInput(InputEvent @event)
	{
		if (@event is not InputEventKey key || !key.Pressed || key.Echo)
			return;

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
				if (key.IsActionPressed("tt_flip"))
					FlipObjects(targets);
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
