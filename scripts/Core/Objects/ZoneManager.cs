using System.Collections.Generic;
using Godot;
using TabletopSimulator.Data;

namespace TabletopSimulator.Core.Objects;

/// <summary>
/// 区域管理器：所有区域的持有者，也是<b>唯一</b>的区域交互入口。
///
/// 它是 <see cref="ObjectManager"/> 的区域对偶物，两者平级、职责互补：
/// <list type="bullet">
/// <item><see cref="ObjectManager"/> 管"东西是什么、在哪、怎么被拖"</item>
/// <item><see cref="ZoneManager"/> 管"东西属于谁、进了区域之后听谁的"</item>
/// </list>
///
/// 依赖是<b>单向可空</b>的：物件系统只依赖 <see cref="IZoneInteraction"/> 接口，
/// 不依赖本类的具体类型。没接区域时区域功能整体关闭，物件系统照常工作 ——
/// 这和 M1 预留 <see cref="IWorldPicker"/> / <see cref="IWheelHandler"/> 是同一个套路。
/// </summary>
public partial class ZoneManager : Node2D, IZoneInteraction
{
	/// <summary>区域右键菜单的条目 id。抽牌用连续区间，其余离散编号，两者不重叠。</summary>
	private enum MenuId
	{
		/// <summary>抽牌选项起点：<c>DrawOne + i</c> 对应 <see cref="DrawChoices"/>[i]。</summary>
		DrawBase = 1,

		Shuffle = 10,
		PullTop,
		Spread,
		LayoutRow,
		LayoutFan,
		FlipUp,
		FlipDown,
		SelectAllInZone,
		ToggleEnabled,

		/// <summary>标题行（禁用状态的纯展示项）。</summary>
		Header = 900,
	}

	private static readonly int[] DrawChoices = { 1, 3, 5 };

	/// <summary>区域 id → 区域节点。id 来自 <see cref="ZoneDefinition.Id"/>。</summary>
	private readonly Dictionary<string, Zone> _zones = new();

	/// <summary>添加顺序。命中重叠区域时取<b>最后添加</b>的（视觉上画在上面的那个）。</summary>
	private readonly List<Zone> _order = new();

	private ObjectManager? _objects;
	private Hud? _hud;
	private PopupMenu? _menu;

	/// <summary>菜单是针对哪个区域弹的 —— 菜单项回调只拿得到 id，拿不到区域。</summary>
	private Zone? _activeZone;

	/// <summary>鼠标当前悬停的区域。`S` 洗牌 / `D` 抽牌的落点 —— 和物件系统"悬停即为目标"同一条规矩。</summary>
	private Zone? _hovered;

	/// <summary>
	/// 本次拖拽的"老家"：物件 → 它拖起来之前所属的区域 id。
	///
	/// 为什么需要它：区域成员在拖拽<b>起点</b>就脱离区域了（否则拖到桌面会留下
	/// 「还在牌库里、位置却在桌面」的半截状态）。但如果最后落点被拒
	/// （目标满了 / 锁了），物件就既不在原区域、也不在新区域，变成一件散件掉在桌上 ——
	/// 从用户角度看就是"我手滑了一下，牌没了归属"。
	/// 记下老家，被拒时原样送回去。
	/// </summary>
	private readonly Dictionary<TabletopObject, string> _dragOrigin = new();

	public IReadOnlyList<Zone> AllZones => _order;

	public int ZoneCount => _order.Count;

	/// <summary>区域右键菜单。<b>必须和物件菜单一样在探针结束时 <c>Hide()</c></b> ——
	/// <c>PopupMenu</c> 是 <c>Window</c>，会抢走后续合成鼠标事件（M2 为此栽过一次）。</summary>
	public PopupMenu? ContextMenu => _menu;

	/// <summary>当前悬停的区域（自检要用）。</summary>
	public Zone? HoveredZone => _hovered;

	[Signal] public delegate void ZonesChangedEventHandler();
	[Signal] public delegate void ZoneCountsChangedEventHandler(string summary);

	// ------------------------------------------------------------------ 装配

	public void Bind(ObjectManager objects, Hud hud, CanvasLayer hudLayer)
	{
		_objects = objects;
		_hud = hud;

		_menu = new PopupMenu { Name = "ZoneMenu" };
		_menu.IdPressed += OnMenuItemPressed;
		hudLayer.AddChild(_menu);
	}

	/// <summary>加一块区域。返回建好的节点。</summary>
	public Zone AddZone(ZoneDefinition definition)
	{
		Zone zone = new();
		zone.Configure(definition);
		AddChild(zone);

		_zones[zone.Id] = zone;
		_order.Add(zone);

		// 成员数变化要广播 —— HUD 的区域计数靠它
		zone.MembersChanged += _ => EmitZoneCounts();

		// 叠放区域的成员次序一变，绘制次序就得跟上，
		// 否则"看到的那张顶牌"和 Members[^1] 不是同一张
		zone.OrderChanged = OnZoneOrderChanged;

		EmitSignal(SignalName.ZonesChanged);
		EmitZoneCounts();
		return zone;
	}

	/// <summary>
	/// 叠放区域重排之后，把绘制次序同步过去。
	///
	/// 不这么做的话，洗过牌 / 翻转过的牌堆会出现「你看到的顶牌不是程序的顶牌」——
	/// 双击抽牌抽走一张不在顶上的牌，而画面上完全看不出异样。
	/// </summary>
	private void OnZoneOrderChanged(Zone zone)
	{
		if (zone.Definition.SortMode == ZoneSortMode.Stack)
			_objects?.SyncDrawOrderToStack(zone.Members);
	}

	public Zone? Find(string id) => _zones.TryGetValue(id, out Zone? z) ? z : null;

	/// <summary>命中测试：该世界点下的区域；没有则 <c>null</c>。</summary>
	public Zone? ZoneAtWorld(Vector2 worldPos)
	{
		for (int i = _order.Count - 1; i >= 0; i--)
		{
			if (_order[i].ContainsWorldPoint(worldPos))
				return _order[i];
		}

		return null;
	}

	/// <summary>清空所有区域及其成员归属（M4 换存档时用）。</summary>
	public void ClearAll()
	{
		foreach (Zone z in _order)
		{
			z.ClearMembers();
			z.QueueFree();
		}

		_zones.Clear();
		_order.Clear();
		_hovered = null;
		_activeZone = null;

		EmitSignal(SignalName.ZonesChanged);
		EmitZoneCounts();
	}

	/// <summary>按类型找第一个区域（例：双击牌库时找"手牌"在哪）。</summary>
	public Zone? FirstOfKind(ZoneKind kind)
	{
		foreach (Zone z in _order)
		{
			if (z.Kind == kind)
				return z;
		}

		return null;
	}

	public string CountSummary()
	{
		if (_order.Count == 0)
			return "区域 0";

		var parts = new List<string>(_order.Count);
		foreach (Zone z in _order)
			parts.Add($"{z.DisplayName} {z.Count}");

		return string.Join(" · ", parts);
	}

	private void EmitZoneCounts()
	{
		string summary = CountSummary();
		EmitSignal(SignalName.ZoneCountsChanged, summary);
		_hud?.SetZoneSummary(summary);
	}

	// ------------------------------------------------------------------ 成员进出
	//
	// 这里是「ZoneId 是唯一真相」的落点。所有跨对象的簿记集中在这几个方法里，
	// 别处不许直接改 obj.ZoneId。

	/// <summary>最近一次 <see cref="MoveInto"/> 被拒的原因（空 = 没被拒）。给调用方拼提示语用。</summary>
	public string LastRejectReason { get; private set; } = "";

	/// <summary>
	/// 把一批物件放进区域。
	/// 处理四件事：容量校验、离开原区域/自由堆、朝向策略、重排。
	/// </summary>
	/// <returns>是否接受（被拒时物件原地不动）。</returns>
	public bool MoveInto(Zone zone, IReadOnlyList<TabletopObject> objects)
	{
		LastRejectReason = "";

		if (!zone.Definition.Enabled)
		{
			LastRejectReason = "区域已锁定";
			_hud?.Toast($"{zone.DisplayName} 已锁定");
			return false;
		}

		// 已经在里面的不计入容量（否则"把牌挪一挪"会被自己挤掉）
		List<TabletopObject> incoming = PickIncoming(zone, objects);

		if (incoming.Count == 0)
		{
			zone.ApplyLayout();
			return true;
		}

		// 超限时整次拒绝，而不是"塞进去一部分"——
		// 后者会让用户以为拖拽成功了一半，且物件数对不上。
		if (zone.Definition.MaxCards > 0 && zone.Count + incoming.Count > zone.Definition.MaxCards)
		{
			LastRejectReason = $"已满 {zone.Count}/{zone.Definition.MaxCards}，放不下 {incoming.Count} 张";
			_hud?.Toast($"{zone.DisplayName} {LastRejectReason}");
			return false;
		}

		foreach (TabletopObject obj in incoming)
		{
			LeaveCurrentZone(obj);

			// 从自由堆里摘出来：PileId 与 ZoneId 互斥
			_objects?.ReleaseFromPile(obj);

			obj.IsFaceDown = zone.Definition.ResolveFaceDown(obj.IsFaceDown);
			obj.Visible = true;
			zone.AddMember(obj);
		}

		zone.ApplyLayout();
		return true;
	}

	private static List<TabletopObject> PickIncoming(Zone zone, IReadOnlyList<TabletopObject> objects)
	{
		var incoming = new List<TabletopObject>(objects.Count);
		foreach (TabletopObject obj in objects)
		{
			if (obj.ZoneId != zone.Id)
				incoming.Add(obj);
		}

		return incoming;
	}

	/// <summary>把物件移出它当前所在的区域（不改位置，调用方决定去哪）。</summary>
	public void LeaveCurrentZone(TabletopObject obj) => LeaveZones(new[] { obj });

	/// <summary>
	/// 批量移出区域：<b>先把所有成员摘掉，最后对受影响的区域排版一次</b>。
	///
	/// 为什么不能"逐个移除、逐个排版"——这里踩过一个很难看的 bug：
	/// 横排区域的排版总是从<b>槽位 0</b> 开始铺，于是
	/// <list type="bullet">
	/// <item>移除 H0 后重排：H1 被挪到槽位 0，H2→槽位 1 …… H0 留在槽位 0</item>
	/// <item>接着移除 H1 —— <b>它已经在槽位 0 了</b>，于是它也从槽位 0 离开</item>
	/// <item>以此类推，<b>每一张都是在槽位 0 被移除的</b></item>
	/// </list>
	/// 结果：从手牌里一次拖走 10 张，它们会先全部塌到最左边那一格，
	/// 再一起跟着鼠标走。用户看到的是"牌莫名其妙叠成一坨、还跟不上光标"，
	/// 而这坨假象又恰好掩盖了"它们并没有真的成堆、所以没有张数徽章"。
	/// </summary>
	public void LeaveZones(IReadOnlyList<TabletopObject> objects)
	{
		var touched = new HashSet<Zone>();

		foreach (TabletopObject obj in objects)
		{
			if (string.IsNullOrEmpty(obj.ZoneId))
				continue;

			if (_zones.TryGetValue(obj.ZoneId, out Zone? current))
			{
				// 只摘掉，先不排版 —— 排一次就够，排在最后
				current.RemoveMember(obj);
				touched.Add(current);
			}
			else
			{
				// 区域没了但物件还自称在里头 —— 修掉，别留半截状态。
				// 注意也要清叠放残留：只清 ZoneId 的话，它会带着上一站的张数徽章留在桌上
				// （和"从牌库拖出来的牌仍显示张数"是同一个根因）。
				obj.ClearStackVisual();
			}
		}

		foreach (Zone zone in touched)
			zone.ApplyLayout();
	}

	/// <summary>
	/// 拖拽<b>一开始</b>就让成员离开区域。
	///
	/// 为什么在起点而不是落点：落点可能不在任何区域里（拖到桌面），
	/// 那时如果还留着 <c>ZoneId</c>，就会出现「它还在牌库里，但位置在桌面」的
	/// 半截状态 —— 牌库的张数会算错，而且下次重排会把它吸回去。
	/// M2 的 <c>DetachFromPile</c> 也是在拖拽起点调的，同一个道理。
	/// </summary>
	public void BeginDrag(IReadOnlyList<TabletopObject> dragging)
	{
		_dragOrigin.Clear();

		bool any = false;
		foreach (TabletopObject obj in dragging)
		{
			if (string.IsNullOrEmpty(obj.ZoneId))
				continue;

			_dragOrigin[obj] = obj.ZoneId;
			any = true;
		}

		if (!any)
			return;

		// 批量摘除（内部只对源区域排版一次）—— 逐个摘会让被拖的牌层层塌到槽位 0，
		// 详见 LeaveZones 的说明。
		LeaveZones(dragging);
		EmitZoneCounts();
	}

	/// <summary>落点被拒时把物件送回拖起来之前所在的区域。</summary>
	private void RestoreDragOrigins(IReadOnlyList<TabletopObject> dropped)
	{
		foreach (TabletopObject obj in dropped)
		{
			if (!_dragOrigin.TryGetValue(obj, out string? origin) || string.IsNullOrEmpty(origin))
				continue;

			if (_zones.TryGetValue(origin, out Zone? home))
				MoveInto(home, new[] { obj });
		}

		_dragOrigin.Clear();
		EmitZoneCounts();
	}

	/// <summary>物件即将被删除 —— 区域先松手，别留野引用。</summary>
	public void ForgetObjects(IReadOnlyList<TabletopObject> objects)
	{
		foreach (TabletopObject obj in objects)
			_dragOrigin.Remove(obj);

		LeaveZones(objects);
		EmitZoneCounts();
	}

	// ------------------------------------------------------------------ 抽牌 / 洗牌

	/// <summary>洗牌。仅对 <see cref="ZoneSortMode.Stack"/> 有意义（其它排版下顺序不影响观感）。</summary>
	public void ShuffleZone(Zone zone)
	{
		if (zone.Count < 2)
		{
			_hud?.Toast($"{zone.DisplayName} 不足 2 张，无需洗牌");
			return;
		}

		zone.Shuffle();
		_hud?.Toast($"{zone.DisplayName} 已洗牌（{zone.Count} 张，种子 {zone.LastShuffleSeed}）");
	}

	/// <summary>
	/// 从区域抽牌。抽出来的牌进 <see cref="ZoneDefinition.DrawTargetId"/> 指定的区域；
	/// 该区域不存在或未配置时，落到源区域右侧的桌面空位上。
	/// </summary>
	/// <returns>实际抽出的张数。</returns>
	public int DrawFrom(Zone zone, int count)
	{
		if (!zone.Definition.Enabled)
		{
			_hud?.Toast($"{zone.DisplayName} 已锁定");
			return 0;
		}

		if (zone.Count == 0)
		{
			_hud?.Toast($"{zone.DisplayName} 是空的");
			return 0;
		}

		Zone? target = ResolveDrawTarget(zone);
		int drawn = 0;
		bool blocked = false;

		for (int i = 0; i < count; i++)
		{
			TabletopObject? top = zone.Top;
			if (top is null)
				break;

			if (target is not null)
			{
				if (!target.AcceptsMembers)
				{
					blocked = true;
					break;
				}

				zone.RemoveMember(top);
				MoveInto(target, new[] { top });
			}
			else
			{
				zone.RemoveMember(top);
				PlaceOnTable(zone, top, drawn);
			}

			drawn++;
		}

		zone.ApplyLayout();
		target?.ApplyLayout();
		EmitZoneCounts();

		string dest = target?.DisplayName ?? "桌面";

		if (blocked)
			_hud?.Toast($"{dest} 已满，只抽了 {drawn} 张");
		else if (drawn > 0)
			_hud?.Toast(drawn == 1 ? $"抽 1 张到{dest}" : $"抽了 {drawn} 张到{dest}");

		return drawn;
	}

	/// <summary>解析抽牌目标。<c>DrawTargetId</c> 没配或指向不存在的区域 → <c>null</c>（抽到桌面）。</summary>
	private Zone? ResolveDrawTarget(Zone source)
	{
		if (string.IsNullOrWhiteSpace(source.Definition.DrawTargetId))
			return null;

		if (_zones.TryGetValue(source.Definition.DrawTargetId, out Zone? target))
			return target;

		// 配了目标但目标不存在：明确说出来，不要静默降级 ——
		// 否则用户会以为"牌怎么抽到桌上了"，却查不出是 id 写错。
		_hud?.Toast($"找不到抽牌目标「{source.Definition.DrawTargetId}」，改为抽到桌面");
		return null;
	}

	/// <summary>没有目标区域时，把牌依次摆在源区域右侧，斜向错开免得完全重叠。</summary>
	private static void PlaceOnTable(Zone source, TabletopObject obj, int index)
	{
		Rect2 rect = source.Definition.Rect;
		obj.Position = new Vector2(
			rect.End.X + 80f + (index * 46f),
			rect.Position.Y + 60f + (index * 46f));
		obj.RotationDeg = 0f;
		obj.Visible = true;
		obj.QueueRedraw();
	}

	// ------------------------------------------------------------------ IZoneInteraction

	/// <summary>该物件所属的叠放区域；不属于、或该区域不是叠放排版时返回 <c>null</c>。</summary>
	public Zone? StackZoneOf(TabletopObject obj)
	{
		if (string.IsNullOrEmpty(obj.ZoneId))
			return null;

		if (!_zones.TryGetValue(obj.ZoneId, out Zone? zone))
			return null;

		return zone.Definition.SortMode == ZoneSortMode.Stack ? zone : null;
	}

	/// <summary>鼠标移动：维护"悬停的区域"。物件优先 —— 悬停在物件上时区域不算被指着。</summary>
	public void NotifyPointerMoved(Vector2 worldPos)
	{
		Zone? hit = ZoneAtWorld(worldPos);
		if (!ReferenceEquals(hit, _hovered))
			_hovered = hit;
	}

	/// <inheritdoc/>
	/// <remarks>
	/// <b>Stack 区域（牌库 / 弃牌堆）优先于物件接管右键。</b>
	/// 理由：一摞牌的正确操作单位是"这一摞"（洗牌 / 抽牌 / 全部翻开），
	/// 而不是"恰好被点到的那第 7 张牌"。别的排版类型（手牌 / 出牌区）
	/// 仍然物件优先 —— 那里每张牌都要能单独操作。
	/// </remarks>
	public bool TryHandleContextMenu(Vector2 worldPos, Vector2 screenPos, TabletopObject? hitObject)
	{
		if (_menu is null)
			return false;

		Zone? zone = ZoneAtWorld(worldPos);
		if (zone is null)
			return false;

		// 点到的物件属于别的区域 —— 让物件菜单处理，别抢
		if (hitObject is not null && hitObject.ZoneId != zone.Id)
			return false;

		// 点到的是本区域成员，但本区域不是叠放类 → 物件优先
		if (hitObject is not null && zone.Definition.SortMode != ZoneSortMode.Stack)
			return false;

		_activeZone = zone;
		BuildMenu(zone);
		ShowMenuAt(screenPos);
		return true;
	}

	/// <inheritdoc/>
	/// <remarks>
	/// <b>这里刻意让区域优先于物件</b>，和别处相反。原因很实际：
	/// 牌库最上面那 3 张牌正好压在自己的矩形中心，双击"牌库"时
	/// 命中的几乎总是那张顶牌而不是区域本身。若按"物件优先"，
	/// 抽牌这个最常用的操作就永远触发不了。
	/// 只有 <see cref="ZoneDefinition.DrawOnDoubleClick"/> 为真的区域会接管，所以不误伤。
	/// </remarks>
	public bool TryHandleDoubleClick(Vector2 worldPos)
	{
		Zone? zone = ZoneAtWorld(worldPos);
		if (zone is null || !zone.Definition.DrawOnDoubleClick)
			return false;

		if (!zone.Definition.Enabled)
		{
			_hud?.Toast($"{zone.DisplayName} 已锁定");
			return true;
		}

		DrawFrom(zone, Mathf.Max(zone.Definition.DrawCount, 1));
		return true;
	}

	/// <inheritdoc/>
	public bool TryHandleDrop(IReadOnlyList<TabletopObject> dropped, Vector2 worldPos)
	{
		Zone? zone = ZoneAtWorld(worldPos);
		if (zone is null)
		{
			// 落到区域之外：老家记录用不上了（物件本来就是要离开区域的）
			_dragOrigin.Clear();
			return false;
		}

		// 落在区域里就由区域定夺 —— 即使被拒（满了 / 锁了）也算"区域处理过了"，
		// 物件不该转头去走自由堆逻辑、意外和桌上的牌粘成一堆。
		if (!MoveInto(zone, dropped))
		{
			// 被拒 → 送回原处。否则用户会看到"牌掉了归属"，还以为自己拖错了。
			string reason = LastRejectReason;
			RestoreDragOrigins(dropped);
			_hud?.Toast($"没能放入 {zone.DisplayName}（{reason}），已退回原处");
		}
		else
		{
			_dragOrigin.Clear();
		}

		return true;
	}

	/// <inheritdoc/>
	public bool TryHandleKey(InputEventKey key)
	{
		if (_hovered is null)
			return false;

		if (key.IsActionPressed("tt_shuffle"))
		{
			ShuffleZone(_hovered);
			return true;
		}

		if (key.IsActionPressed("tt_draw") && _hovered.Definition.DrawOnDoubleClick)
		{
			DrawFrom(_hovered, Mathf.Max(_hovered.Definition.DrawCount, 1));
			return true;
		}

		return false;
	}

	/// <summary>收起区域菜单（探针收尾用 —— <c>PopupMenu</c> 是 <c>Window</c>，会抢合成事件）。</summary>
	public void HideMenu() => _menu?.Hide();

	// ------------------------------------------------------------------ 区域菜单

	private void BuildMenu(Zone zone)
	{
		if (_menu is null)
			return;

		_menu.Clear();
		_menu.AddItem($"{zone.DisplayName}（{zone.Count} 张）", (int)MenuId.Header);
		_menu.SetItemDisabled(0, true);
		_menu.AddSeparator();

		if (zone.Definition.DrawOnDoubleClick)
		{
			for (int i = 0; i < DrawChoices.Length; i++)
				_menu.AddItem($"抽 {DrawChoices[i]} 张", (int)MenuId.DrawBase + i);

			_menu.AddSeparator();
		}

		if (zone.Definition.SortMode == ZoneSortMode.Stack)
		{
			AddItem("洗牌", MenuId.Shuffle, zone.Count >= 2);
			AddItem("取出顶牌", MenuId.PullTop, zone.Count > 0);
			AddItem($"查看区域（摊开 {zone.Count} 张）", MenuId.Spread, zone.Count >= 2);
			_menu.AddSeparator();
		}

		if (zone.Kind == ZoneKind.Hand)
		{
			AddItem("排版：横排", MenuId.LayoutRow, true);
			AddItem("排版：扇形", MenuId.LayoutFan, true);
			_menu.AddSeparator();
		}

		AddItem("全部翻开", MenuId.FlipUp, zone.Count > 0);
		AddItem("全部盖放", MenuId.FlipDown, zone.Count > 0);
		AddItem("选中此区域全部", MenuId.SelectAllInZone, zone.Count > 0);
		_menu.AddSeparator();
		AddItem(zone.Definition.Enabled ? "锁定此区域" : "解锁此区域", MenuId.ToggleEnabled, true);
	}

	/// <summary>加一项。用 <c>AddItem</c> 返回的下标直接禁用，
	/// 不要事后用 <c>GetItemIndex</c> 去查 —— id 打错时会拿到 -1，静默失效。</summary>
	private void AddItem(string label, MenuId id, bool enabled)
	{
		if (_menu is null)
			return;

		int index = _menu.ItemCount;
		_menu.AddItem(label, (int)id);

		if (!enabled)
			_menu.SetItemDisabled(index, true);
	}

	private void OnMenuItemPressed(long id)
	{
		int menuId = (int)id;

		// 抽 N 张（1/3/5 共用一段区间）
		if (menuId >= (int)MenuId.DrawBase && menuId < (int)MenuId.DrawBase + DrawChoices.Length)
		{
			if (_activeZone is Zone drawZone)
				DrawFrom(drawZone, DrawChoices[menuId - (int)MenuId.DrawBase]);

			return;
		}

		if (_activeZone is not Zone zone)
			return;

		switch ((MenuId)menuId)
		{
			case MenuId.Shuffle:
				ShuffleZone(zone);
				break;

			case MenuId.PullTop:
				if (zone.Top is TabletopObject top)
				{
					zone.RemoveMember(top);
					zone.ApplyLayout();
					_objects?.SelectOnly(top);
					_hud?.Toast($"已取出 {zone.DisplayName} 的顶牌");
				}
				break;

			case MenuId.Spread:
				SpreadZone(zone);
				break;

			case MenuId.LayoutRow:
				SetSortMode(zone, ZoneSortMode.Row);
				break;

			case MenuId.LayoutFan:
				SetSortMode(zone, ZoneSortMode.Fan);
				break;

			case MenuId.FlipUp:
				SetFaceDown(zone, false);
				break;

			case MenuId.FlipDown:
				SetFaceDown(zone, true);
				break;

			case MenuId.SelectAllInZone:
				_objects?.SelectOnly(new List<TabletopObject>(zone.Members));
				break;

			case MenuId.ToggleEnabled:
				zone.Definition.Enabled = !zone.Definition.Enabled;
				zone.QueueRedraw();
				_hud?.Toast(zone.Definition.Enabled ? $"{zone.DisplayName} 已解锁" : $"{zone.DisplayName} 已锁定");
				break;
		}

		EmitZoneCounts();
	}

	private void ShowMenuAt(Vector2 screenPos)
	{
		if (_menu is null)
			return;

		// 先用内容最小尺寸估一次（此时还没 Popup，拿不到实际尺寸）
		Vector2 estimate = _menu.GetContentsMinimumSize();
		if (estimate.X <= 0f || estimate.Y <= 0f)
			estimate = new Vector2(200f, 220f);

		_menu.Position = (Vector2I)ClampToViewport(screenPos, estimate);
		_menu.Popup();

		// Popup 之后才有真实尺寸，用真实值再收一次（和 M2 物件菜单同一套做法）
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

	// ------------------------------------------------------------------ 区域命令

	/// <summary>摊开：把叠放区域里的牌斜向铺在桌面上（验玩法时最常用的"看看牌库里有什么"）。</summary>
	public void SpreadZone(Zone zone)
	{
		if (zone.Count == 0)
			return;

		// 摊到区域下方，避免盖住区域自己的边框与计数
		Vector2 start = new(zone.Definition.Rect.Position.X, zone.Definition.Rect.End.Y + 60f);
		var step = new Vector2(46f, 0f);
		var members = new List<TabletopObject>(zone.Members);

		foreach (TabletopObject m in members)
		{
			zone.RemoveMember(m);
			m.Visible = true;
			m.QueueRedraw();
		}

		for (int i = 0; i < members.Count; i++)
			members[i].Position = start + (step * i);

		zone.ApplyLayout();
		EmitZoneCounts();
		_hud?.Toast($"已摊开 {members.Count} 张（区域现在是空的）");
	}

	public void SetSortMode(Zone zone, ZoneSortMode mode)
	{
		zone.Definition.SortMode = mode;
		zone.ApplyLayout();
		_hud?.Toast($"{zone.DisplayName} 排版：{(mode == ZoneSortMode.Fan ? "扇形" : "横排")}");
	}

	public void SetFaceDown(Zone zone, bool faceDown)
	{
		foreach (TabletopObject m in zone.Members)
		{
			m.IsFaceDown = faceDown;
			m.QueueRedraw();
		}

		_hud?.Toast($"{zone.DisplayName}：{zone.Count} 张已{(faceDown ? "盖放" : "翻开")}");
	}
}
