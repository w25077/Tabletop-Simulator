using Godot;
using TabletopSimulator.Core.Objects;
using TabletopSimulator.Data;

namespace TabletopSimulator.Core;

/// <summary>
/// 常驻 HUD：顶部状态条 + 底部快捷键提示 + 轻量吐司。
/// 存档与撤销按钮会在 M4 挂上来。
/// </summary>
[GlobalClass]
public partial class Hud : CanvasLayer
{
	private Label _saveNameLabel = null!;
	private Label _zoomLabel = null!;
	private Label _objectLabel = null!;
	private Label _selectionLabel = null!;
	private Label _zoneLabel = null!;
	private Button _gridSnapButton = null!;
	private Button _fitButton = null!;
	private Button _zoom100Button = null!;
	private Label _toastLabel = null!;

	/// <summary>顶栏左侧的「存档 ▾」按钮（M4 第 8 步）。宽高在这里定，位置靠锚点。</summary>
	private Button _saveButton = null!;

	public Button SaveButton => _saveButton;

	/// <summary>顶栏左侧的「编辑器　F1」按钮（M5.5 第 1 条）。</summary>
	private Button _editorButton = null!;

	public Button EditorButton => _editorButton;

	/// <summary>按钮当前显示的文字（自检用：验它<b>真的随状态变</b>）。</summary>
	internal string EditorButtonText => IsInstanceValid(_editorButton) ? _editorButton.Text : "";

	private Board _board = null!;
	private BoardCamera _camera = null!;
	private ObjectManager? _objects;
	private Tween? _toastTween;

	public override void _Ready()
	{
		// 路径对应 Main.tscn 里 HudRoot/Layout 下的 VBox 布局：
		// 顶栏天然贴顶，弹性 Spacer 吃掉中间，提示条天然贴底。
		// 刻意不用 anchor_preset 的 minsize 模式做"贴底" —— 那依赖容器最小尺寸
		// 的计算时序，实测会把提示条推到屏幕外（y 起点正好等于视口高度）。
		_saveNameLabel = GetNode<Label>("HudRoot/Layout/TopBar/TopRow/SaveNameLabel");
		_zoomLabel = GetNode<Label>("HudRoot/Layout/TopBar/TopRow/ZoomLabel");
		_objectLabel = GetNode<Label>("HudRoot/Layout/TopBar/TopRow/ObjectLabel");
		_selectionLabel = GetNode<Label>("HudRoot/Layout/TopBar/TopRow/SelectionLabel");
		_zoneLabel = GetNode<Label>("HudRoot/Layout/TopBar/TopRow/ZoneLabel");
		_gridSnapButton = GetNode<Button>("HudRoot/Layout/TopBar/TopRow/GridSnapButton");
		_fitButton = GetNode<Button>("HudRoot/Layout/TopBar/TopRow/FitButton");
		_zoom100Button = GetNode<Button>("HudRoot/Layout/TopBar/TopRow/Zoom100Button");
		_toastLabel = GetNode<Label>("HudRoot/Layout/ToastLabel");

		_toastLabel.Modulate = new Color(1f, 1f, 1f, 0f);

		// 存档按钮建在代码里、直接挂 HudRoot。
		//
		// 为什么不像别的控件那样进 Main.tscn 的 TopRow：那样就得手工编辑场景文件，
		// 而 M3 已经吃过一次亏 —— 编辑器把内存里的旧场景写了回去，手改的内容整段消失。
		// 按钮只需要"贴左上角、在顶栏高度内"，两个锚点就够了，不必进布局容器。
		//
		// 位置压在 SaveNameLabel（同一个地方）上面：那个标签是 M1 留下的占位显示，
		// 现在由这个按钮取代它的职责 —— 按钮的文字里就写着当前存档名。
		_saveButton = new Button
		{
			Name = "SaveButton",
			Text = "存档 ▾",
			CustomMinimumSize = new Vector2(150f, 28f),
		};

		_saveButton.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
		_saveButton.Position = new Vector2(8f, 7f);
		GetNode<Control>("HudRoot").AddChild(_saveButton);

		// 编辑器按钮（M5.5 第 1 条）：与存档按钮同一条横线，排在它右边。
		//
		// <b>它存在的唯一理由是"刚进来的人不知道 F1 能开编辑器"。</b>
		// 原先编辑器只有快捷键一条入口，而"入口不可发现"与"功能不存在"
		// 在用户的体感上没有区别 —— 这一条是用户实测反馈里排第一的。
		_editorButton = new Button
		{
			Name = "EditorButton",
			Text = "编辑器　F1",
			TooltipText = "打开/关闭运行时编辑器（Esc 也能关）",
			CustomMinimumSize = new Vector2(150f, 28f),
		};

		_editorButton.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
		_editorButton.Position = new Vector2(166f, 7f);
		_editorButton.Pressed += () => EditorToggleRequested?.Invoke();
		GetNode<Control>("HudRoot").AddChild(_editorButton);
	}

	/// <summary>
	/// 顶栏那个编辑器按钮被按下。
	///
	/// 与 <see cref="SaveRequested"/> 同一个套路：HUD <b>不依赖 EditorPanel 这个类型</b>。
	/// 它只是"有人想让编辑器开一下"，至于面板建没建、叫什么，由 <c>Main</c> 装配时接上。
	/// </summary>
	public System.Action? EditorToggleRequested { get; set; }

	/// <summary>
	/// 由 <see cref="EditorPanel"/> 在开关之后回调：让按钮文字反映当前状态。
	///
	/// <b>不这么做的话，用户看不出按钮现在是"开"还是"关"</b> ——
	/// 而它是个普通按钮、不是开关，按下去没有任何视觉反馈。
	/// </summary>
	public void SyncEditorButton(bool open)
	{
		if (!IsInstanceValid(_editorButton))
			return;

		_editorButton.Text = open ? "关闭编辑器　Esc" : "编辑器　F1";
	}

	/// <summary>把 HUD 接到桌面与相机上。</summary>
	public void Bind(Board board, BoardCamera camera)
	{
		_board = board;
		_camera = camera;

		_camera.ZoomChanged += SetZoom;

		_fitButton.Pressed += OnFitPressed;
		_zoom100Button.Pressed += OnZoom100Pressed;

		SetZoom(_camera.ZoomLevel);
		SetObjectCount(0);
		SetSelectionCount(0);
	}

	/// <summary>把 HUD 接到物件系统上（选中数、网格吸附开关）。</summary>
	public void BindObjects(ObjectManager objects)
	{
		_objects = objects;

		objects.SelectionChanged += SetSelectionCount;
		objects.ObjectCountChanged += SetObjectCount;
		objects.GridSnapChanged += SetGridSnap;

		_gridSnapButton.ToggleMode = true;
		_gridSnapButton.Pressed += OnGridSnapPressed;

		SetSelectionCount(objects.Selection.Count);
		SetObjectCount(objects.ObjectCount);
		SetGridSnap(objects.GridSnapEnabled);
	}

	private void OnFitPressed() => _camera.FocusOnRect(_board.BoardRect, 40f, 1f);

	private void OnZoom100Pressed() => _camera.SetZoomLevel(1f);

	private void OnGridSnapPressed()
	{
		if (_objects is null)
			return;

		_objects.GridSnapEnabled = _gridSnapButton.ButtonPressed;
		SetGridSnap(_objects.GridSnapEnabled);
		GetViewport().SetInputAsHandled();
	}

	// ------------------------------------------------------------------ 显示更新

	public void SetSaveName(string name) => _saveNameLabel.Text = name;

	public void SetZoom(float zoom)
	{
		if (!IsInstanceValid(_zoomLabel))
			return;

		_zoomLabel.Text = $"缩放 {zoom * 100f:0}%";
	}

	public void SetObjectCount(int count)
	{
		if (!IsInstanceValid(_objectLabel))
			return;

		_objectLabel.Text = $"物件 {count}";
	}

	public void SetSelectionCount(int count)
	{
		if (!IsInstanceValid(_selectionLabel))
			return;

		_selectionLabel.Text = $"选中 {count}";
	}

	public void SetGridSnap(bool enabled)
	{
		if (!IsInstanceValid(_gridSnapButton))
			return;

		_gridSnapButton.ButtonPressed = enabled;
		_gridSnapButton.Text = enabled ? "网格吸附：开" : "网格吸附：关";
	}

	/// <summary>
	/// 区域计数（M3）。形如 <c>牌库 24 · 手牌 5 · 弃牌堆 2</c>。
	///
	/// 数据源是 <c>ZoneManager.ZoneCountsChanged</c>，不是在 HUD 里自己遍历区域 ——
	/// 区域张数在抽牌 / 洗牌 / 拖入拖出后都会变，多点各自统计迟早会不一致。
	/// </summary>
	public void SetZoneSummary(string summary)
	{
		if (!IsInstanceValid(_zoneLabel))
			return;

		_zoneLabel.Text = summary;
	}

	/// <summary>底部淡出式提示（保存成功、导入失败之类）。</summary>
	public void Toast(string message)
	{
		if (!IsInstanceValid(_toastLabel))
			return;

		_toastTween?.Kill();
		_toastLabel.Text = message;
		_toastLabel.Modulate = new Color(1f, 1f, 1f, 1f);

		_toastTween = CreateTween();
		_toastTween.TweenInterval(1.6f);
		_toastTween.TweenProperty(_toastLabel, "modulate:a", 0f, 0.5f);
	}

	// ------------------------------------------------------------------ 给编辑器的接口

	/// <summary>
	/// 保存当前存档（<c>Ctrl+S</c> 与编辑器的"保存"按钮共用这一条路）。
	///
	/// 由 <c>Main</c> 在装配时接上 —— 存档面板是 M4 建的，编辑器（M5）不该直接
	/// 依赖它的具体类型：两者都要存，但"谁来存"只该有一个答案。
	/// </summary>
	public System.Action? SaveRequested { get; set; }

	public void RequestSave() => SaveRequested?.Invoke();

	/// <summary>
	/// 在<b>当前视口中心</b>造一张牌 —— 编辑器的"放到桌面"按钮用它。
	///
	/// 落在视口中心而不是桌面原点：原点在"适配整桌"的视角下是可以看见的，
	/// 但用户此刻看的是某个近景局部，新卡落在原点等于"点了一下什么都没发生"。
	/// </summary>
	public CardObject SpawnCardAtViewCenter(CardDefinition definition)
	{
		if (_objects is null)
			throw new System.InvalidOperationException("Hud 还没 BindObjects，造不出卡");

		return _objects.SpawnCard(definition, ViewCenterWorld());
	}

	/// <summary>同上，Token 版。</summary>
	public TokenObject SpawnTokenAtViewCenter(TokenDefinition definition)
	{
		if (_objects is null)
			throw new System.InvalidOperationException("Hud 还没 BindObjects，造不出 Token");

		return _objects.SpawnToken(definition, ViewCenterWorld());
	}

	private Vector2 ViewCenterWorld()
	{
		Vector2 screen = GetViewport().GetVisibleRect().Size * 0.5f;
		return _camera is null ? screen : _camera.ScreenToWorld(screen);
	}
}
