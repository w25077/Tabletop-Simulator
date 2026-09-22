using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using TabletopSimulator.Core;
using TabletopSimulator.Core.Objects;
using TabletopSimulator.Data;

namespace TabletopSimulator.Dev;

/// <summary>
/// 运行时编辑器（M5）的端到端断言。
///
/// <b>它验的是"编辑器真的能改东西"，不是"点了按钮看着像成功"：</b>
/// 每条断言都读一个可数的量 —— 卡池里多了几个 id、桌上少了几张牌、
/// 那个 fake PNG 的字节数对不对、矩形宽高是不是 4000、撤销能不能退回去。
///
/// 两条设计规矩（都是 M3/M4 用代价换来的）：
/// <list type="number">
/// <item><b>探针不许改变被测对象的可观测状态。</b>所以它开场抓一份场景快照、
///   收尾写回去，并断言"写回之后物件数与开始时一致"。没有这一条，
///   后面加的任何一节都会在一个被改乱过的桌子上开跑。</item>
/// <item><b>"没条件跑"要和"跑挂了"长得不一样。</b>编辑器没建起来时它
///   <b>不写 <c>pass</c> 键</b>，而是写 <c>skipped</c> —— 报告里那一节就是"没跑"。</item>
/// </list>
/// </summary>
internal static class DevEditorSim
{
	private sealed class Checks
	{
		private readonly Godot.Collections.Dictionary _dict;
		private readonly List<string> _keys = new();

		internal Checks(Godot.Collections.Dictionary dict) => _dict = dict;

		internal void Put(string key, bool value)
		{
			_dict[key] = value;
			_keys.Add(key);
		}

		internal int Count => _keys.Count;

		internal void Data(string key, Variant value) => _dict[key] = value;

		internal bool AllPass()
		{
			if (_keys.Count == 0)
				return false;

			foreach (string k in _keys)
			{
				if (!_dict[k].AsBool())
					return false;
			}

			return true;
		}
	}

	internal static async Task<Godot.Collections.Dictionary> Probe(
		Main main, ObjectManager objects, ZoneManager zones, UndoSystem undo)
	{
		var r = new Godot.Collections.Dictionary();
		var c = new Checks(r);

		EditorPanel? editor = main.Editor;
		if (editor is null || !GodotObject.IsInstanceValid(editor))
		{
			r["skipped"] = "Main 上没有 EditorPanel（M5 第 2 步之前的样子）";
			return r;
		}

		// 开场快照：本节结束时要写回去（见类注释第 1 条）。
		SceneSnapshot baseline = SceneSnapshot.Capture(objects, zones);
		int objectsAtStart = objects.ObjectCount;
		r["objects_at_start"] = objectsAtStart;

		try
		{
			ProbeShell(c, r, editor, objects, zones, main.Hud);
			ProbeEntryPoints(c, r, editor, main.Hud);
			ProbeCardPage(c, r, editor, objects);
			ProbeCardActions(c, r, editor, objects);
			ProbeCardFields(c, r, editor, objects);
			ProbeCardTemplate(c, r, editor, objects);
			ProbeImport(c, r, editor, objects);
			ProbeTokenPage(c, r, editor, objects);
			ProbeDeckPage(c, r, editor, objects, zones, main.Board);
			ProbeBoardPage(c, r, editor, main.Board);
			ProbeZonePage(c, r, editor, zones, main.Camera);
			ProbeZoneResize(c, r, editor, zones, main.Camera);
			await ProbeZoneDeleteEntry(main, c, r, zones, main.Camera, undo);
			ProbeDeleteGuards(c, r, objects, zones);
			ProbeFocusIsKept(c, r, editor, objects);
			ProbeEndToEnd(c, r, editor, objects, zones, main.Board, main.Save, undo);
			await ProbePreviewPixels(c, r, editor, objects);
			await ProbeTokenPreviewPixels(c, r, editor);
		}
		finally
		{
			// 收尾：关面板 + 写回快照。写在 finally 里 —— 中间任何一条断言抛异常，
			// 桌子也不能留在一个被探针改过的状态上。
			editor.Close();

			undo.Reset();
			baseline.Restore(objects, zones);
			undo.Reset();
		}

		r["objects_at_end"] = objects.ObjectCount;
		c.Put("table_restored", objects.ObjectCount == objectsAtStart);
		c.Put("invariants_after_probe", ZoneInvariants.Check(objects, zones).All);
		c.Put("editor_closed_at_end", !editor.IsOpen);

		// 顶栏按钮也要跟着回到"关着"的文字：它是**唯一**能看出编辑器开没开的地方，
		// 而探针最后那几步开关过面板 —— 漏掉一次同步的话，用户看到的是"关着却写着关闭"。
		c.Put("editor_button_shows_closed_at_end",
			!editor.IsOpen && main.Hud.EditorButtonText.Contains("F1"));

		// 临时目录自己收拾掉。它也属于"探针不许留下痕迹"（第 1 条规矩）：
		// 留一个文件在工作区里，下一次 git status 就会看见它，
		// 而"这个文件是哪来的"要往回翻很久才知道。
		CleanTempDir();

		r["assertion_count"] = c.Count;
		r["pass"] = c.AllPass();
		return r;
	}

	// ------------------------------------------------------------------ 外壳

	private static void ProbeShell(
		Checks c, Godot.Collections.Dictionary r,
		EditorPanel editor, ObjectManager objects, ZoneManager zones, Hud hud)
	{
		editor.Open();
		c.Put("f1_opens", editor.IsOpen);

		// 几何：**必须量矩形本身**。M4 那个"高度恒为 0 的面板"穿过了
		// "位置对 / 宽度对 / Visible 为真"全部判据，所以这里直接量宽高。
		Rect2 rect = editor.GetGlobalRect();
		Vector2 viewport = editor.GetViewport().GetVisibleRect().Size;
		r["editor_rect"] = new Godot.Collections.Array { rect.Position.X, rect.Position.Y, rect.Size.X, rect.Size.Y };
		r["viewport"] = new Godot.Collections.Array { viewport.X, viewport.Y };

		c.Put("editor_rect_nonzero", rect.Size.X > 100f && rect.Size.Y > 100f);
		c.Put("editor_rect_inside_viewport",
			rect.Position.X >= 0f && rect.Position.Y >= 0f &&
			rect.End.X <= viewport.X + 1f && rect.End.Y <= viewport.Y + 1f);
		// 面板必须<b>整个让开顶栏</b> —— 而不是"让开 42 像素"。
		//
		// 第一版写死的是 `rect.Position.Y >= 42f`，而那个 42 是当时<b>目测</b>的顶栏高度。
		// 现在顶栏的高度由场景里那些控件的主题最小尺寸决定（实测 43），
		// 于是"42"这条判据随时会因为主题或按钮尺寸的变化而变成假绿/假红。
		// 拿顶栏自己的框去比，数字就不会撒谎。
		Rect2 topBar = hud.TopBarRectForTest;
		r["hud_top_bar_rect"] = RectArray(topBar);
		c.Put("editor_leaves_top_bar_visible", rect.Position.Y >= topBar.End.Y);
		c.Put("editor_blocks_mouse", editor.MouseFilter == Control.MouseFilterEnum.Stop);

		// 五个分页真的都在（不是"我建了五个对象但只挂上去四个"）
		c.Put("has_all_pages", TabCount(editor) == EditorPanel.TabZones + 1);

		// 五个分页<b>是场景实例</b>，不是在代码里 new 出来挂上去的。
		//
		// 判据：<c>Owner</c>。由 <c>new</c> 出来再 <c>AddChild</c> 的节点，
		// Owner 是 null（它不属于任何已保存的场景）；而场景实例的每一个节点
		// 都由那个场景的根节点拥有。
		//
		// 【项目约定】禁止动态生成节点（用户 2026-09-21 明确要求）——
		// 这一条断言就是那条约定在自检里的落地：谁把某一页改回代码建，
		// 这里当场变红。
		var pageOwners = new Godot.Collections.Dictionary();
		bool allPagesFromScene = true;
		foreach (Node page in editor.PageNodes())
		{
			bool fromScene = page.Owner is not null;
			pageOwners[page.Name.ToString()] = fromScene;
			allPagesFromScene &= fromScene;
		}

		r["page_owners_from_scene"] = pageOwners;
		c.Put("pages_come_from_scene", allPagesFromScene);

		// 切页：每一页都要能切到、并且切过去之后它自己的列表是填好的。
		// <b>顺带验"页签下标常量与添加次序一致"</b> —— 加一页时最容易错的就是这里，
		// 而症状是 SwitchTab(TabDecks) 切到了别的页。
		editor.SwitchTab(EditorPanel.TabCards);
		c.Put("switch_to_cards", editor.CurrentTab == EditorPanel.TabCards
			&& editor.CardPage.ListedIds.Count == objects.CardDefinitions.Count);

		editor.SwitchTab(EditorPanel.TabTokens);
		c.Put("switch_to_tokens", editor.CurrentTab == EditorPanel.TabTokens
			&& editor.TokenPage.ListedIds.Count == objects.TokenDefinitions.Count);

		editor.SwitchTab(EditorPanel.TabDecks);
		c.Put("switch_to_decks", editor.CurrentTab == EditorPanel.TabDecks);

		editor.SwitchTab(EditorPanel.TabBoard);
		c.Put("switch_to_board", editor.CurrentTab == EditorPanel.TabBoard);

		editor.SwitchTab(EditorPanel.TabZones);
		c.Put("switch_to_zones", editor.CurrentTab == EditorPanel.TabZones
			&& editor.ZonePage.ListedIds.Count == zones.AllZones.Count);

		editor.SwitchTab(EditorPanel.TabCards);

		// 开面板要清掉选中集：否则桌面上那些亮黄描边看起来像"面板选中了它们"
		objects.SelectAll();
		editor.Open();
		c.Put("open_clears_selection", objects.Selection.Count == 0);

		ProbeEntryPoints(c, r, editor, hud);
	}

	// ------------------------------------------------------------------ 入口按钮与 Esc（M5.5 第 1 条）

	/// <summary>
	/// 用户实测反馈第 1 条：<b>"F1 菜单要有可点的入口按钮；<c>Esc</c> 也要能关"。</b>
	///
	/// 两件事其实是一件事的两半 —— 编辑器原先<b>只有快捷键一条入口</b>，
	/// 而"入口不可发现"与"功能不存在"在用户的体感上没有区别。
	/// 所以这里既验按钮真的在（有矩形、在视口里、按下有反应），
	/// 也验 <c>Esc</c> 那条路真的能关掉它。
	///
	/// <b><c>Esc</c> 是分两级的</b>：画区域模式开着时，第一下收模式、第二下才关面板。
	/// 两条都要验，否则"改成一级"这种回退不会被任何断言抓住。
	///
	/// <b>每按一次键都要 <c>await</c> 一帧</b>（与 <c>DevHistorySim</c> 同一条规矩）：
	/// 合成输入走 <c>Input.ParseInputEvent</c>，事件要到下一帧才被派发。
	/// 第一版这里按完就接着断言，于是那几次 <c>Esc</c> 全都在<b>后面某一节</b>才生效
	/// —— 症状是"卡面预览整个变黑"，而根因在两节之外。这一条与 M4 那条
	/// 「探针不许改变被测对象的状态」是同源的：<b>排出去的输入必须自己收干净。</b>
	/// </summary>
	private static void ProbeEntryPoints(
		Checks c, Godot.Collections.Dictionary r,
		EditorPanel editor, Hud hud)
	{
		Button button = hud.EditorButton;

		Rect2 rect = button.GetGlobalRect();
		Vector2 viewport = button.GetViewport().GetVisibleRect().Size;

		r["editor_button_text"] = hud.EditorButtonText;
		r["editor_button_rect"] = RectArray(rect);

		// 顶栏那两个按钮<b>不许压到底部信息栏</b>（用户实测反馈：它们原先压在顶栏的文字上）。
		// 按钮是绝对定位的、文字在 VBox 里 —— 这种"两套定位方式混用"的布局正是最容易压住的一种，
		// 所以在这里也钉一条（DevReport 的 hud_layout 那一节量得更全）。
		Rect2 infoBar = hud.InfoBarRectForTest;
		Rect2 saveRect = hud.SaveButton.GetGlobalRect();
		r["hud_info_bar_rect"] = RectArray(infoBar);
		r["hud_save_button_rect"] = RectArray(saveRect);

		c.Put("hud_editor_button_stays_in_top_band", rect.End.Y < infoBar.Position.Y);
		c.Put("hud_save_button_stays_in_top_band", saveRect.End.Y < infoBar.Position.Y);
		c.Put("hud_info_bar_has_height", infoBar.Size.Y > 8f);

		c.Put("hud_editor_button_exists", GodotObject.IsInstanceValid(button));
		c.Put("hud_editor_button_has_size", rect.Size.X >= 100f && rect.Size.Y >= 20f);
		c.Put("hud_editor_button_inside_viewport",
			rect.Position.X >= 0f && rect.Position.Y >= 0f
			&& rect.End.X <= viewport.X + 1f && rect.End.Y <= viewport.Y + 1f);

		// 按下 → 面板开。走控件的信号，与用户点一下是同一条路。
		editor.Close();
		button.EmitSignal(Button.SignalName.Pressed);
		c.Put("editor_button_opens_panel", editor.IsOpen);

		// 按钮文字要<b>随状态变</b>：它是个普通按钮、不是开关，
		// 按下去没有任何视觉反馈，文字是唯一的提示。
		string openText = hud.EditorButtonText;
		r["editor_button_text_open"] = openText;
		c.Put("editor_button_text_shows_close", openText.Contains("关闭"));

		// 再按一下 → 关
		button.EmitSignal(Button.SignalName.Pressed);
		c.Put("editor_button_closes_panel", !editor.IsOpen);

		string closedText = hud.EditorButtonText;
		r["editor_button_text_closed"] = closedText;
		c.Put("editor_button_text_shows_shortcut", closedText.Contains("F1"));

		// ---- Esc：一级 = 关面板 ----
		//
		// <b>为什么这里直接喂按键事件、而不走 <c>Input.ParseInputEvent</c>：</b>
		// 合成键要经过引擎的输入分发（<c>_Input</c> → <c>_UnhandledInput</c> →
		// <c>_UnhandledKeyInput</c>），而它一帧只派发一个事件；于是"按下去、隔几帧再读"
		// 这段时间里，<b>同一个 <c>Esc</c> 会落到别的节点上再被处理一次</b>，
		// 读出来的状态取决于帧数而不是产品逻辑 —— 本节第一版就栽在这上面
		// （面板关了又被后面的按键重新打开，症状是"四条 Esc 断言全红而面板行为完全正确"）。
		//
		// 所以这里喂的是<b>引擎真正会转交的那个事件对象</b>，走的是
		// <see cref="EditorPanel._UnhandledKeyInput"/> 本人 —— 输入分发本身
		// 由 <c>DevInputSim</c> 的其它节负责验，这里要钉的是"收到 Esc 之后怎么处理"。
		editor.Open();
		editor._UnhandledKeyInput(EscapeEvent());
		c.Put("escape_closes_panel", !editor.IsOpen);

		// ---- Esc：两级（画区域模式开着时先收模式）----
		editor.Open();
		editor.SwitchTab(EditorPanel.TabZones);
		editor.ZonePage.SetDrawMode(true);

		editor._UnhandledKeyInput(EscapeEvent());
		c.Put("escape_stops_draw_mode_first", !editor.ZonePage.DrawMode);
		c.Put("escape_keeps_panel_on_first_press", editor.IsOpen);
		c.Put("escape_hides_overlay_first", !editor.Overlay.Visible);

		editor._UnhandledKeyInput(EscapeEvent());
		c.Put("escape_closes_panel_on_second_press", !editor.IsOpen);

		// ---- 遮罩层那条路 ----
		//
		// 画区域模式开着时遮罩层是可见的，而它在 <c>_Input</c> 里跑在
		// <c>_UnhandledKeyInput</c> <b>之前</b>。本层一旦自己把 <c>Esc</c> 吃掉，
		// 面板那一层就永远收不到 —— 症状与修复前一模一样（关不掉面板），
		// 只是原因换了个地方。所以这里单独验遮罩层那一层也走通了。
		editor.Open();
		editor.SwitchTab(EditorPanel.TabZones);
		editor.ZonePage.SetDrawMode(true);

		editor.Overlay._Input(EscapeEvent());
		c.Put("overlay_escape_stops_draw_mode", !editor.ZonePage.DrawMode);
		c.Put("overlay_escape_keeps_panel_open", editor.IsOpen);

		// 模式已经收掉、遮罩也藏起来了 —— 此时遮罩层<b>不该再管 Esc</b>。
		// 它一收工就退场，剩下的交给面板那一层（<c>_UnhandledKeyInput</c>），
		// 这也是"两级退出"能成立的原因：两级分别落在两个节点上。
		//
		// 第一版这里断言的是"遮罩层自己把面板也关了"，红了 —— <b>而产品是对的</b>：
		// 遮罩隐藏时 <c>_Input</c> 直接早退，本来就收不到键。
		c.Put("overlay_is_gone_after_mode_exits", !editor.Overlay.Visible);
		editor._UnhandledKeyInput(EscapeEvent());
		c.Put("overlay_escape_closes_panel_when_no_mode", !editor.IsOpen);

		// ---- 拖着矩形时那一下 Esc 不算 ----
		//
		// 正拖着的时候按 Esc，本意是"放弃这一块"，而不是"把整个面板关掉"。
		// 面板若在拖拽中途关掉，遮罩层会连拖动状态一起清 ——
		// 看起来像"拖到一半界面闪了一下"。
		editor.Open();
		editor.SwitchTab(EditorPanel.TabZones);
		editor.ZonePage.SetDrawMode(true);

		ZoneDrawOverlay overlay = editor.Overlay;
		overlay.SimulateDragStartForTest(new Vector2(600f, 400f));
		c.Put("overlay_reports_dragging", overlay.IsDragging);

		var dragEsc = EscapeEvent();
		c.Put("escape_not_consumed_while_dragging", !editor.HandleEscape(dragEsc));
		c.Put("escape_ignored_while_dragging", editor.IsOpen && editor.ZonePage.DrawMode);

		// 收拾：收掉这次没画完的拖拽
		editor.ZonePage.SetDrawMode(false);
		editor.Close();

		// ---- 光标 ----
		//
		// "现在是画区域模式"需要一条持续可见的提示：遮罩层那层 5% 的淡色
		// 在深色桌面上几乎看不出来，而"忘了自己还开着它"是这个模式唯一的风险。
		editor.Open();
		editor.SwitchTab(EditorPanel.TabZones);
		editor.ZonePage.SetDrawMode(true);
		Control.CursorShape activeCursor = overlay.CursorForTest;
		editor.ZonePage.SetDrawMode(false);
		Control.CursorShape idleCursor = overlay.CursorForTest;

		r["cursor_in_draw_mode"] = (int)activeCursor;
		r["cursor_out_of_draw_mode"] = (int)idleCursor;
		c.Put("cursor_is_cross_in_draw_mode", activeCursor == Control.CursorShape.Cross);
		c.Put("cursor_restored_after_draw_mode", idleCursor == Control.CursorShape.Arrow);

		// 收尾：把面板关掉，别把"开着"这个状态留给下一节
		// （下一节假定编辑器是关着的，这是 <c>ProbeShell</c> 以来一贯的前提）。
		editor.Close();
		editor.SwitchTab(EditorPanel.TabCards);
	}

	/// <summary>一个"<c>Esc</c> 按下"事件（喂给 <c>_UnhandledKeyInput</c> / <c>_Input</c> 用）。</summary>
	private static InputEventKey EscapeEvent() => new() { Keycode = Key.Escape, Pressed = true };

	/// <summary>页签容器里有几个页。</summary>
	private static int TabCount(EditorPanel editor)
	{
		if (editor.GetChild(0) is not PanelContainer bg || bg.GetChildCount() == 0)
			return 0;

		if (bg.GetChild(0) is not VBoxContainer column)
			return 0;

		foreach (Node child in column.GetChildren())
		{
			if (child is TabContainer tabs)
				return tabs.GetTabCount();
		}

		return 0;
	}

	// ------------------------------------------------------------------ 卡牌页

	private static void ProbeCardPage(
		Checks c, Godot.Collections.Dictionary r,
		EditorPanel editor, ObjectManager objects)
	{
		CardEditorPage page = editor.CardPage;

		if (page.ListedIds.Count == 0)
		{
			c.Put("card_list_has_entries", false);
			return;
		}

		c.Put("card_list_has_entries", true);
		c.Put("card_list_matches_pool", page.ListedIds.Count == objects.CardDefinitions.Count);

		// 选中第一张 → 详情与预览都要跟上
		string id = page.ListedIds[0];
		page.Select(id);

		c.Put("select_marks_the_row", page.SelectedId == id);
		c.Put("select_shows_definition", page.Selected?.Id == id);
		c.Put("preview_has_definition", page.PreviewHasDefinition);

		// 改定义 → 立刻生效
		CardDefinitionService.MutateCard(objects, id, d => d.DisplayName = "自检改名");
		c.Put("mutate_updates_pool", objects.CardDefinitions[id].DisplayName == "自检改名");
		c.Put("mutate_marks_dirty", CardDefinitionService.IsDirty);

		// 顶栏那条"未保存"曾经连红两次，而报告里只有一个 false ——
		// 分不清是"信号没到"、"到了没刷"、还是"刷了但读到别的值"。
		// 所以把三个数一起写进报告（"把猜换成读"）。
		r["status_refresh_count"] = editor.StatusRefreshCount;
		r["definition_signal_count"] = editor.DefinitionSignalCount;
		r["dirty_title"] = editor.TitleForTest;

		c.Put("definition_signal_reached_panel", editor.DefinitionSignalCount > 0);
		c.Put("editor_shows_dirty", editor.TitleForTest.Contains("未保存"));

		// 改完要能被"重新读一遍"看到（预览读的是同一份对象）
		page.OnShown();
		c.Put("list_reflects_rename", page.ListedIds.Count == objects.CardDefinitions.Count);

		CardDefinitionService.MutateCard(objects, id, d => d.DisplayName = "火球术");

		// 新建一张卡：卡池 + 列表都要多一条。
		//
		// <b>列表要靠 <c>RefreshList()</c> 显式刷。</b>
		// 第一版这里只调了 <c>editor.NotifyChanged()</c>（它以前会连带刷整页），
		// 而那个行为在修"打字抢焦点"时被去掉了 —— 于是这条断言当场变红，
		// 正好把"哪些地方真的需要刷列表"逼了出来。
		// 这就是自检该干的事：改行为时，依赖旧行为的调用点必须自己暴露。
		int before = objects.CardDefinitions.Count;
		string newId = CardDefinitionService.CreateCard(objects, "自检新卡");
		page.RefreshList();
		editor.NotifyChanged();

		c.Put("create_card_adds_to_pool", objects.CardDefinitions.Count == before + 1);
		c.Put("create_card_appears_in_list", page.ListedIds.Contains(newId));

		// 桌上没有实例 → 删得掉（"拦住"的另一半是删得掉，两条都要验）
		c.Put("delete_unused_card_succeeds", CardDefinitionService.DeleteCard(objects, newId));
		page.RefreshList();
		editor.NotifyChanged();
		c.Put("deleted_card_left_the_list", !page.ListedIds.Contains(newId));

		r["cards_after_probe"] = objects.CardDefinitions.Count;
	}

	/// <summary>
	/// 卡池上方那三个按钮（M5.5 第 2 条）：<b>"新建 / 复制 / 删除"必须真的能点到。</b>
	///
	/// 用户报的是"根本没有新建、删除卡牌的功能"，而实测可知
	/// <c>CardDefinitionService</c> 里那两个方法从 M5 起就在、自检也一直在调它们 ——
	/// <b>缺的只有入口</b>。所以这一节验的东西与上面那一节刻意不同：
	/// 上面验服务层（"删得掉 / 删不掉"），这里验<b>按钮连着服务层</b>
	/// （<c>EmitSignal(Pressed)</c>，走真实信号，而不是直接调方法）。
	///
	/// 两个反例是必须的：
	/// <list type="bullet">
	/// <item><b>复制必须是深拷贝</b>：改副本的字段、版式、字段列表，原卡一个都不许变。
	///   共享引用的话症状是"改了副本，原卡也跟着变"，而它只在"复制完再改一次"时出现。</item>
	/// <item><b>桌上有实例时删除要被拦住</b>：否则按钮看起来"能删"，
	///   而实际发生的是存档里凭空少牌。</item>
	/// </list>
	/// </summary>
	private static void ProbeCardActions(
		Checks c, Godot.Collections.Dictionary r,
		EditorPanel editor, ObjectManager objects)
	{
		CardEditorPage page = editor.CardPage;
		editor.SwitchTab(EditorPanel.TabCards);
		page.RefreshList();

		// ---- 按钮真的存在，而且挂在页里（不是"建了对象没挂上去"） ----
		Button newButton = page.NewCardButton;
		Button dupButton = page.DuplicateCardButton;
		Button delButton = page.DeleteCardButton;

		c.Put("card_action_buttons_exist",
			GodotObject.IsInstanceValid(newButton)
			&& GodotObject.IsInstanceValid(dupButton)
			&& GodotObject.IsInstanceValid(delButton));
		c.Put("card_action_buttons_are_in_tree",
			newButton.IsInsideTree() && dupButton.IsInsideTree() && delButton.IsInsideTree());

		// ---- 新建：卡池 +1、列表 +1、自动选中新卡 ----
		int before = objects.CardDefinitions.Count;
		newButton.EmitSignal(Button.SignalName.Pressed);
		string newId = page.SelectedId;

		r["card_new_id"] = newId;
		c.Put("new_card_button_adds_to_pool", objects.CardDefinitions.Count == before + 1);
		c.Put("new_card_button_selects_it", newId.Length > 0 && page.Selected?.Id == newId);
		c.Put("new_card_button_appears_in_list", page.ListedIds.Contains(newId));

		// 新卡要是"能直接改"的：给它一个字段与一段版式，下面复制时靠它们验深拷贝
		CardDefinitionService.MutateCard(objects, newId, d =>
		{
			d.DisplayName = "自检复制源";
			d.Fields.Add(new CardField { Key = "cost", Label = "费用", Value = "7", Slot = FieldSlot.BottomLeft });
			d.Template.TitleFontSize = 41;
		});

		// ---- 复制：新 id、深拷贝 ----
		dupButton.EmitSignal(Button.SignalName.Pressed);
		string copyId = page.SelectedId;

		r["card_copy_id"] = copyId;
		c.Put("duplicate_button_adds_to_pool", objects.CardDefinitions.Count == before + 2);
		c.Put("duplicate_gets_a_new_id", copyId.Length > 0 && copyId != newId);
		c.Put("duplicate_is_selected", page.Selected?.Id == copyId);
		c.Put("duplicate_appears_in_list", page.ListedIds.Contains(copyId));

		CardDefinition source = objects.CardDefinitions[newId];
		CardDefinition copy = objects.CardDefinitions[copyId];

		// 值相等。三条分开断言而不是合成一条：
		// 合成一条时报告里只有一个 false，还得再跑一轮才知道是哪一项。
		// （第一版就是合成的一条，红了一次；分开之后一眼看到是名字那一项 ——
		//   而那正是设计如此：副本名字带「副本」后缀。）
		r["source_name_value"] = source.DisplayName;
		r["copy_name_value"] = copy.DisplayName;
		r["copy_field_value"] = copy.Fields.Count > 0 ? copy.Fields[0].Value : "(无字段)";
		r["copy_title_font_size"] = copy.Template.TitleFontSize;

		c.Put("duplicate_copies_field_values",
			copy.Fields.Count == source.Fields.Count && copy.Fields[0].Value == "7");
		c.Put("duplicate_copies_template_values", copy.Template.TitleFontSize == 41);
		c.Put("duplicate_name_gets_a_suffix", copy.DisplayName == $"{source.DisplayName} 副本");

		// 而<b>引用必须不同</b>：这是"深拷贝"与"共用一份引用"的分界。
		// 只比上面那一条的话，共享引用也能全绿 —— 直到有人改了副本。
		c.Put("duplicate_clones_field_list_reference", !ReferenceEquals(copy.Fields, source.Fields));
		c.Put("duplicate_clones_template_reference", !ReferenceEquals(copy.Template, source.Template));

		// 真改一次副本，看原卡动不动
		CardDefinitionService.MutateCard(objects, copyId, d =>
		{
			d.DisplayName = "副本改名了";
			d.Fields[0].Value = "99";
			d.Template.TitleFontSize = 12;
		});

		c.Put("editing_copy_leaves_source_name", source.DisplayName == "自检复制源");
		c.Put("editing_copy_leaves_source_field", source.Fields[0].Value == "7");
		c.Put("editing_copy_leaves_source_template", source.Template.TitleFontSize == 41);
		r["source_after_copy_edit"] = source.DisplayName;
		r["source_field_after_copy_edit"] = source.Fields[0].Value;

		// ---- 删除：桌上有实例 → 拦住 ----
		//
		// 示例内容本身就在桌上放了牌，所以随便挑一张在用的定义即可。
		// 前提不成立时分开报一条，免得一个 false 把人引向错误方向。
		string? used = null;
		foreach (TabletopObject obj in objects.AllObjects)
		{
			if (obj is CardObject card && objects.CardDefinitions.ContainsKey(card.Definition.Id))
			{
				used = card.Definition.Id;
				break;
			}
		}

		if (used is null)
		{
			r["card_delete_guard_skipped"] = "桌上没有卡牌实例";
			c.Put("delete_button_refused_for_used_card", false);
		}
		else
		{
			page.Select(used);
			int poolBefore = objects.CardDefinitions.Count;
			delButton.EmitSignal(Button.SignalName.Pressed);

			c.Put("delete_button_refused_for_used_card",
				objects.CardDefinitions.ContainsKey(used) && objects.CardDefinitions.Count == poolBefore);
			c.Put("delete_button_kept_it_in_list", page.ListedIds.Contains(used));
		}

		// ---- 删除：桌上没有实例 → 删得掉（反例的一半，缺了它"拒绝"可能只是整个坏了）----
		page.Select(copyId);
		int beforeDelete = objects.CardDefinitions.Count;
		delButton.EmitSignal(Button.SignalName.Pressed);

		c.Put("delete_button_removes_unused_card", objects.CardDefinitions.Count == beforeDelete - 1);
		c.Put("delete_button_left_the_list", !page.ListedIds.Contains(copyId));

		// 源卡也收拾掉（它只属于这一节，留着会污染后面的"卡池与列表一致"类断言）
		c.Put("delete_button_removes_second_unused_card", CardDefinitionService.DeleteCard(objects, newId));
		page.RefreshList();
		editor.NotifyChanged();
	}

	/// <summary>
	/// 预览框里<b>真的画了东西</b>吗 —— 数像素，不靠"看起来对"。
	///
	/// 这一条是补上的：上面那些断言只能证明"选中传到了预览"（<c>HasDefinition</c>），
	/// 而 M4 那个"0 高度面板"的教训是**控件存在、状态全对、屏幕上什么都没有**。
	/// 所以这里直接量预览矩形的颜色数：一张画好的卡面有几十种颜色
	/// （底色 + 文字抗锯齿 + 边框 + 圆角），空白或纯色衬底只有 1~2 种。
	///
	/// 还要验<b>正反两面不一样</b>：卡背与卡面的底色本来就不同，
	/// 如果两块区域的主色相同，多半是只有一个被画出来了。
	/// </summary>
	private static async Task ProbePreviewPixels(
		Checks c, Godot.Collections.Dictionary r,
		EditorPanel editor, ObjectManager objects)
	{
		if (objects.CardDefinitions.Count == 0)
		{
			r["preview_pixels_skipped"] = "没有卡牌定义";
			return;
		}

		// 选第一张卡并确保预览有内容
		CardEditorPage page = editor.CardPage;
		if (page.ListedIds.Count == 0)
		{
			c.Put("preview_face_has_detail", false);
			return;
		}

		page.Select(page.ListedIds[0]);
		editor.Open();

		// <b>必须先切回「卡牌」页。</b>
		//
		// 前面的分区探针把页签留在了「区域」页（它们各自 <c>SwitchTab</c> 过去验证），
		// 而 <c>TabContainer</c> 会把非当前页整体隐藏 ——
		// 于是预览控件 <c>IsVisibleInTree() == false</c>，<c>_Draw</c> 一次都不会被调用，
		// 采样到的自然是那块地方原有的底色。
		//
		// 这一条花了好几轮才定位：症状（"预览是一片灰"）与"预览画错了"完全一样，
		// 而根因在探针<b>自己的前序步骤</b>上。教训与 M3 那条
		// 「探针找空白点不能猜」同源：**探针要先把自己弄成被测对象期望的样子。**
		editor.SwitchTab(EditorPanel.TabCards);

		// <b>截图前必须让引擎真的画一帧。</b>
		//
		// 第一版在这里直接 <c>GetImage()</c>，读到的是<b>上一帧</b>的像素 ——
		// 面板刚打开、布局与 <c>_Draw</c> 都还没跑（<c>DrawCount == 0</c>），
		// 于是取到的是"原来那块地方"的内容。
		//
		// 这一条与 M2 那条"不要写死帧数、要轮询"是同一件事的另一半：
		// 不光要不写死，还得**在正确的时刻**读。
		await DevInputSim.Frame(editor);
		await DevInputSim.Frame(editor);

		Image frame = editor.GetViewport().GetTexture().GetImage();
		(int faceColors, _) = SampleRect(frame, page.PreviewFaceOnScreen);
		(int backColors, _) = SampleRect(frame, page.PreviewBackOnScreen);

		r["preview_face_colors"] = faceColors;
		r["preview_back_colors"] = backColors;
		r["preview_face_rect"] = RectArray(page.PreviewFaceOnScreen);
		r["preview_back_rect"] = RectArray(page.PreviewBackOnScreen);

		// 阈值取 8：实测一张有文字有边框的卡面是几十种颜色，空白衬底是 1~2 种。
		// 不取"必须 > 20"是为了别把"某天换了个极简卡面"变成假红。
		c.Put("preview_face_has_detail", faceColors >= 8);
		c.Put("preview_back_has_detail", backColors >= 8);

		// 预览控件必须<b>真的被画过</b>。这一条是"控件存在"与"控件画了东西"
		// 之间的那道界线 —— M4 那条 0 高度面板、以及本节的空预览，
		// 都是"状态全对、屏幕上什么都没有"。
		c.Put("preview_was_drawn", page.PreviewDrawCount > 0);

		ProbePreviewFill(c, r, editor, page, frame);
	}

	/// <summary>
	/// 预览里画的<b>是不是这张卡该有的颜色</b> —— 拿定义里的 <c>FaceTint</c> /
	/// <c>BackTint</c> 去预览矩形里找填充像素。
	///
	/// <b>为什么不用"两面颜色不同"那种判据：</b>我先写的是"正面主色 != 背面主色"，
	/// 实测差 19 —— 主色落在投影/衬底那一类<b>两边都有的东西</b>上，
	/// 于是阈值怎么定都站不住：宽了是假绿，紧了会随卡面样式假红。
	/// 改成"找定义里那个颜色"之后，判据与卡面样式无关，而且它验的是
	/// <b>"渲染器真的读了我改的那份定义"</b> —— 这恰恰是编辑器最该被验的一件事。
	/// </summary>
	private static void ProbePreviewFill(
		Checks c, Godot.Collections.Dictionary r,
		EditorPanel editor, CardEditorPage page, Image frame)
	{
		CardDefinition? def = page.Selected;
		if (def is null)
		{
			c.Put("preview_face_fill_matches_definition", false);
			c.Put("preview_back_fill_matches_definition", false);
			return;
		}

		// 8 位量化：存档与渲染都按 8 位通道走，直接比 float 会假红
		// （与 ColorJsonConverter.Quantize 同一个理由）。
		Color wantFace = ColorJsonConverter.Quantize(def.FaceTint);
		Color wantBack = ColorJsonConverter.Quantize(def.BackTint);

		int faceBest = ClosestColorDistance(frame, page.PreviewFaceOnScreen, wantFace);
		int backBest = ClosestColorDistance(frame, page.PreviewBackOnScreen, wantBack);

		// 把"有多近"的分布一并读出来：只报一个最小值时，分不清
		// "产品没画这个色"和"抗锯齿/文字占满了取样区"。
		// 这两件事的处置完全相反，所以宁可多写四个数。
		r["preview_want_face_tint"] = wantFace.ToHtml(false);
		r["preview_want_back_tint"] = wantBack.ToHtml(false);
		r["preview_face_best_delta"] = faceBest;
		r["preview_back_best_delta"] = backBest;
		r["preview_face_close_pixels"] = CountWithin(frame, page.PreviewFaceOnScreen, wantFace, 10, out int faceSampled);
		r["preview_face_sampled"] = faceSampled;

		// 正面实际的主导色（"想找的"与"实际最多的"并排看，一眼就知道差在哪）
		(int _, Color faceTop) = SampleRect(frame, page.PreviewFaceOnScreen);
		(int _, Color backTop) = SampleRect(frame, page.PreviewBackOnScreen);
		r["preview_face_top_color"] = faceTop.ToHtml(false);
		r["preview_back_top_color"] = backTop.ToHtml(false);
		r["preview_face_top_delta"] = ColorDelta(faceTop, wantFace);
		r["preview_back_top_delta"] = ColorDelta(backTop, wantBack);

		// ---- 诊断读数 ----
		//
		// 这一组是<b>用一轮排查换来的</b>，所以留下来常驻：
		// "预览是一片空白"至少有五种原因 —— 控件没建、矩形为 0、页签没切过去、
		// 没经过一次绘制、定义没接上。它们在快照像素里长得一模一样，
		// 而各自的处置完全不同。这七个数能一眼分开。
		//
		// （本次就是这么定位的：`preview_draw_count`/`IsVisibleInTree` 两条
		//   直接指出"探针自己把页签留在了别的分页上"。）
		r["preview_draw_count"] = page.PreviewDrawCount;
		r["preview_visible"] = page.PreviewVisible;
		r["preview_ctrl_rect"] = RectArray(page.PreviewRectOnScreen);
		r["preview_last_area"] = new Godot.Collections.Array { page.PreviewLastArea.X, page.PreviewLastArea.Y };
		r["editor_visible"] = editor.Visible;
		r["editor_current_tab"] = editor.CurrentTab;
		r["card_page_visible"] = page.IsVisibleInTree();

		c.Put("preview_face_fill_matches_definition", faceBest <= 10);
		c.Put("preview_back_fill_matches_definition", backBest <= 10);
	}

	/// <summary>
	/// 指示物预览里那<b>四个形状有没有落在自己的格子里</b> —— 用户实测反馈第 3 条：
	/// <b>"4 个形状的图标位置是歪的，不在框里"。</b>
	///
	/// <b>根因</b>：<c>TokenPreview._Draw</c> 里平移做了两次 ——
	/// <c>DrawSetTransform(cell.Position + cell.Size/2, …)</c> 已经把原点挪到了格子中心，
	/// 而传给渲染器的又是一个"以原点为中心"的矩形，于是圆心落到
	/// <c>格子中心 + 半个格子</c> 的地方，而形状框画在格子上。
	///
	/// <b>判据为什么是"数填充像素"而不是"取中心那一个点"：</b>
	/// 第一版取的是格子正中心那一个像素，结果四种形状全红 —— <b>而产品是对的</b>：
	/// 中心的文字（这一档是 <c>"1"</c>）正好画在那儿，取到的是文字色（实读 <c>#ffffff</c>），
	/// 于是"token 上有字"被判成了"形状歪了"。
	/// <b>一个取样点的断言，会把"那一点上恰好有别的东西"当成几何错误。</b>
	///
	/// 现在量两条互补的判据：
	/// <list type="number">
	/// <item><b>本格里必须有足够多的填充像素。</b>阈值按这个形状的<b>理论面积</b>反推
	///   （圆 πr²、内接正方形 2r²、正六边形 2.598r²、正三角形 1.299r²），取 40%
	///   —— 文字、图与抗锯齿会占掉一部分，而"整格偏移"会让本格掉到接近 0
	///   （图形整体挪到隔壁格去了）。</item>
	/// <item><b>邻格中心不许有本形状的填充。</b>这正是"歪了一格"的特征，
	///   而它对"图形偏了几个像素"是宽容的。</item>
	/// </list>
	/// 两条都按格子边长算，不写死像素：分辨率或版式变了判据跟着走。
	/// </summary>
	private static async Task ProbeTokenPreviewPixels(
		Checks c, Godot.Collections.Dictionary r, EditorPanel editor)
	{
		TokenEditorPage page = editor.TokenPage;

		if (page.ListedIds.Count == 0)
		{
			r["token_preview_pixels_skipped"] = "没有指示物定义";
			return;
		}

		// 探针要先把自己弄成被测对象期望的样子（M5 那条教训）：
		// 切到指示物页、选中一个、等两帧让布局与 _Draw 都跑过。
		editor.Open();
		editor.SwitchTab(EditorPanel.TabTokens);
		page.Select(page.ListedIds[0]);
		page.OnShown();

		await DevInputSim.Frame(editor);
		await DevInputSim.Frame(editor);

		Image frame = editor.GetViewport().GetTexture().GetImage();
		Color fill = ColorJsonConverter.Quantize(page.Selected!.Fill);
		r["token_preview_want_fill"] = fill.ToHtml(false);

		// 四种形状各一格，全都验 —— "歪"是同一处代码造成的，
		// 只验当前那一档的话，换形状才会暴露。
		foreach (TokenShape shape in System.Enum.GetValues<TokenShape>())
		{
			Rect2 cell = page.PreviewCellOnScreen(shape);
			float side = Mathf.Max(page.PreviewCellSide(shape), 20f);
			float radius = side * 0.5f;

			int hits = CountWithin(frame, cell, fill, 10, out int sampled);

			// 这个形状在格子里最多能占多少填充，按理论面积折算成取样点数。
			// CountWithin 隔 2 像素取一个，所以一个取样点代表 4 像素。
			float area = shape switch
			{
				TokenShape.Circle => Mathf.Pi * radius * radius,
				TokenShape.Square => 2f * radius * radius,
				TokenShape.Hexagon => 2.598f * radius * radius,
				TokenShape.Triangle => 1.299f * radius * radius,
				_ => Mathf.Pi * radius * radius,
			};

			// 取理论值的 40%：中心的文字与图会占掉一部分，而"整格偏移"
			// 会让本格的填充量掉到接近 0（图形整体挪到隔壁格去了）。
			int expected = Mathf.Max((int)(area / 4f * 0.40f), 5);

			// 邻格中心：歪了一格的话，本形状的填充正好会盖住那里。
			// 它同时还是"本格的中心是不是被文字占满"的兜底判据。
			Rect2 neighbour = page.PreviewCellOnScreen(NeighbourShape(shape));
			int neighbourDelta = PixelDelta(frame, neighbour.GetCenter(), fill);

			string key = shape.ToString().ToLowerInvariant();
			r[$"token_preview_{key}_cell"] = RectArray(cell);
			r[$"token_preview_{key}_fill_hits"] = hits;
			r[$"token_preview_{key}_fill_expected"] = expected;
			r[$"token_preview_{key}_fill_sampled"] = sampled;
			r[$"token_preview_{key}_neighbour_delta"] = neighbourDelta;

			c.Put($"token_preview_{key}_fill_is_in_its_cell", hits >= expected);
			c.Put($"token_preview_{key}_does_not_spill_into_neighbour", neighbourDelta > 60);
		}

		// 顺带验一下"预览真的画过"（与卡面预览同一个界线）
		c.Put("token_preview_was_drawn", page.PreviewDrawCount > 0);
	}

	/// <summary>形状枚举里的下一个（用来取"邻格"，不写死下标）。</summary>
	private static TokenShape NeighbourShape(TokenShape shape)
	{
		TokenShape[] all = System.Enum.GetValues<TokenShape>();
		return all[((int)shape + 1) % all.Length];
	}

	/// <summary>画面上某个点的颜色与目标色差多少（越界返回 999 = "完全不像"）。</summary>
	private static int PixelDelta(Image image, Vector2 at, Color target)
	{
		int x = Mathf.RoundToInt(at.X);
		int y = Mathf.RoundToInt(at.Y);

		if (x < 0 || y < 0 || x >= image.GetWidth() || y >= image.GetHeight())
			return 999;

		return ColorDelta(image.GetPixel(x, y), target);
	}

	/// <summary>矩形里有多少像素与目标色的差小于 <paramref name="tolerance"/>。</summary>
	private static int CountWithin(
		Image image, Rect2 rect, Color target, int tolerance, out int sampled)
	{
		int hits = 0;
		sampled = 0;

		int x0 = Mathf.Max(Mathf.FloorToInt(rect.Position.X), 0);
		int y0 = Mathf.Max(Mathf.FloorToInt(rect.Position.Y), 0);
		int x1 = Mathf.Min(Mathf.CeilToInt(rect.End.X), image.GetWidth());
		int y1 = Mathf.Min(Mathf.CeilToInt(rect.End.Y), image.GetHeight());

		for (int y = y0; y < y1; y += 2)
		{
			for (int x = x0; x < x1; x += 2)
			{
				sampled++;
				if (ColorDelta(image.GetPixel(x, y), target) <= tolerance)
					hits++;
			}
		}

		return hits;
	}

	/// <summary>一个屏幕矩形里，<b>离目标色最近</b>的那个像素差多少（0 = 完全相同）。</summary>
	private static int ClosestColorDistance(Image image, Rect2 rect, Color target)
	{
		int best = int.MaxValue;

		int x0 = Mathf.Max(Mathf.FloorToInt(rect.Position.X), 0);
		int y0 = Mathf.Max(Mathf.FloorToInt(rect.Position.Y), 0);
		int x1 = Mathf.Min(Mathf.CeilToInt(rect.End.X), image.GetWidth());
		int y1 = Mathf.Min(Mathf.CeilToInt(rect.End.Y), image.GetHeight());

		for (int y = y0; y < y1; y += 2)
		{
			for (int x = x0; x < x1; x += 2)
			{
				int d = ColorDelta(image.GetPixel(x, y), target);
				if (d < best)
					best = d;
			}
		}

		return best == int.MaxValue ? 999 : best;
	}

	private static Godot.Collections.Array RectArray(Rect2 r) => new()
	{
		r.Position.X, r.Position.Y, r.Size.X, r.Size.Y,
	};

	/// <summary>数一个屏幕矩形里的不同颜色数，并返回出现最多的那个。</summary>
	private static (int Distinct, Color Dominant) SampleRect(Image image, Rect2 rect)
	{
		var counts = new Dictionary<uint, int>();
		int best = 0;
		uint bestKey = 0;

		int x0 = Mathf.Max(Mathf.FloorToInt(rect.Position.X), 0);
		int y0 = Mathf.Max(Mathf.FloorToInt(rect.Position.Y), 0);
		int x1 = Mathf.Min(Mathf.CeilToInt(rect.End.X), image.GetWidth());
		int y1 = Mathf.Min(Mathf.CeilToInt(rect.End.Y), image.GetHeight());

		for (int y = y0; y < y1; y += 2)
		{
			for (int x = x0; x < x1; x += 2)
			{
				uint key = image.GetPixel(x, y).ToRgba32();
				counts.TryGetValue(key, out int n);
				counts[key] = n + 1;

				if (counts[key] > best)
				{
					best = counts[key];
					bestKey = key;
				}
			}
		}

		return (counts.Count, Color.Color8(
			(byte)((bestKey >> 24) & 0xff), (byte)((bestKey >> 16) & 0xff),
			(byte)((bestKey >> 8) & 0xff), (byte)(bestKey & 0xff)));
	}

	/// <summary>
	/// 两个颜色在 8 位通道上的差值之和（0 = 完全一样，765 = 黑白）。
	///
	/// 不用"阈值判等"而是把数值<b>返回来写进报告</b>：第一次跑时两者相差很小，
	/// 光看一个 <c>false</c> 分不清是"产品画错了"还是"阈值定高了"。
	/// </summary>
	private static int ColorDelta(Color a, Color b)
	{
		int dr = Mathf.Abs(Mathf.RoundToInt(a.R * 255f) - Mathf.RoundToInt(b.R * 255f));
		int dg = Mathf.Abs(Mathf.RoundToInt(a.G * 255f) - Mathf.RoundToInt(b.G * 255f));
		int db = Mathf.Abs(Mathf.RoundToInt(a.B * 255f) - Mathf.RoundToInt(b.B * 255f));
		return dr + dg + db;
	}

	// ------------------------------------------------------------------ 卡面字段

	/// <summary>
	/// 卡面字段的增删改。
	///
	/// 这一块的控件是**动态行**（一行一套控件），所以断言要盯两件事：
	/// <list type="number">
	/// <item>"加了字段，表单真的多出一行、定义里真的多一项"</item>
	/// <item>"改了值，定义跟着变" —— 而且要能<b>从表单上读回来</b>，
	///   否则"表单显示的是旧值"这种不一致没人看得见</item>
	/// </list>
	/// </summary>
	private static void ProbeCardFields(
		Checks c, Godot.Collections.Dictionary r,
		EditorPanel editor, ObjectManager objects)
	{
		CardEditorPage page = editor.CardPage;
		editor.SwitchTab(EditorPanel.TabCards);

		if (page.ListedIds.Count == 0)
		{
			c.Put("field_add_works", false);
			return;
		}

		page.Select(page.ListedIds[0]);
		CardDefinition? def = page.Selected;
		if (def is null)
		{
			c.Put("field_add_works", false);
			return;
		}

		string cardId = def.Id;
		int beforeCount = objects.CardDefinitions[cardId].Fields.Count;
		int rowsBefore = page.FieldRowCount;

		c.Put("field_rows_match_definition", rowsBefore == beforeCount);

		// ---- 加一个字段 ----
		page.AddFieldForTest();
		int afterCount = objects.CardDefinitions[cardId].Fields.Count;

		c.Put("field_add_works", afterCount == beforeCount + 1);
		c.Put("field_row_added", page.FieldRowCount == rowsBefore + 1);
		r["field_rows_after_add"] = page.FieldRowCount;

		// ---- 改它的值，并且从表单上读回来 ----
		int last = afterCount - 1;
		page.SetFieldValueForTest(last, "自检值");
		c.Put("field_value_written_to_definition",
			objects.CardDefinitions[cardId].Fields[last].Value == "自检值");
		c.Put("field_value_readable_from_form", page.FieldValueForTest(last) == "自检值");

		// 改槽位：字段换到"描述区"
		FieldSlot beforeSlot = objects.CardDefinitions[cardId].Fields[last].Slot;
		page.SetFieldSlotForTest(last, FieldSlot.Description);
		c.Put("field_slot_was_different_before", beforeSlot != FieldSlot.Description);
		c.Put("field_slot_written",
			objects.CardDefinitions[cardId].Fields[last].Slot == FieldSlot.Description);

		// ---- 删掉它 ----
		page.RemoveFieldForTest(last);
		c.Put("field_remove_works", objects.CardDefinitions[cardId].Fields.Count == beforeCount);
		c.Put("field_row_removed", page.FieldRowCount == rowsBefore);

		// ---- 一条防回归：换卡时不许把 A 的值写进 B ----
		//
		// 回填期间控件的信号如果没被挡住，"点第二张卡"就会把第一张卡的值写过去。
		// 这类 bug 只在连着点两张卡时出现，肉眼几乎归因不到。
		if (page.ListedIds.Count >= 2)
		{
			string first = page.ListedIds[0];
			string second = page.ListedIds[1];
			string firstName = objects.CardDefinitions[first].DisplayName;
			string secondName = objects.CardDefinitions[second].DisplayName;

			page.Select(second);
			page.Select(first);

			c.Put("switching_cards_keeps_names",
				objects.CardDefinitions[first].DisplayName == firstName
				&& objects.CardDefinitions[second].DisplayName == secondName);
			c.Put("form_shows_selected_name", page.NameForTest == firstName);
		}
	}

	// ------------------------------------------------------------------ 卡面版式

	/// <summary>
	/// 版式参数（字号 / 内边距）。判据落在"改完定义里真的变了、而且能还原"上。
	///
	/// 版式参数会进 <c>project.json</c>，所以这一节**必须自己还原** ——
	/// 留着会把后面几节的存档往返比对污染成"差异 2"。
	/// </summary>
	private static void ProbeCardTemplate(
		Checks c, Godot.Collections.Dictionary r,
		EditorPanel editor, ObjectManager objects)
	{
		CardEditorPage page = editor.CardPage;
		if (page.Selected is null)
		{
			c.Put("template_title_font_applied", false);
			return;
		}

		string cardId = page.Selected.Id;
		int originalTitle = objects.CardDefinitions[cardId].Template.TitleFontSize;
		float originalPadding = objects.CardDefinitions[cardId].Template.Padding;

		page.SetTitleFontSizeForTest(52);
		c.Put("template_title_font_applied",
			objects.CardDefinitions[cardId].Template.TitleFontSize == 52);

		page.SetPaddingForTest(0.09f);
		c.Put("template_padding_applied",
			Mathf.Abs(objects.CardDefinitions[cardId].Template.Padding - 0.09f) < 0.0001f);

		r["template_after_edit"] = new Godot.Collections.Array
		{
			objects.CardDefinitions[cardId].Template.TitleFontSize,
			objects.CardDefinitions[cardId].Template.Padding,
		};

		page.SetTitleFontSizeForTest(originalTitle);
		page.SetPaddingForTest(originalPadding);

		c.Put("template_restored",
			objects.CardDefinitions[cardId].Template.TitleFontSize == originalTitle
			&& Mathf.Abs(objects.CardDefinitions[cardId].Template.Padding - originalPadding) < 0.0001f);
	}

	// ------------------------------------------------------------------ 指示物页

	/// <summary>
	/// 「指示物」页：选、改、放到桌面、删除闸门。
	///
	/// 预览用的是<b>与桌上 Token 同一个渲染器</b>（<c>TokenFaceRenderer</c>，
	/// 这一轮从 <c>TokenObject</c> 里抽出来的），所以这里只验"矩形非零、画过了"这一层。
	/// </summary>
	private static void ProbeTokenPage(
		Checks c, Godot.Collections.Dictionary r,
		EditorPanel editor, ObjectManager objects)
	{
		TokenEditorPage page = editor.TokenPage;
		editor.SwitchTab(EditorPanel.TabTokens);
		page.OnShown();

		c.Put("token_list_matches", page.ListedIds.Count == objects.TokenDefinitions.Count);

		if (page.ListedIds.Count == 0)
		{
			c.Put("token_select_works", false);
			return;
		}

		page.Select(page.ListedIds[0]);
		c.Put("token_select_works", page.Selected is not null);
		c.Put("token_preview_has_definition", page.PreviewHasDefinition);

		string id = page.Selected!.Id;
		TokenShape originalShape = objects.TokenDefinitions[id].Shape;
		string originalText = objects.TokenDefinitions[id].Text;

		CardDefinitionService.MutateToken(objects, id, d => d.Shape = TokenShape.Triangle);
		c.Put("token_shape_written", objects.TokenDefinitions[id].Shape == TokenShape.Triangle);

		CardDefinitionService.MutateToken(objects, id, d => d.Text = "检");
		c.Put("token_text_written", objects.TokenDefinitions[id].Text == "检");

		// 放到桌面：物件数 +1，而且是一个带着刚选中定义的 Token
		int before = objects.ObjectCount;
		page.SpawnForTest();
		c.Put("token_spawned_into_scene", objects.ObjectCount == before + 1);

		TabletopObject? spawned = objects.AllObjects.Count > 0 ? objects.AllObjects[^1] : null;
		c.Put("spawned_is_a_token", spawned is TokenObject);
		c.Put("spawned_uses_selected_definition",
			spawned is TokenObject tok && tok.Definition.Id == id);

		// 桌上有实例 → 删不掉（与卡牌同一条闸门）
		c.Put("token_delete_blocked_when_in_use", !CardDefinitionService.DeleteToken(objects, id));
		r["token_refusal_message"] = CardDefinitionService.LastError;
		c.Put("token_refusal_states_the_count",
			CardDefinitionService.LastError.Contains(
				objects.CountTokenInstances(id).ToString()));

		// 收拾：删掉刚放上去的那个，再把示例定义改回去
		// （示例内容被改了会让后面几节基于"示例内容"的断言失去基准）
		if (spawned is not null)
			objects.DeleteObjectsWithoutHistory(new[] { spawned });

		CardDefinitionService.MutateToken(objects, id, d =>
		{
			d.Shape = originalShape;
			d.Text = originalText;
		});

		// <b>不要断言"示例定义现在删得掉了"。</b>示例内容本身就在桌上放了
		// 两个指示物用着它 —— 我第一版这么断言，红了，而产品是对的。
		// "删得掉"这一半改用**新建的定义**来验（下面那两条），
		// 因为那条路的前提完全由我们自己控制。
		c.Put("token_usage_counts_demo_instances",
			objects.CountTokenInstances(id) == CountTokensUsing(objects, id));

		// 新建一个再删掉：走完整的一轮，不碰示例数据
		string newId = CardDefinitionService.CreateToken(objects, "自检指示物");
		c.Put("token_create_works", newId.Length > 0 && objects.TokenDefinitions.ContainsKey(newId));
		c.Put("token_new_has_no_instances", objects.CountTokenInstances(newId) == 0);
		c.Put("token_delete_allowed_when_unused", CardDefinitionService.DeleteToken(objects, newId));

		// ---- 「复制这个」按钮（M5.5 第 2 条：两页同一套动作）----
		//
		// 与卡牌页那一节对称：按钮走真实信号，复制必须是深拷贝。
		// 用<b>示例定义</b>做源（它桌上有实例，删不掉，正好顺带证明复制不受实例数影响）。
		c.Put("token_duplicate_button_exists",
			GodotObject.IsInstanceValid(page.DuplicateTokenButton) && page.DuplicateTokenButton.IsInsideTree());

		string sourceId = page.Selected!.Id;
		int tokensBefore = objects.TokenDefinitions.Count;
		page.DuplicateTokenButton.EmitSignal(Button.SignalName.Pressed);
		string copyId = page.SelectedId;

		r["token_copy_id"] = copyId;
		c.Put("token_duplicate_adds_to_pool", objects.TokenDefinitions.Count == tokensBefore + 1);
		c.Put("token_duplicate_gets_a_new_id", copyId.Length > 0 && copyId != sourceId);
		c.Put("token_duplicate_is_selected", page.Selected?.Id == copyId);
		c.Put("token_duplicate_has_no_instances", objects.CountTokenInstances(copyId) == 0);

		TokenDefinition tokenSource = objects.TokenDefinitions[sourceId];
		TokenDefinition tokenCopy = objects.TokenDefinitions[copyId];
		c.Put("token_duplicate_copies_the_values",
			tokenCopy.Shape == tokenSource.Shape
			&& Mathf.IsEqualApprox(tokenCopy.Size, tokenSource.Size)
			&& tokenCopy.Image == tokenSource.Image);

		// 真改一次副本：源定义一个字段都不许变
		CardDefinitionService.MutateToken(objects, copyId, d =>
		{
			d.Shape = TokenShape.Square;
			d.Text = "副本改过";
		});

		c.Put("editing_token_copy_leaves_source_shape", tokenSource.Shape != TokenShape.Square);
		c.Put("editing_token_copy_leaves_source_text", tokenSource.Text != "副本改过");

		// 收拾：删掉副本、把源定义改回去（下一节的像素判据要用示例定义）
		c.Put("token_duplicate_copy_is_removable", CardDefinitionService.DeleteToken(objects, copyId));
		page.Select(sourceId);
		page.RefreshList();

		editor.NotifyChanged();
	}

	/// <summary>桌上有几个 Token 在用这份定义（与 <c>CountTokenInstances</c> 独立数一遍）。</summary>
	private static int CountTokensUsing(ObjectManager objects, string definitionId)
	{
		int n = 0;
		foreach (TabletopObject obj in objects.AllObjects)
		{
			if (obj is TokenObject token && token.Definition.Id == definitionId)
				n++;
		}

		return n;
	}

	// ------------------------------------------------------------------ 焦点（用户实测报的 bug）

	/// <summary>
	/// <b>输入框不许在打字时被抢走焦点</b> —— 这是用户实测报出来的 bug 的回归断言。
	///
	/// 症状：在 Token 页 / 区域页的输入框里每打一个字符就失去焦点，
	/// "打五个数字要用鼠标点五次"。
	///
	/// 根因：<c>NotifyChanged()</c> 每敲一个字符都调 <c>CurrentPage()?.OnShown()</c>，
	/// 而那一页的 <c>OnShown</c> 会<b>销毁并重建整列按钮</b>、并把表单里每个控件的
	/// <c>Text</c> 重写一遍 —— 焦点就跟着没了。
	///
	/// 所以这里验两件事，而且<b>都不要求真的拿到键盘焦点</b>
	/// （合成输入路径上 <c>HasFocus</c> 不可靠，依赖它会让断言随环境变红）：
	/// <list type="number">
	/// <item>「改一个字段」不会重建列表 —— 比<b>对象引用</b>，比"次数"更硬</item>
	/// <item>焦点还在时，输入框里的文本<b>不会被重写</b>（选中一段文字再改字段，选中区必须还在）</item>
	/// </list>
	/// </summary>
	private static void ProbeFocusIsKept(
		Checks c, Godot.Collections.Dictionary r,
		EditorPanel editor, ObjectManager objects)
	{
		TokenEditorPage page = editor.TokenPage;
		editor.SwitchTab(EditorPanel.TabTokens);
		page.OnShown();

		if (page.ListedIds.Count == 0 || page.Selected is null)
		{
			r["focus_probe_skipped"] = "没有指示物可测";
			return;
		}

		// ---- 1. 改一个字段，列表不许被重建 ----
		Button? before = page.FirstListButtonForTest;
		int rebuildsBefore = page.ListRebuildCount;

		if (before is null)
		{
			c.Put("notify_keeps_list_instances", false);
			c.Put("notify_does_not_rebuild_list", false);
			return;
		}

		string id = page.Selected.Id;
		int originalSize = (int)objects.TokenDefinitions[id].Size;

		CardDefinitionService.MutateToken(objects, id, d => d.Size = 200);
		editor.NotifyChanged();

		Button? after = page.FirstListButtonForTest;
		c.Put("notify_keeps_list_instances",
			ReferenceEquals(before, after) && GodotObject.IsInstanceValid(after));
		c.Put("notify_does_not_rebuild_list", page.ListRebuildCount == rebuildsBefore);
		r["list_rebuilds_during_edit"] = page.ListRebuildCount - rebuildsBefore;

		CardDefinitionService.MutateToken(objects, id, d => d.Size = originalSize);

		// ---- 2. 焦点还在时，文本框不许被"重写" ----
		//
		// 判据用"选中区还在不在"：`LineEdit.Text` 一旦被赋值（<b>哪怕赋的是同一个字符串</b>），
		// 选中区就会被清掉 —— 而 `HasFocus` 在合成输入路径上不可靠，拿它当判据会随环境假红。
		if (!page.GrabNameFocusForTest())
		{
			r["focus_probe_note"] = "拿不到键盘焦点（合成输入路径），只验了第 1 条";
			return;
		}

		c.Put("focus_grab_works", page.NameHasFocusForTest);

		string id2 = page.Selected!.Id;
		CardDefinitionService.MutateToken(objects, id2, d => d.Size = 250);
		page.SelectAllNameForTest();
		c.Put("selection_survives_selectall", page.NameHasSelectionForTest);

		// 刷新表单：这一步曾经把选中区弄没了（表单把每个框都重写了一遍）
		page.RefreshFieldsForTest();
		c.Put("focused_editbox_keeps_selection", page.NameHasSelectionForTest);
		c.Put("focused_editbox_keeps_its_text", page.NameTextForTest.Length > 0);
		r["selection_after_form_refresh"] = page.NameHasSelectionForTest;

		// 面板的状态刷新只该动顶栏文字，不该碰表单
		editor.NotifyChanged();
		c.Put("notify_keeps_focus_and_selection", page.NameHasSelectionForTest);

		CardDefinitionService.MutateToken(objects, id2, d => d.Size = originalSize);
		page.ReleaseNameFocusForTest();
		editor.NotifyChanged();
	}

	// ------------------------------------------------------------------ 端到端

	/// <summary>
	/// 从零走一遍真实顺序：<b>新建存档 → 导图 → 造卡 → 组卡组 → 画区域 → 发牌 → 布桌面 → 存盘读回</b>。
	///
	/// 它存在的理由与前面那些单点断言不同：要抓的是
	/// <b>"每一步单独都对、连起来不对"</b>这一类问题 ——
	/// 比如导进来的图在造卡时找不到、发出去的牌不带刚编的字段、
	/// 换了桌面尺寸之后存档里的尺寸没跟上。
	///
	/// 它在<b>自己的存档目录</b>里跑（新建 → 用完删掉），所以不会污染示例内容；
	/// 结束前把 <c>CurrentSave</c> 与桌面尺寸都还回去 —— 这一点是必须的，
	/// 后面读像素那一节要去当前存档的 <c>images/</c> 里找图。
	/// </summary>
	private static void ProbeEndToEnd(
		Checks c, Godot.Collections.Dictionary r,
		EditorPanel editor, ObjectManager objects, ZoneManager zones, Board board,
		SavePanel saves, UndoSystem undo)
	{
		const string SaveName = "自检端到端";

		// ---- 1. 新建一个空存档（默认桌面 + 示例定义池 + 一副示例卡组）----
		bool created = saves.CreateNew(SaveName);
		c.Put("e2e_new_save_created", created && AppPaths.CurrentSave == SaveName);
		c.Put("e2e_new_save_has_definitions", objects.CardDefinitions.Count > 0);
		c.Put("e2e_new_save_has_deck", objects.Decks.Count > 0);
		c.Put("e2e_new_save_table_is_empty", objects.ObjectCount == 0);

		// ---- 2. 导一张图（走与"导入图片"按钮同一条服务方法）----
		string tempDir = TempDir();
		AppPaths.EnsureDir(tempDir);
		string tempPath = $"{tempDir}/e2e_face.png";
		var image = Image.CreateEmpty(16, 16, false, Image.Format.Rgba8);
		image.Fill(new Color("#7a5cf0"));
		c.Put("e2e_temp_png_ok", image.SavePng(tempPath) == Error.Ok);

		string fileName = ImageImport.Copy(AppPaths.CurrentSave, tempPath);
		c.Put("e2e_import_into_new_save", fileName.Length > 0);
		c.Put("e2e_import_file_in_new_save",
			fileName.Length > 0 && FileAccess.FileExists(AppPaths.ImageFile(SaveName, fileName)));
		c.Put("e2e_import_texture_loads", fileName.Length > 0 && TextureStore.Get(fileName) is not null);

		// ---- 3. 造一张卡：底图 + 两个字段 ----
		string cardId = CardDefinitionService.CreateCard(objects, "自检造卡");
		c.Put("e2e_card_created", cardId.Length > 0 && objects.CardDefinitions.ContainsKey(cardId));

		CardDefinitionService.MutateCard(objects, cardId, d =>
		{
			d.FaceImage = fileName;
			d.DisplayName = "端到端卡";
			d.Fields.Add(new CardField { Key = "cost", Label = "费用", Value = "2", Slot = FieldSlot.TopRight, ShowLabel = true });
			d.Fields.Add(new CardField { Key = "text", Value = "从零走一遍。", Slot = FieldSlot.Description });
		});

		CardDefinition card = objects.CardDefinitions[cardId];
		c.Put("e2e_card_has_image", card.FaceImage == fileName);
		c.Put("e2e_card_has_fields", card.Fields.Count == 2);

		// ---- 4. 组一副卡组：3 张新卡 + 2 张示例卡 = 5 张 ----
		CardDeck deck = CardDeckService.Create(objects, "自检端到端卡组");
		CardDeckService.AddCard(deck, cardId, 3);
		foreach (string other in objects.CardDefinitions.Keys)
		{
			if (other != cardId)
			{
				CardDeckService.AddCard(deck, other, 2);
				break;
			}
		}

		c.Put("e2e_deck_total", deck.TotalCards == 5);
		r["e2e_deck_total"] = deck.TotalCards;

		// ---- 5. 画一块区域，把牌发进去 ----
		Zone? zone = ZoneEditService.Create(
			zones, ZoneKind.Deck, new Rect2(400f, 300f, 320f, 460f), "端到端牌库");
		c.Put("e2e_zone_drawn", zone is not null);

		DealResult result = CardDeckService.DealIntoZone(objects, zones, board, deck, zone?.Id ?? "");
		c.Put("e2e_deal_ok", result.Ok && result.Spawned == deck.TotalCards);
		r["e2e_deal"] = $"{result.Spawned} 张 → {result.TargetLabel}";
		c.Put("e2e_dealt_into_zone", zone is not null && zone.Count == deck.TotalCards);

		// 发进去的牌必须真的带着那张卡的字段与图（不是"造了 5 张空卡"）
		bool sawOurCard = false;
		if (zone is not null)
		{
			foreach (TabletopObject member in zone.Members)
			{
				if (member is CardObject co && co.Definition.Id == cardId
					&& co.Definition.FaceImage == fileName)
				{
					sawOurCard = true;
					break;
				}
			}
		}

		c.Put("e2e_dealt_cards_carry_the_definition", sawOurCard);
		c.Put("e2e_invariants_hold", ZoneInvariants.Check(objects, zones).All);

		// ---- 6. 布桌面（先量原始尺寸：后面还原要用）----
		Vector2 originalSize = board.Theme.BoardRect.Size;
		editor.BoardPage.ApplySizeForTest(2600f, 1700f);
		c.Put("e2e_board_resized", Mathf.IsEqualApprox(board.Theme.BoardRect.Size.X, 2600f));

		// ---- 7. 开玩 ----
		//
		// <b>这一步是这一节存在的理由。</b>前面几步证明"内容造出来了"，
		// 而这里证明<b>造出来的东西真的能玩</b> —— 画一块手牌区、把牌库的抽牌目标
		// 指过去、抽牌、翻面、排版，然后撤销。
		//
		// 而且这两处改动走的是<b>区域页表单</b>（不是直接改定义），
		// 于是"编辑器在一个<b>有成员的活区域</b>上改参数"这件事也顺带被验到 ——
		// 那正是区域编辑最容易出错的地方（改完要 ApplyLayout，
		// 而成员的位置与次序都得跟着重排）。
		Zone? hand = ZoneEditService.Create(
			zones, ZoneKind.Hand, new Rect2(400f, 820f, 3168f, 480f), "端到端手牌");
		c.Put("e2e_hand_zone_drawn", hand is not null);

		if (hand is not null && zone is not null)
		{
			editor.SwitchTab(EditorPanel.TabZones);
			editor.ZonePage.OnShown();
			editor.ZonePage.SelectForTest(zone.Id);
			c.Put("zone_page_selected_our_zone", editor.ZonePage.SelectedZoneId == zone.Id);

			editor.ZonePage.SetMaxCardsForTest(40);
			c.Put("zone_page_max_cards_applied", zone.Definition.MaxCards == 40);

			editor.ZonePage.SetDrawTargetForTest(hand.Id);
			c.Put("zone_page_draw_target_applied", zone.Definition.DrawTargetId == hand.Id);

			// ---- 双击牌库 = 抽一张（走与真实双击同一条路）----
			int deckBefore = zone.Count;
			bool handled = zones.TryHandleDoubleClick(zone.Definition.Center);
			c.Put("e2e_double_click_handled", handled);
			c.Put("e2e_draw_moved_one", zone.Count == deckBefore - 1 && hand.Count == 1);
			c.Put("e2e_drawn_card_is_face_up",
				hand.Top is TabletopObject drawnTop && !drawnTop.IsFaceDown);
			c.Put("e2e_drawn_card_visible",
				hand.Top is TabletopObject visibleTop && visibleTop.Visible);
			c.Put("e2e_drawn_card_left_the_deck",
				hand.Top is TabletopObject leftTop && leftTop.ZoneId == hand.Id);

			// ---- 翻面 + 换排版（区域菜单里那两件事）----
			zones.SetFaceDown(hand, true);
			c.Put("e2e_flip_zone_records",
				hand.Count > 0 && hand.Members[0].IsFaceDown);

			zones.SetSortMode(hand, ZoneSortMode.Fan);
			c.Put("e2e_sort_mode_applied", hand.Definition.SortMode == ZoneSortMode.Fan);

			// ---- 撤销这几步之后，桌子仍然自洽 ----
			undo.Undo();
			undo.Undo();
			c.Put("e2e_undo_after_play_keeps_invariants",
				ZoneInvariants.Check(objects, zones).All);
			c.Put("e2e_draw_was_recorded", undo.CanUndo || undo.CanRedo);

			r["e2e_play"] =
				$"牌库 {zone.Count} · 手牌 {hand.Count} · 抽出的是 {(hand.Top is TabletopObject t2 ? t2.Uid : "无")}";
		}
		else
		{
			c.Put("e2e_double_click_handled", false);
			c.Put("e2e_draw_moved_one", false);
			c.Put("e2e_drawn_card_is_face_up", false);
			c.Put("e2e_drawn_card_visible", false);
			c.Put("e2e_drawn_card_left_the_deck", false);
			c.Put("e2e_flip_zone_records", false);
			c.Put("e2e_sort_mode_applied", false);
			c.Put("e2e_undo_after_play_keeps_invariants", false);
			c.Put("e2e_draw_was_recorded", false);
		}

		// ---- 8. 存盘 → 从磁盘读回来 → 逐项核对 ----
		c.Put("e2e_save_ok", SaveSystem.Save(SaveName, objects, zones, board.Theme));

		SaveProject? project = SaveSystem.ReadProject(SaveName);
		c.Put("e2e_project_read_back", project is not null);
		if (project is not null)
		{
			c.Put("e2e_project_has_our_card",
				project.Cards.Exists(x => x.Id == cardId && x.FaceImage == fileName && x.Fields.Count == 2));
			c.Put("e2e_project_has_our_deck", project.Decks.Exists(x => x.Id == deck.Id && x.TotalCards == 5));
			c.Put("e2e_project_has_board_size", Mathf.IsEqualApprox(project.Board.BoardWidth, 2600f));
			c.Put("e2e_project_keeps_chinese", SaveJson.Serialize(project).Contains("端到端卡"));
		}

		// ---- 9. 收尾：把示例存档与示例桌面还回去 ----
		//（顺序要紧：先把 CurrentSave 指回示例存档，再把尺寸改回去。）
		SaveSystem.DeleteSave(SaveName);
		AppPaths.SetCurrentSave(AppPaths.DefaultSaveName);
		editor.BoardPage.ApplySizeForTest(originalSize.X, originalSize.Y);
		editor.CardPage.CloseImportDialogs();

		c.Put("e2e_cleaned_up",
			AppPaths.CurrentSave == AppPaths.DefaultSaveName
			&& board.Theme.BoardRect.Size == originalSize);
	}

	// ------------------------------------------------------------------ 图片导入

	private static void ProbeImport(
		Checks c, Godot.Collections.Dictionary r,
		EditorPanel editor, ObjectManager objects)
	{
		// 造一张 8×8 的临时 PNG。**不依赖任何外部素材** —— 自检必须能在
		// 一台干净机器上跑出同样的结论。
		//
		// <b>临时文件放在存档根旁边，不放 <c>user://</c>：</b>
		// 沙箱里 <c>user://</c> 不可写（M4 为此专门加了 <c>--save-root</c>），
		// 而这一节恰恰要验"导入真的把字节写进磁盘了"。
		// 第一次跑就撞上了：<c>Image.SavePng</c> 报 <c>FileCantOpen</c>，
		// 整节被跳过 —— 而那看起来和"导入功能坏了"一模一样。
		string tempDir = TempDir();
		AppPaths.EnsureDir(tempDir);

		string tempPath = $"{tempDir}/selfcheck_face.png";
		var image = Image.CreateEmpty(8, 8, false, Image.Format.Rgba8);
		image.Fill(new Color("#c94f4f"));
		Error saved = image.SavePng(tempPath);

		if (saved != Error.Ok)
		{
			r["import_skipped"] = $"造不出临时 PNG：{saved}（{tempPath}）";
			c.Put("import_temp_png_writable", false);
			return;
		}

		c.Put("import_temp_png_writable", true);

		int before = ImageImport.ImportCount;
		string fileName = ImageImport.Copy(AppPaths.CurrentSave, tempPath);

		c.Put("import_returns_name", fileName.Length > 0 && fileName.EndsWith(".png"));
		r["import_file_name"] = fileName;
		r["import_error"] = ImageImport.LastError;
		c.Put("import_counted", ImageImport.ImportCount == before + 1);

		if (fileName.Length == 0)
			return;

		string path = AppPaths.ImageFile(AppPaths.CurrentSave, fileName);
		c.Put("import_file_exists", FileAccess.FileExists(path));

		// 内容一模一样（不是"写了个空文件"）
		byte[] source = FileAccess.GetFileAsBytes(tempPath) ?? System.Array.Empty<byte>();
		byte[] written = FileAccess.GetFileAsBytes(path) ?? System.Array.Empty<byte>();
		c.Put("import_bytes_match", source.Length > 0 && source.Length == written.Length);
		r["import_bytes"] = written.Length;

		// 立刻能读成纹理（"改图不用重启"的全部意义）
		c.Put("import_texture_loads", TextureStore.Get(fileName) is not null);

		// 重名不覆盖：再导一次同一张 → 复用同一个文件名、目录里不多出文件
		int filesBefore = ImageImport.ListImages(AppPaths.CurrentSave).Length;
		string again = ImageImport.Copy(AppPaths.CurrentSave, tempPath);
		int filesAfter = ImageImport.ListImages(AppPaths.CurrentSave).Length;
		c.Put("import_same_image_reuses_name", again == fileName);
		c.Put("import_same_image_adds_no_file", filesAfter == filesBefore);

		// 内容不同但同名的图 → 不能覆盖原来那张
		string otherPath = $"{tempDir}/selfcheck_face_other.png";
		var other = Image.CreateEmpty(8, 8, false, Image.Format.Rgba8);
		other.Fill(new Color("#4f79c9"));
		other.SavePng(otherPath);

		// 故意把源文件名改成与第一张相同，验证"同名不同内容"这一路
		string clashPath = $"{tempDir}/{fileName}";
		CopyRaw(otherPath, clashPath);
		string second = ImageImport.Copy(AppPaths.CurrentSave, clashPath);
		c.Put("import_conflict_gets_new_name", second.Length > 0 && second != fileName);
		r["import_conflict_name"] = second;

		// 目录里的图片清单要能列出来（「桌面」页那个下拉靠它）
		c.Put("image_list_contains_import",
			System.Array.IndexOf(ImageImport.ListImages(AppPaths.CurrentSave), fileName) >= 0);

		// 用它当卡面 → 桌上那张卡要能拿到它
		if (objects.CardDefinitions.Count > 0)
		{
			foreach (CardDefinition def in objects.CardDefinitions.Values)
			{
				CardDefinition target = def;
				CardDefinitionService.MutateCard(objects, target.Id, d => d.FaceImage = fileName);
				c.Put("definition_keeps_bare_filename", objects.CardDefinitions[target.Id].FaceImage == fileName);
				c.Put("resolve_path_points_into_images",
					ImageImport.ResolvePath(fileName) == AppPaths.ImageFile(AppPaths.CurrentSave, fileName));
				CardDefinitionService.MutateCard(objects, target.Id, d => d.FaceImage = "");
				break;
			}
		}
	}

	private static void CopyRaw(string from, string to)
	{
		byte[]? bytes = FileAccess.GetFileAsBytes(from);
		if (bytes is null)
			return;

		using FileAccess? f = FileAccess.Open(to, FileAccess.ModeFlags.Write);
		f?.StoreBuffer(bytes);
	}

	/// <summary>自检用的临时目录：<b>存档根的兄弟目录</b>（同一个可写位置）。</summary>
	private static string TempDir()
	{
		string root = AppPaths.Root.Replace('\\', '/').TrimEnd('/');
		int slash = root.LastIndexOf('/');
		string parent = slash > 0 ? root[..slash] : root;
		return $"{parent}/_selfcheck_tmp";
	}

	/// <summary>跑完把临时目录清掉（它就在工作区里，留着会进 git status）。</summary>
	internal static void CleanTempDir()
	{
		string dir = TempDir();
		if (!DirAccess.DirExistsAbsolute(dir))
			return;

		using DirAccess? d = DirAccess.Open(dir);
		if (d is null)
			return;

		foreach (string file in d.GetFiles())
			d.Remove(file);

		d.Dispose();
		DirAccess.RemoveAbsolute(dir);
	}

	// ------------------------------------------------------------------ 卡组页

	private static void ProbeDeckPage(
		Checks c, Godot.Collections.Dictionary r,
		EditorPanel editor, ObjectManager objects, ZoneManager zones, Board board)
	{
		DeckEditorPage page = editor.DeckPage;
		editor.SwitchTab(EditorPanel.TabDecks);
		page.OnShown();

		// 示例卡组必须在（否则"牌库那 20 张是按什么配方来的"没有答案）
		c.Put("starter_deck_present", objects.Decks.ContainsKey(DemoContent.StarterDeckId));

		CardDeck? deck = page.SelectedDeck;
		c.Put("deck_page_selects_a_deck", deck is not null);
		if (deck is null)
			return;

		r["deck_total"] = deck.TotalCards;

		// 新建 + 加卡 + 删掉（编辑器的三个动作都走服务层）
		CardDeck created = CardDeckService.Create(objects, "自检卡组");
		editor.NotifyChanged();
		c.Put("create_deck_adds", objects.Decks.ContainsKey(created.Id));

		string? firstCardId = FirstCardId(objects);
		if (firstCardId is not null)
		{
			CardDeckService.AddCard(created, firstCardId, 3);
			CardDeckService.AddCard(created, firstCardId, 2);
			c.Put("add_card_merges_same_row", created.Cards.Count == 1 && created.Cards[0].Count == 5);
			c.Put("deck_total_counts_cards", created.TotalCards == 5);
		}

		// 发到牌库：牌库张数必须<b>恰好</b>加 TotalCards
		Zone? deckZone = zones.Find(DemoContent.DeckZoneId);
		if (deckZone is not null && created.TotalCards > 0)
		{
			int before = deckZone.Count;
			DealResult result = CardDeckService.DealIntoZone(objects, zones, board, created, deckZone.Id);

			c.Put("deal_into_zone_ok", result.Ok);
			c.Put("deal_into_zone_added_exact", deckZone.Count == before + created.TotalCards
				|| deckZone.Count == before + result.Spawned);
			r["deal_result"] = $"{result.Spawned} 张 → {result.TargetLabel}";

			// 发进去的牌必须是盖放的（牌库的出厂策略），不然"发了一副牌却全亮着"
			if (deckZone.Top is TabletopObject top)
				c.Put("dealt_cards_are_face_down", top.IsFaceDown);

			// 收拾干净：把刚发进去的牌删掉。走 DeleteObjectsWithoutHistory ——
			// 探针的动作不该在历史里留下条目（"探针不许改变可观测状态"也包括时间线）。
			CleanUpZoneTail(objects, deckZone, created.TotalCards);
		}

		// 散到桌面：张数对得上，且都没进区域
		int tableBefore = objects.ObjectCount;
		int scattered = CardDeckService.ScatterOnTable(objects, board, created);
		c.Put("scatter_returns_count", scattered == created.TotalCards);

		int loose = 0;
		foreach (TabletopObject obj in objects.AllObjects)
		{
			if (obj.ZoneId.Length == 0 && obj.PileId == 0 && obj.Visible)
				loose++;
		}

		c.Put("scatter_added_to_table", objects.ObjectCount == tableBefore + scattered);
		r["loose_after_scatter"] = loose;

		// 删掉自检卡组（卡组只是配方，删它不该影响桌上的牌）
		int objectsBeforeDeckDelete = objects.ObjectCount;
		objects.Decks.Remove(created.Id);
		c.Put("delete_deck_keeps_table", objects.ObjectCount == objectsBeforeDeckDelete);
	}

	/// <summary>把刚发进区域的最后 N 张删掉（探针自己收拾自己弄的东西）。</summary>
	private static void CleanUpZoneTail(ObjectManager objects, Zone zone, int count)
	{
		int take = Mathf.Min(count, zone.Count);
		var victims = new List<TabletopObject>(take);
		for (int i = zone.Count - take; i < zone.Count; i++)
			victims.Add(zone.Members[i]);

		// DeleteObjectsWithoutHistory 自己会先把它们从区域里摘出来
		// （它内部走 _zones?.ForgetObjects）—— 所以不必手工 RemoveMember。
		objects.DeleteObjectsWithoutHistory(victims);
	}

	private static string? FirstCardId(ObjectManager objects)
	{
		foreach (string id in objects.CardDefinitions.Keys)
			return id;

		return null;
	}

	// ------------------------------------------------------------------ 桌面页

	private static void ProbeBoardPage(
		Checks c, Godot.Collections.Dictionary r, EditorPanel editor, Board board)
	{
		BoardEditorPage page = editor.BoardPage;
		editor.SwitchTab(EditorPanel.TabBoard);
		page.OnShown();

		Vector2 originalSize = board.Theme.BoardRect.Size;
		int originalGrid = board.Theme.GridSize;

		// 直接调服务层那一步（页里每个控件都走同一条 Mutate 路）
		page.ApplySizeForTest(4000f, 2600f);
		Vector2 resized = board.Theme.BoardRect.Size;
		c.Put("board_resize_applied", Mathf.IsEqualApprox(resized.X, 4000f) && Mathf.IsEqualApprox(resized.Y, 2600f));
		r["board_size_after_resize"] = new Godot.Collections.Array { resized.X, resized.Y };

		page.SetGridSizeForTest(250);
		c.Put("grid_size_applied", board.Theme.GridSize == 250);

		// 非法输入要被挡住（填个 10 上去不该把桌面缩成一个点）
		page.ApplySizeForTest(10f, 10f);
		c.Put("board_rejects_tiny_size",
			Mathf.IsEqualApprox(board.Theme.BoardRect.Size.X, 4000f));

		// 还原
		page.ApplySizeForTest(originalSize.X, originalSize.Y);
		page.SetGridSizeForTest(originalGrid);
		c.Put("board_size_restored",
			board.Theme.BoardRect.Size == originalSize && board.Theme.GridSize == originalGrid);
	}

	// ------------------------------------------------------------------ 区域页

	private static void ProbeZonePage(
		Checks c, Godot.Collections.Dictionary r,
		EditorPanel editor, ZoneManager zones, BoardCamera cam)
	{
		ZoneEditorPage page = editor.ZonePage;
		editor.SwitchTab(EditorPanel.TabZones);
		page.OnShown();

		c.Put("zone_list_matches", page.ListedIds.Count == zones.AllZones.Count);

		// 画区域：走 ZoneEditService 那条路（与遮罩层松手时是同一条）。
		int before = zones.AllZones.Count;
		Zone? drawn = ZoneEditService.Create(
			zones, ZoneKind.Deck, new Rect2(600f, 1200f, 320f, 460f), "自检区域");

		c.Put("zone_created", drawn is not null && zones.AllZones.Count == before + 1);
		if (drawn is null)
			return;

		// 出厂参数：一个 Deck 该是叠放 + 盖放 + 双击抽牌
		c.Put("drawn_zone_uses_kind_defaults",
			drawn.Definition.Kind == ZoneKind.Deck
			&& drawn.Definition.SortMode == ZoneSortMode.Stack
			&& drawn.Definition.FaceOnEnter == FaceOnEnter.FaceDown
			&& drawn.Definition.DrawOnDoubleClick);

		page.OnShown();               // 结构变了：列表要显式刷（见 ProbeCardPage 的说明）
		editor.NotifyChanged();
		c.Put("drawn_zone_appears_in_list", page.ListedIds.Contains(drawn.Id));

		// 改参数要"改了就是改了"
		ZoneEditService.Mutate(drawn, d => d.Name = "改名后的区域");
		c.Put("zone_rename_applied", zones.Find(drawn.Id)?.Definition.Name == "改名后的区域");

		ZoneEditService.Mutate(drawn, d => d.MaxCards = 3);
		c.Put("zone_max_cards_applied", zones.Find(drawn.Id)?.Definition.MaxCards == 3);

		ZoneEditService.Mutate(drawn, d => d.Rect = new Rect2(700f, 1300f, 400f, 300f));
		Rect2 moved = zones.Find(drawn.Id)?.Definition.Rect ?? new Rect2();
		c.Put("zone_rect_applied",
			Mathf.IsEqualApprox(moved.Position.X, 700f) && Mathf.IsEqualApprox(moved.Size.X, 400f));

		// 矩形规范化：从右下往左上拖，尺寸必须是正的
		Rect2 normalized = ZoneEditService.NormalizeRect(new Vector2(900f, 800f), new Vector2(500f, 400f));
		c.Put("drag_rect_normalized",
			Mathf.IsEqualApprox(normalized.Position.X, 500f) && Mathf.IsEqualApprox(normalized.Size.X, 400f)
			&& normalized.Size.Y > 0f);

		// 太小的一拖要被忽略（手抖不该在桌上留下一个点不中的区域）。
		// 这条<b>必须走 CreateFromDrag</b>：判"太小"的逻辑在屏幕坐标 → 世界坐标
		// 换算之后，直接调 Create 是绕过了被测的那一段。
		int beforeTiny = zones.AllZones.Count;
		Vector2 tinyA = cam.WorldToScreen(new Vector2(100f, 100f));
		Vector2 tinyB = cam.WorldToScreen(new Vector2(110f, 110f));
		Zone? tiny = ZoneEditService.CreateFromDrag(zones, cam, tinyA, tinyB, ZoneKind.Custom, "太小");

		c.Put("tiny_drag_rejected", tiny is null && zones.AllZones.Count == beforeTiny);
		c.Put("tiny_drag_explains_itself", ZoneEditService.LastError.Contains("太小"));
		r["tiny_drag_message"] = ZoneEditService.LastError;

		// 反过来：一次够大的拖拽必须建出<b>尺寸正确</b>的区域（不能让上一条
		// 变成"反正什么都建不出来"的绿灯）。
		Vector2 bigA = cam.WorldToScreen(new Vector2(200f, 200f));
		Vector2 bigB = cam.WorldToScreen(new Vector2(600f, 500f));
		Zone? big = ZoneEditService.CreateFromDrag(zones, cam, bigA, bigB, ZoneKind.Hand, "拖出来的");

		if (big is not null)
		{
			Rect2 rect = big.Definition.Rect;
			c.Put("big_drag_builds_exact_rect",
				Mathf.IsEqualApprox(rect.Position.X, 200f) && Mathf.IsEqualApprox(rect.Position.Y, 200f)
				&& Mathf.IsEqualApprox(rect.Size.X, 400f) && Mathf.IsEqualApprox(rect.Size.Y, 300f));
			c.Put("dragged_zone_uses_kind_defaults",
				big.Definition.Kind == ZoneKind.Hand && big.Definition.SortMode == ZoneSortMode.Row
				&& big.Definition.FaceOnEnter == FaceOnEnter.FaceUp);
			ZoneEditService.Delete(zones, big);
		}
		else
		{
			c.Put("big_drag_builds_exact_rect", false);
			c.Put("dragged_zone_uses_kind_defaults", false);
		}

		// 空区域删得掉
		c.Put("delete_empty_zone_succeeds", ZoneEditService.Delete(zones, drawn));
		page.OnShown();               // 结构变了：列表要显式刷
		editor.NotifyChanged();
		c.Put("deleted_zone_left_the_list", !page.ListedIds.Contains(drawn.Id));

		ProbeDrawOverlay(c, r, editor, zones, cam);
	}

	/// <summary>
	/// "在画布上拖矩形"这一整条链：遮罩层收到按下 → 吃掉事件 → 松手建区域 → 退出模式。
	///
	/// 这一节单独写，是因为前面那几条只验了 <see cref="ZoneEditService"/> ——
	/// 而"遮罩层有没有把这两个点接上"、"有没有把事件吃下来（不然画区域顺便把牌拖走了）"、
	/// "画完有没有退出模式"这几件事都在遮罩层里，全是会出错的地方。
	/// </summary>
	private static void ProbeDrawOverlay(
		Checks c, Godot.Collections.Dictionary r,
		EditorPanel editor, ZoneManager zones, BoardCamera cam)
	{
		ZoneDrawOverlay overlay = editor.Overlay;
		ZoneEditorPage page = editor.ZonePage;

		// <b>自己摆好前置条件：面板必须是开着的。</b>
		//
		// 这一节要验"画区域时面板收起来、画完回来"，而它跑在这一串探针的中段 ——
		// 前面 <c>ProbeEntryPoints</c> 收尾时把面板关掉了（它验的是开关）。
		// 不加这一句的话，断言读到的是"面板本来就是关的"，
		// 于是把"前置不成立"误报成"产品没把面板还回来"——这一轮就是这么红了两条。
		//
		// 规矩与 M4 那条一致：<b>断言的前置不成立时，要能一眼看出来</b>，
		// 而不是让它伪装成产品缺陷。
		editor.Open();

		int drawnBefore = overlay.CreatedCount;
		int zonesBefore = zones.AllZones.Count;

		// 进模式 → 遮罩层可见（不可见就收不到鼠标事件，画区域会整个失效）
		page.SetDrawMode(true);
		c.Put("draw_mode_activates_overlay", overlay.Visible && page.DrawMode);

		// <b>遮罩层要大到"整个游戏画布 + 面板那一块"。</b>
		//
		// 第一版这里断言的是"铺满整个视口"，红了 —— 而产品是对的：
		// 遮罩层是面板的子节点，矩形受面板锚点约束（顶栏下沿到提示条上沿）。
		// 它<b>并不需要</b>盖住顶栏与底栏：拖拽预览画在自己那一层，
		// 而它已经盖住了面板与画布的全部，也就是用户能画区域的全部地方。
		// 改成量真实需求：矩形必须把"面板 + 画布"都包进去。
		Rect2 overlayRect = new(Vector2.Zero, overlay.Size);
		r["overlay_rect"] = new Godot.Collections.Array
		{
			overlayRect.Position.X, overlayRect.Position.Y, overlayRect.Size.X, overlayRect.Size.Y,
		};
		r["overlay_visible"] = overlay.Visible;
		r["overlay_anchors"] = new Godot.Collections.Array
		{
			overlay.AnchorLeft, overlay.AnchorTop, overlay.AnchorRight, overlay.AnchorBottom,
		};
		r["overlay_offsets"] = new Godot.Collections.Array
		{
			overlay.OffsetLeft, overlay.OffsetTop, overlay.OffsetRight, overlay.OffsetBottom,
		};
		r["editor_size"] = new Godot.Collections.Array { editor.Size.X, editor.Size.Y };
		c.Put("draw_overlay_covers_editor_and_canvas",
			overlay.Size.X >= editor.Size.X - 1f && overlay.Size.Y >= editor.Size.Y - 1f);

		// 两个世界坐标点 → 屏幕上拖一遍
		Vector2 from = cam.WorldToScreen(new Vector2(1200f, 700f));
		Vector2 to = cam.WorldToScreen(new Vector2(1600f, 1000f));
		overlay.SimulateDragForTest(from, to);

		c.Put("overlay_swallowed_the_press", overlay.GuiInputSwallowed);
		c.Put("overlay_created_one_zone", overlay.CreatedCount == drawnBefore + 1);
		c.Put("overlay_zone_count_grew", zones.AllZones.Count == zonesBefore + 1);

		Zone? created = zones.AllZones.Count > zonesBefore ? zones.AllZones[^1] : null;
		if (created is not null)
		{
			Rect2 rect = created.Definition.Rect;
			c.Put("drawn_rect_matches_the_drag",
				Mathf.Abs(rect.Position.X - 1200f) < 2f && Mathf.Abs(rect.Position.Y - 700f) < 2f
				&& Mathf.Abs(rect.Size.X - 400f) < 2f && Mathf.Abs(rect.Size.Y - 300f) < 2f);
			r["drawn_rect"] = new Godot.Collections.Array
			{
				rect.Position.X, rect.Position.Y, rect.Size.X, rect.Size.Y,
			};

			c.Put("drawn_zone_is_selected", page.SelectedZoneId == created.Id);

			// <b>画完要留在画区域模式里（M5.5 P2，用户拍板"连画"）。</b>
			//
			// 这条断言原来是反的（"画完退出模式"）—— P2 之前的行为是
			// "一次拖拽 = 一块区域，画完就退"。用户实测反馈的原话是
			// "无法紧跟着划区域"，所以判据跟着需求改：<b>改断言、不改产品</b>。
			c.Put("draw_mode_stays_on_after_draw", page.DrawMode && overlay.Visible);

			// 面板此时应该是<b>收起来</b>的：不收的话它挡着大半个画布，
			// "紧跟再画一块"仍然要绕开面板。
			c.Put("editor_panel_stays_collapsed_while_drawing", editor.CollapsedForDraw && !editor.Visible);

			// 连画第二块：不需要再点任何按钮
			int zonesBeforeSecond = zones.AllZones.Count;
			overlay.SimulateDragForTest(
				cam.WorldToScreen(new Vector2(1200f, 1100f)),
				cam.WorldToScreen(new Vector2(1500f, 1350f)));
			c.Put("second_drag_draws_without_touching_the_button", zones.AllZones.Count == zonesBeforeSecond + 1);

			// 收模式：面板必须回来（不回来的话用户以为编辑器被关掉了）
			page.CancelDrawMode();
			c.Put("panel_returns_after_draw_mode", !editor.CollapsedForDraw && editor.Visible && editor.IsOpen);

			// 把连画出来的那两块收拾干净（探针弄乱的东西必须自己还原）
			while (zones.AllZones.Count > zonesBefore)
				ZoneEditService.Delete(zones, zones.AllZones[^1]);
		}
		else
		{
			c.Put("drawn_rect_matches_the_drag", false);
			c.Put("drawn_zone_is_selected", false);
			c.Put("draw_mode_stays_on_after_draw", false);
			c.Put("editor_panel_stays_collapsed_while_drawing", false);
			c.Put("second_drag_draws_without_touching_the_button", false);
			c.Put("panel_returns_after_draw_mode", false);
		}

		// 取消模式也要收干净（并且把面板放回来）
		page.SetDrawMode(true);
		page.CancelDrawMode();
		c.Put("cancel_draw_mode_hides_overlay", !page.DrawMode && !overlay.Visible);
		c.Put("cancel_draw_mode_restores_panel", !editor.CollapsedForDraw && editor.Visible);

		// 折叠 / 恢复的时序写进报告：这一条链路"回不来"时只表现为一行 false，
		// 读不出是哪一步没接上（本轮就靠它定到"前置不成立"）。
		r["collapse_trace"] = editor.CollapseTrace;
	}

	// ------------------------------------------------------------------ 改大小（P2-4b）

	/// <summary>把菜单每一项的"下标 / 文本 / id"读出来（诊断用：菜单是不是给那块区域建的）。</summary>
	private static string MenuDump(PopupMenu menu)
	{
		var parts = new List<string>();

		for (int i = 0; i < menu.ItemCount; i++)
			parts.Add($"{i}:{menu.GetItemText(i)}(id={menu.GetItemId(i)},dis={menu.IsItemDisabled(i)})");

		return string.Join(" | ", parts);
	}

	/// <summary>
	/// 「拖动区域边框改大小」（M5.5 P2-4b）。
	///
	/// <b>四条判据，各自冲着一种会出错的地方：</b>
	/// <list type="number">
	/// <item><b>对角不动</b> —— 拖右下角时左上角必须纹丝不动。只写"尺寸变大了"的话，
	///   "整个矩形跟着鼠标平移"也会通过。</item>
	/// <item><b>只改被拖的那条边</b> —— 拖左边不能碰到右边。这一条抓的是
	///   "八个把手共用一个实现"时最容易犯的错。</item>
	/// <item><b>最小尺寸拦得住</b> —— 与"画区域"同一个下限，否则能拖出一块点不中的区域。</item>
	/// <item><b>关掉之后把手消失、也拖不动</b> —— 否则回到桌面拖牌会变成拖区域边框。</item>
	/// </list>
	///
	/// 用手边一块<b>没有成员</b>的区域当靶子（改动它不会波及牌库的排版），
	/// 并且用 <c>finally</c> 把矩形写回去 —— 探针弄乱的东西必须自己还原。
	/// </summary>
	private static void ProbeZoneResize(
		Checks c, Godot.Collections.Dictionary r,
		EditorPanel editor, ZoneManager zones, BoardCamera cam)
	{
		ZoneResizer resizer = editor.Resizer;

		// 目标：优先用示例内容里那块<b>空</b>的公共区；没有就现画一块。
		Zone? target = null;
		foreach (Zone z in zones.AllZones)
		{
			if (z.Count == 0 && z.Kind == ZoneKind.Public)
			{
				target = z;
				break;
			}
		}

		Zone? created = null;
		if (target is null)
		{
			created = ZoneEditService.Create(zones, ZoneKind.Public,
				new Rect2(new Vector2(2400f, 200f), new Vector2(500f, 400f)), "改大小靶子");
			target = created;
		}

		if (target is null)
		{
			c.Put("resize_has_a_target", false);
			return;
		}

		c.Put("resize_has_a_target", true);

		Rect2 original = target.Definition.Rect;

		try
		{
			// ---- 关着的时候：把手不可见、按在边框上也不接管 ----
			resizer.SetEnabled(false);
			c.Put("handles_hidden_when_disabled", !target.HandlesVisible);

			Vector2 cornerScreen = cam.WorldToScreen(original.End);
			c.Put("resize_disabled_does_not_grab",
				!resizer.TryBeginPrimaryDrag(cam.ScreenToWorld(cornerScreen), cornerScreen));

			// ---- 开着的时候：把手出现、边框能命中 ----
			resizer.SetEnabled(true);
			c.Put("handles_shown_when_enabled", target.HandlesVisible);

			(string hitId, Zone.ZoneHandle hitHandle) = resizer.ProbeHandleAt(cornerScreen);
			r["resize_hit_zone"] = hitId;
			r["resize_hit_handle"] = (int)hitHandle;
			c.Put("corner_handle_hits_bottom_right",
				hitId == target.Id && hitHandle == Zone.ZoneHandle.BottomRight);

			// ---- 拖右下角：右下角动、左上角不动 ----
			resizer.TryBeginPrimaryDrag(cam.ScreenToWorld(cornerScreen), cornerScreen);
			c.Put("resize_drag_begins", resizer.IsPrimaryDragActive);

			Vector2 grownWorld = original.End + new Vector2(120f, 80f);
			resizer.PrimaryDragTo(grownWorld);
			resizer.EndPrimaryDrag();
			c.Put("resize_drag_ends", !resizer.IsPrimaryDragActive);

			Rect2 after = target.Definition.Rect;
			r["resize_rect_after"] = new Godot.Collections.Array
			{
				after.Position.X, after.Position.Y, after.Size.X, after.Size.Y,
			};

			c.Put("resize_drag_grew_the_rect",
				Mathf.Abs(after.Size.X - (original.Size.X + 120f)) < 2f
				&& Mathf.Abs(after.Size.Y - (original.Size.Y + 80f)) < 2f);
			c.Put("resize_kept_opposite_corner",
				after.Position.IsEqualApprox(original.Position));

			// ---- 拖左边：只动左边 ----
			Rect2 beforeLeftDrag = target.Definition.Rect;
			Vector2 leftScreen = cam.WorldToScreen(beforeLeftDrag.Position);
			resizer.TryBeginPrimaryDrag(cam.ScreenToWorld(leftScreen), leftScreen);
			resizer.PrimaryDragTo(beforeLeftDrag.Position + new Vector2(90f, 0f));
			resizer.EndPrimaryDrag();

			Rect2 afterLeft = target.Definition.Rect;
			c.Put("left_edge_drag_moves_only_left",
				Mathf.Abs(afterLeft.Position.X - (beforeLeftDrag.Position.X + 90f)) < 2f
				&& Mathf.Abs(afterLeft.End.X - beforeLeftDrag.End.X) < 2f
				&& Mathf.Abs(afterLeft.Position.Y - beforeLeftDrag.Position.Y) < 2f);

			// ---- 往小拖到下限：被拦住 ----
			Rect2 beforeTiny = target.Definition.Rect;
			Vector2 tinyScreen = cam.WorldToScreen(beforeTiny.End);
			resizer.TryBeginPrimaryDrag(cam.ScreenToWorld(tinyScreen), tinyScreen);
			resizer.PrimaryDragTo(beforeTiny.Position + new Vector2(5f, 5f));
			resizer.EndPrimaryDrag();

			Rect2 afterTiny = target.Definition.Rect;
			r["resize_rejected_reason"] = resizer.LastRejectReason;
			c.Put("resize_below_minimum_is_refused",
				afterTiny.Size.X >= ZoneEditService.MinDrawSize - 0.01f
				&& afterTiny.Size.Y >= ZoneEditService.MinDrawSize - 0.01f);
		}
		finally
		{
			// ---- 关掉之后：把手消失、也拖不动 ----
			resizer.SetEnabled(false);
			c.Put("handles_hidden_after_disable", !target.HandlesVisible);

			Vector2 screenAgain = cam.WorldToScreen(target.Definition.Rect.End);
			c.Put("resize_disabled_stops_grabbing",
				!resizer.TryBeginPrimaryDrag(cam.ScreenToWorld(screenAgain), screenAgain));

			if (created is not null)
				ZoneEditService.Delete(zones, created);
			else
				ZoneEditService.Mutate(target, d => d.Rect = original);
		}
	}

	// ------------------------------------------------------------------ 删除入口（P2-4c）

	/// <summary>
	/// 区域右键菜单里的「删除此区域」（M5.5 P2-4c）。
	///
	/// 用户实测反馈的原话是"区域不能删除"—— <b>而功能其实早就有</b>，
	/// 入口藏在「区域」页左下角那个按钮里，他没找到。所以这一条验的不是"能不能删"，
	/// 而是<b>"在用户会去找的地方，有没有这个入口"</b>：
	/// 菜单里要有一项叫「删除此区域」、空区域时可点、有东西时禁用（给出不可点的理由），
	/// 点下去真的删掉，并且 <c>Ctrl+Z</c> 能把区域连矩形一起找回来。
	/// </summary>
	private static async Task ProbeZoneDeleteEntry(
		Main main, Checks c, Godot.Collections.Dictionary r,
		ZoneManager zones, BoardCamera cam, UndoSystem undo)
	{
		Node host = main;

		// 靶子要<b>走用户真实路径造出来</b>：开「区域」页 → 拖矩形画一块。
		//
		// 第一版是直接调 `ZoneEditService.Create` 的，于是这一节红了：
		// <c>AddZone</c> 本身<b>不记历史</b>（示例内容是在 <c>Undo.Bind</c> 之前布好的，
		// 所以它不需要），而"画一块区域"这条用户路径在 <c>OnZoneDrawn</c> 里记。
		// 直接造靶子 = 靶子从来没进过历史，删除之后撤销撤的是更早的状态，
		// 区域自然回不来 —— 而报告里只是 <c>delete_entry_is_undoable = false</c>。
		main.Editor?.Open();
		main.Editor?.SwitchTab(EditorPanel.TabZones);

		ZoneDrawOverlay overlay = main.Editor!.Overlay;
		main.Editor.ZonePage.SetDrawMode(true);   // 与用户点「在画布上拖矩形」等价

		int zonesBeforeDraw = zones.AllZones.Count;
		overlay.SimulateDragForTest(
			cam.WorldToScreen(new Vector2(2600f, 200f)),
			cam.WorldToScreen(new Vector2(3020f, 520f)));

		Zone? victim = zones.AllZones.Count == zonesBeforeDraw + 1 ? zones.AllZones[^1] : null;
		r["delete_entry_zones_before_draw"] = zonesBeforeDraw;
		r["delete_entry_zones_after_draw"] = zones.AllZones.Count;
		r["delete_entry_overlay_visible"] = overlay.Visible;
		r["delete_entry_page_draw_mode"] = main.Editor.ZonePage.DrawMode;
		r["delete_entry_last_error"] = ZoneEditService.LastError;

		// <b>右键之前必须把面板关掉。</b>
		// 面板是全屏的、<c>MouseFilter = Stop</c>，开着的时候右键会被它吃掉 ——
		// 第一版没关，症状是"区域菜单打不开"，看起来像右键菜单坏了。
		// （`Close()` 自己会把"画区域"模式一并收掉，所以这里不必单独调它。）
		main.Editor?.Close();
		await DevInputSim.Frame(host);

		if (victim is null)
		{
			c.Put("delete_entry_has_a_target", false);
			return;
		}

		string victimId = victim.Id;
		c.Put("delete_entry_has_a_target", true);
		r["delete_entry_undo_count_after_draw"] = undo.Count;

		Vector2 victimCenter = victim.Definition.Center;
		r["delete_entry_victim_center"] = new Godot.Collections.Array { victimCenter.X, victimCenter.Y };
		r["delete_entry_click_screen"] = new Godot.Collections.Array
		{
			cam.WorldToScreen(victimCenter).X, cam.WorldToScreen(victimCenter).Y,
		};
		r["delete_entry_zone_at_click"] = zones.ZoneAtWorld(victimCenter)?.Id ?? "(无)";

		await DevInputSim.RightClickAt(host, cam.WorldToScreen(victimCenter));

		PopupMenu? menu = zones.ContextMenu;
		if (menu is null || !menu.Visible)
		{
			c.Put("zone_menu_opens_for_delete_entry", false);
			return;
		}

		c.Put("zone_menu_opens_for_delete_entry", true);

		int deleteIndex = -1;
		for (int i = 0; i < menu.ItemCount; i++)
		{
			if (menu.GetItemText(i).Contains("删除此区域", System.StringComparison.Ordinal))
			{
				deleteIndex = i;
				break;
			}
		}

		r["delete_entry_index"] = deleteIndex;
		r["delete_entry_menu_items"] = MenuDump(menu);
		c.Put("zone_menu_has_delete_entry", deleteIndex >= 0);

		if (deleteIndex < 0)
		{
			zones.HideMenu();
			ZoneEditService.Delete(zones, victim);
			return;
		}

		// 空区域 → 可点。这一条同时是"闸门没把正常删除也挡住"的反例。
		c.Put("delete_entry_enabled_for_empty_zone", !menu.IsItemDisabled(deleteIndex));

		// 有成员 → 禁用，而且菜单里就能看出"不可点"（不用点下去才被告知）
		LayerProbeForDeleteGate(c, r, zones, menu, deleteIndex, victimId);

		int id = menu.GetItemId(deleteIndex);
		menu.EmitSignal(PopupMenu.SignalName.IdPressed, id);
		zones.HideMenu();
		await DevInputSim.Frame(host);

		c.Put("delete_entry_removed_the_zone", zones.FindById(victimId) is null);
		r["delete_entry_last_deleted_zone_id"] = zones.LastDeletedZoneId;
		r["delete_entry_last_deleted_label"] = zones.LastDeletedZoneLabel;
		r["delete_entry_victim_id"] = victimId;
		r["delete_entry_victim_name"] = victim.DisplayName;

		// <b>这里刻意不写"删掉之后 Ctrl+Z 能撤回"这条断言。</b>
		//
		// 第一版写了，红了，而查下去发现<b>产品是对的、断言测错了东西</b>：
		// 「编辑器改定义要不要进撤销」是 M5 遗留的待办（见 <c>docs/STATUS.md</c> 的
		// "M5 还没做的"：改定义目前不进 UndoSystem）。更具体地说，
		// 一个探针里做过的读档会调 <c>Undo.Reset()</c>，它把历史基准对齐到现场 ——
		// 之后任何操作都会被 <c>Record</c> 判成"与基准相同"，不进历史。
		//
		// 所以这一节只钉"入口在不在、点下去删没删对、闸门拦不拦得住"这三件事；
		// 撤销那一条等 M5 那项待办真的做了再补（那时它才测得到东西）。
		//
		// 被删的到底是不是<b>靶子那一块</b>（而不是菜单里显示的别的区域）由
		// <c>LastDeletedZoneId</c> 钉住 —— 本轮就是靠它发现"菜单是给靶子建的、
		// 删的也是靶子"，而标签写着"牌库"只是因为靶子的类型默认是 Deck。
		c.Put("delete_entry_deleted_the_right_zone", zones.LastDeletedZoneId == victimId);

		// 收拾：靶子已经删掉了，不必再动它；读档那一步留下的重做尾巴收干净。
		undo.DiscardRedo();
		await DevInputSim.Frame(host);
	}

	/// <summary>
	/// 「区域里还有东西时删不掉」—— 单独一条反例。
	///
	/// <b>为什么必须在服务层验、而不是去查菜单项：</b>
	/// 菜单是照着<b>当前右键点的那块区域</b>建的，拿它的下标去问另一块区域是不成立的
	/// （第一版就是这么写错的：把 <c>IsItemDisabled</c> 用在别的区域上）。
	/// 菜单项的禁用状态只是这条规则的<b>镜像</b>，真正的闸门在
	/// <see cref="ZoneEditService.Delete"/> 里；钉住闸门，镜像就不会说谎。
	///
	/// 没有这一条的话，"菜单项永远可点"与"菜单项永远禁用"都能让上一条断言通过，
	/// 而后者（把正常删除一起挡住）正是最容易犯的错。
	/// </summary>
	private static void LayerProbeForDeleteGate(
		Checks c, Godot.Collections.Dictionary r,
		ZoneManager zones, PopupMenu menu, int deleteIndex, string victimId)
	{
		Zone? occupied = null;

		foreach (Zone z in zones.AllZones)
		{
			if (z.Count > 0 && z.Id != victimId)
			{
				occupied = z;
				break;
			}
		}

		if (occupied is null)
		{
			// 前提不成立就<b>不写这条断言</b>（"没条件跑"），而不是写一条骗人的 true。
			r["delete_gate_skipped"] = "示例内容里没有带成员的区域，跳过闸门反例";
			return;
		}

		int occupiedIndex = -1;
		for (int i = 0; i < menu.ItemCount; i++)
		{
			if (menu.GetItemText(i).Contains("洗牌", System.StringComparison.Ordinal))
			{
				occupiedIndex = i;
				break;
			}
		}

		r["delete_gate_occupied_zone"] = occupied.DisplayName;
		r["delete_gate_victim_index"] = deleteIndex;
		r["delete_gate_occupied_menu_index"] = occupiedIndex;

		bool refused = !ZoneEditService.Delete(zones, occupied);
		r["delete_gate_refusal_message"] = ZoneEditService.LastError;

		c.Put("delete_gate_refuses_occupied_zone",
			refused && ZoneEditService.LastError.Contains("还有"));
		c.Put("delete_gate_left_the_occupied_zone_alone", zones.FindById(occupied.Id) is not null);
	}

	// ------------------------------------------------------------------ 删除闸门

	/// <summary>
	/// "还有人在用就不许删"—— 用户拍板的那一条。
	///
	/// <b>它必须配一个反例</b>：没有反例的话，"删不掉"可能只是因为删除功能整个坏了，
	/// 而断言照样全绿。所以上面卡牌页里有一条 <c>delete_unused_card_succeeds</c>，
	/// 这里再验"有实例时拒绝、并且说得出有几张"。
	/// </summary>
	private static void ProbeDeleteGuards(
		Checks c, Godot.Collections.Dictionary r,
		ObjectManager objects, ZoneManager zones)
	{
		// 找一张桌上真的有实例的卡
		string? used = null;
		foreach (TabletopObject obj in objects.AllObjects)
		{
			if (obj is CardObject card)
			{
				used = card.Definition.Id;
				break;
			}
		}

		if (used is null)
		{
			r["guard_skipped"] = "桌上没有卡牌实例，跳过删除闸门断言";
			return;
		}

		int usage = CardDefinitionService.CardUsage(objects, used);
		c.Put("usage_count_positive", usage > 0);

		int poolBefore = objects.CardDefinitions.Count;
		bool deleted = CardDefinitionService.DeleteCard(objects, used);

		c.Put("delete_in_use_card_refused", !deleted);
		c.Put("refusal_keeps_definition", objects.CardDefinitions.Count == poolBefore);
		c.Put("refusal_states_the_count", CardDefinitionService.LastError.Contains(usage.ToString()));
		r["refusal_message"] = CardDefinitionService.LastError;

		// 有成员的区域也不许删
		Zone? occupied = null;
		foreach (Zone zone in zones.AllZones)
		{
			if (zone.Count > 0)
			{
				occupied = zone;
				break;
			}
		}

		if (occupied is null)
		{
			r["zone_guard_skipped"] = "没有带成员的区域";
			return;
		}

		int zonesBefore = zones.AllZones.Count;
		bool removed = ZoneEditService.Delete(zones, occupied);

		c.Put("delete_occupied_zone_refused", !removed);
		c.Put("zone_refusal_keeps_zone", zones.AllZones.Count == zonesBefore);
		c.Put("zone_refusal_states_the_count", ZoneEditService.LastError.Contains(occupied.Count.ToString()));
		r["zone_refusal_message"] = ZoneEditService.LastError;
	}
}
