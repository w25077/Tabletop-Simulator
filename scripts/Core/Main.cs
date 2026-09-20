using Godot;

namespace TabletopSimulator.Core;

/// <summary>
/// 主场景根节点：把桌面、相机、输入路由、HUD 装配起来。
///
/// 场景树形状（见 <c>res://scenes/Main.tscn</c>）：
/// <code>
/// Main (Node2D)
/// ├── Board               桌面底图 + 网格
/// ├── Zones               区域（M3）
/// ├── Objects             物件（M2）
/// ├── ViewportController  输入路由
/// ├── Camera2D            桌面相机
/// └── HUD (CanvasLayer)   常驻界面
/// </code>
/// </summary>
[GlobalClass]
public partial class Main : Node2D
{
	private Board _board = null!;
	private BoardCamera _camera = null!;
	private ViewportController _viewport = null!;
	private Hud _hud = null!;

	public Board Board => _board;
	public BoardCamera Camera => _camera;
	public ViewportController Viewport => _viewport;
	public Hud Hud => _hud;

	/// <summary>所有桌面物件的父节点（M2 起往里塞卡牌/Token/骰子）。</summary>
	public Node2D ObjectsRoot { get; private set; } = null!;

	/// <summary>所有区域的父节点（M3）。</summary>
	public Node2D ZonesRoot { get; private set; } = null!;

	public override void _Ready()
	{
		_board = GetNode<Board>("Board");
		_camera = GetNode<BoardCamera>("Camera2D");
		_viewport = GetNode<ViewportController>("ViewportController");
		_hud = GetNode<Hud>("HUD");
		ObjectsRoot = GetNode<Node2D>("Objects");
		ZonesRoot = GetNode<Node2D>("Zones");

		// M1：先上一份默认桌面主题。M4 起改为从当前存档的 project.json 读。
		_board.ApplyTheme(new BoardTheme());

		_hud.Bind(_board, _camera);
		_hud.SetSaveName("未命名存档（M1：尚未接入存档）");

		FrameInitialView();
	}

	/// <summary>开场把整张桌面装进视野；上限 100%，避免小桌面被拉糊。</summary>
	private void FrameInitialView()
	{
		_camera.FocusOnRect(_board.BoardRect, 40f, 1f);
		_camera.SnapToTargets();
		_hud.SetZoom(_camera.ZoomLevel);
	}
}
