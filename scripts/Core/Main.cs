using Godot;
using TabletopSimulator.Core.Objects;

namespace TabletopSimulator.Core;

/// <summary>
/// 主场景根节点：把桌面、相机、输入路由、物件系统、区域系统、HUD 装配起来。
///
/// 场景树形状（见 <c>res://scenes/Main.tscn</c>）：
/// <code>
/// Main (Node2D)
/// ├── Board               桌面底图 + 网格（z_index = -1000）
/// ├── Zones               区域系统（z_index = -500，ZoneManager 脚本挂在这里）
/// ├── Objects             物件容器（ObjectManager 脚本就挂在这里）
/// ├── SelectionBox        框选矩形（抬高 ZIndex，压在物件之上）
/// ├── ViewportController  输入路由
/// ├── Camera2D            桌面相机
/// ├── HUD (CanvasLayer)   常驻界面 + 右键菜单
/// └── DevCapture          开发期截图自检（无参数时完全休眠）
/// </code>
///
/// 装配顺序有讲究：先物件、后区域，最后才用 <see cref="ObjectManager.Zones"/>
/// 把两者接起来 —— 区域需要一个已经就绪的物件管理器（它要调
/// <c>ReleaseFromPile</c> / <c>SelectOnly</c>），而那是一个运行时引用，不是构造依赖。
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

	/// <summary>撤销 / 重做（M4）。</summary>
	public UndoSystem Undo { get; private set; } = null!;

	/// <summary>操作日志面板（M4）。</summary>
	public LogPanel Log { get; private set; } = null!;

	/// <summary>物件管理器。同时也是场景里的 <c>Objects</c> 容器节点。</summary>
	public ObjectManager Objects { get; private set; } = null!;

	/// <summary>框选矩形。</summary>
	public SelectionBox SelectionBox { get; private set; } = null!;

	/// <summary>
	/// 区域系统。同时也是场景里的 <c>Zones</c> 容器节点（所有区域的父节点）。
	/// M1 只留了一个空 <c>Node2D</c>，M3 给它挂上了 <see cref="ZoneManager"/>。
	/// </summary>
	public ZoneManager Zones { get; private set; } = null!;

	public override void _Ready()
	{
		// <b>先定存档根，再碰任何路径。</b>
		//
		// 沙箱里 user:// 不可写，而"存 → 重启 → 读 → 逐字段比对"这条循环
		// 正是 M4 最值钱的断言。自检脚本传 --save-root 把根指到工作区里，
		// 于是整条循环每次自检都能重跑，真实存档也绝不会被碰脏。
		// 放在 _Ready 最前面：CreateDirectories 之类的调用一旦先跑，
		// 目录就已经按默认根建好了。
		AppPaths.Initialize(Dev.CmdLine.GetValue(AppPaths.RootFlag));

		_board = GetNode<Board>("Board");
		_camera = GetNode<BoardCamera>("Camera2D");
		_viewport = GetNode<ViewportController>("ViewportController");
		_hud = GetNode<Hud>("HUD");
		Objects = GetNode<ObjectManager>("Objects");
		SelectionBox = GetNode<SelectionBox>("SelectionBox");
		Zones = GetNode<ZoneManager>("Zones");
		Undo = GetNode<UndoSystem>("Undo");

		// M1/M2：先用一份默认桌面主题 + 代码生成的示例内容。
		// M4 起改为从当前存档的 project.json 读。
		_board.ApplyTheme(new BoardTheme());

		_hud.Bind(_board, _camera);
		Objects.Bind(_board, _camera, _viewport, SelectionBox, _hud);
		_hud.BindObjects(Objects);

		// 区域系统要能调物件管理器（摘自由堆、设选中集），所以先 Bind 它；
		// 再把区域系统以接口形式交给物件管理器 —— 物件系统只依赖 IZoneInteraction，
		// 不依赖 ZoneManager 具体类型（同 M1 的 IWorldPicker / IWheelHandler）。
		Zones.Bind(Objects, _hud, _hud);
		Objects.Zones = Zones;

		// ---- 开局内容先布好 ----
		//
		// <b>这三行的顺序是修完一个真 bug 之后定下来的，别调换。</b>
		//
		// 撤销系统的初始快照必须是"这一局已经布好"的那张桌子。原来写成
		// "先 Bind 再 Populate"，于是历史起点是一张<b>空桌子</b>，
		// 而玩家一路 Ctrl+Z 退到底时，那一步写回就会把整个桌面删光 ——
		// 删除还会走写回路径顺手记一条历史，把重做那一截一起吃掉。
		// 用户实测报的「一直撤销会删除桌面全部内容，且重做不回来」正是这个：
		// 第一次拖动就把它记成了"之前"（手势的起点快照取自 <c>_current</c>）。
		//
		// 自检 undo_flow_simulation 现在常驻盯这件事：撤销到底必须回到开局的桌面。
		DemoContent.Populate(Objects, Zones, _board.BoardRect.GetCenter());

		// 撤销系统最后接上 —— 连"记历史"的引用都在 Populate 之后才给，
		// 于是示例内容无论将来走哪条路都不可能混进历史。
		Objects.Undo = Undo;
		Zones.Undo = Undo;
		Undo.Bind(Objects, Zones);

		// 操作日志面板（M4 第 6 步）。挂到 HUD 层上，UI 在它自己的 _Ready 里建。
		// 建在这里而不是 Main.tscn 里：少一处手工同步场景文件的地方
		// （编辑器把内存里旧场景写回去过一次，代价很大）。
		Log = LogPanel.Attach(_hud, Undo, _hud);

		// 拖拽走"起止"两条边而不是"每一步"：一次手势只产出 1 条历史。
		// 若每帧记一条，拖一张牌 100 帧就是 100 条，Ctrl+Z 要按 100 次。
		_viewport.PrimaryDragStarted += _ => Undo.BeginGesture();
		_viewport.PrimaryReleased += _ => Undo.EndGesture();

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
