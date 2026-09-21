using System.Collections.Generic;
using Godot;
using TabletopSimulator.Data;

namespace TabletopSimulator.Core.Objects;

/// <summary>
/// 一块区域：矩形范围 + 一组有归属的物件。
///
/// 分工（和 <see cref="Pile"/> 的「纯逻辑」精神一致，但区域<b>要</b>画自己）：
/// <list type="bullet">
/// <item>区域<b>管</b>：矩形范围、成员列表、成员的 <c>ZoneId</c>、按 <see cref="ZoneSortMode"/> 排版。</item>
/// <item>区域<b>不管</b>：容量校验、朝向策略、抽牌、洗牌、与自由堆的互斥 —— 那些是
///   <c>ZoneManager</c> 的事（它们要跨区域、要动别的节点）。</item>
/// </list>
///
/// <b>成员不是区域的子节点</b>：它们留在 <c>Objects</c> 容器下，绘制次序由
/// <c>ObjectManager._drawOrder</c> 统一管。理由和 M2「堆不新增节点」相同 ——
/// 否则「牌库整体压在桌上所有物件之上」这种层级污染会破坏全局绘制次序。
/// 代价是区域自己的层级是固定的：永远在桌面之上、物件之下（场景里
/// <c>Zones</c> 节点 <c>z_index = -500</c>）。
/// </summary>
public partial class Zone : Node2D
{
	/// <summary>上一次洗牌用的随机种子。存下来是为了让"这把为什么是这个顺序"可复现。</summary>
	private int _lastShuffleSeed;

	/// <summary>
	/// 区域定义（纯数据）。
	/// 走 <see cref="Configure"/> 注入而不是构造函数 —— 和 <c>DiceObject.Configure</c> /
	/// <c>CardObject.SetDefinition</c> 保持一致：Godot 的节点子类保持可无参构造。
	/// </summary>
	public ZoneDefinition Definition { get; private set; } = new();

	/// <summary>成员的 <c>ZoneId</c> 指向它。</summary>
	public string Id => Definition.Id;

	/// <summary>显示名（画在矩形上）。<b>不叫 <c>Name</c></b>：那是 <see cref="Node.Name"/>，
	/// 类型是 <c>StringName</c>，而且 Godot 的节点名不允许含 <c>.</c>，与区域 id 的命名风格冲突。</summary>
	public string DisplayName => Definition.Name;

	public ZoneKind Kind => Definition.Kind;

	/// <summary>成员，<b>从底到顶</b>（顺序对 <see cref="ZoneSortMode.Stack"/> 有实际意义）。</summary>
	public List<TabletopObject> Members { get; } = new();

	public int Count => Members.Count;

	/// <summary>叠放的锚点 —— 矩形中心。</summary>
	public Vector2 StackAnchor => Definition.Center;

	/// <summary>上一次洗牌的种子。</summary>
	public int LastShuffleSeed => _lastShuffleSeed;

	/// <summary>容量是否已满（<c>MaxCards == 0</c> 表示无上限，永不满）。</summary>
	public bool IsFull => Definition.MaxCards > 0 && Members.Count >= Definition.MaxCards;

	/// <summary>是否还能接受新成员：启用中且没满。</summary>
	public bool AcceptsMembers => Definition.Enabled && !IsFull;

	/// <summary>顶牌（<see cref="ZoneSortMode.Stack"/> 下即"最上面那张"）。</summary>
	public TabletopObject? Top => Members.Count > 0 ? Members[^1] : null;

	public TabletopObject? Bottom => Members.Count > 0 ? Members[0] : null;

	[Signal] public delegate void MembersChangedEventHandler(int count);

	/// <summary>注入定义。必须在 <c>AddChild</c> 之前调用。</summary>
	public void Configure(ZoneDefinition definition)
	{
		Definition = definition;

		// 节点名不能用区域 id 原样：Godot 的节点名禁止 `.` `/` `:` `@` `%` `"`，
		// 而区域 id 的命名风格是 `demo.zone.deck`。清一遍，只为在远程场景树里好认。
		Name = string.IsNullOrWhiteSpace(definition.Id)
			? "Zone"
			: definition.Id.Replace('.', '_').Replace('/', '_').Replace(':', '_');

		QueueRedraw();
	}

	/// <summary>
	/// 就地换一份定义（撤销 / 读档写回时用）。与 <see cref="Configure"/> 的区别：
	/// 后者是"建区"，必须在 <c>AddChild</c> 之前；这个是"把已有区域的定义改成快照里那份"。
	///
	/// <b>为什么必须有它：</b>区域定义是引用类型，而快照存的是 <c>Clone()</c> ——
	/// 两者永远不会引用相同。若写回时只对 id、不换定义，那些
	/// <b>只改区域定义</b>的动作（锁定 / 排版 / 盖放策略 / 容量）就撤销不掉：
	/// 牌序与成员都正确恢复，唯独那一项设定还留在改过的状态上。
	///
	/// 界面上它会表现成"撤销了，但牌库还是锁定状态"，而报告里其它断言全绿。
	/// </summary>
	public void ApplyDefinition(ZoneDefinition definition)
	{
		Definition = definition;
		QueueRedraw();
	}

	public override void _Ready()
	{
		Position = Vector2.Zero;   // 矩形本身就是世界坐标，节点不再偏移
		ZIndex = 0;                // 层级由父节点 Zones 统一决定，区域之间不再分高低
		QueueRedraw();
	}

	/// <summary>世界坐标是否落在区域内。</summary>
	public bool ContainsWorldPoint(Vector2 world) => Definition.Rect.HasPoint(world);

	public bool Contains(TabletopObject obj) => Members.Contains(obj);

	// ------------------------------------------------------------------ 成员变更
	//
	// 注意：这两个方法是**唯一**允许改动 Members 的地方。
	// 它们同时维护 obj.ZoneId —— 这是「成员关系唯一真相」的落点。

	/// <summary>加入成员（追加到顶）。调用方负责先做容量/朝向/自由堆的处理。</summary>
	public void AddMember(TabletopObject obj)
	{
		if (Members.Contains(obj))
			return;

		Members.Add(obj);
		obj.ZoneId = Id;
		EmitSignal(SignalName.MembersChanged, Members.Count);
	}

	/// <summary>移出成员。返回是否真的移掉了。</summary>
	public bool RemoveMember(TabletopObject obj)
	{
		if (!Members.Remove(obj))
			return false;

		obj.ZoneId = "";

		// 一并清掉叠放位置与可见性 —— 漏掉这一步，卡片会带着上一站的张数徽章
		// 和"不可见"状态离开（见 TabletopObject.ClearStackVisual 的说明）。
		obj.ClearStackVisual();

		EmitSignal(SignalName.MembersChanged, Members.Count);
		return true;
	}

	/// <summary>
	/// 清空成员。
	/// 每个成员都要走一遍"离开区域"的清理 —— 只把列表清掉的话，
	/// 它们会带着 <c>ZoneId</c> 与叠放残留留在桌面上（换存档时就是一批孤儿声明）。
	/// </summary>
	public void ClearMembers()
	{
		foreach (TabletopObject obj in Members)
		{
			obj.ZoneId = "";
			obj.ClearStackVisual();
		}

		Members.Clear();
		EmitSignal(SignalName.MembersChanged, 0);
	}

	// ------------------------------------------------------------------ 排版

	/// <summary>
	/// 成员次序变化后的回调，由 <c>ZoneManager</c> 接到 <c>ObjectManager</c> 的绘制次序上。
	///
	/// 为什么需要它：画面上谁压谁完全由 <c>ObjectManager._drawOrder</c> 决定，
	/// 而"谁是顶牌"由 <c>Members</c> 的次序决定。两者一旦不同步，
	/// <b>你看到的那张顶牌和程序认为的顶牌就不是同一张</b> ——
	/// 双击抽牌会抽走一张不在顶上的牌，而翻转看起来像没生效。
	///
	/// 挂在 <see cref="ApplyLayout"/> 上是刻意的：那是区域一切次序变化的必经之路，
	/// 逐个改动点去补调迟早会漏掉一处（洗牌那条路就是这么漏的）。
	/// </summary>
	public System.Action<Zone>? OrderChanged { get; set; }

	/// <summary>
	/// 按 <see cref="ZoneDefinition.SortMode"/> 重排所有成员。
	///
	/// <b>每次成员或排版模式变化后都必须调它</b> —— 否则会出现
	/// 「数据说在牌库里、画面上还摊在桌上」这种只对了一半的状态。
	/// </summary>
	public void ApplyLayout()
	{
		switch (Definition.SortMode)
		{
			case ZoneSortMode.Stack:
				ApplyStack();
				break;

			case ZoneSortMode.Row:
				ApplyFlow(fan: false);
				break;

			case ZoneSortMode.Fan:
				ApplyFlow(fan: true);
				break;

			default:
				ApplyFree();
				break;
		}

		QueueRedraw();
		OrderChanged?.Invoke(this);
	}

	/// <summary>
	/// 叠放：位置阶梯错开、只在最上面几张可见。
	///
	/// <b>刻意不复用 <see cref="Pile"/> 对象</b>，只复用它的两个产物：
	/// <list type="bullet">
	/// <item><see cref="Pile.OffsetFor"/> 的阶梯公式</item>
	/// <item><see cref="TabletopObject.DrawPileBadge"/> 读的 <c>PileIndex</c> / <c>PileCount</c></item>
	/// </list>
	/// 原因是 <see cref="Pile"/> 的生命周期和区域打架：它在成员只剩 1 个时会自行解散、
	/// 它的 <c>MergeInto</c> 会把区域成员并进桌面上的自由堆、
	/// 它的 <c>ResetPileFields</c> 会把"只画最上面 3 张"改回全可见。
	/// 所以约定：<b>区域成员恒有 <c>PileId == 0</c></b>（见 <c>ZoneManager</c> 的互斥规则）。
	/// </summary>
	private void ApplyStack()
	{
		int count = Members.Count;

		for (int i = 0; i < count; i++)
		{
			TabletopObject m = Members[i];

			// 区域叠用 PileIndex/PileCount 表达位置，PileId 恒为 0（与自由堆互斥）
			m.PileId = 0;
			m.Position = StackAnchor + Pile.OffsetFor(i);
			m.RotationDeg = 0f;
			m.PileIndex = i;
			m.PileCount = count;

			// 只画最上面几张 —— 一叠 60 张牌没必要全画（和自由堆同一套常量）
			m.Visible = i >= count - GameConfig.PileVisibleDepth;
			m.QueueRedraw();
		}
	}

	/// <summary>自由摆放：不动位置与朝向，只把它标成区域成员，并清掉任何叠放残留。</summary>
	private void ApplyFree()
	{
		foreach (TabletopObject m in Members)
			m.ClearStackVisual();
	}

	/// <summary>
	/// 横排 / 扇形：从左上内边距开始逐张摆放，超出宽度自动换行。
	///
	/// 换行用<b>逐张累加当前行最高</b>的方式，而不是"卡牌高度 × 行号" ——
	/// 一个区域里可能混着卡牌（300×420）、Token（150）、骰子（120），
	/// 用统一行高会让小物件之间出现莫名其妙的空隙。
	/// </summary>
	private void ApplyFlow(bool fan)
	{
		Rect2 rect = Definition.Rect;
		float pad = GameConfig.ZonePadding;
		float left = rect.Position.X + pad;
		float top = rect.Position.Y + pad;
		float right = rect.End.X - pad;
		float gap = fan ? GameConfig.ZoneFanGap : GameConfig.ZoneRowGap;

		int count = Members.Count;
		float cursorX = left;
		float cursorY = top;
		float rowHeight = 0f;

		for (int i = 0; i < count; i++)
		{
			TabletopObject m = Members[i];
			Vector2 size = m.Size;

			// 横排/扇形没有"叠放位置"这回事，所以整组清干净：
			// 既保证不画张数徽章，也保证从牌库深处抽出来的牌一定可见。
			// 这里刻意不清 PileCount 会留下一个很难发现的隐患 ——
			// 徽章条件是「PileIndex == PileCount - 1」，序号恰好为 0 时碰巧不画，
			// 于是 bug 在某个排版下"看起来正常"，换个排版就冒出来。
			m.ClearStackVisual();

			// 注意"cursorX > left"这个前置条件：单张比整行还宽的物件不能死循环换行
			if (cursorX > left && cursorX + size.X > right)
			{
				cursorX = left;
				cursorY += rowHeight + GameConfig.ZoneRowLineGap;
				rowHeight = 0f;
			}

			m.Position = new Vector2(cursorX + (size.X * 0.5f), cursorY + (size.Y * 0.5f));

			if (fan)
			{
				// 以中间那张为 0°，向两侧对称张开：-1 → 左端，+1 → 右端
				float t = count <= 1 ? 0f : ((i / (float)(count - 1)) * 2f) - 1f;
				m.RotationDeg = t * GameConfig.ZoneFanMaxDegrees;
			}
			else
			{
				m.RotationDeg = 0f;
			}

			cursorX += (fan ? gap : size.X + gap);
			rowHeight = Mathf.Max(rowHeight, size.Y);
		}
	}

	// ------------------------------------------------------------------ 洗牌

	/// <summary>
	/// 主动广播一次成员数变化。
	///
	/// 撤销 / 读档是<b>直接改 <c>Members</c> 列表</b>的（走 <c>AddMember</c> 与 <c>Clear</c>），
	/// 而 HUD 的区域计数挂在 <see cref="MembersChanged"/> 上 —— 不补这一次广播，
	/// 顶栏就会一直显示撤销之前的张数。症状是"数据对了、HUD 没跟上"。
	/// </summary>
	public void EmitCountsChanged() => EmitSignal(SignalName.MembersChanged, Members.Count);

	/// <summary>
	/// 洗牌：把成员随机重排（Fisher-Yates）。
	///
	/// 记下种子是为了复现 —— 和 M2 骰子存 <c>DiceSeed</c> 同一个理由：
	/// 试玩时出现"这把牌怎么这么离谱"的时候，能拿着种子重现它。
	/// </summary>
	/// <param name="seed">0 = 取当前时间。非 0 则用它，结果可复现。</param>
	public void Shuffle(int seed = 0)
	{
		_lastShuffleSeed = seed != 0 ? seed : (int)(Time.GetTicksUsec() & 0x7FFFFFFF);
		var rng = new System.Random(_lastShuffleSeed);

		for (int i = Members.Count - 1; i > 0; i--)
		{
			int j = rng.Next(i + 1);
			(Members[i], Members[j]) = (Members[j], Members[i]);
		}

		ApplyLayout();
	}

	/// <summary>
	/// 整摞翻过来 —— 物理意义上的"把这一摞拿起来翻个面"。
	///
	/// 两件事同时发生，缺一不可：
	/// <list type="number">
	/// <item><b>成员次序反转</b>：底牌变顶牌。</item>
	/// <item><b>每张牌的正反面翻转</b>：整摞转了 180°，朝向自然跟着变。</item>
	/// </list>
	///
	/// 为什么不逐张翻面了事：在一摞盖着的牌上，逐张翻面会得到"同一张顶牌翻了面"，
	/// 用户看到的是"牌堆没动、只是顶上那张变得能看见了"—— 而他要的是
	/// <b>整个牌堆反过来</b>。只反转次序也不行：盖着的一摞反转之后画面上毫无变化，
	/// 看起来像没反应。
	/// </summary>
	public void FlipOver()
	{
		if (Members.Count == 0)
			return;

		Members.Reverse();

		foreach (TabletopObject m in Members)
			m.IsFaceDown = !m.IsFaceDown;

		ApplyLayout();
	}

	// ------------------------------------------------------------------ 绘制

	public override void _Draw()
	{
		Rect2 rect = Definition.Rect;

		DrawRect(rect, Definition.Tint);

		// 边框状态直接表达"这块区域现在能不能用"：
		// 正常 = 类型色；空 / 锁定 = 淡；满 = 警示色。
		Color border = IsFull || !Definition.Enabled
			? GameConfig.ZoneBlockedBorder
			: Definition.BorderColor;

		float alpha = (Count == 0 || !Definition.Enabled) && !IsFull
			? GameConfig.ZoneIdleBorderAlpha
			: border.A;

		DrawRect(rect, new Color(border.R, border.G, border.B, alpha), false, GameConfig.ZoneBorderWidth);

		DrawTitle(rect);
	}

	private void DrawTitle(Rect2 rect)
	{
		string text = Count == 0
			? $"{DisplayName}（空）"
			: $"{DisplayName} {Count}";

		if (!Definition.Enabled)
			text += "  已锁定";
		else if (IsFull)
			text += "  已满";

		Font font = Fonts.Ui;
		int size = GameConfig.ZoneTitleFontSize;

		// DrawString 的 pos 是**基线**不是左上角（M2 踩过），所以要自己加上 ascent
		float ascent = font.GetAscent(size);
		var pos = new Vector2(
			rect.Position.X + GameConfig.ZonePadding,
			rect.Position.Y + GameConfig.ZonePadding + ascent);

		DrawString(font, pos, text, HorizontalAlignment.Left, -1f, size, GameConfig.ZoneTitleColor);
	}
}
