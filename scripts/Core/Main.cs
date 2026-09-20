using Godot;
using TabletopSimulator.Core.Objects;

namespace TabletopSimulator.Core;

/// <summary>
/// 主场景根节点：把桌面、相机、输入路由、物件系统、HUD 装配起来。
///
/// 场景树形状（见 <c>res://scenes/Main.tscn</c>）：
/// <code>
/// Main (Node2D)
/// ├── Board               桌面底图 + 网格
/// ├── Zones               区域（M3）
/// ├── Objects             物件容器（ObjectManager 脚本就挂在这里）
/// ├── SelectionBox        框选矩形（抬高 ZIndex，压在物件之上）
/// ├── ViewportController  输入路由
/// ├── Camera2D            桌面相机
/// ├── HUD (CanvasLayer)   常驻界面 + 右键菜单
/// └── DevCapture          开发期截图自检（无参数时完全休眠）
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

	/// <summary>物件管理器。同时也是场景里的 <c>Objects</c> 容器节点。</summary>
	public ObjectManager Objects { get; private set; } = null!;

	/// <summary>框选矩形。</summary>
	public SelectionBox SelectionBox { get; private set; } = null!;

	/// <summary>所有区域的父节点（M3）。</summary>
	public Node2D ZonesRoot { get; private set; } = null!;

	public override void _Ready()
	{
		_board = GetNode<Board>("Board");
		_camera = GetNode<BoardCamera>("Camera2D");
		_viewport = GetNode<ViewportController>("ViewportController");
		_hud = GetNode<Hud>("HUD");
		Objects = GetNode<ObjectManager>("Objects");
		SelectionBox = GetNode<SelectionBox>("SelectionBox");
		ZonesRoot = GetNode<Node2D>("Zones");

		// M1/M2：先用一份默认桌面主题 + 代码生成的示例内容。
		// M4 起改为从当前存档的 project.json 读。
		_board.ApplyTheme(new BoardTheme());

		_hud.Bind(_board, _camera);
		Objects.Bind(_board, _camera, _viewport, SelectionBox, _hud);
		_hud.BindObjects(Objects);

		DemoContent.Populate(Objects, _board.BoardRect.GetCenter());

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
