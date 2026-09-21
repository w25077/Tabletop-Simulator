using Godot;
using TabletopSimulator.Core.Objects;

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
}
