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
	public bool IsOpen => Visible;

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

	/// <summary>建面板并挂到 HUD 层上（与 <c>LogPanel</c> / <c>SavePanel</c> 同一套路）。</summary>
	public static EditorPanel Attach(
		CanvasLayer layer, ObjectManager objects, ZoneManager zones, Board board,
		BoardCamera camera, Hud hud)
	{
		EditorPanel panel = new() { Name = "EditorPanel" };

		// 依赖先塞进字段，UI 在 _Ready 里建（原因见类注释第 2 条）。
		panel._objects = objects;
		panel._zones = zones;
		panel._board = board;
		panel._camera = camera;
		panel._hud = hud;

		layer.AddChild(panel);
		return panel;
	}	public override void _Ready()
	{
		Build();

		// 整个面板建完之后才让各页刷一次。
		//
		// <b>这一步必须排在 Build() 之后，而各页的 <c>_Ready</c> 里不能做这件事。</b>
		// Godot 是"子节点先 _Ready"，页被 AddChild 进页签容器时面板还没建完，
		// 那一刻页脚那两个标签字段还是 null —— 于是 OnShown 链上的
		// <c>RefreshStatus</c> 会抛 <c>NullReferenceException</c>。
		// 第一次跑就这么炸了 7 条，而面板看起来完全正常、<b>自检也全绿</b>。
		foreach (EditorPage page in Pages())
			page.OnShown();

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
		// 顶栏下沿 → 底部提示条上沿，左右各留 8px。
		// 手动锚点而不是 preset：preset 里没有"顶栏下沿到提示条"这一档（M1 栽过）。
		SetAnchorsPreset(LayoutPreset.FullRect);
		OffsetLeft = 8f;
		OffsetRight = -8f;
		OffsetTop = 50f;
		OffsetBottom = -34f;

		MouseFilter = MouseFilterEnum.Stop;   // 挡住底下的桌面交互
		Visible = false;

		var bg = new PanelContainer();
		bg.SetAnchorsPreset(LayoutPreset.FullRect);
		AddChild(bg);

		var column = new VBoxContainer();
		column.AddThemeConstantOverride("separation", 6);
		bg.AddChild(column);

		column.AddChild(BuildHeader());

		_tabs = new TabContainer
		{
			Name = "Tabs",
			SizeFlagsVertical = SizeFlags.ExpandFill,
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
		};

		column.AddChild(_tabs);

		_cardPage = new CardEditorPage { Name = "卡牌" };
		_tokenPage = new TokenEditorPage { Name = "指示物" };
		_deckPage = new DeckEditorPage { Name = "卡组" };
		_boardPage = new BoardEditorPage { Name = "桌面" };
		_zonePage = new ZoneEditorPage { Name = "区域" };

		foreach (EditorPage page in Pages())
		{
			page.Bind(_objects, _zones, _board, _camera, _hud, this);
			_tabs.AddChild(page);
		}

		// 画区域的遮罩层铺满整个视口（原因见 ZoneDrawOverlay 的说明），
		// 挂到<b>同一个 HudRoot 下、面板之后</b>（后添加 = 画在上面）。
		//
		// <b>它不能是面板的子节点。</b>第一版就是那样写的，结果它的矩形恒为 0 ——
		// 因为面板自己 <c>Visible = false</c>，而<b>不可见的 Control 子树会被跳过布局</b>。
		// 于是"画区域"点下去什么都不发生：遮罩零尺寸 → 收不到鼠标事件。
		// 这与 M4 那条"0 高度面板"是同一个坑的另一种长相：
		// <b>矩形为 0 的控件不会报错，它只是永远收不到输入。</b>
		_overlay = new ZoneDrawOverlay { Name = "ZoneDrawOverlay" };
		_overlay.Bind(_zonePage, _zones, _camera, _hud, this);
		GetParent().AddChild(_overlay);
		_zonePage.BindOverlay(_overlay);

		column.AddChild(BuildFooter());
	}

	private Control BuildHeader()
	{
		var row = new HBoxContainer { Name = "Header" };
		row.AddThemeConstantOverride("separation", 10);

		_dirtyLabel = new Label { Text = "运行时编辑器" };
		row.AddChild(_dirtyLabel);

		row.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });

		// 文字里把两条退出方式都写上：顶栏那个入口按钮与这里要能互相印证
		// （用户实测反馈第 1 条：`Esc` 关不掉，而面板上没有任何地方说它能关）。
		var close = new Button { Text = "关闭　F1 / Esc" };
		close.Pressed += Close;
		row.AddChild(close);

		return row;
	}

	private Control BuildFooter()
	{
		var row = new HBoxContainer { Name = "Footer" };
		row.AddThemeConstantOverride("separation", 10);

		_statusLabel = new Label { Text = "" };
		row.AddChild(_statusLabel);

		row.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });

		var save = new Button { Text = "保存到存档　Ctrl+S" };
		save.Pressed += () => _hud.RequestSave();
		row.AddChild(save);

		return row;
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
	public override void _UnhandledKeyInput(InputEvent @event)
	{
		if (@event is InputEventKey probe && probe.Pressed && !probe.Echo)
		{
			KeySeen++;
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

		if (!Visible)
			return;

		// <b>`Esc` 排在"吃掉其余按键"之前。</b>
		//
		// 面板开着时后面那行会把所有按键标记为已处理，所以这个判断只要落在它后面，
		// `Esc` 就永远轮不到关闭面板 —— 用户实测反馈第 1 条报的正是这件事。
		if (HandleEscape(key))
		{
			GetViewport().SetInputAsHandled();
			return;
		}

		// 面板开着时吃掉其余按键：否则在名称输入框里按 d 会抽牌、f 会翻面。
		//
		// 走 _UnhandledKeyInput 的天然好处：**焦点在 LineEdit 里时这个函数根本不会被调用**
		// （控件先吃掉打字键），所以"输入框里按 Ctrl+Z 是撤销文字"这件事自动成立，
		// 不需要自己判断焦点。
		GetViewport().SetInputAsHandled();
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
		if (Visible)
			Close();
		else
			Open();
	}

	public void Open()
	{
		OpenCount++;
		OpenTrace = $"[f{Engine.GetProcessFrames()} open={OpenCount} close={CloseCount} " +
			$"wasOpen={IsOpen} vis={Visible} callers={string.Join(" < ", Callers())}] ";

		Visible = true;
		_objects.ClearSelection();
		RefreshStatus();
		CurrentPage()?.OnShown();
		_hud.SyncEditorButton(true);
	}

	public void Close()
	{
		CloseCount++;
		CloseTrace = $"[f{Engine.GetProcessFrames()} open={OpenCount} close={CloseCount} " +
			$"from={string.Join(" < ", Callers())}] ";

		Visible = false;

		// 关面板要把"画区域"那种模式收掉，否则回到桌面还是画矩形模式，
		// 而画布上没有任何东西提示这件事 —— 表现为"拖拽突然不能拖牌了"。
		_zonePage?.CancelDrawMode();
		_hud.SyncEditorButton(false);
		GetViewport().SetInputAsHandled();
	}

	/// <summary>切分页（自检与潜在的命令行入口都走它）。</summary>
	public void SwitchTab(int index)
	{
		if (index < 0 || index >= _tabs.GetTabCount())
			return;

		_tabs.CurrentTab = index;
		CurrentPage()?.OnShown();
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
	internal void NotifyChanged()
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
	private EditorPage[] Pages() => new EditorPage[]
	{
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
