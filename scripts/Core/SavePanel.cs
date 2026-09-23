using System.Collections.Generic;
using Godot;
using TabletopSimulator.Core.Objects;

namespace TabletopSimulator.Core;

/// <summary>
/// 存档界面：顶栏一个「存档 ▾」按钮，弹出存档列表与增删改存。
///
/// <b>为什么用 <c>PopupMenu</c> 而不是自建面板：</b>它原生就有条目列表、
/// 分隔线、禁用项、勾选标记与二次确认对话框，而这三个正是存档菜单需要的东西。
/// 自建一套只会把"点击命中 / 焦点 / 关闭时机"这些坑重新踩一遍。
///
/// 代价要记牢（M2/M3 都栽过）：<c>PopupMenu</c> 是 <c>Window</c>，
/// <b>弹出来会抢走后续合成鼠标事件</b>。所以自检用它时必须程序化触发
/// （<c>EmitSignal(IdPressed)</c>），用完还要 <c>Hide()</c>。
/// </summary>
[GlobalClass]
public partial class SavePanel : Node
{
	private enum Item
	{
		/// <summary>切换存档区间：<c>SaveBase + i</c>。</summary>
		SaveBase = 1,

		New = 500,
		SaveNow,
		SaveAs,
		Rename,
		Delete,
		Header = 900,
	}

	private Button _button = null!;
	private PopupMenu _menu = null!;
	private ConfirmationDialog _confirm = null!;
	private AcceptDialog _prompt = null!;
	private LineEdit _promptInput = null!;
	private ThumbnailView _thumb = null!;

	private ObjectManager _objects = null!;
	private ZoneManager _zones = null!;
	private BoardTheme _theme = null!;
	private UndoSystem _undo = null!;
	private Hud _hud = null!;

	/// <summary>当前存档名。<b>唯一真相</b>；顶栏那个标签也是从它刷的。</summary>
	public string CurrentSave { get; private set; } = AppPaths.DefaultSaveName;

	/// <summary>
	/// 定义池被整体换掉了（读档 / 切存档 / 新建存档）→ 通知外面刷新。
	///
	/// <b>为什么要有这个回调：</b>那三条路都会把 <c>CardDefinitions</c> 等换成
	/// 另一批对象，而<b>编辑器的分页里存的是上一次刷新的结果</b>。
	/// 不通知的话，启动后第一次按 F1 看到的是<b>示例内容</b>（火球术那一套），
	/// 而不是刚读回来的存档内容 —— 这正是用户实测报上来的那一条。
	///
	/// 与 <c>Hud.SaveRequested</c> / <c>Objects.EditInstanceRequested</c> 同一个套路：
	/// <b>接线在 <c>Main</c>，两端互不认识</b>（SavePanel 不该知道编辑器长什么样）。
	/// </summary>
	public System.Action? DefinitionsReloaded { get; set; }

	/// <summary>待确认的删除目标（二次确认对话框是复用的，所以要把意图记在外面）。</summary>
	private string _pendingDelete = "";

	private enum PromptMode
	{
		None,
		New,
		SaveAs,
		Rename,
	}

	private PromptMode _promptMode = PromptMode.None;

	/// <summary>菜单里当前列出的存档名（下标与 <c>Item.SaveBase + i</c> 对应）。自检用。</summary>
	private readonly List<string> _listed = new();

	internal IReadOnlyList<string> ListedForTest => _listed;

	public PopupMenu Menu => _menu;

	public static SavePanel Attach(CanvasLayer layer, Hud hud, Button button, ObjectManager objects,
		ZoneManager zones, BoardTheme theme, UndoSystem undo)
	{
		SavePanel panel = new() { Name = "SavePanel" };
		panel._hud = hud;
		panel._button = button;
		panel._objects = objects;
		panel._zones = zones;
		panel._theme = theme;
		panel._undo = undo;

		layer.AddChild(panel);
		return panel;
	}

	public override void _Ready()
	{
		_button.Pressed += OnButtonPressed;

		// 【项目约定】菜单与对话框<b>都在 Main.tscn 里搭好</b>（挂在 HudRoot 下），
		// 这里只取来接线。不许在代码里 new 节点。
		_menu = GetParent().GetNode<PopupMenu>("HudRoot/SaveMenu");
		_menu.IdPressed += OnItemPressed;

		// 存档缩略图预览。它在 HudRoot 里排在最后（画在所有 HUD 之上），
		// 由这里按菜单的实际位置摆放 —— 见 PositionThumbnail 的说明。
		_thumb = GetParent().GetNode<ThumbnailView>("HudRoot/ThumbnailView");
		_menu.IdFocused += OnMenuItemFocused;
		_menu.PopupHide += OnMenuClosed;

		_prompt = GetParent().GetNode<AcceptDialog>("HudRoot/SaveNamePrompt");
		_promptInput = _prompt.GetNode<LineEdit>("SaveNameInput");
		_prompt.Confirmed += OnPromptConfirmed;

		_confirm = GetParent().GetNode<ConfirmationDialog>("HudRoot/DeleteSaveConfirm");
		_confirm.Confirmed += OnDeleteConfirmed;

		Refresh();
	}

	// ------------------------------------------------------------------ 界面

	/// <summary>按当前存档列表重建菜单（每次弹出前刷一次，条数/时间才是新的）。</summary>
	public void Refresh()
	{
		_listed.Clear();

		_menu.Clear();
		_menu.AddItem($"当前：{CurrentSave}", (int)Item.Header);
		_menu.SetItemDisabled(0, true);
		_menu.AddSeparator();

		List<SaveSystem.Entry> saves = SaveSystem.ListSaves();
		if (saves.Count == 0)
		{
			_menu.AddItem("（还没有存档）", (int)Item.Header);
			_menu.SetItemDisabled(_menu.ItemCount - 1, true);
		}

		for (int i = 0; i < saves.Count; i++)
		{
			SaveSystem.Entry e = saves[i];
			_listed.Add(e.Name);

			// 当前存档打勾 —— 一眼看出"我现在在哪个档里"
			_menu.AddItem($"{e.Name}　{e.Modified}　{e.ObjectCount} 件", (int)Item.SaveBase + i);
			_menu.SetItemChecked(_menu.ItemCount - 1, e.Name == CurrentSave);
		}

		_menu.AddSeparator();
		_menu.AddItem("新建存档…", (int)Item.New);
		_menu.AddItem("立即保存　Ctrl+S", (int)Item.SaveNow);
		_menu.AddItem("另存为…　Ctrl+Shift+S", (int)Item.SaveAs);
		_menu.AddSeparator();
		_menu.AddItem("重命名…", (int)Item.Rename);
		_menu.AddItem("删除…", (int)Item.Delete);

		_button.Text = $"存档：{CurrentSave} ▾";

		// 底部信息栏里也显示当前存档名。
		//
		// <b>挂在这里而不是让 HUD 自己去读 <c>AppPaths.CurrentSave</c>：</b>
		// 存档名的唯一真相是这个类里的 <c>CurrentSave</c>（它在改名 / 切档 / 新建 /
		// 删档之后都会变），而 <c>Refresh()</c> 恰好是这四条路的公共出口 ——
		// 让 HUD 自己去查一个"同步的副本"，迟早会查到旧值。
		_hud.SetSaveName(CurrentSave);
	}

	private void OnButtonPressed()
	{
		Refresh();
		_menu.Position = (Vector2I)ClampToViewport(_button.GetGlobalRect());
		_menu.Popup();
		PositionThumbnail();
	}

	// ------------------------------------------------------------------ 缩略图预览

	/// <summary>
	/// 把预览框摆到菜单<b>下方</b>。
	///
	/// <b>为什么不写死坐标：</b>菜单的位置是跟着「存档 ▾」按钮算出来的
	/// （<see cref="ClampToViewport"/>），按钮一动、或窗口一窄，写死的预览框
	/// 就会和菜单错位 —— 而"预览框飘在别处"和"预览没弹出来"在截图里很像。
	///
	/// 菜单高度用 <c>GetContentsMinimumSize()</c> 估：它读的是内容，
	/// 不依赖"窗口已经真的显示出来了"（<c>Popup()</c> 之后窗口尺寸未必立刻可用）。
	/// 估不出来时退回 <see cref="MinimumMenuEstimate"/>，宁可离得远一点也不要压住菜单。
	/// </summary>
	private void PositionThumbnail()
	{
		Vector2 viewport = GetViewport().GetVisibleRect().Size;
		Vector2 menuSize = _menu.GetContentsMinimumSize();
		if (menuSize.X <= 0f || menuSize.Y <= 0f)
			menuSize = MinimumMenuEstimate;

		Vector2 size = _thumb.Size;
		if (size.X < 40f || size.Y < 30f)
			size = new Vector2(320f, 180f);

		// 下边界让开底部那两条栏（信息栏 + 快捷键提示），它们也是用户要看的东西。
		float maxBottom = BottomBarsReserve;
		float x = Mathf.Clamp(_menu.Position.X, 8f, Mathf.Max(viewport.X - size.X - 8f, 8f));
		float y = _menu.Position.Y + menuSize.Y + 8f;
		y = Mathf.Clamp(y, 8f, Mathf.Max(viewport.Y - maxBottom - size.Y - 8f, 8f));

		_thumb.Position = new Vector2(x, y);
	}

	/// <summary>菜单内容量不出来时的估计值（够放"当前 + 若干存档 + 五个动作"）。</summary>
	private static readonly Vector2 MinimumMenuEstimate = new(280f, 240f);

	/// <summary>底部两条状态栏的高度预留（<c>hud_layout</c> 里量到的是两条各 34）。</summary>
	private const float BottomBarsReserve = 76f;

	/// <summary>悬停某一行 → 显示那个存档的缩略图；悬停非存档行 → 收起来。</summary>
	private void OnMenuItemFocused(long id)
	{
		FocusEvents++;
		LastFocusedId = id;

		int item = (int)id;
		if (item < (int)Item.SaveBase || item >= (int)Item.SaveBase + _listed.Count)
		{
			_thumb.Clear();
			_thumb.Hide();
			return;
		}

		string name = _listed[item - (int)Item.SaveBase];
		_thumb.Show(ThumbnailPath(name));
		_thumb.Show();
		PreviewedSaveForTest = name;
	}

	private void OnMenuClosed()
	{
		_thumb.Hide();
		PreviewedSaveForTest = "";
	}

	/// <summary>某个存档的缩略图路径（有没有这个文件由 <see cref="ThumbnailView.Show"/> 判）。</summary>
	private static string ThumbnailPath(string saveName) => $"{AppPaths.ThumbsDir(saveName)}/board.png";

	/// <summary>自检用：此刻预览的是哪个存档（空串 = 没在预览）。</summary>
	internal string PreviewedSaveForTest { get; private set; } = "";

	/// <summary>自检用：预览控件的引用。</summary>
	internal ThumbnailView ThumbForTest => _thumb;

	/// <summary>
	/// 只读计数器：菜单悬停回调被调用了几次、最后一次的 id 是多少。
	///
	/// 存在的理由与 M4 那几个计数器一样 —— <b>把猜换成读</b>。
	/// 自检里"悬停之后什么都没显示"这一条红了三轮，而报告里没有任何一项
	/// 能回答"回调到底有没有被调用"：它可能是信号没送到，也可能是送到了、
	/// 但 id 落在"非存档行"那一支里被正确地收了起来。这两个的处置完全相反。
	/// </summary>
	internal int FocusEvents { get; private set; }

	internal long LastFocusedId { get; private set; } = -999;

	/// <summary>
	/// 自检用：把预览框按真实规则摆一次并显示某个存档的图。
	///
	/// 走的是与"菜单弹出"完全相同的那两条路（<see cref="PositionThumbnail"/> +
	/// <see cref="ThumbnailView.Show"/>），因为自检里菜单<b>从来没有真的弹出来过</b>
	/// （<c>PopupMenu</c> 是 <c>Window</c>，真弹会抢走后续合成鼠标事件）——
	/// 若在这里另写一套摆放，验的就不是产品那份规则了。
	/// </summary>
	internal void ShowPreviewForTest(string saveName)
	{
		PositionThumbnail();
		_thumb.Show(ThumbnailPath(saveName));
		_thumb.Show();
		PreviewedSaveForTest = saveName;
	}

	/// <summary>自检用：把预览收干净（探针不许留下可观测的状态变化）。</summary>
	internal void ReleasePreviewForTest()
	{
		_thumb.Clear();
		_thumb.Hide();
		PreviewedSaveForTest = "";
	}

	private Vector2 ClampToViewport(Rect2 anchor)
	{
		// SavePanel 是普通 Node（不是 Control），拿视口尺寸要经 GetViewport()
		Vector2 viewport = GetViewport().GetVisibleRect().Size;
		Vector2 estimate = _menu.GetContentsMinimumSize();
		if (estimate.X <= 0f)
			estimate = new Vector2(280f, 240f);

		return new Vector2(
			Mathf.Clamp(anchor.Position.X, 0f, Mathf.Max(viewport.X - estimate.X, 0f)),
			Mathf.Clamp(anchor.End.Y, 0f, Mathf.Max(viewport.Y - estimate.Y, 0f)));
	}

	// ------------------------------------------------------------------ 菜单动作

	private void OnItemPressed(long id)
	{
		int item = (int)id;
		_menu.Hide();

		if (item >= (int)Item.SaveBase && item < (int)Item.SaveBase + _listed.Count)
		{
			SwitchTo(_listed[item - (int)Item.SaveBase]);
			return;
		}

		switch ((Item)item)
		{
			case Item.New:
				AskForName(PromptMode.New, $"存档{SaveSystem.ListSaves().Count + 1}");
				break;

			case Item.SaveNow:
				SaveNow();
				break;

			case Item.SaveAs:
				AskForName(PromptMode.SaveAs, $"{CurrentSave} 副本");
				break;

			case Item.Rename:
				AskForName(PromptMode.Rename, CurrentSave);
				break;

			case Item.Delete:
				_pendingDelete = CurrentSave;
				_confirm.DialogText =
					$"删除存档「{CurrentSave}」？\n\n这个操作不可撤销 —— 那个目录会被整个删掉。";
				_confirm.PopupCentered();
				break;
		}
	}

	private void AskForName(PromptMode mode, string initial)
	{
		_promptMode = mode;
		_promptInput.Text = initial;
		_prompt.DialogText = mode switch
		{
			PromptMode.New => "新建一个空存档（只有一张默认桌面，内容用示例模板）",
			PromptMode.SaveAs => "把当前这一桌另存为新的存档",
			PromptMode.Rename => "改存档目录名",
			_ => "",
		};

		_prompt.PopupCentered(new Vector2I(360, 140));
		_promptInput.GrabFocus();
		_promptInput.SelectAll();
	}

	private void OnPromptConfirmed()
	{
		string name = AppPaths.Sanitize(_promptInput.Text);
		PromptMode mode = _promptMode;
		_promptMode = PromptMode.None;

		switch (mode)
		{
			case PromptMode.New:
				CreateNew(name);
				break;

			case PromptMode.SaveAs:
				if (!SaveSystem.IsNameAvailable(name))
				{
					_hud.Toast($"「{name}」已经存在了，换个名字");
					return;
				}

				SwitchTo(name, keepContent: true);
				break;

			case PromptMode.Rename:
				if (SaveSystem.RenameSave(CurrentSave, name))
				{
					CurrentSave = name;
					Refresh();
					_hud.Toast($"已改名为「{name}」");
				}
				else
				{
					_hud.Toast(SaveSystem.LastError.Length > 0 ? SaveSystem.LastError : "改名失败");
				}

				break;
		}
	}

	private void OnDeleteConfirmed()
	{
		string name = _pendingDelete;
		_pendingDelete = "";

		if (!SaveSystem.DeleteSave(name))
		{
			_hud.Toast(SaveSystem.LastError.Length > 0 ? SaveSystem.LastError : "删除失败");
			return;
		}

		Refresh();
		_hud.Toast($"已删除「{name}」");

		// 删掉的正是当前存档 → 落回示例内容，别留一个指着空气的当前档
		if (name == CurrentSave)
		{
			CurrentSave = AppPaths.DefaultSaveName;
			Refresh();
		}
	}

	// ------------------------------------------------------------------ 存 / 切

	/// <summary>弹「另存为」输入框（<c>Ctrl+Shift+S</c> 与菜单项共用）。</summary>
	public void AskSaveAs() => AskForName(PromptMode.SaveAs, $"{CurrentSave} 副本");

	/// <summary>立即保存当前存档（<c>Ctrl+S</c>，不弹框）。</summary>
	public bool SaveNow()
	{
		bool ok = SaveSystem.Save(CurrentSave, _objects, _zones, _theme);

		// 存档成功才抓缩略图 —— 存失败时视口里那一桌并没落盘，
		// 拿它当缩略图会让人以为"这个存档里有这些东西"。
		//
		// 这里刻意不判"编辑器面板开着没有"：抓到的画面上可能压着面板，
		// 但"确实存过一张图"与"图里没有面板"是两件事，后者不值一次额外的隐藏/恢复。
		if (ok)
			CaptureThumbnail();

		_hud.Toast(ok
			? $"已保存「{CurrentSave}」（{_objects.ObjectCount} 件）"
			: $"保存失败：{SaveSystem.LastError}");

		Refresh();

		// 正好在预览这个存档 → 立刻换上刚抓的新图（不然它还显示存档前那一张）。
		if (_thumb.Visible && PreviewedSaveForTest == CurrentSave)
			_thumb.Show(ThumbnailPath(CurrentSave));

		return ok;
	}

	/// <summary>
	/// 新建并切到一个空存档。
	///
	/// "空存档"= 只有一块默认桌面 + 一份<b>示例定义池</b>，桌面上不放任何物件。
	/// 放一份定义池是刻意的：M5 的编辑器要从"有卡可改"开始，
	/// 而一个连卡牌定义都没有的存档让人无从下手。
	/// </summary>
	public bool CreateNew(string saveName)
	{
		if (!SaveSystem.IsNameAvailable(saveName))
		{
			_hud.Toast($"「{saveName}」已经存在了，换个名字");
			return false;
		}

		CurrentSave = saveName;
		AppPaths.EnsureSaveLayout(saveName);

		// 裸文件名的解析基准（定义里的 FaceImage 之类）要跟着当前存档走。
		AppPaths.SetCurrentSave(saveName);

		// 清空桌子，只留示例定义（含一副示例卡组）
		_undo.Reset();
		_zones.ClearAll();
		_objects.ClearAll();
		_objects.SeedDemoDefinitions();
		CardDefinitionService.MarkClean();

		BoardTheme fresh = new();
		_theme.BoardWidth = fresh.BoardWidth;
		_theme.BoardHeight = fresh.BoardHeight;
		_theme.BackgroundImage = fresh.BackgroundImage;
		_theme.BackgroundColor = fresh.BackgroundColor;

		SaveSystem.Save(CurrentSave, _objects, _zones, _theme);
		_objects.NotifyRestored();
		Refresh();

		// 定义池刚被换成"示例那一份"→ 让编辑器各页重新读一遍。
		// 漏掉这一句的症状：新建存档之后按 F1，看到的是上一个存档的卡池。
		DefinitionsReloaded?.Invoke();

		_hud.Toast($"已新建存档「{saveName}」");
		return true;
	}

	/// <summary>切换存档。<paramref name="keepContent"/> 为真时<b>先把当前内容存进去</b>再切。</summary>
	public bool SwitchTo(string saveName, bool keepContent = false)
	{
		if (saveName == CurrentSave && !keepContent)
		{
			_hud.Toast($"已经在「{saveName}」里了");
			return false;
		}

		if (keepContent && !SaveSystem.Save(saveName, _objects, _zones, _theme))
		{
			_hud.Toast($"另存失败：{SaveSystem.LastError}");
			return false;
		}

		if (!SaveSystem.Load(saveName, _objects, _zones, _theme, _undo, out string failure))
		{
			_hud.Toast($"读档失败：{failure}");
			return false;
		}

		CurrentSave = saveName;

		// 换存档必须清掉操作日志的落盘 —— 上一个存档的流水对新存档没有意义，
		// 而混在一起会让"我昨天是怎么把那摞牌搞乱的"这个问题彻底失去答案。
		HistoryLog.Clear();

		Refresh();

		// 换存档 = 换了一整套定义 → 编辑器各页必须重新读（详见 DefinitionsReloaded）。
		DefinitionsReloaded?.Invoke();

		_hud.Toast($"已切到「{saveName}」（{_objects.ObjectCount} 件）");
		return true;
	}

	/// <summary>设置当前存档名（启动时由 <c>Main</c> 按 <c>last_save.txt</c> 定）。不读档。</summary>
	public void SetCurrent(string saveName)
	{
		CurrentSave = saveName;
		AppPaths.SetCurrentSave(saveName);
		Refresh();
	}

	// ------------------------------------------------------------------ 缩略图

	/// <summary>
	/// 即时抓一张缩略图存进 <c>thumbs/board.png</c>。
	///
	/// <b>为什么从 <c>async void</c> 改成同步：</b>原来它 <c>await</c> 一帧再抓，
	/// 但抓图的调用点就是"保存刚刚成功"那一刻 —— 此刻视口里已经是这一桌的当前画面，
	/// 再等一帧只会多出两种无关的失败可能：存档被删掉（<c>await</c> 之后
	/// <c>CurrentSave</c> 可能已经换了）与视图被换掉。同步抓还让"存了图"这件事
	/// 在 <see cref="SaveNow"/> 返回时就已经成立，自检不必靠等帧去赌。
	/// </summary>
	public void CaptureThumbnail()
	{
		// <b>先强制画一帧。</b>
		//
		// 两条实测出来的理由：
		// 1. 调用点就在"保存成功"那一刻，而视口里可能还是<b>上一帧</b>的画面
		//    （相机刚移动过、面板刚要收起）；
		// 2. 更隐蔽的一条：自检里连续存两次之间相机被移动过，第二张图于是拍到
		//    移动<b>之前</b>的机位 —— 而"缩略图里是上一帧"这件事，
		//    在任何单点读数里都看不出来（文件在、尺寸对、内容也是这一桌）。
		//
		// <c>ForceDraw</c> 让当前帧立刻画完，于是 <c>GetImage()</c> 拿到的是此刻的画面。
		RenderingServer.ForceDraw();

		Image? image = GetViewport()?.GetTexture()?.GetImage();
		if (image is null)
		{
			GD.PushWarning("[SavePanel] 抓不到视口画面，缩略图跳过");
			return;
		}

		image.Resize(320, 180, Image.Interpolation.Bilinear);

		string dir = AppPaths.ThumbsDir(CurrentSave);
		AppPaths.EnsureDir(dir);

		Error err = image.SavePng($"{dir}/board.png");
		if (err != Error.Ok)
			GD.PushWarning($"[SavePanel] 缩略图存不下：{err}");
	}
}
