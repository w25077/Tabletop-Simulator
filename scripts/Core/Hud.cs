using Godot;
using TabletopSimulator.Core.Objects;
using TabletopSimulator.Data;

namespace TabletopSimulator.Core;

/// <summary>
/// 常驻 HUD：顶部按钮带 + 底部两条信息栏（状态 / 快捷键提示）+ 轻量吐司。
/// 存档与撤销按钮会在 M4 挂上来。
///
/// <b>为什么状态文字在底部而不是顶部（用户实测反馈）：</b>
/// 原先那几行状态（缩放 / 物件 / 选中 / 区域）与右上角三个按钮挤在同一条 42px 的横条里，
/// 「存档 ▾」与「编辑器　F1」两个按钮把左边的文字整段压住了 ——
/// 用户的原话是"左上角的两个按钮挡住了顶部的信息文本展示"。
/// 处置：<b>顶栏只留按钮、状态文字全部搬到底部</b>，并且底部多一条与提示条等高的栏。
///
/// 这个布局有一条硬约束值得记住：<b>按钮是绝对定位的（贴 HudRoot），文字在 VBox 里</b>。
/// 两者一旦放进同一个容器就会互相挤，而"挤"的表现就是压住 —— 分开之后各管各的。
/// </summary>
[GlobalClass]
public partial class Hud : CanvasLayer
{
	private Button _gridSnapButton = null!;
	private Button _fitButton = null!;
	private Button _zoom100Button = null!;
	private Label _toastLabel = null!;

	/// <summary>底部信息栏里那一段文字（缩放 / 物件 / 选中 / 区域 / 存档）。</summary>
	private Label _infoLabel = null!;

	/// <summary>底部信息栏那一条本身（自检量它的几何）。</summary>
	private PanelContainer _infoBar = null!;

	/// <summary>底部提示栏那一条本身（自检量"两栏等高"要用）。</summary>
	private PanelContainer _hintBar = null!;

	/// <summary>当前存档名（信息栏里显示）。由 <see cref="SetSaveName"/> 维护。</summary>
	private string _saveName = AppPaths.DefaultSaveName;

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
		// 顶栏与底部两条栏天然贴边，弹性 Spacer 吃掉中间。
		// 刻意不用 anchor_preset 的 minsize 模式做"贴底" —— 那依赖容器最小尺寸
		// 的计算时序，实测会把提示条推到屏幕外（y 起点正好等于视口高度）。
		_gridSnapButton = GetNode<Button>("HudRoot/Layout/TopBar/TopRow/GridSnapButton");
		_fitButton = GetNode<Button>("HudRoot/Layout/TopBar/TopRow/FitButton");
		_zoom100Button = GetNode<Button>("HudRoot/Layout/TopBar/TopRow/Zoom100Button");
		_toastLabel = GetNode<Label>("HudRoot/Layout/ToastLabel");
		_hintBar = GetNode<PanelContainer>("HudRoot/Layout/HintBar");
		_infoBar = GetNode<PanelContainer>("HudRoot/Layout/InfoBar");
		_infoLabel = GetNode<Label>("HudRoot/Layout/InfoBar/InfoLabel");

		_toastLabel.Modulate = new Color(1f, 1f, 1f, 0f);

		// 下面这几个控件<b>全部在 Main.tscn 里搭好</b>，这里只取来接信号。
		//
		// 【项目约定】<b>禁止在代码里动态生成节点</b> —— 界面一律先在场景里搭好，
		// 代码只负责 GetNode 与接线。理由：
		// <list type="number">
		// <item><b>能看见。</b>在编辑器里打开场景就能看到每个控件的位置与属性，
		//   而"代码里 new 出来的界面"只存在于运行时 —— 调布局要改代码、重新编译、再截图。</item>
		// <item><b>矩形不用自己管。</b>进场景由容器排，不用手算 Position/anchors。</item>
		// <item><b>少一类"运行期才发现"的错误。</b>路径写错在场景里一眼能看出来。</item>
		// </list>
		//
		// （这一条是用户明确要求的，推翻了 M5 时"面板都建在代码里"的旧做法 ——
		//   那个做法当初是为了躲"编辑器把内存里的旧场景写回去"那个坑，
		//   而那个坑的处置已经改成"动 git 之前先退出编辑器"，不冲突了。）
		_saveButton = GetNode<Button>("HudRoot/Layout/TopBar/TopRow/SaveButton");
		_editorButton = GetNode<Button>("HudRoot/Layout/TopBar/TopRow/EditorButton");
		_editorButton.Pressed += () => EditorToggleRequested?.Invoke();

		RefreshInfoText();
	}

	/// <summary>
	/// 底部那两条栏（状态 / 快捷键）—— <b>它们都在 <c>Main.tscn</c> 里搭好</b>，
	/// 这里只说明为什么是两条：
	///
	/// <b>为什么不把状态塞进已有的提示条：</b>
	/// 提示条那一行装的是 15 条快捷键，宽度已经占满 1906px；
	/// 把状态再拼进去就只有两种下场 —— 挤成一行看不清，或者提示被截断。
	/// 所以分成<b>两条等高的栏</b>，上下叠放：上面是状态、下面是快捷键。
	///
	/// 两个位置上的约束（都写在场景里，这里记下来免得下次改坏）：
	/// <list type="number">
	/// <item><c>InfoBar</c> 必须在 <c>Layout</c> 里排 <b>HintBar 之前</b> ——
	///   VBox 按子节点次序自上而下排，排在后面就跑到屏幕外去了。</item>
	/// <item><b>两条栏等高</b>不是自动的：它们内容不同，各自的最小高度就不同。
	///   做法是两条栏都 <c>size_flags_vertical = 1</c>（Fill），于是 VBox 给同一个 y 尺寸，
	///   而两份最小高度里大的那个胜出 —— 效果就是"同高且以高的那条为准"。
	///   这一条有断言盯着（<c>bottom_bars_same_height</c>）。</item>
	/// </list>
	///
	/// 顺带修掉一个从 M1 起就没生效过的东西：原先那个存档名标签在
	/// <c>TopRow</c> 里、与「存档 ▾」按钮同一个位置（y 都是 11、宽度 75 对 150），
	/// <b>整个被按钮盖住</b>，而且 <c>SetSaveName</c> 从来没人调用。
	/// 现在它显示在信息栏里，由 <c>SavePanel.Refresh</c> 推过来。
	/// </summary>
	private void RefreshInfoText()
	{
		if (!IsInstanceValid(_infoLabel))
			return;

		_infoLabel.Text = string.Join("　·　",
			$"存档：{_saveName}",
			_zoomText,
			_objectText,
			_selectionText,
			_zoneText);
	}

	private string _zoomText = "缩放 100%";
	private string _objectText = "物件 0";
	private string _selectionText = "选中 0";
	private string _zoneText = "区域 0";

	// ------------------------------------------------------------------ 自检入口

	/// <summary>底部信息栏里的文字（自检读它，而不是去查五个已经不在的标签）。</summary>
	internal string InfoTextForTest => IsInstanceValid(_infoLabel) ? _infoLabel.Text : "";

	internal Rect2 InfoBarRectForTest => IsInstanceValid(_infoBar) ? _infoBar.GetGlobalRect() : new Rect2();

	internal Rect2 HintBarRectForTest => IsInstanceValid(_hintBar) ? _hintBar.GetGlobalRect() : new Rect2();

	internal Rect2 TopBarRectForTest =>
		GetNodeOrNull<Control>("HudRoot/Layout/TopBar") is { } bar ? bar.GetGlobalRect() : new Rect2();


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
	//
	// 这几条<b>不再各自写一个标签</b>，而是各自更新自己那一段、再拼成信息栏那一行。
	// 仍然保持"一个数据源一条 setter"的形状：区域计数由 ZoneManager 推过来，
	// HUD 不自己去遍历区域。

	/// <summary>当前存档名。由 <c>SavePanel.Refresh</c> 在每次存档名变化时推过来。</summary>
	public void SetSaveName(string name)
	{
		_saveName = string.IsNullOrWhiteSpace(name) ? AppPaths.DefaultSaveName : name;
		RefreshInfoText();
	}

	public void SetZoom(float zoom)
	{
		_zoomText = $"缩放 {zoom * 100f:0}%";
		RefreshInfoText();
	}

	public void SetObjectCount(int count)
	{
		_objectText = $"物件 {count}";
		RefreshInfoText();
	}

	public void SetSelectionCount(int count)
	{
		_selectionText = $"选中 {count}";
		RefreshInfoText();
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
		_zoneText = summary;
		RefreshInfoText();
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
