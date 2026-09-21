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
	/// </summary>
	internal static (Vector2 Screen, float Clearance) FindEmptiestScreenPoint(
		BoardCamera cam, ObjectManager objects, ZoneManager? zones = null)
	{
		Vector2 viewport = cam.GetViewportRect().Size;

		// 避开顶栏与底部提示条 —— 那些地方会被 HUD 控件吃掉
		const float MarginTop = 60f;
		const float MarginBottom = 118f;
		const float MarginSide = 24f;
		const float Step = 24f;

		Vector2 best = new(viewport.X * 0.5f, viewport.Y * 0.5f);
		float bestClearance = -1f;
		int candidates = 0;

		for (float y = MarginTop; y <= viewport.Y - MarginBottom; y += Step)
		{
			for (float x = MarginSide; x <= viewport.X - MarginSide; x += Step)
			{
				var screen = new Vector2(x, y);
				Vector2 world = cam.ScreenToWorld(screen);

				if (objects.PickTopmost(world) is not null)
					continue;

				if (zones is not null && zones.ZoneAtWorld(world) is not null)
					continue;

				candidates++;

				float clearance = ClearanceToNearestObject(objects, world);
				if (clearance > bestClearance)
				{
					bestClearance = clearance;
					best = screen;
				}
			}
		}

		return (best, candidates > 0 ? Mathf.Max(bestClearance, 0f) : -1f);
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
	/// </summary>
	internal static Vector2 FindEmptyScreenPoint(BoardCamera cam, ObjectManager objects, ZoneManager? zones = null)
		=> FindEmptiestScreenPoint(cam, objects, zones).Screen;

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
		Vector2 viewport = cam.GetViewportRect().Size;

		for (float radius = 90f; radius <= maxRadius; radius += 55f)
		{
			const int Steps = 20;
			for (int i = 0; i < Steps; i++)
			{
				float angle = Mathf.Tau * i / Steps;
				Vector2 screen = nearScreen + (new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius);

				// 避开顶栏与底部提示条 —— 那些区域会被 HUD 控件吃掉
				if (screen.X < 24f || screen.X > viewport.X - 24f ||
					screen.Y < 56f || screen.Y > viewport.Y - 110f)
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

		// 空白点必须动态找 —— 写死坐标在桌面有内容之后就会假失败
		Vector2 clickAt = objects is not null
			? FindEmptyScreenPoint(cam, objects)
			: new Vector2(700f, 400f);

		r["empty_click_screen"] = new Godot.Collections.Array { clickAt.X, clickAt.Y };

		PushButton(clickAt, MouseButton.Left, true);
		await Frame(host);
		PushButton(clickAt, MouseButton.Left, false);
		await Frame(host);

		PushButton(clickAt, MouseButton.Right, true);
		await Frame(host);
		PushButton(clickAt, MouseButton.Right, false);
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
		bool clicksOk = emptyClicks == 1 && contextMenus == 1;

		r["wheel_ok"] = wheelOk;
		r["clicks_ok"] = clicksOk;
		r["pass"] = wheelOk && (bool)r["pan_ok"] && clicksOk;

		return r;
	}
}
