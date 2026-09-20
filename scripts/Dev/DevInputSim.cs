using System.Threading.Tasks;
using Godot;
using TabletopSimulator.Core;

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
	/// 找一块真正空着的屏幕点。
	/// 测试里<b>绝不能写死坐标</b> —— 桌面内容一变，写死的点就可能压在卡上，
	/// 于是"点空白"根本没发生，断言假失败（这个坑 M2 第一版就踩了）。
	/// </summary>
	internal static Vector2 FindEmptyScreenPoint(BoardCamera cam, Core.Objects.ObjectManager objects)
	{
		Vector2 viewport = cam.GetViewportRect().Size;

		// 从靠边的位置往中间扫，优先拿到远离物件的点
		for (float ty = 0.88f; ty >= 0.2f; ty -= 0.08f)
		{
			for (float tx = 0.08f; tx <= 0.92f; tx += 0.06f)
			{
				var screen = new Vector2(viewport.X * tx, viewport.Y * ty);
				Vector2 world = cam.ScreenToWorld(screen);

				if (objects.PickTopmost(world) is not null)
					continue;

				// 半径内也没有物件才算"真空"（拖过去不会意外堆叠）
				if (HasObjectNear(objects, world, 520f))
					continue;

				return screen;
			}
		}

		return new Vector2(viewport.X * 0.5f, viewport.Y * 0.92f);
	}

	internal static bool HasObjectNear(Core.Objects.ObjectManager objects, Vector2 world, float radius)
	{
		foreach (Core.Objects.TabletopObject obj in objects.AllObjects)
		{
			if (!obj.Visible)
				continue;

			if (obj.GetWorldAabb().Grow(radius).HasPoint(world))
				return true;
		}

		return false;
	}

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
	/// 在某个屏幕点附近找一块空位。框选测试要"起点在空白"才能触发框选，
	/// 同时又得离目标卡够近，框才罩得住它。
	/// </summary>
	internal static Vector2? FindEmptyScreenPointNear(
		BoardCamera cam, Core.Objects.ObjectManager objects, Vector2 nearScreen, float maxRadius)
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

				if (objects.PickTopmost(cam.ScreenToWorld(screen)) is null)
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
		objects?.ContextMenu?.Hide();
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
