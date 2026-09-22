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

	/// <summary>滚轮优先拦截器（例如 Alt+滚轮旋转物件）；为空或返回 false 时滚轮归相机缩放。</summary>
	public IWheelHandler? WheelHandler { get; set; }

	/// <summary>
	/// 左键拖拽的"被问"钩子（M5.5 P2：区域边框改大小）。
	///
	/// 与 <see cref="WheelHandler"/> 同一个套路：<b>先问，再由它决定要不要接管</b>。
	/// 它是本项目"输入只有一处入口"那条约定的延续 ——
	/// 编辑器不该为了拖一下区域边框就去抢 <c>_UnhandledInput</c>。
	/// </summary>
	public IPrimaryPressHook? PrimaryPressHook { get; set; }

	private BoardCamera _camera = null!;
	private Board _board = null!;

	private Gesture _gesture = Gesture.None;
	private MouseButton _gestureButton = MouseButton.None;
	private Vector2 _gestureStartScreen;
	private Node2D? _pressedTarget;

	// ---- 双击判定状态（只统计左键的"点击"，见 RegisterClickAndCheckDouble）----
	private ulong _lastClickMsec;
	private Vector2 _lastClickScreen;

	// ------------------------------------------------------------------ 事件

	/// <summary>鼠标在世界坐标中移动（悬停）。</summary>
	[Signal] public delegate void PointerMovedEventHandler(Vector2 worldPos);

	/// <summary>左键按在了某个物件上。</summary>
	[Signal] public delegate void PrimaryPressedEventHandler(Vector2 worldPos);

	/// <summary>左键按下后越过拖拽阈值，开始真正拖动。参数是<b>按下时</b>的世界坐标。</summary>
	/// <remarks>
	/// 这个信号是框选能工作的前提：在空白处拖动时不会有 <see cref="PrimaryPressed"/>，
	/// 只有 <see cref="PrimaryDragged"/>（且那只给增量，拿不到起点）。
	/// </remarks>
	[Signal] public delegate void PrimaryDragStartedEventHandler(Vector2 startWorldPos);

	/// <summary>物件拖拽中。第二个参数是世界坐标下的增量。</summary>
	[Signal] public delegate void PrimaryDraggedEventHandler(Vector2 worldDelta);

	/// <summary>物件拖拽结束。</summary>
	[Signal] public delegate void PrimaryReleasedEventHandler(Vector2 worldPos);

	/// <summary>左键在空白处单击（未拖拽）—— 用来取消选中。</summary>
	[Signal] public delegate void EmptyAreaClickedEventHandler(Vector2 worldPos);

	/// <summary>
	/// 左键双击（两次"未拖动的点击"落在几乎同一处且间隔很短）。
	///
	/// 判定是这里的（见 <see cref="GameConfig.DoubleClickSeconds"/> 的说明），
	/// 而不是用事件自带的 <c>DoubleClick</c> 标志 —— 那个标志在合成输入路径上不会被填写，
	/// 用它会让"双击抽牌"变成自检覆盖不到的盲区。
	///
	/// 发出时机在<b>第二次点击的松开</b>时，且在 <see cref="PrimaryReleased"/> /
	/// <see cref="EmptyAreaClicked"/> 之后 —— 于是"双击牌库"既能让被点到的顶牌先完成
	/// 一次普通点击，又能让落在牌库空白处的双击照样被区域收到。
	/// </summary>
	[Signal] public delegate void PrimaryDoubleClickedEventHandler(Vector2 worldPos);

	/// <summary>
	/// 右键轻点（未拖拽）—— 请求上下文菜单。
	/// 第二个参数是<b>视口坐标</b>（不是桌面坐标），菜单必须用它来定位：
	/// <c>PopupMenu</c> 是嵌入式子窗口，它的 <c>Position</c> 属于视口坐标系。
	/// 早先这里只传世界坐标、让菜单自己去问 <c>DisplayServer.MouseGetPosition()</c>，
	/// 拿到的是桌面坐标，于是菜单位置整体偏移、还会被顶到视口最右边。
	/// </summary>
	[Signal] public delegate void ContextMenuRequestedEventHandler(Vector2 worldPos, Vector2 screenPos);

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
				KeyEvents++;
				HandleKey(k);
				break;
		}
	}

	// ------------------------------------------------------------------ 鼠标按键

	/// <summary>
	/// 收到过多少次鼠标按键事件（自检用）。
	///
	/// 用途是回答"这一下点击到底有没有送到输入路由" —— <c>PrimaryPressed</c> 信号
	/// 只在<b>命中物件</b>时才发，所以"点空白处"用它验不出来。
	/// 而"面板关着的时候会不会挡住桌面右边那一竖条的点击"恰恰只有空白点击能验。
	/// </summary>
	public int MouseButtonEvents { get; private set; }

	/// <summary>
	/// 收到过多少次<b>键盘按下</b>事件（自检用）。与 <see cref="MouseButtonEvents"/> 同一套路。
	///
	/// 为什么需要它：有一条断言时红时绿——"按 E 能转、按 F 不翻"，而两次跑的是同一个二进制、
	/// 同一段代码。报告里只有 <c>flip_ok = false</c>，看不出到底是
	/// <b>键没送到输入路由</b>还是<b>送到了但目标集是空的</b>。
	/// 有了这个计数器，两种情况的报告长得完全不一样，不必再靠推测。
	///
	/// 只读、不改变任何行为，符合本项目"探针与诊断不许改变被测对象的可观测状态"。
	/// </summary>
	public int KeyEvents { get; private set; }

	private void HandleMouseButton(InputEventMouseButton mb)
	{
		MouseButtonEvents++;
		Vector2 screen = mb.Position;

		// 钩子正在接管时，左键的移动与松手都归它 —— 走的是<b>同一条分发路径</b>，
		// 只是目的地换了一个（见 IPrimaryPressHook 的说明）。
		if (PrimaryPressHook is { IsPrimaryDragActive: true } hook && mb.ButtonIndex == MouseButton.Left)
		{
			if (mb.Pressed)
				hook.PrimaryDragTo(_camera.ScreenToWorld(screen));
			else
				hook.EndPrimaryDrag();

			Consume();
			return;
		}

		if (mb.Pressed)
			HandlePress(mb, screen);
		else
			HandleRelease(mb, screen);
	}

	private void HandlePress(InputEventMouseButton mb, Vector2 screen)
	{
		// ---- 滚轮：先给拦截器（Alt+滚轮旋转物件），没人要才用来缩放 ----
		if (mb.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
		{
			float steps = mb.ButtonIndex == MouseButton.WheelUp ? 1f : -1f;

			if (WheelHandler is not null && WheelHandler.HandleWheel(steps, screen))
			{
				Consume();
				return;
			}

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
			// 先问"被问"钩子（区域边框改大小）。它说接管就不再起拖拽手势 ——
			// 否则改大小的同时会把桌面上那张牌一起拖走。
			if (PrimaryPressHook is not null
				&& PrimaryPressHook.TryBeginPrimaryDrag(_camera.ScreenToWorld(screen), screen))
			{
				Consume();
				return;
			}

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
					EmitSignal(SignalName.ContextMenuRequested, world, screen);
				break;

			case Gesture.Panning:
				break;

			case Gesture.PrimaryPending:
				// 先判双击（用松开位置），再决定这次是"点物件"还是"点空白"。
				// 顺序有讲究：双击信号必须最后发，这样区域看到的是"点击已经处理完了"的状态。
				bool isDoubleClick = RegisterClickAndCheckDouble(screen);

				if (_pressedTarget is null)
				{
					// 左键轻点落空 → 取消选中
					EmitSignal(SignalName.EmptyAreaClicked, world);
				}
				else
				{
					// 点在物件上但没拖动：照样发一次"释放"，
					// 物件系统靠它实现"轻点"语义（例如轻点骰子即掷）。
					EmitSignal(SignalName.PrimaryReleased, world);
				}

				if (isDoubleClick)
					EmitSignal(SignalName.PrimaryDoubleClicked, world);
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

		// 钩子接管期间只喂它，不发普通的 PointerMoved ——
		// 否则改大小的同时，桌面上的悬停/选中还在跟着变。
		if (PrimaryPressHook is { IsPrimaryDragActive: true } hook)
		{
			hook.PrimaryDragTo(world);
			Consume();
			return;
		}

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
				EmitSignal(SignalName.PrimaryDragStarted, _camera.ScreenToWorld(_gestureStartScreen));
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

	/// <summary>
	/// 记一次"点击"并判断它是不是双击的第二次。
	///
	/// 只在<b>按住平移键之外的左键点击</b>上调用（回到这里说明这次手势没有越过拖拽阈值），
	/// 所以"拖拽"永远不会被算成第一次点击 —— 否则拖完一张牌再点一下同一位置，
	/// 会被误判成双击。
	///
	/// 双击一旦成立就把记录清掉，避免三连击被算成两次双击。
	/// </summary>
	private bool RegisterClickAndCheckDouble(Vector2 screen)
	{
		ulong now = Time.GetTicksMsec();
		ulong window = (ulong)(GameConfig.DoubleClickSeconds * 1000.0);

		bool isDouble =
			_lastClickMsec != 0 &&
			now - _lastClickMsec <= window &&
			screen.DistanceTo(_lastClickScreen) <= GameConfig.DoubleClickMaxDriftPixels;

		if (isDouble)
		{
			_lastClickMsec = 0;
		}
		else
		{
			_lastClickMsec = now;
			_lastClickScreen = screen;
		}

		return isDouble;
	}
}
