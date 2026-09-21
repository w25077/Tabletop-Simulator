using Godot;
using TabletopSimulator.Core.Objects;
using TabletopSimulator.Data;

namespace TabletopSimulator.Core;

/// <summary>
/// 「画区域」的遮罩层：铺满整个视口，负责拖矩形、以及那一层淡淡的模式提示。
///
/// <b>为什么不能画在「区域」页里：</b>面板只占屏幕的一块，而区域要画在桌面的任何地方
/// —— 拖到面板外面时预览框会当场消失（半个矩形），看起来像拖拽断了。
/// 所以这一层必须铺满视口，而它挂在 <c>EditorPanel</c>（<c>HUD</c> 这一层）下面，
/// <b>盖住整个桌面</b>。
///
/// 三件事它必须做对：
/// <list type="number">
/// <item><b>吃掉鼠标按下</b>（<c>AcceptEvent</c>）：否则这次拖拽会同时被桌面上的
///   物件系统收到，画一块区域顺便把一张牌拖走了。</item>
/// <item><b>屏幕坐标 ↔ 桌面坐标都走相机</b>：自己算"除缩放"的话，
///   一旦缩放不是 100% 预览框就会从光标底下漂走。</item>
/// <item><b>松手时用<b>世界坐标</b>算矩形</b>：缩放平移之后屏幕矩形与桌面矩形不成正比。</item>
/// </list>
/// </summary>
public partial class ZoneDrawOverlay : Control
{
	private ZoneEditorPage _page = null!;
	private ZoneManager _zones = null!;
	private BoardCamera _camera = null!;
	private Hud _hud = null!;
	private EditorPanel _panel = null!;

	/// <summary>正在拖的那个矩形（屏幕坐标；null = 没在拖）。<b>自检靠它核对拖拽确实发生了。</b></summary>
	public Rect2? DragRect { get; private set; }

	/// <summary>开始拖的那个点（屏幕坐标）。</summary>
	public Vector2? DragStart { get; private set; }

	/// <summary>鼠标是否正按着在拖。<c>Esc</c> 的分级判断要用它（见 <see cref="EditorPanel.HandleEscape"/>）。</summary>
	public bool IsDragging => DragStart is not null;

	/// <summary>本次拖拽已经画出了几块区域（自检核对"一次拖拽 = 一块区域"）。</summary>
	public int CreatedCount { get; private set; }

	internal void Bind(ZoneEditorPage page, ZoneManager zones, BoardCamera camera, Hud hud, EditorPanel panel)
	{
		_page = page;
		_zones = zones;
		_camera = camera;
		_hud = hud;
		_panel = panel;
	}

	public override void _Ready()
	{
		SetAnchorsPreset(LayoutPreset.FullRect);
		MouseFilter = MouseFilterEnum.Stop;
		Visible = false;
		QueueRedraw();
	}

	public void SetActive(bool active)
	{
		Visible = active;
		DragRect = null;
		DragStart = null;

		// 光标是"现在是画区域模式"的第二条持续提示（第一条是那层淡色遮罩）。
		// 退出时<b>必须还原</b>，否则回到桌面还得拖牌，而光标还停在十字上。
		//
		// 用 <c>SetDefaultCursorShape</c> / <c>SetCursorShape</c> 而不是
		// <c>MouseDefaultCursorShape</c>：这是 <c>Control</c> 自己的接口，
		// 不会去动 Godot 全局的默认光标（那个一改就会漏到别的控件上）。
		if (active)
			SetDefaultCursorShape(CursorShape.Cross);
		else
			SetDefaultCursorShape(CursorShape.Arrow);

		// <b>变可见之后重设一次锚点。</b>
		//
		// 这个遮罩在编辑器关着的时候是隐藏的（它只在"画区域"模式里出现），
		// 而 <b>Godot 会跳过不可见 Control 的布局</b>：进树时那次锚点计算
		// 虽然把 anchors/offsets 都算对了（0,0,1,1 / 0,0,-1920,-1080），
		// <c>Size</c> 却停在 <c>(0,0)</c>。于是——
		// <list type="bullet">
		/// <item><c>_Draw</c> 画的遮罩是零尺寸的（看不见）</item>
		/// <item>用户按下鼠标时命中的是别的东西（遮罩压根没有面积）</item>
		/// <item>而 <c>_GuiInput</c> 里那套逻辑看起来完全正常</item>
		/// </list>
		/// <b>矩形为 0 的控件不报错，它只是永远收不到输入。</b>M4 那条"0 高度面板"
		/// 是同一个坑，这次换了张脸（那次是设锚点的时机，这次是可见性）。
		/// 显式重算一次是这里最省事也最可靠的处置。
		if (active)
		{
			SetAnchorsPreset(LayoutPreset.FullRect);
			OffsetLeft = 0f;
			OffsetTop = 0f;
			OffsetRight = 0f;
			OffsetBottom = 0f;
		}

		QueueRedraw();
	}

	/// <summary>当前的默认光标形状（自检用：验"进出模式时光标跟着变"）。</summary>
	internal CursorShape CursorForTest => GetDefaultCursorShape();

	/// <summary>
	/// <c>Esc</c>：交给 <see cref="EditorPanel.HandleEscape"/> 做<b>两级</b>处理
	/// —— 先收掉本模式、再关面板。
	///
	/// <b>为什么本层不能自己处理掉：</b>这里是在 <c>_Input</c> 里，跑在
	/// <c>_UnhandledKeyInput</c> <b>之前</b>，所以本层一旦把事件吃掉并标记已处理，
	/// 面板那一层就永远收不到它 —— 症状与修复前一模一样（<c>Esc</c> 关不掉面板），
	/// 只是原因换了个地方。
	///
	/// 做成"遮罩不再自己决定怎么退出"，还顺手消掉了一条重复逻辑：
	/// 退出画区域模式现在只有一处实现。
	/// </summary>
	public override void _Input(InputEvent @event)
	{
		if (!Visible)
			return;

		if (@event is InputEventKey key && key.Pressed && !key.Echo
			&& key.Keycode == Key.Escape && _panel.HandleEscape(key))
		{
			GetViewport().SetInputAsHandled();
			return;
		}

		if (@event is InputEventMouseMotion motion && DragRect is { } current)
		{
			DragRect = Between(DragStart ?? current.Position, motion.Position);
			QueueRedraw();
		}
	}

	public override void _GuiInput(InputEvent @event)
	{
		if (!Visible)
			return;

		if (@event is not InputEventMouseButton button || button.ButtonIndex != MouseButton.Left)
			return;

		if (button.Pressed)
		{
			DragStart = button.Position;
			DragRect = new Rect2(button.Position, Vector2.Zero);
			AcceptEvent();   // 关键：桌面不会同时拿到这次按下
			QueueRedraw();
			return;
		}

		Vector2 start = DragStart ?? button.Position;
		Vector2 end = button.Position;

		Zone? created = ZoneEditService.CreateFromDrag(
			_zones, _camera, start, end, _page.DrawKind, "");

		DragRect = null;
		DragStart = null;
		AcceptEvent();

		if (created is null)
		{
			_hud.Toast(ZoneEditService.LastError);
			QueueRedraw();
			return;
		}

		CreatedCount++;
		_page.OnZoneDrawn(created);
		QueueRedraw();
	}

	/// <summary>两个点之间的规范化矩形（左上角 + 正尺寸）。</summary>
	private static Rect2 Between(Vector2 a, Vector2 b) => new(
		new Vector2(Mathf.Min(a.X, b.X), Mathf.Min(a.Y, b.Y)),
		new Vector2(Mathf.Abs(a.X - b.X), Mathf.Abs(a.Y - b.Y)));

	// ------------------------------------------------------------------ 自检入口

	/// <summary>
	/// 用两个屏幕点走一遍完整的"按下 → 松手"。
	///
	/// <b>为什么要有这个入口：</b>自检里"画区域"如果只调
	/// <c>ZoneEditService.CreateFromDrag</c>，验的是那条服务方法，
	/// 而没有验"遮罩层到底有没有把这两个点接上、有没有把事件吃下来、
	/// 画完有没有退出模式"。这三件事都在这层里，且都是会出错的地方。
	///
	/// 走的是与真实输入完全相同的 <see cref="_GuiInput"/>（只是自己造事件），
	/// 不另写一条"给测试用的捷径"——捷径验的是捷径。
	/// </summary>
	internal void SimulateDragForTest(Vector2 fromScreen, Vector2 toScreen)
	{
		_GuiInput(new InputEventMouseButton
		{
			ButtonIndex = MouseButton.Left,
			Pressed = true,
			Position = fromScreen,
		});

		GuiInputSwallowed = GetViewport().IsInputHandled();

		_GuiInput(new InputEventMouseButton
		{
			ButtonIndex = MouseButton.Left,
			Pressed = false,
			Position = toScreen,
		});
	}

	/// <summary>上一次模拟拖拽里"按下"是否被标记为已处理（桌面因此收不到它）。</summary>
	internal bool GuiInputSwallowed { get; private set; }

	/// <summary>
	/// 只按下、不松手 —— 用来验"正拖着的时候 <c>Esc</c> 不算数"。
	///
	/// 走的是与真实输入相同的 <see cref="_GuiInput"/>，不是另写一条捷径：
	/// 要验的正是"遮罩层收到按下之后有没有记成正在拖"。
	/// </summary>
	internal void SimulateDragStartForTest(Vector2 fromScreen)
	{
		_GuiInput(new InputEventMouseButton
		{
			ButtonIndex = MouseButton.Left,
			Pressed = true,
			Position = fromScreen,
		});
	}

	public override void _Draw()
	{
		if (!Visible)
			return;

		// 极淡的一层：让"现在是画区域模式"有个持续可见的提示，
		// 又不会把桌面看不清楚（要照着桌面位置画区域）。
		DrawRect(new Rect2(Vector2.Zero, Size), new Color(0.36f, 0.51f, 0.67f, 0.05f));

		if (DragRect is not { } rect || rect.Size.X < 1f || rect.Size.Y < 1f)
			return;

		DrawRect(rect, new Color(0.53f, 0.74f, 0.73f, 0.18f));
		DrawRect(rect, new Color("#88c0d0"), false, 2f);
	}
}
