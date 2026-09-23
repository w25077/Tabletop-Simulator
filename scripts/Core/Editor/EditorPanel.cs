using Godot;
using TabletopSimulator.Core.Objects;

namespace TabletopSimulator.Core;

/// <summary>
/// 运行时编辑器（M5）：游戏内按 <c>F1</c> 打开的编辑面板，四个分页
/// —— 卡牌 / 卡组 / 桌面 / 区域。
///
/// <b>为什么是"运行时编辑器"而不是一个独立的编辑器程序：</b>
/// 这个工具的全部价值在于"改完立刻开玩"。独立编辑器要来回切换、要重启、
/// 要重新读档，而那正是验玩法时最费时间的一段。做进游戏里之后，
/// 循环变成"改一个数字 → 抬头看桌上的牌变了 → 接着拖牌试"。
///
/// 四条设计约定（都是 M4 的教训换来的）：
/// <list type="number">
/// <item><b>节点在代码里建，不进 <c>Main.tscn</c>。</b>少一处要手工同步的场景文件
///   （M3 被"编辑器把内存里的旧场景写回去"坑过一次）。</item>
/// <item><b>锚点在进树之后设。</b>在 <c>AddChild</c> 之前设 anchors/offsets，
///   矩形会算成 0 高 —— 而那个面板能穿过"位置对 / 宽度对 / Visible 为真"全部判据。
///   所以 <see cref="Build"/> 在 <c>_Ready</c> 里。</item>
/// <item><b>改动立即生效，落盘要显式保存</b>（用户拍板）。顶栏因此必须显示
///   "未保存"——不然随手调的数值会在切存档时无声消失。</item>
/// <item><b>开着的时候要挡住键盘快捷键。</b>否则在名称输入框里按 <c>d</c> 会抽牌、
///   按 <c>f</c> 会翻面。做法是 <c>_UnhandledKeyInput</c> 里把按键吃掉并标记已处理。</item>
/// </list>
/// </summary>
[GlobalClass]
public partial class EditorPanel : Control
{
	/// <summary>分页次序，同时也是 <c>TabContainer</c> 里的下标。</summary>
	public const int TabCards = 0;
	public const int TabTokens = 1;
	public const int TabDecks = 2;
	public const int TabBoard = 3;
	public const int TabZones = 4;

	private ObjectManager _objects = null!;
	private ZoneManager _zones = null!;
	private Board _board = null!;
	private BoardCamera _camera = null!;
	private Hud _hud = null!;

	private TabContainer _tabs = null!;
	private Label _dirtyLabel = null!;
	private Label _statusLabel = null!;

	private CardEditorPage _cardPage = null!;
	private TokenEditorPage _tokenPage = null!;
	private DeckEditorPage _deckPage = null!;
	private BoardEditorPage _boardPage = null!;
	private ZoneEditorPage _zonePage = null!;
	private ZoneDrawOverlay _overlay = null!;

	/// <summary>面板是否打开。<b>键盘快捷键的闸门看它。</b></summary>
	/// <summary>
	/// 面板是否<b>逻辑上</b>开着。
	///
	/// <b>它不等于 <c>Visible</c>，这是 M5.5 P2 用一次真 bug 换来的：</b>
	/// 原先这里是 <c>IsOpen =&gt; Visible</c>，而 P2 要"画区域时把面板收起来"——
	/// 于是收起面板（<c>Visible = false</c>）当场把 <see cref="IsOpen"/> 也变成了假，
	/// <c>RestoreFromDrawMode</c> 里那句 <c>if (IsOpen)</c> 永远不成立，
	/// <b>面板再也回不来</b>。症状是四条 <c>Esc</c> 断言全红，而 <c>Esc</c> 的逻辑本身没错。
	///
	/// 教训：一个属性一旦被两种语义共用（"用户开着它吗" vs "它现在画在屏幕上吗"），
	/// 迟早会有一条路把它们拆开。所以拆开这件事要在需要之前就做掉。
	/// </summary>
	public bool IsOpen => _logicalOpen;

	/// <summary>逻辑开关（由 <see cref="Open"/> / <see cref="Close"/> 维护）。</summary>
	private bool _logicalOpen;

	/// <summary>当前分页（自检用：确认 <c>SwitchTab</c> 真的切了）。</summary>
	public int CurrentTab => _tabs.CurrentTab;

	/// <summary>标题文字（自检用：不带 <c>pass</c> 键的判据也要有可读证据）。</summary>
	internal string TitleForTest => _dirtyLabel.Text;

	internal int KeySeen { get; private set; }

	internal string KeySeenTrace { get; private set; } = "";

	internal int CloseCount { get; private set; }

	internal string CloseTrace { get; private set; } = "";

	internal string EscapeTrace { get; private set; } = "";

	internal int OpenCount { get; private set; }

	internal string OpenTrace { get; private set; } = "";

	/// <summary>调用栈里属于本项目的头几帧（诊断用）。</summary>
	internal static System.Collections.Generic.List<string> Callers()
	{
		var frames = new System.Collections.Generic.List<string>();
		foreach (string line in System.Environment.StackTrace.Split('\n'))
		{
			if (line.Contains("TabletopSimulator") && frames.Count < 4)
				frames.Add(line.Trim().Split(" in ")[0]);
		}

		return frames;
	}

	/// <summary>
	/// <see cref="RefreshStatus"/> 被调用了几次（自检用）。
	///
	/// 存在的理由与 M4 的 <c>PushTrace</c> 一样：<b>把猜换成读。</b>
	/// 这一次是"改了定义，顶栏却还写着已保存"——光看一个 <c>false</c>
	/// 完全分不出是"信号没到"、"到了但没刷"、还是"刷了但读到的是别的值"。
	/// </summary>
	internal int StatusRefreshCount { get; private set; }

	/// <summary>定义改动信号到达面板的次数（自检用，与上一个分开数：前者含主动调用）。</summary>
	internal int DefinitionSignalCount { get; private set; }

	internal string StatusForTest => _statusLabel.Text;

	public CardEditorPage CardPage => _cardPage;

	public TokenEditorPage TokenPage => _tokenPage;

	public DeckEditorPage DeckPage => _deckPage;

	public BoardEditorPage BoardPage => _boardPage;

	public ZoneEditorPage ZonePage => _zonePage;

	/// <summary>画区域的遮罩层（自检要直接驱动它，验"整条拖拽"而不只是服务方法）。</summary>
	internal ZoneDrawOverlay Overlay => _overlay;

	/// <summary>
	/// 是否正为了"画区域"而把面板收起来（M5.5 P2）。
	///
	/// <b>为什么用"藏起来"而不是"关掉"：</b><see cref="IsOpen"/> 是<b>逻辑</b>开关，
	/// 由 <c>F1</c> / 顶栏按钮 / <c>Esc</c> 决定；而"画区域时面板别挡着画布"是<b>视觉</b>状态。
	/// 两者混在一起的话，收起面板就会被记成"用户关掉了编辑器" ——
	/// 画完之后面板回不来，而且 <c>Esc</c> 的语义也跟着乱（第一次按应该退出画区域模式，
	/// 而不是"关掉一个已经关掉的面板"）。
	/// </summary>
	internal bool CollapsedForDraw { get; private set; }

	/// <summary>
	/// 自检用：把面板的可见性钉住，别让"退出画区域模式"之类把它复原
	/// （<c>RestoreFromDrawMode</c> 见到 <see cref="IsOpen"/> 就会把 <c>Visible</c> 设回来）。
	///
	/// 存在的理由是实测出来的一条：<c>editor_simulation</c> 收尾时面板还开着，
	/// 紧跟其后的存档探针于是<b>把面板拍进了缩略图</b>（画面中间一整片是面板底色），
	/// 而"缩略图里是不是桌子"这条断言读起来像产品坏了。
	/// 有了它，探针就能明确地摆出"面板开着"这一种局面。
	/// </summary>
	internal bool AlwaysVisible { get; set; }

	/// <summary>自检用：摆成"面板开着 / 关着"的样子，并返回它是否真的落了地。</summary>
	internal bool SetVisibleForTest(bool visible)
	{
		AlwaysVisible = visible;
		Visible = visible;
		return Visible == visible;
	}

	/// <summary>
	/// 进入"画区域"模式时把面板收起来（用户实测反馈第 4 条：
	/// "在画布上拖矩形点击后没有关闭当前 F1 界面，无法紧跟着划区域"）。
	///
	/// 面板实测量到的是 1904×996，几乎盖满屏幕 —— 不收起来的话，
	/// 用户能画的只有面板之外的一圈，而"整个画布"听起来却不是那个意思。
	///
	/// 遮罩层之所以不受影响：它在场景里是 <c>HudRoot</c> 的<b>兄弟</b>节点
	/// （不是面板的子节点），所以藏面板不会连带藏掉它。
	/// </summary>
	internal void CollapseForDrawMode()
	{
		if (CollapsedForDraw)
			return;

		CollapsedForDraw = true;
		if (!AlwaysVisible)
			Visible = false;
		CollapseTrace += $"[f{Engine.GetProcessFrames()} collapse open={IsOpen} vis={Visible}] ";
	}

	/// <summary>
	/// 画完一块 / 退出模式时把面板放回来。
	///
	/// <see cref="CollapseTrace"/> 是这条链路专用的只读时序日志 ——
	/// 它存在的理由与 M4 那几个计数器一样（"把猜换成读"）：这一条改动里，
	/// "面板有没有回来"同时取决于折叠标志、逻辑开关、可见性三者，
	/// 而"回不来"的症状在报告里只表现为一行 <c>false</c>，读不出是哪一步没接上。
	/// </summary>
	internal void RestoreFromDrawMode()
	{
		if (!CollapsedForDraw)
		{
			CollapseTrace += $"[f{Engine.GetProcessFrames()} restore-skip open={IsOpen} vis={Visible}] ";
			return;
		}

		CollapsedForDraw = false;

		// 只有"面板逻辑上就该开着"时才恢复 —— 用户在画区域途中按 F1 关掉编辑器的话，
		// 收模式时不该把一个已被关掉的面板重新显示出来。
		//
		// 这里判的必须是 <see cref="IsOpen"/>（逻辑），不能是 <c>Visible</c>：
		// 收起面板正是把 Visible 设成 false 的那一步。
		//
		// 自检钉住可见性时（<see cref="AlwaysVisible"/>）跳过这一步：
		// 探针要摆的是"面板开着 / 关着"这一种局面，不该被收模式顺手改掉。
		if (IsOpen && !AlwaysVisible)
			Visible = true;

		CollapseTrace += $"[f{Engine.GetProcessFrames()} restore open={IsOpen} vis={Visible}] ";
	}

	/// <summary>折叠 / 恢复的时序（自检诊断用）。</summary>
	internal string CollapseTrace { get; private set; } = "";

	/// <summary>
	/// 把场景里那个编辑器面板接上依赖并返回它。
	///
	/// <b>它不再新建节点</b>（【项目约定】禁止动态生成节点）：面板与遮罩层都在
	/// <c>Main.tscn</c> 里搭好、挂在 <c>HudRoot</c> 下、排在存档面板之后
	/// （全屏面板先加会挡住顶栏那两个按钮）。
	///
	/// 依赖仍然在这个方法里塞进字段，而不是让 <c>_Ready</c> 自己去 <c>GetNode</c> 找
	/// 物件系统 / 区域系统 —— 那些是**同级的兄弟节点、且由 <c>Main</c> 装配**，
	/// 面板自己去猜路径只会多一处会漂移的耦合。
	/// </summary>
	public static EditorPanel Attach(
		CanvasLayer layer, ObjectManager objects, ZoneManager zones, Board board,
		BoardCamera camera, Hud hud, ViewportController viewport)
	{
		EditorPanel panel = layer.GetNode<EditorPanel>("HudRoot/EditorPanel");

		panel._objects = objects;
		panel._zones = zones;
		panel._board = board;
		panel._camera = camera;
		panel._hud = hud;
		panel._viewport = viewport;

		return panel;
	}

	/// <summary>
	/// 「拖动区域边框改大小」（M5.5 P2-4b）。<b>这个对象是纯逻辑，不是节点</b> ——
	/// 它按项目约定走 <c>ViewportController</c> 的"被问"接口拿事件，
	/// 不需要进场景树，也就不存在"动态生成节点"的问题（见 HANDOFF 第 29 条）。
	/// </summary>
	public ZoneResizer Resizer { get; } = new();

	private ViewportController _viewport = null!;

	/// <summary>
	/// <b>刻意不在 <c>_Ready</c> 里建界面 / 挂信号。</b>
	///
	/// 面板现在活在 <c>Main.tscn</c> 里，于是它的 <c>_Ready</c> 跑在
	/// <c>Main._Ready</c> <b>之前</b>（Godot 是子节点先 <c>_Ready</c>）——
	/// 而那会儿 <see cref="Attach"/> 还没把物件系统 / 区域系统 / 相机塞进来。
	/// 就地建界面会一路 NRE（第一次改完就是这么炸的：<c>CurrentPage()</c> 里 `_tabs` 还是 null）。
	///
	/// 所以初始化改成 <c>Main</c> 在装配完之后显式调一次 <see cref="Initialize"/>。
	/// 这与 M5 那条"刷新要排在整棵树建完之后"是同一个道理，只是"谁先谁后"换了个方向。
	/// </summary>
	public override void _Ready()
	{
	}

	/// <summary>
	/// 装配完成之后调一次：取场景里的控件、把页挂进页签容器、接信号。
	/// 由 <c>Main._Ready</c> 在 <see cref="Attach"/> 之后立刻调用。
	/// </summary>
	public void Initialize()
	{
		Build();

		// ---- 「改区域大小」的钩子接线（M5.5 P2-4b）----
		//
		// 钩子交给 ViewportController（"输入只有一处入口"），
		// 鼠标移动订阅它自己的 PointerMoved 信号 —— 不另开一条输入路径。
		Resizer.Bind(_zones, _camera);
		_viewport.PrimaryPressHook = Resizer;
		_viewport.PointerMoved += Resizer.NotifyPointerMoved;
		_tabs.TabChanged += _ => SyncResizer();

		// 区域是动态增删的（画一块 / 删一块 / 读档换一桌），所以"该不该有把手"
		// 不能只在开关那一刻算一次 —— 开着编辑器新画一块，它的把手要立刻出现。
		_zones.ZonesChanged += Resizer.SyncHandles;

		// 整个面板建完之后才让各页刷一次。
		//
		// <b>这一步必须排在 Build() 之后，而各页的 <c>_Ready</c> 里不能做这件事。</b>
		// Godot 是"子节点先 _Ready"，页被 AddChild 进页签容器时面板还没建完，
		// 那一刻页脚那两个标签字段还是 null —— 于是 OnShown 链上的
		// <c>RefreshStatus</c> 会抛 <c>NullReferenceException</c>。
		// 第一次跑就这么炸了 7 条，而面板看起来完全正常、<b>自检也全绿</b>。
		foreach (EditorPage page in Pages())
			OnShownSafely(page);

		RefreshStatus();

		// 定义改了 → 顶栏的"未保存"要跟着变。挂在这里而不是每个控件各自调：
		// 控件的调用点会越加越多，漏掉一个的症状是"改了东西却没显示未保存"。
		//
		// <b>这一条是自检逼出来的：</b>探针里"改名字 → 立刻读顶栏"第一次是红的，
		// 因为那次改动走的是服务层（<c>CardDefinitionService.MutateCard</c>），
		// 没经过面板的 <c>NotifyChanged</c>。人从面板上改确实会调它，
		// 但"只有从面板改才显示未保存"本身就是个不该有的约束 ——
		// 那个标记说的是<b>存档的状态</b>，不是"谁改的"。
		_objects.CardDefinitionChanged += _ =>
		{
			DefinitionSignalCount++;
			RefreshStatus();
		};
		_objects.TokenDefinitionChanged += _ =>
		{
			DefinitionSignalCount++;
			RefreshStatus();
		};

		// 顶栏那个「编辑器　F1」按钮 → 开关面板（M5.5 第 1 条）。
		//
		// 走 HUD 的回调而不是让 HUD 直接持有面板类型：HUD 是常驻界面，
		// 编辑器是可有可无的一层，方向不该反过来。
		_hud.EditorToggleRequested = Toggle;
		_hud.SyncEditorButton(IsOpen);

		_built = true;
	}

	/// <summary>
	/// 界面是否已经建完。
	///
	/// <b>这个标志是必须的，不是防御性编程的洁癖：</b>建界面的过程中会触发
	/// 一些回调（控件初值 → <c>Toggled</c> → 页里的 <c>Mutate</c> → 面板的
	/// <see cref="NotifyChanged"/>），而那会儿页脚的标签还不存在。
	/// 只看"面板是否已在场景里"是不够的 —— 面板在 <c>_Ready</c> 中途就已经在树里了。
	/// </summary>
	private bool _built;

	private void Build()
	{
		// 【项目约定】面板的骨架（Bg / Column / Header / Tabs / Footer）与遮罩层
		// <b>都在 Main.tscn 里搭好</b>（挂在 HudRoot 下、排在其它 HUD 之后），
		// 这里只取来接线。矩形与锚点在场景里：左右各留 8px、上让开顶栏、下让开提示条。
		//
		// （M5 时这里是纯代码建树，理由是"少一处要手工同步的场景文件"；
		//   用户 2026-09-21 明确要求改成场景搭建，见 docs/HANDOFF.md 第 29 条。）
		_dirtyLabel = GetNode<Label>("Bg/Column/Header/DirtyLabel");
		_statusLabel = GetNode<Label>("Bg/Column/Footer/StatusLabel");
		_tabs = GetNode<TabContainer>("Bg/Column/Tabs");

		GetNode<Button>("Bg/Column/Header/CloseButton").Pressed += Close;
		GetNode<Button>("Bg/Column/Footer/SaveButton").Pressed += () => _hud.RequestSave();

		// 五个分页是**场景实例**（scenes/editor/*.tscn）—— 在编辑器里打开
		// Main.tscn 就能看到它们、双击进去改它们的内部。
		//
		// 用强类型的 GetNode 而不是"缺了就造一个"：后者会把
		// "忘了往场景里加一页"这件事藏起来 —— 面板看起来正常，
		// 只有退出编辑器重新打开场景时才会发现那一页不在文件里。
		_cardPage = _tabs.GetNode<CardEditorPage>("CardPage");
		_tokenPage = _tabs.GetNode<TokenEditorPage>("TokenPage");
		_deckPage = _tabs.GetNode<DeckEditorPage>("DeckPage");
		_boardPage = _tabs.GetNode<BoardEditorPage>("BoardPage");
		_zonePage = _tabs.GetNode<ZoneEditorPage>("ZonePage");

		foreach (EditorPage page in Pages())
		{
			page.Bind(_objects, _zones, _board, _camera, _hud, this);
			page.Initialize();
		}

		// 画区域的遮罩层铺满整个视口（原因见 ZoneDrawOverlay 的说明），
		// 挂在<b>同一个 HudRoot 下、面板之后</b>（后添加 = 画在上面）。
		//
		// <b>它不能是面板的子节点。</b>第一版就是那样写的，结果它的矩形恒为 0 ——
		// 因为面板自己 <c>Visible = false</c>，而<b>不可见的 Control 子树会被跳过布局</b>。
		// 于是"画区域"点下去什么都不发生：遮罩零尺寸 → 收不到鼠标事件。
		// 这与 M4 那条"0 高度面板"是同一个坑的另一种长相：
		// <b>矩形为 0 的控件不会报错，它只是永远收不到输入。</b>
		// 面板自己现在就挂在 <c>HudRoot</c> 下，所以遮罩层是它的**兄弟**：
		// 路径从父节点（HudRoot）算，而不是从 CanvasLayer 算。
		_overlay = GetParent().GetNode<ZoneDrawOverlay>("ZoneDrawOverlay");
		_overlay.Bind(_zonePage, _zones, _camera, _hud, this);
		_zonePage.BindOverlay(_overlay);
	}


	// ------------------------------------------------------------------ 开关

	/// <summary>
	/// <c>F1</c> 开关。
	///
	/// <b>两件事必须做，而且第二件容易被漏掉：</b>
	/// <list type="number">
	/// <item>切换面板可见性。</item>
	/// <item><b>打开时清掉选中集。</b>否则面板盖住的那半边桌面上，
	///   选中的牌还带着亮黄描边，而用户此刻的注意力在表单上 ——
	///   那些描边看起来像"面板里的某个东西选中了它们"。</item>
	/// </list>
	/// </summary>
	/// <summary>本面板看到过的按键总数（只读诊断；见 <c>_UnhandledKeyInput</c> 的说明）。</summary>
	internal static int KeySeenTotal { get; private set; }

	public override void _UnhandledKeyInput(InputEvent @event)
	{
		if (@event is InputEventKey probe && probe.Pressed && !probe.Echo)
		{
			KeySeen++;
			KeySeenTotal++;
			KeySeenTrace = $"keycode={probe.Keycode}({(long)probe.Keycode}) frame={Engine.GetProcessFrames()} open={IsOpen}";
		}
		if (@event is not InputEventKey key || !key.Pressed || key.Echo)
			return;

		if (key.IsActionPressed("tt_editor"))
		{
			Toggle();
			GetViewport().SetInputAsHandled();
			return;
		}

		// <b>`Esc` 的判定要排在 `Visible` 早退之前。</b>
		//
		// M5.5 P2 把"画区域"改成了<b>收起面板</b>（`Visible = false`），
		// 而这一行原本是 `if (!Visible) return;` —— 于是收起的瞬间，
		// `Esc` 就再也轮不到面板：症状是"按 Esc 退不出画区域模式"。
		//
		// 真实运行里遮罩层会在 `_Input` 里先兜住这一下（所以界面上还能用），
		// 但那是"谁先被派发到"决定的，不该是两级退出语义的依靠 ——
		// 自检直接喂 `_UnhandledKeyInput` 就把它抓出来了（四条断言全红）。
		//
		// 所以：逻辑上开着（`IsOpen`）就一直处理 `Esc`，并在处理时按需恢复可见性。
		if (HandleEscape(key))
		{
			GetViewport().SetInputAsHandled();
			return;
		}

		if (!Visible)
			return;

		// ---- 撤销 / 重做（M5 遗留口子之四）----
		//
		// <b>这一段必须排在下面"吃掉其余按键"之前，而且必须在这里、不在物件系统里。</b>
		// 实测逼出来的：把路由写在 <c>ObjectManager._UnhandledKeyInput</c> 时，
		// <c>Ctrl+Z</c> 压根到不了那里 —— 它自带的只读计数器
		// （<c>KeyEventsSeen</c>）在按下前后都是 19，一动不动。
		// 原因是派发次序：面板开着时是**这一层**先看到按键，
		// 而下面那行 <c>SetInputAsHandled</c> 把一切都吃掉了。
		//
		// 分工因此是这样：
		// <list type="bullet">
		// <item><b>面板开着</b> → 这里处理：<c>Ctrl+Z</c> 走编辑器栈；
		//   <c>Ctrl+Alt+Z</c> 显式转发给主栈（想撤刚才拖的那一下，不必先关面板）。</item>
		// <item><b>面板关着</b> → 物件系统那条老路，走主栈。</item>
		// </list>
		if (HandleHistoryKeys(key, out bool forwardToTable))
		{
			GetViewport().SetInputAsHandled();
			return;
		}

		// 显式转发给主栈（<c>Ctrl+Alt+Z</c>）：<b>这里必须 return，不能往下走到"吃掉"那行</b>
		// —— 否则转发不出去，而症状与"这个键没绑上"一模一样。
		if (forwardToTable)
			return;

		// 面板开着时吃掉其余按键：否则在名称输入框里按 d 会抽牌、f 会翻面。
		//
		// 走 _UnhandledKeyInput 的天然好处：**焦点在 LineEdit 里时这个函数根本不会被调用**
		// （控件先吃掉打字键），所以"输入框里按 Ctrl+Z 是撤销文字"这件事自动成立，
		// 不需要自己判断焦点。
		GetViewport().SetInputAsHandled();
	}

	/// <summary>
	/// 面板开着时的撤销 / 重做。返回值 = "这个键我处理掉了"。
	///
	/// 两条规则：
	/// <list type="number">
	/// <item><b><c>Ctrl+Z</c> / <c>Ctrl+Shift+Z</c> / <c>Ctrl+Y</c> 走编辑器栈</b>
	///   —— 面板开着时用户改的是定义，他要退的也是定义。</item>
	/// <item><b><c>Ctrl+Alt+Z</c> 显式转发给主栈</b>（<paramref name="forwardToTable"/> 为真）
	///   —— 面板开着也能退桌面动作。没有它的话，"刚拖完一张牌、想退回去"
	///   就得先关面板、按 Z、再开面板。</item>
	/// </list>
	///
	/// 编辑器栈空时<b>不吞键</b>：返回 <c>false</c> 且不转发，让按键继续往下走
	/// （于是面板开着、编辑器历史空着时，<c>Ctrl+Z</c> 仍然能退桌面动作 ——
	/// 而那正是这时候用户唯一想要的结果）。
	/// </summary>
	private bool HandleHistoryKeys(InputEventKey key, out bool forwardToTable)
	{
		forwardToTable = false;

		bool undo = key.IsActionPressed("tt_undo");
		bool redo = key.IsActionPressed("tt_redo");

		// <c>tt_undo_table</c>（Ctrl+Alt+Z）也是 <c>tt_undo</c> 的超集，
		// 所以这一条要先判，否则它会被当成"编辑器撤销"接走。
		if (key.IsActionPressed("tt_undo_table"))
		{
			forwardToTable = true;
			return false;
		}

		if (!undo && !redo)
			return false;

		if (_editorUndo is null)
			return false;

		bool can = redo ? _editorUndo.CanRedo : _editorUndo.CanUndo;
		if (!can)
		{
			// 编辑器没有可退的了 → 不吞这个键，让它去退桌面动作。
			return false;
		}

		string label = redo ? _editorUndo.RedoLabel : _editorUndo.UndoLabel;
		bool did = redo ? _editorUndo.Redo() : _editorUndo.Undo();

		if (did)
		{
			OnDefinitionUndone();
			_hud.Toast($"编辑器：{label}");
		}

		return true;
	}

	/// <summary>
	/// <c>Esc</c> 的两级退出：<b>先收掉"画区域"模式，再关面板</b>。
	///
	/// 两级而不是一级，是因为画区域模式有它自己的"半开着"状态：
	/// 用户按下 <c>Esc</c> 想收的是那个模式，而面板一并关掉的话，
	/// 想接着画第二块就得再按一次 <c>F1</c> 并重新找到那一页。
	///
	/// <b>正拖着矩形时不响应</b>：那一下松手的本意是"放弃这一块"，
	/// 而面板若在拖拽中途关掉，遮罩层会连拖动状态一起清掉 ——
	/// 看起来像"拖到一半整个界面闪了一下"。所以先松手、再按 <c>Esc</c>。
	///
	/// 做成"一个按键事件进、布尔出"而不是直接读 <c>Input</c>：自检要能<b>合成一次
	/// <c>Esc</c></b> 并读回"它消费掉了吗"，否则这一条只验得了"方法被调过"。
	/// </summary>
	/// <returns>是否消费掉了这次 <c>Esc</c>。</returns>
	internal bool HandleEscape(InputEventKey key)
	{
		EscapeTrace += $"[{Engine.GetProcessFrames()} " +
			$"code={(long)key.Keycode} press={key.Pressed} open={IsOpen} " +
			$"draw={_zonePage?.DrawMode} drag={_overlay?.IsDragging}] ";

		if (key.Keycode != Key.Escape)
			return false;

		if (_zonePage is not null && _zonePage.DrawMode)
		{
			if (_overlay is not null && _overlay.IsDragging)
				return false;

			_zonePage.CancelDrawMode();
			_hud.Toast("已退出画区域模式（再按一次 Esc 关闭编辑器）");
			return true;
		}

		Close();
		return true;
	}

	public void Toggle()
	{
		if (IsOpen)
			Close();
		else
			Open();
	}

	public void Open()
	{
		OpenCount++;
		OpenTrace = $"[f{Engine.GetProcessFrames()} open={OpenCount} close={CloseCount} " +
			$"wasOpen={IsOpen} vis={Visible} callers={string.Join(" < ", Callers())}] ";

		_logicalOpen = true;
		Visible = true;
		_objects.ClearSelection();
		RefreshStatus();
		OnShownSafely(CurrentPage());
		_hud.SyncEditorButton(true);
		SyncResizer();
	}

	/// <summary>
	/// 把"改区域大小"的启用状态同步给 <see cref="Resizer"/>。
	///
	/// 判据是"编辑器开着<b>且停在「区域」页</b>"：把手与拖拽是编辑期的东西，
	/// 平时待在别的页或关着编辑器时，桌面上冒出一圈黄方块只会让人以为桌子坏了。
	/// 开关面板、切页、关面板三处都要调 —— 所以收成一个方法，而不是散在三处各写一遍。
	/// </summary>
	private void SyncResizer()
	{
		bool onZonesTab = CurrentPage() is ZoneEditorPage;
		Resizer.SetEnabled(IsOpen && onZonesTab);
	}

	public void Close()
	{
		CloseCount++;
		CloseTrace = $"[f{Engine.GetProcessFrames()} open={OpenCount} close={CloseCount} " +
			$"from={string.Join(" < ", Callers())}] ";

		// 逻辑开关先落下来，再收画区域模式 —— 顺序反了的话
		// CancelDrawMode → RestoreFromDrawMode 会看到"面板还开着"而把它重新显示出来，
		// 紧跟着这一行又把它藏掉：结果对，但中间闪一下。
		_logicalOpen = false;
		Visible = false;

		// 关面板要把"画区域"那种模式收掉，否则回到桌面还是画矩形模式，
		// 而画布上没有任何东西提示这件事 —— 表现为"拖拽突然不能拖牌了"。
		_zonePage?.CancelDrawMode();
		_hud.SyncEditorButton(false);
		SyncResizer();   // 把手也跟着收掉（`IsOpen` 已经是 false，这里必然关）
		GetViewport().SetInputAsHandled();
	}

	/// <summary>切分页（自检与潜在的命令行入口都走它）。</summary>
	public void SwitchTab(int index)
	{
		if (index < 0 || index >= _tabs.GetTabCount())
			return;

		_tabs.CurrentTab = index;
		OnShownSafely(CurrentPage());
		SyncResizer();
	}

	/// <summary>
	/// 打开编辑器并停在"桌上这一个物件"上（右键菜单「编辑这张」的落点）。
	///
	/// 它做三件事，缺一不可：
	/// <list type="number">
	/// <item>开面板（用户可能压根没开过编辑器）；</item>
	/// <item>切到对应那一页（卡 / 指示物）；</item>
	/// <item>把<b>实例</b>交给那一页 —— 于是字段表改的是"这一张"，
	///   而不是整副共用同一份定义的牌。</item>
	/// </list>
	///
	/// 第三步是这个入口存在的全部理由：实例级覆盖的数据层从 M2 就有
	/// （<c>CardObject.SetFieldOverride</c>，也早就进了快照与撤销），
	/// 但编辑器一直没有暴露它 —— 用户在桌上想调"这一张牌的费用"，
	/// 只能改定义、于是牌库里另外三张同名卡一起变了。
	/// </summary>
	public void OpenForInstance(string kind, string uid)	{
		if (!IsOpen)
			Open();

		if (kind == "token")
		{
			SwitchTab(TabTokens);
			_tokenPage.EditInstance(uid);
		}
		else
		{
			SwitchTab(TabCards);
			_cardPage.EditInstance(uid);
		}
	}

	/// <summary>自检用：按 uid 找桌上那个物件，再走一遍「编辑这张」的完整路径。</summary>
	internal bool OpenForInstanceUidForTest(string uid)
	{
		foreach (TabletopObject obj in _objects.AllObjects)
		{
			if (obj.Uid != uid)
				continue;

			OpenForInstance(obj is TokenObject ? "token" : "card", uid);
			return true;
		}

		return false;
	}

	/// <summary>
	/// 把编辑器撤销栈接进来（由 <c>Main</c> 在装配完之后调一次）。
	///
	/// <b>不放在构造函数里</b>：那条栈的快照依赖"定义池已经装好"，
	/// 而定义池是 <c>Main._Ready</c> 前半段才装配的。
	/// </summary>
	internal void BindEditorUndo(EditorUndo editorUndo) => _editorUndo = editorUndo;

	private EditorUndo? _editorUndo;

	/// <summary>自检用：编辑器撤销栈（可能为 null —— 没接线时）。</summary>
	internal EditorUndo? EditorUndoForTest => _editorUndo;

	/// <summary>
	/// 记一次编辑器改动。
	///
	/// <b>它是"编辑器改了东西"的唯一收口</b>：五个分页的每一个改动点都已经在调
	/// <see cref="NotifyChanged"/>（那是"存档脏了"的信号），所以记录挂在这里
	/// 就等于<b>自动覆盖全部改动点</b>，不必去 30 个调用点各加一行 ——
	/// 而"各加一行"正是最容易漏一处的那种写法，漏掉的那处表现是
	/// "改那个东西之后 Ctrl+Z 退不回去"，其余地方都正常。
	///
	/// <b>没变化就不进历史</b>由 <see cref="EditorUndo.Record"/> 自己判
	/// （它比的是整份定义快照），所以"NotifyChanged 被调了但什么都没改"
	/// 这类情况不会污染历史。
	/// </summary>
	public void NotifyChanged(string label = "", string mergeKey = "")
	{
		NotifyStatusOnly();
		NotifyChangedCalls++;

		// 写回快照期间不许记录（否则撤销自己产出新历史）。
		if (_editorUndo is null || _editorUndo.IsApplying)
			return;

		// <b>"正在刷界面"期间也不许记录。</b>
		//
		// 这条是实测逼出来的：<c>OnShown</c> / <c>RefreshDetail</c> 会把定义里的值
		// 重新写进表单控件，而<b>写进控件本身会触发回调</b>
		// （<c>LineEdit.TextChanged</c> → 页里的 <c>Mutate</c> → 这里）。
		// 于是"打开一页"或"选中另一张卡"都会记一条历史 —— 而那是刷新，不是用户的改动。
		//
		// 它的后果不只是多几条：那些噪声记录会<b>把用户真正的改动合并进自己</b>
		// （连击合并按 <c>mergeKey</c> 与时间窗判定），于是"改了一个字段"之后
		// 历史条数纹丝不动 —— 自检里那条 <c>editor_undo_records_override</c>
		// 就是这么红的（读到 <c>attempts=33</c> 而 <c>Count</c> 只涨了 0）。
		if (_refreshing)
		{
			NotifyChangedSuppressedRefreshing++;
			return;
		}

		_editorUndo.Record(label.Length > 0 ? label : "编辑器修改", mergeKey);
	}

	/// <summary>只读计数器：<see cref="NotifyChanged(string)"/> 被调了几次、其中几次因刷新被挡。</summary>
	internal int NotifyChangedCalls { get; private set; }

	internal int NotifyChangedSuppressedRefreshing { get; private set; }

	/// <summary>正在刷界面（<see cref="OnShownSafely"/> 的执行窗口）。</summary>
	private bool _refreshing;

	/// <summary>
	/// 安全地让一页刷新自己：<b>这期间发生的一切都不进历史</b>。
	///
	/// 切换页签 / 打开面板 / 撤销写回之后都走它 —— 那些时刻的共同点是
	/// "界面在读模型"，而不是"用户在改模型"。
	/// </summary>
	public void OnShownSafely(EditorPage? page)
	{
		_refreshing = true;
		try
		{
			page?.OnShown();
		}
		finally
		{
			_refreshing = false;

			// <b>刷完之后把历史基线对齐到现场。</b>
			//
			// 回填表单可能触发一个"提交"回调（最典型的是输入框失焦 → <c>FocusExited</c>），
			// 而 <see cref="_refreshing"/> 只挡住了"记录"，挡不住那个回调<b>改模型</b>。
			// 不对齐的话，下一次真正改动的 <c>before</c> 会指向这一串刷新之前的状态 ——
			// 用户按一次 Ctrl+Z 退回去的是一份更老的快照，
			// 而报告里只看到"历史条数没涨"。
			_editorUndo?.SyncBaseline();
		}
	}

	/// <summary>
	/// 编辑器撤销 / 重做<b>写回之后</b>调一次：让界面跟上被改回去的定义。
	///
	/// <b>为什么不走 <see cref="NotifyChanged(string)"/>：</b>那不是一次新的改动
	/// （走它会在历史里多出一条净效果为零的记录，用户按 Ctrl+Z 会连按几次空步）。
	/// 它要的只是"把现在这份定义重新显示出来"。
	///
	/// 三件事都要做：
	/// <list type="number">
	/// <item>当前页 <c>OnShown</c>（列表 / 表单 / 预览）；</item>
	/// <item>物件系统把新定义推给场上的实例 —— 不做的话桌面上那些卡还在画旧卡面
	///   （"撤销了但桌子没变"）；</item>
	/// <item>顶栏状态（脏标记）。</item>
	/// </list>
	/// </summary>
	internal void OnDefinitionUndone()
	{
		OnShownSafely(CurrentPage());
		_objects.NotifyDefinitionsReloaded();
		NotifyStatusOnly();
	}

	/// <summary>
	/// 某一页改了东西之后调用：<b>只重算顶栏的"未保存"标记与底栏摘要。</b>
	///
	/// <b>它刻意不刷任何页的内容。</b>第一版这里调了 <c>CurrentPage()?.OnShown()</c>，
	/// 于是每敲一个字符都重刷整页 —— 而 Token 页与区域页的 <c>OnShown</c> 会
	/// 重建列表（销毁并重建整列按钮）、并把表单里每个控件的 <c>Text</c>/<c>Value</c> 重写一遍。
	/// <b>那会把键盘焦点抢走</b>，症状是"每次只能在输入框里输入一个字符，
	/// 想打五个数字要用鼠标点五次"。
	///
	/// 所以规矩改成：<b>改模型与刷界面分开</b>——
	/// <list type="bullet">
	/// <item>这里只管"存档脏了"这件事（顶栏要变）</item>
	/// <item>谁真的改了<b>列表结构</b>（新建 / 删除 / 换选中项 / 加删字段），
	///   谁自己调那一页的刷新方法</item>
	/// <item>正在输入的输入框<b>永远不许被重写</b>（见 <see cref="EditorPage.KeepEditingText"/>）</item>
	/// </list>
	///
	/// 代价是"删完卡切到卡组页，卡组还显示着那一行"这类跨页不一致需要各页自己管 ——
	/// 而那个代价比"打字要一直点鼠标"小得多，且每页的刷新点都在它自己的动作里，看得见。
	/// </summary>
	/// <summary>
	/// 只重算顶栏与底栏（<b>不记录历史</b>）。
	///
	/// 与 <see cref="NotifyChanged(string)"/> 分开是为了两个诚实的用途：
	/// <list type="bullet">
	/// <item>撤销 / 重做<b>写回</b>之后要刷新状态，但那不是"一次新的改动"；</item>
	/// <item>"按实例编辑"的目标变了、字段标记变了这类<b>纯界面</b>变化，
	///   状态要更新，但定义一个字都没动。</item>
	/// </list>
	/// 合成一个方法的话，第二类会被记成一条净效果为零的历史 ——
	/// 用户按 Ctrl+Z 时会连按几次空步。
	/// </summary>
	public void NotifyStatusOnly()
	{
		// 建界面中途控件会触发自己初值的回调（勾选框 → 页里的 Mutate →
		// 这里），那时面板还没建完。挡掉而不是让它炸（见 _built 的说明）。
		if (!_built)
			return;

		RefreshStatus();
	}

	/// <summary>
	/// 全部分页，<b>次序必须与 <c>Tab*</c> 常量一致</b>。
	///
	/// 收成一个方法而不是每次写一遍数组：加一页时只改这一处，
	/// 而"页签下标常量"与"实际添加次序"不一致的症状是
	/// <c>SwitchTab(TabDecks)</c> 切到了别的页 —— 很难一眼看出来。
	/// </summary>
	/// <summary>
	/// 五个分页节点（自检用：验它们**来自场景**而不是代码建的）。
	/// </summary>
	internal Node[] PageNodes() => new Node[]
	{
		_cardPage, _tokenPage, _deckPage, _boardPage, _zonePage,
	};

	/// <summary>
	/// 让<b>五个分页全部</b>重新读一遍现在的模型。
	///
	/// <b>什么时候必须调它：定义池被整体换掉之后。</b>读档、切存档、新建存档
	/// 都会把 <c>CardDefinitions</c> / <c>TokenDefinitions</c> / <c>Decks</c>
	/// 换成另一批对象，而各页里的列表与表单<b>是上一次刷新的结果</b> ——
	/// 它们不知道"模型换了"这件事。
	///
	/// 症状（用户实测报的）：<b>启动后第一次按 F1，卡池 / 卡组 / 区域显示的还是
	/// 示例内容（火球术那一套），而不是刚读回来的存档内容</b>。
	/// 根因是启动顺序：<c>Main</c> 里 <c>Editor.Initialize()</c>（它会刷一次各页）
	/// 跑在 <c>TryLoadLastSave()</c> <b>之前</b> —— 那一次刷的是示例内容，
	/// 而读档之后<b>没有任何人</b>再叫各页刷一次。
	///
	/// 只在<b>切页签 / 开面板</b>时才刷（<c>OnShown</c>）是原设计，
	/// 它假定"没打开过的页不需要是新的"—— 那个假定在"模型被整体换掉"时不成立。
	/// </summary>
	public void RefreshAllPages()
	{
		foreach (EditorPage page in Pages())
			OnShownSafely(page);
	}

	private EditorPage[] Pages() => new EditorPage[]	{
		_cardPage, _tokenPage, _deckPage, _boardPage, _zonePage,
	};

	private EditorPage? CurrentPage() => _tabs.CurrentTab switch
	{
		TabCards => _cardPage,
		TabTokens => _tokenPage,
		TabDecks => _deckPage,
		TabBoard => _boardPage,
		TabZones => _zonePage,
		_ => null,
	};

	// ------------------------------------------------------------------ 状态行

	/// <summary>重算顶栏的"未保存"标记与底栏的状态文字。</summary>
	public void RefreshStatus()
	{
		StatusRefreshCount++;

		// 建界面中途也会走到这里（见 _built 的说明），那时标签还不存在。
		if (!_built || !IsInstanceValid(_dirtyLabel))
			return;

		// <b>定义池刚换过一整套时（读档 / 新建存档）顺手把"未保存"清掉。</b>
		// 存档系统自己会调 MarkClean，但那依赖"每条入口都记得调"；
		// 这里补一道与信号同源的兜底，代价只是一次字典数数。
		bool dirty = CardDefinitionService.IsDirty;

		_dirtyLabel.Text = dirty
			? "运行时编辑器　·　未保存改动（Ctrl+S）"
			: "运行时编辑器　·　已保存";

		_statusLabel.Text =
			$"卡牌 {_objects.CardDefinitions.Count}　Token {_objects.TokenDefinitions.Count}　" +
			$"卡组 {_objects.Decks.Count}　区域 {_zones.AllZones.Count}　" +
			$"存档：{AppPaths.CurrentSave}";
	}
}
