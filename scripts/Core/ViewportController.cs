using Godot;

namespace TabletopSimulator.Core;

/// <summary>
/// 输入路由：把鼠标/键盘事件分派给「相机」或「物件交互」。
///
/// 挂在 <c>_UnhandledInput</c> 上，所以 UI 控件（按钮、输入框、面板）优先拿到事件 ——
/// 鼠标悬在面板上时绝不会误触发桌面操作。
///
/// 内部是一个小状态机。关键点是<b>用位移阈值区分"点击"和"拖拽"</b>：
/// 右键轻点 = 上下文菜单，右键拖动 = 平移画布，两者共用同一个按键。
/// </summary>
[GlobalClass]
public partial class ViewportController : Node
{
	private enum Gesture
	{
		None,
		/// <summary>平移键已按下，但还没超过拖拽阈值 —— 此时还不知道是"拖"还是"点"。</summary>
		PanPending,
		Panning,
		/// <summary>左键已按下，还不知道是"点物件"还是"拖物件"。</summary>
		PrimaryPending,
		PrimaryDragging,
	}

	/// <summary>相机节点路径。留空则自动取同级节点 <c>../Camera2D</c>。</summary>
	[Export] public NodePath? CameraPath { get; set; }

	/// <summary>桌面节点路径。留空则自动取同级节点 <c>../Board</c>。</summary>
	[Export] public NodePath? BoardPath { get; set; }

	/// <summary>物件查询器；为空时任何左键都视为点在空白处。</summary>
	public IWorldPicker? Picker { get; set; }

	private BoardCamera _camera = null!;
	private Board _board = null!;

	private Gesture _gesture = Gesture.None;
	private MouseButton _gestureButton = MouseButton.None;
	private Vector2 _gestureStartScreen;
	private Node2D? _pressedTarget;

	// ------------------------------------------------------------------ 事件

	/// <summary>鼠标在世界坐标中移动（悬停）。</summary>
	[Signal] public delegate void PointerMovedEventHandler(Vector2 worldPos);

	/// <summary>左键按在了某个物件上。</summary>
	[Signal] public delegate void PrimaryPressedEventHandler(Vector2 worldPos);

	/// <summary>物件拖拽中。第二个参数是世界坐标下的增量。</summary>
	[Signal] public delegate void PrimaryDraggedEventHandler(Vector2 worldDelta);

	/// <summary>物件拖拽结束。</summary>
	[Signal] public delegate void PrimaryReleasedEventHandler(Vector2 worldPos);

	/// <summary>左键在空白处单击（未拖拽）—— 用来取消选中。</summary>
	[Signal] public delegate void EmptyAreaClickedEventHandler(Vector2 worldPos);

	/// <summary>右键轻点（未拖拽）—— 请求上下文菜单。</summary>
	[Signal] public delegate void ContextMenuRequestedEventHandler(Vector2 worldPos);

	/// <summary>视角发生变化（缩放或平移）。</summary>
	[Signal] public delegate void ViewChangedEventHandler();

	// ------------------------------------------------------------------ 生命周期

	public override void _Ready()
	{
		_camera = Resolve<BoardCamera>(CameraPath, "Camera2D");
		_board = Resolve<Board>(BoardPath, "Board");
		_camera.ZoomChanged += _ => EmitSignal(SignalName.ViewChanged);
	}

	/// <summary>
	/// 取同级节点。优先用显式配置的路径，没配就按 <paramref name="siblingName"/> 找兄弟。
	/// 这样场景一搭好就能跑，不需要在编辑器里手工拖引用；需要时又可以在 Inspector 里覆盖。
	/// </summary>
	private T Resolve<T>(NodePath? explicitPath, string siblingName) where T : Node
	{
		if (explicitPath is not null && !explicitPath.IsEmpty)
		{
			T? configured = GetNodeOrNull<T>(explicitPath);
			if (configured is not null)
				return configured;

			GD.PushWarning($"[ViewportController] 配置的路径 '{explicitPath}' 没找到 {typeof(T).Name}，改用同级节点 '{siblingName}'。");
		}

		return GetParent().GetNode<T>(siblingName);
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		switch (@event)
		{
			case InputEventMouseButton mb:
				HandleMouseButton(mb);
				break;
			case InputEventMouseMotion mm:
				HandleMouseMotion(mm);
				break;
			case InputEventKey k when k.Pressed && !k.Echo:
				HandleKey(k);
				break;
		}
	}

	// ------------------------------------------------------------------ 鼠标按键

	private void HandleMouseButton(InputEventMouseButton mb)
	{
		Vector2 screen = mb.Position;

		if (mb.Pressed)
			HandlePress(mb, screen);
		else
			HandleRelease(mb, screen);
	}

	private void HandlePress(InputEventMouseButton mb, Vector2 screen)
	{
		// ---- 滚轮：以鼠标为锚缩放 ----
		if (mb.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
		{
			float steps = mb.ButtonIndex == MouseButton.WheelUp ? 1f : -1f;
			_camera.ZoomAtScreenPoint(steps, screen);
			Consume();
			return;
		}

		// ---- 平移键：中键 / 右键 / 空格+左键 ----
		if (IsPanTrigger(mb.ButtonIndex))
		{
			BeginGesture(Gesture.PanPending, mb.ButtonIndex, screen);
			Consume();
			return;
		}

		// ---- 左键：可能拖物件，也可能只是点空白 ----
		if (mb.ButtonIndex == MouseButton.Left)
		{
			BeginGesture(Gesture.PrimaryPending, mb.ButtonIndex, screen);
			_pressedTarget = Picker?.PickTopmost(_camera.ScreenToWorld(screen));
			if (_pressedTarget is not null)
				EmitSignal(SignalName.PrimaryPressed, _camera.ScreenToWorld(screen));
			Consume();
		}
	}

	private void HandleRelease(InputEventMouseButton mb, Vector2 screen)
	{
		if (_gesture == Gesture.None || mb.ButtonIndex != _gestureButton)
			return;

		Vector2 world = _camera.ScreenToWorld(screen);
		Gesture finished = _gesture;
		_gesture = Gesture.None;

		switch (finished)
		{
			case Gesture.PanPending:
				// 按了平移键但没动 —— 右键当上下文菜单，其余忽略
				if (mb.ButtonIndex == MouseButton.Right)
					EmitSignal(SignalName.ContextMenuRequested, world);
				break;

			case Gesture.Panning:
				break;

			case Gesture.PrimaryPending:
				// 左键轻点且落空 → 取消选中
				if (_pressedTarget is null)
					EmitSignal(SignalName.EmptyAreaClicked, world);
				break;

			case Gesture.PrimaryDragging:
				EmitSignal(SignalName.PrimaryReleased, world);
				break;
		}

		_pressedTarget = null;
		Consume();
	}

	private bool IsPanTrigger(MouseButton button) =>
		button is MouseButton.Middle or MouseButton.Right ||
		(button == MouseButton.Left && Input.IsKeyPressed(Key.Space));

	// ------------------------------------------------------------------ 鼠标移动

	private void HandleMouseMotion(InputEventMouseMotion mm)
	{
		Vector2 world = _camera.ScreenToWorld(mm.Position);
		EmitSignal(SignalName.PointerMoved, world);

		if (_gesture == Gesture.None)
			return;

		bool passedThreshold =
			(mm.Position - _gestureStartScreen).LengthSquared() >=
			GameConfig.DragThresholdPixels * GameConfig.DragThresholdPixels;

		switch (_gesture)
		{
			case Gesture.PanPending when passedThreshold:
				_gesture = Gesture.Panning;
				goto case Gesture.Panning;

			case Gesture.Panning:
				_camera.PanByScreenDelta(mm.Relative);
				EmitSignal(SignalName.ViewChanged);
				Consume();
				break;

			case Gesture.PrimaryPending when passedThreshold:
				_gesture = Gesture.PrimaryDragging;
				goto case Gesture.PrimaryDragging;

			case Gesture.PrimaryDragging:
				// Relative 是屏幕像素增量，除以缩放换成世界增量，物件才会跟着鼠标走。
				EmitSignal(SignalName.PrimaryDragged, mm.Relative / _camera.TargetZoom);
				Consume();
				break;
		}
	}

	// ------------------------------------------------------------------ 键盘

	private void HandleKey(InputEventKey k)
	{
		if (k.IsActionPressed("tt_view_fit"))
		{
			_camera.FocusOnRect(_board.BoardRect, 40f, 1f);
			Consume();
		}
		else if (k.IsActionPressed("tt_view_100"))
		{
			_camera.SetZoomLevel(1f);
			Consume();
		}
	}

	// ------------------------------------------------------------------ 工具

	private void BeginGesture(Gesture gesture, MouseButton button, Vector2 screen)
	{
		_gesture = gesture;
		_gestureButton = button;
		_gestureStartScreen = screen;
	}

	private void Consume() => GetViewport().SetInputAsHandled();

	/// <summary>供外部（如 Esc 键、场景切换）强制中断当前手势。</summary>
	public void CancelGesture()
	{
		_gesture = Gesture.None;
		_pressedTarget = null;
	}
}
