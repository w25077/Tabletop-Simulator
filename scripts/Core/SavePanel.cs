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

	private ObjectManager _objects = null!;
	private ZoneManager _zones = null!;
	private BoardTheme _theme = null!;
	private UndoSystem _undo = null!;
	private Hud _hud = null!;

	/// <summary>当前存档名。<b>唯一真相</b>；顶栏那个标签也是从它刷的。</summary>
	public string CurrentSave { get; private set; } = AppPaths.DefaultSaveName;

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

		_menu = new PopupMenu { Name = "SaveMenu" };
		_menu.IdPressed += OnItemPressed;
		GetParent().AddChild(_menu);

		_promptInput = new LineEdit { CustomMinimumSize = new Vector2(280f, 0f) };

		_prompt = new AcceptDialog { Title = "存档名" };
		_prompt.AddChild(_promptInput);
		_prompt.Confirmed += OnPromptConfirmed;
		GetParent().AddChild(_prompt);

		_confirm = new ConfirmationDialog { Title = "删除存档" };
		_confirm.Confirmed += OnDeleteConfirmed;
		GetParent().AddChild(_confirm);

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
	}

	private void OnButtonPressed()
	{
		Refresh();
		_menu.Position = (Vector2I)ClampToViewport(_button.GetGlobalRect());
		_menu.Popup();
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
		_hud.Toast(ok
			? $"已保存「{CurrentSave}」（{_objects.ObjectCount} 件）"
			: $"保存失败：{SaveSystem.LastError}");

		Refresh();
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

		// 清空桌子，只留示例定义
		_undo.Reset();
		_zones.ClearAll();
		_objects.ClearAll();
		_objects.SeedDemoDefinitions();

		BoardTheme fresh = new();
		_theme.BoardWidth = fresh.BoardWidth;
		_theme.BoardHeight = fresh.BoardHeight;
		_theme.BackgroundImage = fresh.BackgroundImage;
		_theme.BackgroundColor = fresh.BackgroundColor;

		SaveSystem.Save(CurrentSave, _objects, _zones, _theme);
		_objects.NotifyRestored();
		Refresh();
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
		_hud.Toast($"已切到「{saveName}」（{_objects.ObjectCount} 件）");
		return true;
	}

	/// <summary>设置当前存档名（启动时由 <c>Main</c> 按 <c>last_save.txt</c> 定）。不读档。</summary>
	public void SetCurrent(string saveName)
	{
		CurrentSave = saveName;
		Refresh();
	}

	// ------------------------------------------------------------------ 缩略图

	/// <summary>
	/// 抓一张存档缩略图存进 <c>thumbs/</c>。
	///
	/// <b>必须等一帧再抓</b>：调用它的那一刻视口里还是上一帧的内容
	/// （比如换存档之后还是旧桌面）。所以它是个协程而不是普通方法。
	/// </summary>
	public async void CaptureThumbnail()
	{
		await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);

		Image? image = GetViewport()?.GetTexture()?.GetImage();
		if (image is null)
			return;

		image.Resize(320, 180, Image.Interpolation.Bilinear);

		string dir = AppPaths.ThumbsDir(CurrentSave);
		AppPaths.EnsureDir(dir);

		Error err = image.SavePng($"{dir}/board.png");
		if (err != Error.Ok)
			GD.PushWarning($"[SavePanel] 缩略图存不下：{err}");
	}
}
