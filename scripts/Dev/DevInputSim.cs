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
	private static async Task Frame(Node host) =>
		await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);

	private static void PushButton(Vector2 pos, MouseButton button, bool pressed)
	{
		Input.ParseInputEvent(new InputEventMouseButton
		{
			ButtonIndex = button,
			Pressed = pressed,
			Position = pos,
			GlobalPosition = pos,
		});
	}

	private static void PushWheel(Vector2 pos, bool up)
	{
		MouseButton button = up ? MouseButton.WheelUp : MouseButton.WheelDown;
		PushButton(pos, button, true);
		PushButton(pos, button, false);
	}

	private static void PushMotion(Vector2 pos, Vector2 relative)
	{
		Input.ParseInputEvent(new InputEventMouseMotion
		{
			Position = pos,
			GlobalPosition = pos,
			Relative = relative,
		});
	}

	/// <summary>
	/// 依次验证：滚轮缩放（含锚点不跑偏）、中键拖拽平移、左键点空白、右键轻点出菜单。
	/// 全部读完相机<b>目标值</b>而不是当前值 —— 平滑会让当前值滞后，判定会假阴性。
	/// </summary>
	internal static async Task<Godot.Collections.Dictionary> CameraAndPointerProbe(
		Node host, BoardCamera cam, ViewportController vc)
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
		vc.ContextMenuRequested += _ => contextMenus++;

		var clickAt = new Vector2(700f, 400f);
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
