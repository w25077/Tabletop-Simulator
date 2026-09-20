using Godot;

namespace TabletopSimulator.Core;

/// <summary>
/// 常驻 HUD：顶部状态条 + 底部快捷键提示 + 轻量吐司提示。
/// M1 只承担"显示视角状态"和"提供视角按钮"；存档与撤销按钮会在 M4 挂上来。
/// </summary>
[GlobalClass]
public partial class Hud : CanvasLayer
{
	private Label _saveNameLabel = null!;
	private Label _zoomLabel = null!;
	private Label _objectLabel = null!;
	private Button _fitButton = null!;
	private Button _zoom100Button = null!;
	private Label _toastLabel = null!;

	private Board _board = null!;
	private BoardCamera _camera = null!;
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
	}

	private void OnFitPressed() => _camera.FocusOnRect(_board.BoardRect, 40f, 1f);

	private void OnZoom100Pressed() => _camera.SetZoomLevel(1f);

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

	/// <summary>底部淡出式提示（保存成功、导入失败之类）。</summary>
	public void Toast(string message)
	{
		if (!IsInstanceValid(_toastLabel))
			return;

		_toastTween?.Kill();
		_toastLabel.Text = message;
		_toastLabel.Modulate = new Color(1f, 1f, 1f, 1f);

		_toastTween = CreateTween();
		_toastTween.TweenInterval(1.6);
		_toastTween.TweenProperty(_toastLabel, "modulate:a", 0f, 0.5f);
	}
}
