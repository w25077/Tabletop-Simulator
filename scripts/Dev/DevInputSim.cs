using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using TabletopSimulator.Core;
using TabletopSimulator.Core.Objects;

namespace TabletopSimulator.Dev;

/// <summary>
/// 合成输入探针：把伪造的鼠标事件塞进 Godot 真实输入管线，验证「输入路由 → 相机」
/// 这一整条链路，而不只是相机自身的数学。
///
/// 为什么走 <c>Input.ParseInputEvent</c> 而不是 MCP 的输入模拟：
/// 事件是在游戏进程内部合成的，因此<b>不需要窗口处于前台</b>，
/// 可以在命令行里自动跑。MCP 的输入模拟要求窗口有焦点，做自动验收不可靠。
/// </summary>
internal static class DevInputSim
{
	internal static async Task Frame(Node host) =>
		await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);

	/// <summary>
	/// 取 <see cref="Board"/>（桌面）。
	///
	/// <b>从 <c>host</c> 的父节点取，不是从场景根取</b> —— 实测踩过一次：
	/// <c>host</c> 是 <c>DevCapture</c>，它是 <c>Main</c> 的<b>子节点</b>，
	/// 而 <c>Board</c> 是它的<b>兄弟</b>；从 <c>SceneTree.Root</c> 找 <c>"Board"</c>
	/// 只会命中 Main 自己，于是永远取不到（症状是选点又跑回了桌外，
	/// 而报告里只写着"选点变了"，看不出是这里取不到东西）。
	///
	/// 先试父节点，再退到场景根 —— 两种挂法都认，且拿到的都是同一个对象，
	/// 不存在第二份几何。
	/// </summary>
	internal static Board? FindBoard(Node host)
		=> host.GetParent()?.GetNodeOrNull<Board>("Board")
			?? host.GetTree()?.CurrentScene?.GetNodeOrNull<Board>("Board");

	internal static void PushButton(Vector2 pos, MouseButton button, bool pressed)
	{
		Input.ParseInputEvent(new InputEventMouseButton
		{
			ButtonIndex = button,
			Pressed = pressed,
			Position = pos,
			GlobalPosition = pos,
		});
	}

	internal static void PushWheel(Vector2 pos, bool up)
	{
		MouseButton button = up ? MouseButton.WheelUp : MouseButton.WheelDown;
		PushButton(pos, button, true);
		PushButton(pos, button, false);
	}

	internal static void PushMotion(Vector2 pos, Vector2 relative)
	{
		Input.ParseInputEvent(new InputEventMouseMotion
		{
			Position = pos,
			GlobalPosition = pos,
			Relative = relative,
		});
	}

	/// <summary>合成一次按键。只设 Keycode —— InputMap 里登记的也是 keycode，设 physical 反而可能不匹配。</summary>
	internal static void PushKey(Key key)
	{
		Input.ParseInputEvent(new InputEventKey { Keycode = key, Pressed = true });
		Input.ParseInputEvent(new InputEventKey { Keycode = key, Pressed = false });
	}

	/// <summary>
	/// 合成一次按键，<b>并等它真的被派发完</b>。
	///
	/// <b>为什么需要它（这条是用一轮排查换来的）：</b>
	/// <see cref="PushKey"/> 发的是"按下 + 松开"<b>两个</b>事件，而
	/// <c>Input.ParseInputEvent</c> 把它们排进队列，<b>一帧只派发一个</b>。
	/// 所以"发完就断言"看到的必然是第一帧之后的状态，而那个"松开"事件
	/// 会留在队列里，等到<b>后面某一帧</b>才派发 —— 落到那会儿正在跑的另一段探针头上。
	///
	/// 症状与 M4 那条「探针不许改变被测对象的状态」是同一类：
	/// 这里当时表现为"卡面预览整个变黑"，而根因在十几步之外的入口探针里。
	/// 两帧是够的：第一帧派发按下、第二帧派发松开，此后队列里什么都不剩。
	/// </summary>
	internal static async Task PushKeyAndSettle(Node host, Key key)
	{
		PushKey(key);
		await Frame(host);
		await Frame(host);
	}

	/// <summary>
	/// 按下（不松开）一个键。
	/// 修饰键必须这样处理：物件系统用 <c>Input.IsKeyPressed(Key.Shift)</c> 判断，
	/// 那读的是 Input 的全局按键状态，一次按下+松开是测不出来的。
	/// </summary>
	internal static void PushKeyDown(Key key)
	{
		var ev = new InputEventKey { Keycode = key, Pressed = true };

		if (key == Key.Shift)
			ev.ShiftPressed = true;

		Input.ParseInputEvent(ev);
	}

	internal static void PushKeyUp(Key key)
	{
		var ev = new InputEventKey { Keycode = key, Pressed = false };

		if (key == Key.Shift)
			ev.ShiftPressed = false;

		Input.ParseInputEvent(ev);
	}

	/// <summary>
	/// 合成一次双击。
	///
	/// 注意<b>不设</b> <c>DoubleClick</c> 标志：那个标志由 DisplayServer 层填写，
	/// 走 <c>Input.ParseInputEvent</c> 根本到不了那一层。而 <c>ViewportController</c>
	/// 的双击判定是自己按"两次未拖动的点击 + 时间 + 位移"算的
	/// （见 <c>GameConfig.DoubleClickSeconds</c>），所以这里只要老老实实发两对
	/// 按下/松开，走的就是和真实鼠标完全相同的那条路径。
	///
	/// 代价：同一帧里发两次会被 <c>Time.GetTicksMsec()</c> 看成 0 毫秒间隔，
	/// 依然在窗口内，所以是成立的 —— 但为了贴近真实手感，中间让一帧。
	/// </summary>
	internal static async Task PushDoubleClick(Node host, Vector2 pos, MouseButton button = MouseButton.Left)
	{
		PushButton(pos, button, true);
		PushButton(pos, button, false);
		await Frame(host);

		PushButton(pos, button, true);
		PushButton(pos, button, false);
		await Frame(host);
	}

	/// <summary>
	/// 合成一次完整拖拽（从物件当前位置拖到某个屏幕点）。
	///
	/// 第一段必须越过 <see cref="GameConfig.DragThresholdPixels"/>，才会从"待定"
	/// 变成"拖拽中" —— 否则这次手势会被当成"点击"，区域根本收不到落点。
	///
	/// 从 <c>DevZoneSim</c> 搬到这里：撤销系统的断言也要拖拽，
	/// 而<b>两份各写一份拖拽代码迟早会分叉</b>（M3 已经吃过"辅助函数没跟着升级"的亏）。
	/// </summary>
	internal static async Task DragToScreen(Node host, BoardCamera cam, TabletopObject obj, Vector2 screenTarget)
	{
		Vector2 start = cam.WorldToScreen(obj.Position);
		Vector2 delta = screenTarget - start;

		var first = new Vector2(Mathf.Sign(delta.X) * 20f, 0f);
		if (Mathf.Abs(delta.X) < 25f)
			first = delta * 0.5f;

		Vector2 second = delta - first;

		PushButton(start, MouseButton.Left, true);
		await Frame(host);
		PushMotion(start + first, first);
		PushMotion(screenTarget, second);
		PushButton(screenTarget, MouseButton.Left, false);

		await Frame(host);
		await Frame(host);
	}

	/// <summary>合成一次右键轻点（用于弹上下文菜单）。</summary>
	internal static async Task RightClick(Node host, BoardCamera cam, TabletopObject obj)
		=> await RightClickAt(host, cam.WorldToScreen(obj.Position));

	/// <summary>
	/// 按<b>屏幕坐标</b>右键轻点。
	///
	/// 与 <see cref="RightClick"/> 的区别不只是多一个重载：后者从物件的
	/// <c>Position</c> 反算屏幕点，而"我要点的是这个世界坐标"与"我要点的是这个物件"
	/// 是两回事 —— 骰子这类物件被拖动过、或光标正压在它边缘时，
	/// 两者算出来的命中结果可能不同（命中判定用的是鼠标落点，不是物件中心）。
	/// </summary>
	internal static async Task RightClickAt(Node host, Vector2 screenPos)
	{
		PushButton(screenPos, MouseButton.Right, true);
		PushButton(screenPos, MouseButton.Right, false);
		await Frame(host);
	}

	/// <summary>
	/// 合成一次左键轻点（按下即松开、中间不移动）。
	///
	/// 与 <see cref="DragToScreen"/> 的区别是<b>它不越过拖动阈值</b>，
	/// 于是走的是"点击"那条分支 —— 轻点骰子掷骰靠的正是这一条
	/// （<c>ViewportController</c> 判定没拖动也会补发一次 <c>PrimaryReleased</c>）。
	/// </summary>
	internal static async Task ClickAt(Node host, Vector2 screenPos)
	{
		PushButton(screenPos, MouseButton.Left, true);
		await Frame(host);
		PushButton(screenPos, MouseButton.Left, false);
		await Frame(host);
	}

	/// <summary>
	/// 在整屏范围里找<b>最空</b>的一个屏幕点：既没有物件压在下面，也不在任何区域矩形内。
	/// 返回值附带"到最近可见物件的间隙（世界单位）"，让调用方能把空缺程度写进报告。
	///
	/// 为什么是"求最空"而不是"取第一个通过的"：
	/// 早先的写法是从屏幕边缘往里扫、撞到第一个满足条件的点就返回，扫不到就<b>猜</b>一个
	/// <c>(中心, 视口 92%)</c>。M3 加了下带的手牌区之后，那个猜出来的点正好落在手牌区里、
	/// 而且上面还压着一张卡 —— 于是三处断言同时变红（拖拽位移、悬停移开取消选中、
	/// 从牌库拖到桌面），而症状看起来毫无关联、根本猜不到是同一个坐标在作祟。
	///
	/// 改成全局最优之后，"兜底猜一个"这条路径就不存在了：只要屏幕上还有一寸空地，
	/// 拿到的就是那一寸；实在没有，返回值里的间隙会诚实地告诉调用方"这里并不空"。
	///
	/// 代价是一次约 3000 个候选点的扫描，每个候选要问一遍拾取与包围盒 ——
	/// 对 36 个物件的桌面是毫秒级，完全可接受。
	///
	/// <b>第二次修订（同一类问题的第二种长相）：探针还得避开 HUD。</b>
	/// 第一版只问了"世界里有没有东西"，而对"屏幕上这块地方被顶栏压着"一无所知 ——
	/// 于是它把最空点选在了顶栏下沿 16px 处的一个角上。那个位置正好落在控件的
	/// 命中边缘：同一个二进制、同一个坐标，这一次点得到、下一次点不到。
	/// 症状是 <c>input_simulation</c> / <c>object_simulation</c> / <c>zone_simulation</c>
	/// 三节时红时绿，而产品完全正常。
	///
	/// 处置：候选点先过一遍 <see cref="CollectBlockingControlRects"/>（控件真实几何），
	/// 排序时再优先选"离 UI 更远"的那个；一个候选都找不到就返回 <c>null</c>，
	/// 由调用方按"没条件跑"跳过 —— <b>不猜</b>。
	/// </summary>
	internal static (Vector2? Screen, float Clearance, int Candidates) FindEmptiestScreenPoint(
		BoardCamera cam, ObjectManager objects, ZoneManager? zones = null, Board? board = null)
	{
		Vector2 viewport = cam.GetViewportRect().Size;

		// 只留一圈贴边的保险，剩下的交给<b>真实几何</b>去判。
		//
		// 这里曾经写死的是 MarginTop = 60 / MarginBottom = 118。
		// 那两个数字是"照当时 HUD 的高度估的"，而估出来的边距会有两个下场：
		// <list type="number">
		// <item>HUD 长高了 → 落点跑进控件里，点击被吃掉，报一条<b>与产品毫无关系</b>的红；</item>
		// <item>落点正好落在控件的命中边缘 → 同一个二进制、同一个坐标，
		//   这一次点得到、下一次点不到（实测 y=60 距顶栏下沿只有 16px 时就发生过）。</item>
		// </list>
		// 两种都是"测试说谎"。现在改成 <see cref="CollectBlockingControlRects"/>
		// 每帧读控件真实矩形，边距不再承担任何"猜 HUD 多高"的职责。
		const float SafetyInset = 8f;
		const float Step = 16f;

		List<Rect2> bands = BuildBlockerBands(cam, viewport);

		// 搜索范围取<b>桌面矩形在屏幕上的那块</b>（与视口保险带求交）。
		//
		// <b>为什么必须限定在桌面上（这一轮实测抓到的坑）：</b>
		// 只在"整个视口"里找，答案永远是<b>桌外</b> ——
		// 桌面上摆着几十个物件，而桌外一片空白。于是落点跑到 (3418, 1038) 这种地方，
		// 而"把 10 张牌拖到空地堆成一摞"这类断言要求落点<b>还在桌上</b>，
		// 当场变红（multi_formed_one_pile / multi_pile_forms_at_drop_point）。
		//
		// 语义上也该如此：探针要的是"桌上一块没被占用的地方"，
		// 不是"屏幕上一块什么也没有的地方"。
		Rect2 search = new(new Vector2(SafetyInset, SafetyInset), viewport - (Vector2.One * SafetyInset * 2f));

		if (board is not null)
		{
			Rect2 limited = WorldRectToScreen(cam, board.BoardRect).Intersection(search);
			if (limited.Size.X > Step && limited.Size.Y > Step)
				search = limited;
		}

		// 判据：<b>离搜索区中心最近的可用点</b>。
		//
		// 这一条换过四版，值得把三次弯路写下来 —— 它们都是"断言照样绿/照样红，
		// 而落点早就选歪了、报告里看不出来"：
		// <list type="number">
		// <item>"世界间隙最大" → 选到<b>桌外</b>（桌外最空）；</item>
		// <item>加一把"离 UI 带边越远越好"的尺子 → 选到<b>桌面右下角</b>；
		//   两把尺子方向不同，会互相打架 —— 这是"叠第二把尺子"的通病。</item>
		// <item>"离任何物件/区域最远"（maximin）→ 选到<b>桌面右上角</b>，
		//   因为角落天然离物件网格最远。断言随之稳定地红。</item>
		// </list>
		//
		// "离中心最近"没有偏好角落的毛病：它落在桌面中间那块最宽敞的地方，
		// 而"可用"这个条件已经保证了不压物件、不压区域、不被 HUD 吃掉。
		Vector2 center = search.GetCenter();
		var best = center;
		float bestScore = float.MaxValue;
		float bestClearance = -1f;
		int candidates = 0;

		for (float y = search.Position.Y; y <= search.End.Y; y += Step)
		{
			for (float x = search.Position.X; x <= search.End.X; x += Step)
			{
				var screen = new Vector2(x, y);

				// ① 屏幕上不能被任何"会吃鼠标的控件"压住 —— 这是本次修复的核心
				if (IsScreenPointBlockedByControl(bands, screen, 0f))
					continue;

				Vector2 world = cam.ScreenToWorld(screen);

				// ② 世界里不能有物件
				if (objects.PickTopmost(world) is not null)
					continue;

				// ③ 也不能落在区域矩形里（区域会把它接走，断言就变成在测别的东西）
				if (zones is not null && zones.ZoneAtWorld(world) is not null)
					continue;

				candidates++;

				float score = screen.DistanceTo(center);
				if (score < bestScore)
				{					bestScore = score;
					bestClearance = ClearanceToNearestObject(objects, world);
					best = screen;
				}
			}
		}

		// 一个合格候选都没有 = "桌上没有一块可用的空地"。返回 null 让调用方跳过，
		// <b>不要</b>退回一个中间点当兜底 —— 那正是"猜一个坐标"的老毛病。
		return candidates > 0
			? (best, Mathf.Max(bestClearance, 0f), candidates)
			: (null, -1f, 0);
	}

	/// <summary>
	/// 该世界点到"最近物件 / 最近区域矩形"的最小距离（越大越宽敞）。
	///
	/// 与 <see cref="ClearanceToNearestObject"/> 的区别：后者只算可见物件，
	/// 而落点同样不该压在某个区域的矩形里（区域会把它接走，断言就变成在测别的东西）。
	/// </summary>
	private static float MinDistanceToObstacles(ObjectManager objects, ZoneManager? zones, Vector2 world)
	{
		float nearest = ClearanceToNearestObject(objects, world);

		if (zones is not null)
		{
			foreach (Zone zone in zones.AllZones)
			{
				// 区域的世界矩形就是它的定义矩形（Zone 自己不再存一份）。
				Rect2 rect = zone.Definition.Rect;
				float dx = Mathf.Max(Mathf.Max(rect.Position.X - world.X, world.X - rect.End.X), 0f);
				float dy = Mathf.Max(Mathf.Max(rect.Position.Y - world.Y, world.Y - rect.End.Y), 0f);
				nearest = Mathf.Min(nearest, Mathf.Sqrt((dx * dx) + (dy * dy)));
			}
		}

		return nearest;
	}

	/// <summary>
	/// 描述一次选点用了什么范围（只读诊断）。
	/// 存在的理由：这一轮连续踩了三个"看起来全绿、其实选点早就跑偏"的坑
	/// （贴控件边缘 / 跑出桌面 / 跑出视口），而没有一次是报告自己能看出来的。
	/// 现在把范围和结果一起写进报告，下次不必再靠反推。
	/// </summary>
	internal static Godot.Collections.Dictionary DescribeSearchArea(
		BoardCamera cam, Board? board, Vector2? chosen)
	{
		Vector2 viewport = cam.GetViewportRect().Size;
		var info = new Godot.Collections.Dictionary
		{
			["viewport"] = new Godot.Collections.Array { viewport.X, viewport.Y },
		};

		if (board is not null)
		{
			Rect2 boardScreen = WorldRectToScreen(cam, board.BoardRect);
			info["board_world"] = new Godot.Collections.Array
				{ board.BoardRect.Position.X, board.BoardRect.Position.Y, board.BoardRect.Size.X, board.BoardRect.Size.Y };
			info["board_screen"] = new Godot.Collections.Array
				{ boardScreen.Position.X, boardScreen.Position.Y, boardScreen.Size.X, boardScreen.Size.Y };

			if (chosen is Vector2 point)
			{
				Vector2 world = cam.ScreenToWorld(point);
				info["chosen_world"] = new Godot.Collections.Array { world.X, world.Y };
				info["chosen_inside_board"] = board.BoardRect.HasPoint(world);
			}
		}
		else
		{
			info["board"] = "(没取到 Board)";
		}

		return info;
	}

	/// <summary>
	/// 把"避让依据"本身写进报告（只读，不改变任何行为）。
	///
	/// 为什么值得单独记：选点逻辑一旦悄悄失效（例如控件矩形的收集挂了），
	/// 症状是"断言照样全绿，但落点又贴回了控件边缘"—— <b>全绿的报告看不出这件事</b>。
	/// 这条诊断让"避让到底有没有生效"变成一个可以直接读的数，而不是靠推断。
	/// </summary>
	internal static Godot.Collections.Dictionary DescribeScreenBlockers(BoardCamera cam)
	{
		Vector2 viewportSize = cam.GetViewportRect().Size;
		List<Rect2> raw = CollectBlockingControlRects(cam);
		List<Rect2> bands = BuildBlockerBands(cam, viewportSize);

		var rawList = new Godot.Collections.Array();
		foreach (Rect2 rect in raw)
			rawList.Add(new Godot.Collections.Array { rect.Position.X, rect.Position.Y, rect.Size.X, rect.Size.Y });

		var bandList = new Godot.Collections.Array();
		foreach (Rect2 rect in bands)
			bandList.Add(new Godot.Collections.Array { rect.Position.X, rect.Position.Y, rect.Size.X, rect.Size.Y });

		var probes = new Godot.Collections.Dictionary();
		foreach ((string name, Vector2 at) in new (string, Vector2)[]
		{
			("top_left_10_10", new Vector2(10f, 10f)),
			("just_below_top_bar", new Vector2(viewportSize.X - 136f, 44f)),
			("viewport_center", viewportSize * 0.5f),
			("bottom_center", new Vector2(viewportSize.X * 0.5f, viewportSize.Y - 10f)),
		})
		{
			probes[name] = IsScreenPointBlockedByControl(bands, at, 0f);
		}

		return new Godot.Collections.Dictionary
		{
			["blocker_count"] = raw.Count,
			["band_count"] = bands.Count,
			["blocker_rects"] = rawList,
			["band_rects"] = bandList,
			["probe_blocked"] = probes,
		};
	}

	/// <summary>世界矩形 → 屏幕矩形（自动归一化：相机旋转 / 翻转都不会得到负尺寸）。</summary>
	internal static Rect2 WorldRectToScreen(BoardCamera cam, Rect2 world)	{
		Vector2 a = cam.WorldToScreen(world.Position);
		Vector2 b = cam.WorldToScreen(world.End);

		return new Rect2(
			new Vector2(Mathf.Min(a.X, b.X), Mathf.Min(a.Y, b.Y)),
			new Vector2(Mathf.Abs(b.X - a.X), Mathf.Abs(b.Y - a.Y)));
	}

	/// <summary>
	/// 收集控件矩形、并扩展成"整条带子"。选点与诊断<b>共用这一份</b> ——
	/// 两处各写一遍迟早会分叉，而分叉的症状是"报告说避开了、实际没避开"，
	/// 那正是这一轮要消灭的那种假象。
	/// </summary>
	private static List<Rect2> BuildBlockerBands(BoardCamera cam, Vector2 viewport)
	{
		List<Rect2> raw = CollectBlockingControlRects(cam);
		var bands = new List<Rect2>(raw.Count);

		foreach (Rect2 rect in raw)
			bands.Add(ExtendBandIfEdgeToEdge(rect, viewport));

		return bands;
	}

	/// <summary>
	/// 收集当前<b>会吃掉鼠标事件</b>的控件矩形（屏幕坐标）。
	///
	/// 判据完全按 Godot 的规则来，一条都不猜：
	/// <list type="bullet">
	/// <item><c>IsVisibleInTree()</c> —— 自己或祖先不可见就收不到事件；</item>
	/// <item><c>MouseFilter != Ignore</c> —— <c>Ignore</c> 的控件（HudRoot / Layout /
	///   MidSpacer / Spacer 这些纯布局容器）<b>真的不挡</b>，所以它们天然被排除，
	///   不必靠"节点叫什么名字"来筛；</item>
	/// <item>有非零矩形 —— 零尺寸画不出来也点不到。</item>
	/// </list>
	///
	/// 于是"顶栏多高""底部两条栏多高""编辑器面板开没开"全部由场景说了算：
	/// 布局改一次，这里自动跟上，不会再出现"改了 HUD、探针忘了改边距"。
	/// </summary>
	private static List<Rect2> CollectBlockingControlRects(BoardCamera cam)
		=> CollectControlRects(cam, blockingOnly: true);

	/// <summary>
	/// 收集<b>所有</b>可见控件矩形（含 <c>MouseFilter == Ignore</c> 的纯布局容器）。
	///
	/// 它只用于"离 UI 越远越好"这一条排序，不参与排除 ——
	/// 排除必须严格按会不会吃事件来判，而排序可以用更保守的几何。
	/// </summary>
	private static List<Rect2> CollectLayoutControlRects(BoardCamera cam)
		=> CollectControlRects(cam, blockingOnly: false);

	private static List<Rect2> CollectControlRects(BoardCamera cam, bool blockingOnly)
	{
		var rects = new List<Rect2>();
		Viewport? viewport = cam.GetViewport();

		if (viewport is null)
			return rects;

		foreach (Node child in viewport.GetChildren())
			CollectControlRectsInto(child, rects, blockingOnly);

		return rects;
	}

	private static void CollectControlRectsInto(Node node, List<Rect2> rects, bool blockingOnly)
	{
		// 注意这里收的是 Node 而不是 Control —— <b>HUD 的根是 <c>CanvasLayer</c>，
		// 它本身不是 Control</b>。第一版只递归 Control，于是整棵 HUD 树一个矩形都没收到
		// （实测立刻表现为选点退到 y=8、也就是顶栏里面），三遍稳定地红。
		// 递归不认识的节点类型是安全的：它们没有子控件，循环自然为空。
		if (node is Control control)
		{
			if (!control.IsVisibleInTree())
				return;

			if (!blockingOnly || control.MouseFilter != Control.MouseFilterEnum.Ignore)
			{
				Rect2 rect = control.GetGlobalRect();
				if (rect.Size.X > 0f && rect.Size.Y > 0f)
					rects.Add(rect);
			}
		}

		// 注意：<c>PopupMenu</c> / <c>AcceptDialog</c> 是 <c>Window</c> 而不是 <c>Control</c>，
		// 所以它们自己不会被收进来（它们的子控件会在 <c>IsVisibleInTree()</c> 为假时被跳过）。
		// 这是有意的 —— 探针自己负责在收尾时 Hide 掉菜单（<c>ObjectManager.HideAllMenus</c>），
		// 那是另一条已经存在的规矩。
		foreach (Node child in node.GetChildren())
			CollectControlRectsInto(child, rects, blockingOnly);
	}

	/// <summary>该屏幕点是否被某个"会吃鼠标"的控件压住（<paramref name="inset"/> 为额外安全间距）。</summary>
	internal static bool IsScreenPointBlockedByControl(IReadOnlyList<Rect2> blockers, Vector2 screen, float inset)
	{
		foreach (Rect2 rect in blockers)
		{
			if (rect.Grow(inset).HasPoint(screen))
				return true;
		}

		return false;
	}

	/// <summary>
	/// 把"会吃鼠标的控件"扩展成"整条带子"。
	///
	/// <b>为什么需要这一步（这一轮实测抓到的）：</b>
	/// 顶部那条控件带实测是 <c>(0,0,1920,43)</c>，而搜索选出来的落点是 <c>y=44</c> ——
	/// 它<b>确实不在控件里</b>，避让逻辑并没有失效。可它离控件的下沿只有 <b>1px</b>，
	/// 仍然是个"贴着命中边缘"的点，正是要避免的东西。
	///
	/// 光靠"排除控件矩形"挡不住这种情况：控件之外的第一个像素永远是合法的。
	/// 于是再进一步 —— 一条<b>横跨整个视口宽度</b>的控件带（顶栏 / 底栏这类），
	/// 本质上把屏幕切成两半，落点不该停在它的边线上，
	/// 所以把它当成"上边是视口上边缘、下边是控件下边缘"的一整条带来排除。
	///
	/// 判据是"控件在这一维上是否几乎横贯视口"（留 5% 容差给左右边距），
	/// 不是"控件叫什么名字"—— 名字会变，几何不会。
	/// </summary>
	private static Rect2 ExtendBandIfEdgeToEdge(Rect2 rect, Vector2 viewport)
	{
		const float Tolerance = 0.05f;

		float dx = viewport.X * Tolerance;
		float dy = viewport.Y * Tolerance;

		Rect2 result = rect;

		// 横向几乎铺满 → 它是"横贯屏幕的一条" → 往它贴着的那一边扩
		if (rect.Size.X >= viewport.X - dx)
		{
			if (rect.Position.Y <= dy)
			{
				// 贴着上边缘（顶栏）：向上吃满到 y=0，向下沿用到它自己的下沿。
				// 方向不能搞反 —— 第一版这里把 Position.Y 设为 0 却没改高度，
				// 于是顶栏被"撑"成了一条零高度的带子，y=44 依旧不被挡。
				result.Position = new Vector2(rect.Position.X, 0f);
				result.Size = new Vector2(rect.Size.X, rect.End.Y);
			}
			else if (rect.End.Y >= viewport.Y - dy)
			{
				// 贴着下边缘（底部两条信息栏）：向下吃满到视口底部
				result.Size = new Vector2(rect.Size.X, viewport.Y - rect.Position.Y);
			}
		}

		// 纵向几乎铺满 → 同理（右侧的日志面板就是这一类）
		if (rect.Size.Y >= viewport.Y - dy)
		{
			if (rect.Position.X <= dx)
			{
				result.Position = new Vector2(0f, result.Position.Y);
				result.Size = new Vector2(rect.End.X, result.Size.Y);
			}
			else if (rect.End.X >= viewport.X - dx)
			{
				result.Size = new Vector2(viewport.X - result.Position.X, result.Size.Y);
			}
		}

		return result;
	}

	/// <summary>该屏幕点离矩形 <paramref name="rect"/> 的边的最近距离（内部为 0）。</summary>
	private static float DistanceToRectEdges(Rect2 rect, Vector2 screen)
	{
		float dx = Mathf.Max(Mathf.Max(rect.Position.X - screen.X, screen.X - rect.End.X), 0f);
		float dy = Mathf.Max(Mathf.Max(rect.Position.Y - screen.Y, screen.Y - rect.End.Y), 0f);

		return Mathf.Sqrt((dx * dx) + (dy * dy));
	}

	/// <summary>该屏幕点到最近一个控件矩形的距离（一个都没有时返回一个很大的数）。</summary>
	private static float DistanceToNearestRect(IReadOnlyList<Rect2> rects, Vector2 screen)
	{
		float nearest = float.MaxValue;

		foreach (Rect2 rect in rects)
			nearest = Mathf.Min(nearest, DistanceToRectEdges(rect, screen));

		return nearest == float.MaxValue ? 1e9f : nearest;
	}

	/// <summary>该世界点到最近一个可见物件包围盒的间隙（落在某个包围盒里则为 0）。</summary>
	internal static float ClearanceToNearestObject(ObjectManager objects, Vector2 world)
	{
		float nearest = float.MaxValue;

		foreach (TabletopObject obj in objects.AllObjects)
		{
			if (!obj.Visible)
				continue;

			Rect2 box = obj.GetWorldAabb();
			if (box.HasPoint(world))
				return 0f;

			// 点到矩形的最近距离
			float dx = Mathf.Max(Mathf.Max(box.Position.X - world.X, world.X - box.End.X), 0f);
			float dy = Mathf.Max(Mathf.Max(box.Position.Y - world.Y, world.Y - box.End.Y), 0f);
			nearest = Mathf.Min(nearest, Mathf.Sqrt((dx * dx) + (dy * dy)));
		}

		return nearest == float.MaxValue ? float.MaxValue : nearest;
	}

	/// <summary>
	/// 沿用旧签名的薄包装：返回"最空的那个点"的屏幕坐标。
	/// 需要知道它到底有多空的调用方请直接用 <see cref="FindEmptiestScreenPoint"/>。
	///
	/// <b>找不到干净点时这里会返回 <c>null</c></b>，而不是给一个兜底坐标 ——
	/// 调用方必须处理这种情况（能跳过就跳过，跳不过就明说"没条件跑"）。
	/// </summary>
	internal static Vector2? FindEmptyScreenPoint(
		BoardCamera cam, ObjectManager objects, ZoneManager? zones = null, Board? board = null)
		=> FindEmptiestScreenPoint(cam, objects, zones, board).Screen;

	/// <summary>
	/// 世界点是否落在当前视口内（留出 HUD 边距）。
	/// 测试挑目标时<b>必须</b>过滤这个 —— 前面的步骤可能把物件拖到屏幕外，
	/// 对屏幕外的点合成点击会落在视口之外，得到毫无意义的失败。
	/// </summary>
	internal static bool IsOnScreen(BoardCamera cam, Vector2 worldPos)
	{
		Vector2 screen = cam.WorldToScreen(worldPos);
		Vector2 viewport = cam.GetViewportRect().Size;

		return screen.X > 24f && screen.X < viewport.X - 24f
			&& screen.Y > 56f && screen.Y < viewport.Y - 110f;
	}

	/// <summary>
	/// 数一下屏幕上还有几张可自由操作的散件卡（不属于任何区域、可见、且落在视口内）。
	///
	/// 用途是给探针一个"能不能跑"的判据。这是 M3 才暴露出来的一类问题：
	/// 用 <c>--zoom 1</c> 出近景图时，可视范围只有 1920×1080 个世界单位，
	/// 而桌面是 3200×2000 —— 大部分散件根本不在屏幕里。
	/// 此时拖拽 / 悬停 / 框选这些断言会因为"找不到靶子"而失败，
	/// 但那**不是产品有 bug，是探针没条件跑**。
	/// 报告必须把这两件事分清楚，否则一条红会把人引向完全错误的方向。
	/// </summary>
	internal static int CountLooseOnScreen(ObjectManager objects, BoardCamera cam)
	{
		int n = 0;

		foreach (TabletopObject obj in objects.AllObjects)
		{
			if (obj.ZoneId == "" && obj.Visible && IsOnScreen(cam, obj.Position))
				n++;
		}

		return n;
	}

	/// <summary>
	/// 在某个屏幕点附近找一块空位。框选测试要"起点在空白"才能触发框选，
	/// 同时又得离目标卡够近，框才罩得住它。
	/// </summary>
	internal static Vector2? FindEmptyScreenPointNear(
		BoardCamera cam, Core.Objects.ObjectManager objects, Vector2 nearScreen, float maxRadius,
		ZoneManager? zones = null)
	{
		List<Rect2> blockers = CollectBlockingControlRects(cam);

		for (float radius = 90f; radius <= maxRadius; radius += 55f)
		{
			const int Steps = 20;
			for (int i = 0; i < Steps; i++)
			{
				float angle = Mathf.Tau * i / Steps;
				Vector2 screen = nearScreen + (new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius);

				// 避开 HUD 控件 —— 那些地方会被控件吃掉合成鼠标事件。
				// 这里同样按控件<b>真实几何</b>判，不再靠写死的边距。
				if (IsScreenPointBlockedByControl(blockers, screen, 8f))
					continue;

				Vector2 world = cam.ScreenToWorld(screen);

				if (objects.PickTopmost(world) is not null)
					continue;

				// 框选的起点若落在区域里也不算数：区域不吞左键（会正常起框选），
				// 但"空白起点"的语义应该是不在任何东西之上，包含区域。
				if (zones is not null && zones.ZoneAtWorld(world) is not null)
					continue;

				return screen;
			}
		}

		return null;
	}

	/// <summary>
	/// 依次验证：滚轮缩放（含锚点不跑偏）、中键拖拽平移、左键点空白、右键轻点出菜单。
	/// 全部读完相机<b>目标值</b>而不是当前值 —— 平滑会让当前值滞后，判定会假阴性。
	/// </summary>
	internal static async Task<Godot.Collections.Dictionary> CameraAndPointerProbe(
		Node host, BoardCamera cam, ViewportController vc, Core.Objects.ObjectManager? objects = null)
	{
		var r = new Godot.Collections.Dictionary();

		// 记住初始状态，探针跑完要还原，免得污染后续观察
		Vector2 savedPos = cam.TargetPosition;
		float savedZoom = cam.ZoomLevel;

		// ---------------------------------------------------------- 滚轮缩放
		var anchor = new Vector2(500f, 300f);
		float zoomBefore = cam.ZoomLevel;
		Vector2 worldBefore = cam.ScreenToWorld(anchor);

		PushWheel(anchor, true);
		await Frame(host);

		float zoomAfter = cam.ZoomLevel;
		Vector2 worldAfter = cam.ScreenToWorld(anchor);

		r["wheel_zoom_before"] = zoomBefore;
		r["wheel_zoom_after"] = zoomAfter;
		r["wheel_zoom_increased"] = zoomAfter > zoomBefore;
		r["wheel_anchor_drift_px"] = worldBefore.DistanceTo(worldAfter);

		// ---------------------------------------------------------- 中键拖拽平移
		Vector2 camPosBefore = cam.TargetPosition;
		var from = new Vector2(900f, 500f);
		const float dragDx = 120f;
		const float dragDy = 60f;

		PushButton(from, MouseButton.Middle, true);
		await Frame(host);
		// 第一段要越过拖拽阈值，才会从"待定"变成"平移中"
		PushMotion(from + new Vector2(20f, 0f), new Vector2(20f, 0f));
		await Frame(host);
		PushMotion(from + new Vector2(dragDx, dragDy), new Vector2(dragDx - 20f, dragDy));
		await Frame(host);
		PushButton(from + new Vector2(dragDx, dragDy), MouseButton.Middle, false);
		await Frame(host);

		Vector2 camPosAfter = cam.TargetPosition;
		Vector2 camDelta = camPosAfter - camPosBefore;
		// 画布朝拖拽反方向移动，世界位移 = 屏幕位移 / 缩放
		var expected = new Vector2(-dragDx / cam.ZoomLevel, -dragDy / cam.ZoomLevel);

		r["pan_camera_delta"] = new Godot.Collections.Array { camDelta.X, camDelta.Y };
		r["pan_expected"] = new Godot.Collections.Array { expected.X, expected.Y };
		r["pan_error_px"] = camDelta.DistanceTo(expected);
		r["pan_ok"] = camDelta.DistanceTo(expected) < 1.0f;

		// ---------------------------------------------------------- 点击 / 右键
		int emptyClicks = 0;
		int contextMenus = 0;
		vc.EmptyAreaClicked += _ => emptyClicks++;
		vc.ContextMenuRequested += (_, _) => contextMenus++;

		// 空白点必须动态找 —— 写死坐标在桌面有内容之后就会假失败，
		// 而"只避开物件、不避开 HUD"会在控件长高之后假失败（本轮就是这种）。
		(Vector2? clickAt, float clearance, int candidates) = objects is not null
			? FindEmptiestScreenPoint(cam, objects, null, FindBoard(host))
			: ((Vector2?)new Vector2(700f, 400f), 0f, 1);

		r["empty_click_candidates"] = candidates;
		r["empty_click_clearance"] = clearance;

		// 避让依据本身也要能读出来 —— 见 DescribeScreenBlockers 的说明。
		Godot.Collections.Dictionary blockersInfo = DescribeScreenBlockers(cam);
		r["ui_blocker_count"] = blockersInfo["blocker_count"];
		r["ui_band_count"] = blockersInfo["band_count"];
		r["ui_blocker_rects"] = blockersInfo["blocker_rects"];
		r["ui_band_rects"] = blockersInfo["band_rects"];
		r["ui_probe_blocked"] = blockersInfo["probe_blocked"];
		r["search_area"] = DescribeSearchArea(cam, FindBoard(host), clickAt);

		// 找不到干净点 = 这一节<b>没条件跑</b>，不是失败。
		// 按项目约定："没条件跑"与"跑挂了"必须长得不一样 —— 报告里不写 pass 键即跳过，
		// 这里单独写一条 note 说明为什么，免得以后有人把它当成"忘了跑"。
		if (clickAt is not Vector2 clickPoint)
		{
			r["empty_click_note"] = "屏幕上没有既无物件、又不被 HUD 控件压住的点 —— 跳过点击判定";
			r["empty_area_clicks"] = -1;
			r["context_menu_requests"] = -1;
			r["clicks_ok"] = false;
			r["clicks_skipped"] = true;

			objects?.HideAllMenus();
			await Frame(host);

			cam.SetZoomLevel(savedZoom, anchor);
			cam.CenterOn(savedPos);
			cam.SnapToTargets();

			bool wheelOkSkip = zoomAfter > zoomBefore && worldBefore.DistanceTo(worldAfter) < 0.01f;
			r["wheel_ok"] = wheelOkSkip;
			r["pass"] = wheelOkSkip && (bool)r["pan_ok"];
			return r;
		}

		Vector2 clickAtResolved = clickPoint;
		r["empty_click_screen"] = new Godot.Collections.Array { clickAtResolved.X, clickAtResolved.Y };

		// ---- 落点自身的"合格性"判定 ----
		//
		// 这一节的全部价值都建立在"这个落点真的点在桌面上、而且没被控件吃掉"之上。
		// 少了这两条，选点逻辑一旦退化（贴控件边缘 / 跑出桌面），报告仍然可能是绿的 ——
		// 而"时红时绿"就是这么来的：落点落在命中边缘时，点得到与点不到各占一半。
		//
		// 所以把"落点合格"本身写成断言，让它自己站住。
		List<Rect2> bandsNow = BuildBlockerBands(cam, cam.GetViewportRect().Size);
		Board? boardForCheck = FindBoard(host);
		Vector2 clickWorld = cam.ScreenToWorld(clickAtResolved);

		r["empty_click_not_on_ui"] = !IsScreenPointBlockedByControl(bandsNow, clickAtResolved, 0f);
		r["empty_click_on_board"] = boardForCheck is null || boardForCheck.BoardRect.HasPoint(clickWorld);
		r["empty_click_world"] = new Godot.Collections.Array { clickWorld.X, clickWorld.Y };

		PushButton(clickAtResolved, MouseButton.Left, true);
		await Frame(host);
		PushButton(clickAtResolved, MouseButton.Left, false);
		await Frame(host);

		PushButton(clickAtResolved, MouseButton.Right, true);
		await Frame(host);
		PushButton(clickAtResolved, MouseButton.Right, false);
		await Frame(host);

		r["empty_area_clicks"] = emptyClicks;
		r["context_menu_requests"] = contextMenus;

		// 右键会弹出一个 PopupMenu，而 Popup 是 Window，会抢走后续的合成鼠标事件。
		// 探针必须自己收尾，否则下一个探针的点击全落在菜单上 —— 表现为"结果时对时错"，
		// 极难查。这个坑本轮就踩了一次。
		//
		// M3 起有两个菜单：物件的与区域的。右键落点若在某个区域矩形内，
		// 弹的是区域菜单，所以必须两个一起收 —— 只收物件菜单会漏掉一半。
		objects?.HideAllMenus();
		await Frame(host);

		// ---------------------------------------------------------- 还原并判定
		cam.SetZoomLevel(savedZoom, anchor);
		cam.CenterOn(savedPos);
		cam.SnapToTargets();

		bool wheelOk = zoomAfter > zoomBefore && worldBefore.DistanceTo(worldAfter) < 0.01f;
		bool clicksOk = emptyClicks == 1 && contextMenus == 1 && (bool)r["empty_click_not_on_ui"] && (bool)r["empty_click_on_board"];

		r["wheel_ok"] = wheelOk;
		r["clicks_ok"] = clicksOk;
		r["clicks_skipped"] = false;
		r["pass"] = wheelOk && (bool)r["pan_ok"] && clicksOk;

		return r;
	}
}
